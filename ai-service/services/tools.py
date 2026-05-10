"""LangGraph tools for the production chat agent.

Why a fixed tool set (vs raw SQL access)
=========================================
Production agents must not be able to query the database freely. The
blast radius of an LLM emitting bad SQL — accidentally or via prompt
injection — is too large. Every tool in this module:

  • has a Pydantic-typed input schema the agent can't violate
  • runs a parameterised query (no string interpolation)
  • scopes by `accessible_report_ids` (the user's permission set) at the
    wrapper, not at the LLM, so the agent literally cannot see other
    orgs' data
  • caps result size to keep the context window predictable

Auth identity (`user_id`, `session_id`) is injected into a per-turn
ToolContext object. The agent calls tools with their *typed args only*;
the wrapper consumes the ToolContext from the LangGraph state. This is
how we keep auth out of the LLM-controllable surface area.

Tool list (16)
==============
search_chunks            — semantic + BM25 + fuzzy retrieval over a single report
get_page                 — fetch one page's full text
get_page_image           — render one PDF page as an image for multimodal Q&A
list_reports             — filtered metadata search
get_report_metadata      — title, org, sector, country, year, status, page count
get_report_summary       — AI-generated summary + key findings
get_report_indicators    — extracted KPIs / numbers
get_report_trends        — extracted trends
get_report_recommendations — extracted recommendations
get_report_keywords      — keywords + language
get_report_topics        — high-level topics / sectors covered (per-language)
get_translation          — translated TITLE / DESCRIPTION / SUMMARY (text only,
                            never a download URL — locked-in policy)
list_saved_reports       — current user's bookmarks
list_user_interests      — sectors / orgs / countries the user follows
find_similar_reports     — embedding-similarity search across reports
get_session_history      — previous turns in the current chat session
"""
from __future__ import annotations

import asyncio
import json
import logging
import re
import unicodedata
from dataclasses import dataclass
from typing import Any
from uuid import UUID

from langchain_core.tools import StructuredTool
from psycopg import AsyncConnection
from pydantic import BaseModel, Field

from core.chunking import DEFAULT_CHUNK_CHARS  # for size hints
from core.config import settings
from core.db import conn_ctx
from services import embed, query_rewriter, reranker
from services.access import accessible_report_ids

logger = logging.getLogger(__name__)


# ── Per-turn auth context ──────────────────────────────────────────────────

@dataclass(frozen=True)
class ToolContext:
    """Auth + session identity for a single chat turn.

    Built once at agent invocation, attached to LangGraph state, and read
    by every tool's wrapper. The LLM never sees these fields — it only
    sees the typed tool args. This is the entire reason the LLM can't
    accidentally pivot to another user's data.
    """
    user_id: UUID
    session_id: UUID
    accessible_ids: list[str]   # cached for the turn — see services/access.py


# ── Helpers ────────────────────────────────────────────────────────────────

# Hard cap on what any single tool returns to the agent, in characters.
# Prevents one bad list_reports from flooding the context window.
_MAX_TOOL_OUTPUT_CHARS = 8000


# ── Arabic normalization (mirror of arabic_normalize() SQL function) ─────
# Index-side and query-side MUST produce identical canonical forms or hybrid
# retrieval misses the very matches this normalization is supposed to catch.
# The SQL function lives in migration Feature_ArabicSearchTuning; if you
# change either side, change the other.

# Codepoints to strip outright.
_ARABIC_STRIP_CODEPOINTS = frozenset(
    list(range(0x064B, 0x0660))  # harakat: fathatan .. wavy hamza below
    + [0x0640]                   # tatweel / kashida
    + [0x0670]                   # superscript alef
)

# Single-char folds: variant -> canonical form.
_ARABIC_FOLD_TABLE = str.maketrans({
    "أ": "ا",  # alef-with-hamza-above   -> alef
    "إ": "ا",  # alef-with-hamza-below   -> alef
    "آ": "ا",  # alef-with-madda-above   -> alef
    "ٱ": "ا",  # alef-wasla              -> alef
    "ى": "ي",  # alef-maksura            -> yeh
    "ة": "ه",  # teh-marbuta             -> heh
    "ؤ": "و",  # waw-with-hamza-above    -> waw
    "ئ": "ي",  # yeh-with-hamza-above    -> yeh
})


def _normalize_query_for_fts(text: str) -> str:
    """Canonicalize a user query for FTS (and trigram) lookup.

    Mirror of the `arabic_normalize(text)` SQL function in the
    Feature_ArabicSearchTuning migration. Pipeline:

      1. NFKC — collapse Arabic presentation forms (U+FB50-FEFF) and other
         compatibility characters to their canonical codepoints.
      2. Strip harakat (U+064B-U+065F), tatweel (U+0640), superscript alef
         (U+0670). Users routinely omit these; indexed text was inconsistent
         with them. Both sides must drop them.
      3. Fold alef/yaa/taa-marbuta/hamza variants to canonical forms — the
         single biggest Arabic-FTS win we have today (resolves the
         "السعودية" vs "السعوديه" / "أحمد" vs "احمد" mismatches).
      4. lower() — applied last so the english arm of the FTS sees
         case-folded Latin and the trigram index sees one canonical form
         for mixed-script chunks.

    We do NOT reverse RTL order: user keyboard input is already in logical
    Unicode order (unlike PDF glyph extraction, which arrives in visual order).
    """
    text = unicodedata.normalize("NFKC", text)
    text = "".join(ch for ch in text if ord(ch) not in _ARABIC_STRIP_CODEPOINTS)
    text = text.translate(_ARABIC_FOLD_TABLE)
    return text.lower()


def _truncate(s: str, limit: int = _MAX_TOOL_OUTPUT_CHARS) -> str:
    """Truncate long strings with a clear marker so the agent knows there's
    more data it could ask for via narrower queries."""
    if len(s) <= limit:
        return s
    return s[:limit] + "\n…[truncated; ask a narrower question for full text]"


def _serialize(payload: Any) -> str:
    """Return a JSON-encoded string the agent can read. Non-ASCII (Arabic)
    stays unescaped so the LLM sees real characters, not \\uXXXX noise."""
    text = json.dumps(payload, ensure_ascii=False, default=str)
    return _truncate(text)


def _no_results(reason: str = "no results") -> str:
    """Stable shape for empty-result returns. The agent learns to recognise
    this as 'try a different query / report'."""
    return _serialize({"results": [], "reason": reason})


def _scope_clause_for_chunks(ctx: ToolContext, table_alias: str = "r") -> tuple[str, list[Any]]:
    """SQL fragment + params that constrains a JOIN'd `reports` row to the
    user's accessible set. Returns ('', []) when the user has zero access
    so the caller can short-circuit."""
    if not ctx.accessible_ids:
        return "", []
    return f'AND {table_alias}."Id" = ANY(%s)', [ctx.accessible_ids]


def _collect_section_keys(
    candidates: list[dict[str, Any]],
) -> list[tuple[str, int, str]]:
    """Distinct (report_id, page_number, section_title) tuples for the
    candidates that have a non-empty section_title. Hits with empty section
    titles fall back to single-chunk content (the section fetch can't
    sensibly group them)."""
    seen: set[tuple[str, int, str]] = set()
    keys: list[tuple[str, int, str]] = []
    for c in candidates:
        rid = c.get("report_id")
        pg = c.get("page_number")
        sec = (c.get("section_title") or "").strip()
        if not rid or pg is None or not sec:
            continue
        key = (str(rid), int(pg), sec)
        if key in seen:
            continue
        seen.add(key)
        keys.append(key)
    return keys


async def _fetch_section_chunks(
    *,
    section_keys: list[tuple[str, int, str]],
    target_ids: list[str],
    max_chars: int,
    max_chunks: int,
) -> dict[tuple[str, int, str], str]:
    """For each (report, page, section) group, return chunks concatenated in
    chunk_index order, capped at `max_chars` total and `max_chunks` chunks.

    Uses one round-trip with a VALUES join. The LIMIT is applied per group
    via ROW_NUMBER, so a single huge section can't blow the result set.
    """
    if not section_keys:
        return {}

    vals_sql = ", ".join("(%s::uuid, %s::int, %s)" for _ in section_keys)
    vals_params: list[Any] = [x for t in section_keys for x in t]

    async with conn_ctx() as conn:
        cur = await conn.execute(
            f"""
            WITH want(rid, pg, sec) AS (VALUES {vals_sql}),
            ranked AS (
                SELECT rc."ReportId"        AS rid,
                       rc."PageNumber"      AS pg,
                       w.sec                AS sec,
                       rc."ChunkIndex"      AS ci,
                       rc."Content"         AS content,
                       ROW_NUMBER() OVER (
                           PARTITION BY rc."ReportId", rc."PageNumber", w.sec
                           ORDER BY rc."ChunkIndex"
                       ) AS rnk
                FROM report_chunks rc
                JOIN want w
                  ON rc."ReportId" = w.rid
                 AND rc."PageNumber" = w.pg
                 AND COALESCE(rc.metadata->>'section_title', '') = w.sec
                WHERE rc."ReportId" = ANY(%s)
                  AND rc."ParentChunkId" IS NULL
            )
            SELECT rid::text, pg, sec, ci, content
              FROM ranked
             WHERE rnk <= %s
             ORDER BY rid, pg, sec, ci
            """,
            [*vals_params, target_ids, max_chunks],
        )
        rows = await cur.fetchall()

    grouped: dict[tuple[str, int, str], list[str]] = {}
    for r in rows:
        key = (str(r[0]), int(r[1]), r[2])
        grouped.setdefault(key, []).append(r[4] or "")

    out: dict[tuple[str, int, str], str] = {}
    for key, chunks in grouped.items():
        joined = "\n\n".join(c for c in chunks if c.strip())
        if len(joined) > max_chars:
            joined = joined[:max_chars] + "\n…[section truncated]"
        out[key] = joined
    return out


async def _hybrid_retrieve_one(
    *,
    target_ids: list[str],
    query_text: str,
    qvec: list[float],
    pool: int,
    bt_sql: str,
    bt_params: list[Any],
) -> list[dict[str, Any]]:
    """Run dense + BM25 (+ optional fuzzy/trigram) + RRF for one query variant.

    Three retrieval arms feeding one RRF (k=60) fusion:

      • dense  — pgvector cosine ANN over `embedding`.
      • sparse — Postgres FTS over `search_vector` (Arabic + English),
        with a vector-cosine floor (`hybrid_bm25_vector_floor`) so keyword-
        heavy noise (page numbers, footers, repeated boilerplate) cannot ride
        BM25 alone into the top-K. Setting the floor to 0.0 disables the gate.

      • fuzzy  — pg_trgm trigram similarity over arabic_normalize("Content").
        Catches typos, OCR errors, partial-name lookups, and Arabic spelling
        variants the synonym fold doesn't cover. Backed by the GIN expression
        index `IX_report_chunks_content_trgm`. Disabled when
        `settings.fuzzy_retrieval_enabled=False`.

    Both fts_query and the trigram query string are produced by the same
    `_normalize_query_for_fts` so query-side and index-side share one
    canonical form (the SQL function arabic_normalize() built by the
    Feature_ArabicSearchTuning migration).
    """
    rrf_k = 60
    fts_query = _normalize_query_for_fts(query_text)
    bm25_floor = float(settings.hybrid_bm25_vector_floor)
    fuzzy_on = settings.fuzzy_retrieval_enabled

    # ── Optional fuzzy CTE + RRF pieces composed into the f-string ──────────
    # When disabled we emit no CTE and no extra RRF term, keeping the legacy
    # two-arm shape exactly. When enabled, fuzzy joins as a third FULL OUTER
    # arm using `Id` equality (same key as dense/sparse).
    if fuzzy_on:
        fuzzy_cte_sql = f""",
            fuzzy AS (
                SELECT rc."Id", rc."ReportId", rc."PageNumber", rc."ChunkIndex",
                       rc."Content", rc."ParentChunkId",
                       rc.metadata->>'section_title' AS section_title,
                       ROW_NUMBER() OVER (
                           ORDER BY arabic_normalize(rc."Content") <-> %s
                       ) AS rnk
                FROM report_chunks rc
                WHERE rc."ReportId" = ANY(%s)
                  AND rc.embedding IS NOT NULL
                  AND arabic_normalize(rc."Content") %% %s
                  {bt_sql}
                LIMIT %s
            )"""
        fuzzy_select_extra = """,
                       fz."Id"             AS fz_id,
                       fz."ReportId"       AS fz_report_id,
                       fz."PageNumber"     AS fz_page_number,
                       fz."ChunkIndex"     AS fz_chunk_index,
                       fz."Content"        AS fz_content,
                       fz."ParentChunkId"  AS fz_parent_chunk_id,
                       fz.section_title    AS fz_section_title"""
        fuzzy_join_sql = (
            "FULL OUTER JOIN fuzzy fz "
            'ON fz."Id" = COALESCE(d."Id", s."Id")'
        )
        fuzzy_coalesce_id     = 'COALESCE(d."Id",            s."Id",            fz."Id")'
        fuzzy_coalesce_rid    = 'COALESCE(d."ReportId",      s."ReportId",      fz."ReportId")'
        fuzzy_coalesce_page   = 'COALESCE(d."PageNumber",    s."PageNumber",    fz."PageNumber")'
        fuzzy_coalesce_chunk  = 'COALESCE(d."ChunkIndex",    s."ChunkIndex",    fz."ChunkIndex")'
        fuzzy_coalesce_cont   = 'COALESCE(d."Content",       s."Content",       fz."Content")'
        fuzzy_coalesce_parent = 'COALESCE(d."ParentChunkId", s."ParentChunkId", fz."ParentChunkId")'
        fuzzy_coalesce_sec    = 'COALESCE(d.section_title,   s.section_title,   fz.section_title)'
        fuzzy_rrf_term        = " + COALESCE(1.0 / (%s + fz.rnk), 0)"
    else:
        fuzzy_cte_sql = ""
        fuzzy_select_extra = ""
        fuzzy_join_sql = ""
        fuzzy_coalesce_id     = 'COALESCE(d."Id",            s."Id")'
        fuzzy_coalesce_rid    = 'COALESCE(d."ReportId",      s."ReportId")'
        fuzzy_coalesce_page   = 'COALESCE(d."PageNumber",    s."PageNumber")'
        fuzzy_coalesce_chunk  = 'COALESCE(d."ChunkIndex",    s."ChunkIndex")'
        fuzzy_coalesce_cont   = 'COALESCE(d."Content",       s."Content")'
        fuzzy_coalesce_parent = 'COALESCE(d."ParentChunkId", s."ParentChunkId")'
        fuzzy_coalesce_sec    = 'COALESCE(d.section_title,   s.section_title)'
        fuzzy_rrf_term        = ""

    async with conn_ctx() as conn:
        cur = await conn.execute(
            f"""
            WITH dense AS (
                SELECT rc."Id", rc."ReportId", rc."PageNumber", rc."ChunkIndex",
                       rc."Content", rc."ParentChunkId",
                       rc.metadata->>'section_title' AS section_title,
                       ROW_NUMBER() OVER (ORDER BY rc.embedding <=> %s::vector) AS rnk
                FROM report_chunks rc
                WHERE rc."ReportId" = ANY(%s)
                  AND rc.embedding IS NOT NULL
                  {bt_sql}
                ORDER BY rc.embedding <=> %s::vector
                LIMIT %s
            ),
            sparse AS (
                SELECT rc."Id", rc."ReportId", rc."PageNumber", rc."ChunkIndex",
                       rc."Content", rc."ParentChunkId",
                       rc.metadata->>'section_title' AS section_title,
                       ROW_NUMBER() OVER (
                           ORDER BY ts_rank(
                               rc.search_vector,
                               websearch_to_tsquery('arabic',  %s) ||
                               websearch_to_tsquery('english', %s)
                           ) DESC
                       ) AS rnk
                FROM report_chunks rc
                WHERE rc."ReportId" = ANY(%s)
                  AND rc.search_vector @@ (
                      websearch_to_tsquery('arabic',  %s) ||
                      websearch_to_tsquery('english', %s)
                  )
                  AND rc.embedding IS NOT NULL
                  AND (1 - (rc.embedding <=> %s::vector)) >= %s
                  {bt_sql}
                LIMIT %s
            ){fuzzy_cte_sql},
            fused AS (
                SELECT {fuzzy_coalesce_id}     AS hit_id,
                       {fuzzy_coalesce_rid}    AS report_id,
                       {fuzzy_coalesce_page}   AS page_number,
                       {fuzzy_coalesce_chunk}  AS chunk_index,
                       {fuzzy_coalesce_cont}   AS content,
                       {fuzzy_coalesce_parent} AS parent_chunk_id,
                       {fuzzy_coalesce_sec}    AS section_title,
                       COALESCE(1.0 / (%s + d.rnk), 0)
                     + COALESCE(1.0 / (%s + s.rnk), 0){fuzzy_rrf_term} AS rrf_score
                FROM dense d
                FULL OUTER JOIN sparse s ON d."Id" = s."Id"
                {fuzzy_join_sql}
            ),
            -- HyQE substitution: when a hit row has ParentChunkId set (i.e.
            -- a hypothetical question), swap in the parent chunk's identity
            -- (ReportId, PageNumber, ChunkIndex, Content, section_title) so
            -- the agent only ever sees real prose. Real-chunk hits pass
            -- through unchanged.
            resolved AS (
                SELECT
                    COALESCE(p."ReportId",   f.report_id)                   AS report_id,
                    COALESCE(p."PageNumber", f.page_number)                 AS page_number,
                    COALESCE(p."ChunkIndex", f.chunk_index)                 AS chunk_index,
                    COALESCE(p."Content",    f.content)                     AS content,
                    COALESCE(p.metadata->>'section_title', f.section_title) AS section_title,
                    f.rrf_score
                FROM fused f
                LEFT JOIN report_chunks p ON p."Id" = f.parent_chunk_id
            ),
            -- After substitution, the same parent might be represented by
            -- multiple hits (real chunk + its hypothetical questions). Keep
            -- only the best score per (report, page, chunk).
            deduped AS (
                SELECT report_id, page_number, chunk_index,
                       content, section_title,
                       MAX(rrf_score) AS rrf_score
                FROM resolved
                GROUP BY report_id, page_number, chunk_index, content, section_title
            )
            SELECT d.report_id, d.page_number, d.chunk_index, d.content,
                   r."Title" AS report_title, d.rrf_score, d.section_title
            FROM deduped d
            JOIN reports r ON r."Id" = d.report_id
            ORDER BY d.rrf_score DESC
            LIMIT %s
            """,
            [
                # dense: vec, ids, [bt?], vec, pool
                qvec, target_ids, *bt_params, qvec, pool,
                # sparse: fts x2 (ts_rank), ids, fts x2 (@@), vec, floor, [bt?], pool
                fts_query, fts_query, target_ids,
                fts_query, fts_query, qvec, bm25_floor, *bt_params, pool,
                # fuzzy: <-> query, ids, % query, [bt?], pool
                *([fts_query, target_ids, fts_query, *bt_params, pool] if fuzzy_on else []),
                # rrf k for dense + sparse (+ fuzzy)
                rrf_k, rrf_k,
                *([rrf_k] if fuzzy_on else []),
                # final limit
                pool,
            ],
        )
        rows = await cur.fetchall()

    return [
        {
            "report_id":     str(r[0]),
            "page_number":   int(r[1]) if r[1] is not None else None,
            "chunk_index":   int(r[2]) if r[2] is not None else None,
            "content":       r[3],
            "report_title":  r[4],
            "rrf_score":     float(r[5]),
            "section_title": r[6] or "",
        }
        for r in rows
    ]


# ────────────────────────────────────────────────────────────────────────────
# 1. search_chunks
# ────────────────────────────────────────────────────────────────────────────

class SearchChunksArgs(BaseModel):
    """Semantic + BM25 retrieval over a single report (or all accessible
    reports if report_id is omitted)."""
    query: str = Field(..., description="The natural-language search query.")
    report_id: str | None = Field(
        None, description="Optional report to search within. Omit to search across all reports the user can access.",
    )
    top_k: int = Field(5, ge=1, le=20, description="How many chunks to return.")
    block_types: list[str] | None = Field(
        None,
        description=(
            "Optional content-type filter. Pass ['table'] for KPI/number questions, "
            "['figure'] for chart questions. Omit to search all content types."
        ),
    )


async def _search_chunks_impl(ctx: ToolContext, args: SearchChunksArgs) -> str:
    """Hybrid retrieval over `report_chunks` (dense + sparse fused via RRF)
    optionally reranked by the Vertex AI Ranking API.

    Latency-shaped pipeline (fan-out + merge):

      Two pipelines run **in parallel via asyncio.gather** so the user-perceived
      latency is max(orig, variants), not orig + variants.

      • Original-query pipeline:
          1. Embed the user's question (gemini-embedding-001 RETRIEVAL_QUERY).
          2. Hybrid retrieval (dense ANN + BM25 sparse + RRF k=60). The BM25
             arm is gated by `hybrid_bm25_vector_floor` so keyword-only noise
             (page numbers, boilerplate) cannot enter the candidate pool.

      • Variant pipeline:
          1. `query_rewriter.rewrite_query` produces 0-N bilingual /
             decomposed variants with its own internal timeout. Failure or
             timeout collapses to "no extra retrievals."
          2. Variants are de-duped against the original (case-insensitive),
             embedded in one batched call, then each runs the same hybrid
             retrieval in parallel.

    Both pipelines write into a single merge step: keep the BEST RRF score per
    (report, page, chunk_index). Then:

      3. Optional block_types filter — restricts both arms to chunks whose
         metadata JSONB contains all requested types (e.g. ["table"]).
      4. Cross-encoder rerank via Vertex AI Ranking API. We pass the
         ORIGINAL query — the variants are a recall device, not a relevance
         signal; the reranker is the truth-teller.
      5. Neighbor context — for each top-k hit, the immediately preceding and
         following chunks on the same page are fetched and attached as
         `prev_content` / `next_content` so the LLM sees complete thoughts
         that straddle a chunk boundary.
    """
    if not ctx.accessible_ids:
        return _no_results("user has no accessible reports")

    target_ids = ctx.accessible_ids
    if args.report_id:
        if args.report_id not in ctx.accessible_ids:
            return _no_results("report_id is outside accessible scope")
        target_ids = [args.report_id]

    pool = (
        max(settings.reranker_candidate_pool, args.top_k)
        if settings.reranker_enabled else args.top_k
    )

    # Optional block_types filter — checks that the chunk's metadata JSONB
    # array contains every requested type. Uses GIN index on metadata.
    bt_sql = ""
    bt_params: list[Any] = []
    if args.block_types:
        bt_sql = "AND rc.metadata @> %s::jsonb"
        bt_params = [json.dumps({"block_types": args.block_types})]

    # Step 1 + 2 — fan out: original-query retrieval AND rewriter run in
    # parallel. User-perceived latency is max(orig_pipeline, rewrite_pipeline)
    # rather than orig + rewrite. The reranker downstream is the truth-teller,
    # so handing it 30 candidates from two query branches instead of 20 from
    # one is fine.
    async def _orig_pipeline() -> list[dict[str, Any]]:
        qvec = await asyncio.to_thread(embed.embed_query, args.query)
        if not qvec:
            return []
        return await _hybrid_retrieve_one(
            target_ids=target_ids,
            query_text=args.query,
            qvec=qvec,
            pool=pool,
            bt_sql=bt_sql,
            bt_params=bt_params,
        )

    async def _variants_pipeline() -> list[list[dict[str, Any]]]:
        # Rewriter has its own internal timeout; on failure it returns
        # [original] which we then dedupe out below — net effect is no extra
        # retrievals fired.
        variants = await query_rewriter.rewrite_query(args.query)
        # Drop the original (case-insensitive, whitespace-collapsed) — it's
        # already covered by _orig_pipeline.
        orig_norm = re.sub(r"\s+", " ", args.query.strip().lower())
        extra = [
            v for v in variants
            if re.sub(r"\s+", " ", v.strip().lower()) != orig_norm
        ]
        if not extra:
            return []
        vecs = await asyncio.to_thread(embed.embed_queries, extra)
        return list(await asyncio.gather(*[
            _hybrid_retrieve_one(
                target_ids=target_ids,
                query_text=v_text,
                qvec=v_vec,
                pool=pool,
                bt_sql=bt_sql,
                bt_params=bt_params,
            )
            for v_text, v_vec in zip(extra, vecs)
            if v_vec
        ]))

    orig_candidates, variant_batches = await asyncio.gather(
        _orig_pipeline(),
        _variants_pipeline(),
    )

    # Step 3 — merge: keep the BEST rrf_score per (report, page, chunk).
    all_batches = [orig_candidates, *variant_batches]
    merged: dict[tuple[str, int | None, int | None], dict[str, Any]] = {}
    for batch in all_batches:
        for c in batch:
            key = (c["report_id"], c["page_number"], c["chunk_index"])
            existing = merged.get(key)
            if existing is None or c["rrf_score"] > existing["rrf_score"]:
                merged[key] = c
    candidates = sorted(
        merged.values(), key=lambda c: c["rrf_score"], reverse=True,
    )[:pool]

    if not candidates:
        return _no_results("no matching chunks")

    final = candidates
    if settings.reranker_enabled and len(candidates) > args.top_k:
        final = await asyncio.to_thread(
            reranker.rerank, args.query, candidates, args.top_k,
        )
    final = final[: args.top_k]

    # ── Parent-section context fetch ──────────────────────────────────────────
    # For each hit, retrieve every chunk on the same page that shares the hit's
    # section_title and concatenate them in chunk_index order as
    # `section_content`. The LLM gets the full section (charts + caption,
    # bullet list + intro, table + surrounding paragraphs) instead of an
    # arbitrary ±1 window. Capped at `parent_section_max_chars` /
    # `parent_section_max_chunks` so a giant chapter can't poison the context
    # window. Disabled → fall back to the original ±1 neighbour fetch.
    if settings.parent_section_enabled:
        section_keys = _collect_section_keys(final)
        section_map = await _fetch_section_chunks(
            section_keys=section_keys,
            target_ids=target_ids,
            max_chars=settings.parent_section_max_chars,
            max_chunks=settings.parent_section_max_chunks,
        )
        results = []
        for c in final:
            rid = c["report_id"]
            pg = c.get("page_number")
            sec = (c.get("section_title") or "").strip()
            entry: dict[str, Any] = {
                "report_id":     rid,
                "report_title":  c.get("report_title"),
                "page":          pg,
                "section_title": c.get("section_title") or None,
                "content":       c.get("content"),
            }
            section_content = section_map.get((rid, pg, sec)) if pg is not None else None
            if section_content and section_content != c.get("content"):
                entry["section_content"] = section_content
            results.append(entry)
        return _serialize({"results": results})

    # ── Legacy neighbour fetch (parent_section_enabled=false) ─────────────────
    returned_keys = {
        (c["report_id"], c.get("page_number"), c.get("chunk_index"))
        for c in final
        if c.get("page_number") is not None and c.get("chunk_index") is not None
    }
    neighbor_keys: list[tuple[str, int, int]] = []
    for c in final:
        rid, pg, ci = c["report_id"], c.get("page_number"), c.get("chunk_index")
        if pg is None or ci is None:
            continue
        for nci in (ci - 1, ci + 1):
            if nci >= 0:
                key = (rid, pg, nci)
                if key not in returned_keys and key not in neighbor_keys:
                    neighbor_keys.append(key)

    neighbor_map: dict[tuple[str, int, int], str] = {}
    if neighbor_keys:
        vals_sql = ", ".join("(%s::uuid, %s::int, %s::int)" for _ in neighbor_keys)
        vals_params: list[Any] = [x for t in neighbor_keys for x in t]
        async with conn_ctx() as nb_conn:
            nb_cur = await nb_conn.execute(
                f"""
                WITH want(rid, pg, ci) AS (VALUES {vals_sql})
                SELECT rc."ReportId"::text, rc."PageNumber", rc."ChunkIndex",
                       left(rc."Content", 500)
                FROM report_chunks rc
                JOIN want
                  ON rc."ReportId"::text = want.rid
                 AND rc."PageNumber"     = want.pg
                 AND rc."ChunkIndex"     = want.ci
                WHERE rc."ReportId" = ANY(%s)
                """,
                [*vals_params, target_ids],
            )
            for nb_row in await nb_cur.fetchall():
                neighbor_map[
                    (str(nb_row[0]), int(nb_row[1]), int(nb_row[2]))
                ] = nb_row[3] or ""

    results = []
    for c in final:
        rid   = c["report_id"]
        pg    = c.get("page_number")
        ci    = c.get("chunk_index")
        entry: dict[str, Any] = {
            "report_id":     rid,
            "report_title":  c.get("report_title"),
            "page":          pg,
            "section_title": c.get("section_title") or None,
            "content":       c.get("content"),
        }
        if pg is not None and ci is not None:
            prev_text = neighbor_map.get((rid, pg, ci - 1))
            next_text = neighbor_map.get((rid, pg, ci + 1))
            if prev_text:
                entry["prev_content"] = prev_text
            if next_text:
                entry["next_content"] = next_text
        results.append(entry)

    return _serialize({"results": results})


# ────────────────────────────────────────────────────────────────────────────
# 2. get_page
# ────────────────────────────────────────────────────────────────────────────

class GetPageArgs(BaseModel):
    report_id: str = Field(..., description="The report's UUID.")
    page_number: int = Field(..., ge=1, description="1-based page number.")


async def _get_page_impl(ctx: ToolContext, args: GetPageArgs) -> str:
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")

    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            SELECT string_agg("Content", E'\n\n' ORDER BY "ChunkIndex") AS content
            FROM report_chunks
            WHERE "ReportId" = %s
              AND "PageNumber" = %s
              AND "ParentChunkId" IS NULL
            """,
            [args.report_id, args.page_number],
        )
        row = await cur.fetchone()

    content = (row[0] if row else None) or ""
    if not content:
        return _no_results(f"page {args.page_number} not found or empty")
    return _serialize({"page_number": args.page_number, "content": content})


# ────────────────────────────────────────────────────────────────────────────
# 3. list_reports
# ────────────────────────────────────────────────────────────────────────────

class ListReportsArgs(BaseModel):
    sector_slug: str | None = Field(None, description="Filter by sector slug, e.g. 'energy'.")
    country_iso: str | None = Field(None, description="ISO-3166 country code, e.g. 'SA'.")
    year: int | None = Field(None, ge=1900, le=2100, description="Publication year.")
    keyword: str | None = Field(None, description="Match against title or report_keywords.")
    featured_only: bool = Field(False, description="Restrict to featured reports.")
    limit: int = Field(10, ge=1, le=50)


async def _list_reports_impl(ctx: ToolContext, args: ListReportsArgs) -> str:
    if not ctx.accessible_ids:
        return _no_results("user has no accessible reports")

    where_parts = ['r."Id" = ANY(%s)']
    params: list[Any] = [ctx.accessible_ids]

    if args.sector_slug:
        where_parts.append('s."Slug" = %s')
        params.append(args.sector_slug.lower())
    if args.country_iso:
        where_parts.append('UPPER(c."IsoCode") = %s')
        params.append(args.country_iso.upper())
    if args.year is not None:
        where_parts.append('r."PublicationYear" = %s')
        params.append(args.year)
    if args.featured_only:
        where_parts.append('r."IsFeatured" = true')
    if args.keyword:
        where_parts.append(
            '(r."Title" ILIKE %s OR EXISTS ('
            ' SELECT 1 FROM report_keywords rk '
            ' WHERE rk."ReportId" = r."Id" AND rk."Keyword" ILIKE %s'
            '))'
        )
        kw = f"%{args.keyword}%"
        params.extend([kw, kw])

    where_sql = " AND ".join(where_parts)

    async with conn_ctx() as conn:
        cur = await conn.execute(
            f"""
            SELECT r."Id", r."Title", r."Slug", o."NameAr", o."NameEn",
                   s."NameAr" AS sector_ar, s."NameEn" AS sector_en,
                   c."IsoCode" AS country, r."PublicationYear",
                   r."PageCount", r."IsFeatured"
            FROM reports r
            LEFT JOIN organizations o ON o."Id" = r."OrganizationId"
            LEFT JOIN sectors s       ON s."Id" = r."SectorId"
            LEFT JOIN countries c     ON c."Id" = r."CountryId"
            WHERE {where_sql}
            ORDER BY r."PublicationDate" DESC NULLS LAST,
                     r."CreatedAt"      DESC
            LIMIT %s
            """,
            [*params, args.limit],
        )
        rows = await cur.fetchall()

    results = [
        {
            "report_id":      str(r[0]),
            "title":          r[1],
            "slug":           r[2],
            "organization":   r[3] or r[4],
            "sector":         r[5] or r[6],
            "country":        r[7],
            "year":           r[8],
            "page_count":     r[9],
            "is_featured":    r[10],
        }
        for r in rows
    ]
    payload: dict[str, Any] = {"count": len(results), "results": results}
    if not results:
        payload["reason"] = "no reports match these filters"
    return _serialize(payload)


# ────────────────────────────────────────────────────────────────────────────
# 4. get_report_metadata
# ────────────────────────────────────────────────────────────────────────────

class GetReportMetadataArgs(BaseModel):
    report_id: str = Field(..., description="The report's UUID.")


async def _get_report_metadata_impl(ctx: ToolContext, args: GetReportMetadataArgs) -> str:
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")

    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            SELECT r."Title", r."Slug", r."Description", r."ReportType",
                   r."OriginalLanguage", r."PublicationYear", r."PublicationDate",
                   r."PageCount", r."Status", r."IsFeatured",
                   r."ViewsCount", r."DownloadsCount",
                   r."AvgRating", r."RatingsCount",
                   o."NameAr", o."NameEn",
                   s."NameAr", s."NameEn",
                   c."IsoCode", c."NameAr", c."NameEn"
            FROM reports r
            LEFT JOIN organizations o ON o."Id" = r."OrganizationId"
            LEFT JOIN sectors s       ON s."Id" = r."SectorId"
            LEFT JOIN countries c     ON c."Id" = r."CountryId"
            WHERE r."Id" = %s
            """,
            [args.report_id],
        )
        r = await cur.fetchone()

    if not r:
        return _no_results("report not found")

    payload = {
        "title":             r[0],
        "slug":              r[1],
        "description":       r[2],
        "report_type":       r[3],
        "original_language": r[4],
        "year":              r[5],
        "publication_date":  str(r[6]) if r[6] else None,
        "page_count":        r[7],
        "status":            r[8],
        "is_featured":       r[9],
        "views":             r[10],
        "downloads":         r[11],
        "avg_rating":        float(r[12]) if r[12] is not None else None,
        "ratings_count":     r[13],
        "organization":      {"ar": r[14], "en": r[15]},
        "sector":            {"ar": r[16], "en": r[17]},
        "country":           {"iso": r[18], "ar": r[19], "en": r[20]},
    }
    return _serialize(payload)


# ────────────────────────────────────────────────────────────────────────────
# 5-8. AI content getters (summary / indicators / trends / recommendations)
# ────────────────────────────────────────────────────────────────────────────

class GetAiContentArgs(BaseModel):
    report_id: str = Field(..., description="The report's UUID.")
    language: str = Field("ar", description="'ar' or 'en'. AI content is generated per-language.")


async def _get_ai_content_field(
    ctx: ToolContext, args: GetAiContentArgs, field: str,
) -> str:
    """Shared body for summary/indicators/trends/recommendations.
    `field` is the literal column name on report_ai_contents."""
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")

    async with conn_ctx() as conn:
        cur = await conn.execute(
            f"""
            SELECT "{field}", "GeneratedAt"
            FROM report_ai_contents
            WHERE "ReportId" = %s
              AND "Language" = %s
              AND "DeletedAt" IS NULL
            ORDER BY "GeneratedAt" DESC NULLS LAST
            LIMIT 1
            """,
            [args.report_id, args.language.lower()],
        )
        row = await cur.fetchone()

    if not row or row[0] is None:
        return _no_results(f"{field} not generated yet for this language")

    value = row[0]
    # Summary is a plain text column; the others are jsonb. Decode jsonb.
    if isinstance(value, str) and field != "Summary":
        try:
            value = json.loads(value)
        except Exception:
            pass

    return _serialize({"language": args.language, field.lower(): value,
                       "generated_at": str(row[1]) if row[1] else None})


# ────────────────────────────────────────────────────────────────────────────
# 9. get_report_keywords
# ────────────────────────────────────────────────────────────────────────────

class GetKeywordsArgs(BaseModel):
    report_id: str = Field(..., description="The report's UUID.")


async def _get_report_keywords_impl(ctx: ToolContext, args: GetKeywordsArgs) -> str:
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")

    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            SELECT "Keyword", "Language"
            FROM report_keywords
            WHERE "ReportId" = %s
            ORDER BY "Keyword"
            """,
            [args.report_id],
        )
        rows = await cur.fetchall()

    if not rows:
        return _no_results("no keywords have been generated for this report yet")
    return _serialize({
        "keywords": [{"keyword": r[0], "language": r[1]} for r in rows],
    })


# ────────────────────────────────────────────────────────────────────────────
# 10. get_translation — TEXT ONLY, never a download URL (locked-in policy)
# ────────────────────────────────────────────────────────────────────────────

class GetTranslationArgs(BaseModel):
    report_id: str = Field(..., description="The report's UUID.")
    language: str = Field(..., description="Target language: 'ar' or 'en'.")


async def _get_translation_impl(ctx: ToolContext, args: GetTranslationArgs) -> str:
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")

    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            SELECT "TranslatedTitle", "TranslatedDescription",
                   "TranslatedSummary", "TranslationStatus", "TranslatedAt"
            FROM report_translations
            WHERE "ReportId" = %s
              AND "Language" = %s
              AND "DeletedAt" IS NULL
            ORDER BY "TranslatedAt" DESC NULLS LAST
            LIMIT 1
            """,
            [args.report_id, args.language.lower()],
        )
        row = await cur.fetchone()

    if not row:
        return _no_results(f"no {args.language} translation for this report")

    # Deliberate: NEVER return TranslatedFileUrl. Users can't download
    # PDFs through the agent. Locked-in 2026-04-30.
    payload = {
        "language":    args.language,
        "title":       row[0],
        "description": row[1],
        "summary":     row[2],
        "status":      str(row[3]),
        "translated_at": str(row[4]) if row[4] else None,
    }
    return _serialize(payload)


# ────────────────────────────────────────────────────────────────────────────
# 11. list_saved_reports — Published-only filter applied
# ────────────────────────────────────────────────────────────────────────────

class ListSavedReportsArgs(BaseModel):
    limit: int = Field(20, ge=1, le=50)


async def _list_saved_reports_impl(ctx: ToolContext, args: ListSavedReportsArgs) -> str:
    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            SELECT r."Id", r."Title", r."Slug", r."PublicationYear",
                   o."NameAr", o."NameEn", sr."SavedAt"
            FROM saved_reports sr
            JOIN reports r       ON r."Id"             = sr."ReportId"
            LEFT JOIN organizations o ON o."Id"         = r."OrganizationId"
            WHERE sr."UserId" = %s
              AND r."Status"  = 'Published'           -- "Published only" rule
              AND r."DeletedAt" IS NULL
            ORDER BY sr."SavedAt" DESC
            LIMIT %s
            """,
            [str(ctx.user_id), args.limit],
        )
        rows = await cur.fetchall()

    payload: dict[str, Any] = {
        "count": len(rows),
        "results": [
            {
                "report_id":    str(r[0]),
                "title":        r[1],
                "slug":         r[2],
                "year":         r[3],
                "organization": r[4] or r[5],
                "saved_at":     str(r[6]),
            }
            for r in rows
        ],
    }
    if not rows:
        payload["reason"] = "you have no saved reports yet"
    return _serialize(payload)


# ────────────────────────────────────────────────────────────────────────────
# 12. list_user_interests
# ────────────────────────────────────────────────────────────────────────────

class ListUserInterestsArgs(BaseModel):
    pass


async def _list_user_interests_impl(ctx: ToolContext, _: ListUserInterestsArgs) -> str:
    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            SELECT s."NameAr" AS sector_ar, s."NameEn" AS sector_en,
                   c."IsoCode", c."NameAr" AS country_ar, c."NameEn" AS country_en,
                   o."NameAr" AS org_ar, o."NameEn" AS org_en
            FROM user_interests ui
            LEFT JOIN sectors       s ON s."Id" = ui."SectorId"
            LEFT JOIN countries     c ON c."Id" = ui."CountryId"
            LEFT JOIN organizations o ON o."Id" = ui."OrganizationId"
            WHERE ui."UserId" = %s
            """,
            [str(ctx.user_id)],
        )
        rows = await cur.fetchall()

    payload: dict[str, Any] = {
        "interests": [
            {
                "sector":       (r[0] or r[1]) if (r[0] or r[1]) else None,
                "country":      (r[3] or r[4] or r[2]) if (r[2] or r[3] or r[4]) else None,
                "organization": (r[5] or r[6]) if (r[5] or r[6]) else None,
            }
            for r in rows
        ],
    }
    if not rows:
        payload["reason"] = "you haven't set any sector / country / organization interests yet"
    return _serialize(payload)


# ────────────────────────────────────────────────────────────────────────────
# 13. find_similar_reports
# ────────────────────────────────────────────────────────────────────────────

class FindSimilarReportsArgs(BaseModel):
    report_id: str = Field(..., description="The reference report's UUID.")
    top_k: int = Field(5, ge=1, le=10)


async def _find_similar_reports_impl(ctx: ToolContext, args: FindSimilarReportsArgs) -> str:
    """Hybrid similarity search combining three signals:

      1. Embedding centroid cosine — semantic similarity over chunk content.
         Structural chunks (table/figure/formula) excluded so the centroid
         reflects narrative content, not page furniture.
      2. Shared topics — count of overlapping high-level topics from
         report_ai_contents.Topics (case-insensitive). Direct evidence the
         summarizer thinks the two reports are about the same things.
      3. Shared keywords — count of overlapping report_keywords entries
         (case-insensitive). Catches similarity even when summaries phrase
         themes differently.

    Final score = embed_sim + 0.05·shared_topics + 0.02·shared_keywords.
    The boost weights are intentionally small so embedding still dominates,
    but a few overlapping topics can promote a strong topical match past a
    slightly-more-similar-by-vector but topically-unrelated report.

    Both shared counts are returned to the agent so it can explain WHY two
    reports were grouped (e.g. "they share the topic 'renewable energy'
    and 7 keywords").
    """
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")
    if not ctx.accessible_ids:
        return _no_results("no accessible reports to compare against")

    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            WITH src_v AS (
                -- Embedding centroid over narrative chunks only.
                -- ParentChunkId IS NULL excludes HyQE hypothetical rows so
                -- the centroid stays in chunk-text space, not question-space.
                SELECT AVG(embedding)::vector AS v
                FROM report_chunks
                WHERE "ReportId" = %s
                  AND embedding IS NOT NULL
                  AND "ParentChunkId" IS NULL
                  AND NOT (metadata @> '{"block_types":["table"]}'::jsonb)
                  AND NOT (metadata @> '{"block_types":["figure"]}'::jsonb)
                  AND NOT (metadata @> '{"block_types":["formula"]}'::jsonb)
            ),
            src_topics AS (
                -- Topics from the source report's AI content (any language).
                -- LATERAL unnest of the Topics jsonb array → one row per topic.
                SELECT DISTINCT lower(t.value) AS topic
                FROM report_ai_contents rac
                CROSS JOIN LATERAL jsonb_array_elements_text(
                    COALESCE(rac."Topics"::jsonb, '[]'::jsonb)
                ) AS t(value)
                WHERE rac."ReportId" = %s
                  AND rac."DeletedAt" IS NULL
            ),
            src_kw AS (
                -- Keywords for the source report (case-folded for matching).
                SELECT DISTINCT lower("Keyword") AS kw
                FROM report_keywords
                WHERE "ReportId" = %s
            ),
            candidates AS (
                -- Per-candidate embedding similarity.
                SELECT r."Id" AS rid,
                       r."Title", r."PublicationYear",
                       o."NameAr", o."NameEn",
                       1 - (AVG(rc.embedding) <=> (SELECT v FROM src_v)) AS embed_sim
                FROM report_chunks rc
                JOIN reports r ON r."Id" = rc."ReportId"
                LEFT JOIN organizations o ON o."Id" = r."OrganizationId"
                WHERE rc."ReportId" <> %s
                  AND rc."ReportId" = ANY(%s)
                  AND rc.embedding IS NOT NULL
                  AND rc."ParentChunkId" IS NULL
                  AND NOT (rc.metadata @> '{"block_types":["table"]}'::jsonb)
                  AND NOT (rc.metadata @> '{"block_types":["figure"]}'::jsonb)
                  AND NOT (rc.metadata @> '{"block_types":["formula"]}'::jsonb)
                  AND r."Status"     = 'Published'
                  AND r."DeletedAt" IS NULL
                GROUP BY r."Id", r."Title", r."PublicationYear", o."NameAr", o."NameEn"
                HAVING (SELECT v FROM src_v) IS NOT NULL
            ),
            topic_overlap AS (
                -- Count topics each candidate shares with the source.
                SELECT c.rid, COUNT(DISTINCT lower(t.value)) AS n
                FROM candidates c
                JOIN report_ai_contents rac
                  ON rac."ReportId" = c.rid AND rac."DeletedAt" IS NULL
                CROSS JOIN LATERAL jsonb_array_elements_text(
                    COALESCE(rac."Topics"::jsonb, '[]'::jsonb)
                ) AS t(value)
                WHERE lower(t.value) IN (SELECT topic FROM src_topics)
                GROUP BY c.rid
            ),
            kw_overlap AS (
                -- Count keywords each candidate shares with the source.
                SELECT c.rid, COUNT(DISTINCT lower(rk."Keyword")) AS n
                FROM candidates c
                JOIN report_keywords rk ON rk."ReportId" = c.rid
                WHERE lower(rk."Keyword") IN (SELECT kw FROM src_kw)
                GROUP BY c.rid
            )
            SELECT c.rid, c."Title", c."PublicationYear",
                   c."NameAr", c."NameEn",
                   c.embed_sim,
                   COALESCE(t.n, 0) AS shared_topics,
                   COALESCE(k.n, 0) AS shared_keywords,
                   (c.embed_sim
                    + 0.05 * COALESCE(t.n, 0)
                    + 0.02 * COALESCE(k.n, 0)) AS score
            FROM candidates c
            LEFT JOIN topic_overlap t ON t.rid = c.rid
            LEFT JOIN kw_overlap    k ON k.rid = c.rid
            ORDER BY score DESC NULLS LAST
            LIMIT %s
            """,
            [args.report_id, args.report_id, args.report_id,
             args.report_id, ctx.accessible_ids, args.top_k],
        )
        rows = await cur.fetchall()

    if not rows:
        return _no_results(
            "no similar reports found — the source report may not have any "
            "embedded chunks yet, or no other accessible reports overlap "
            "topically."
        )
    return _serialize({
        "results": [
            {
                "report_id":       str(r[0]),
                "title":           r[1],
                "year":            r[2],
                "organization":    r[3] or r[4],
                "similarity":      round(float(r[5]), 4) if r[5] is not None else None,
                "shared_topics":   int(r[6]),
                "shared_keywords": int(r[7]),
                "score":           round(float(r[8]), 4) if r[8] is not None else None,
            }
            for r in rows
        ],
    })


# ────────────────────────────────────────────────────────────────────────────
# 14. get_session_history
# ────────────────────────────────────────────────────────────────────────────

class GetSessionHistoryArgs(BaseModel):
    limit: int = Field(10, ge=1, le=20)


async def _get_session_history_impl(ctx: ToolContext, args: GetSessionHistoryArgs) -> str:
    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            SELECT "Role", "Content", "CreatedAt"
            FROM chat_messages
            WHERE "SessionId" = %s
            ORDER BY "CreatedAt" DESC
            LIMIT %s
            """,
            [str(ctx.session_id), args.limit],
        )
        rows = await cur.fetchall()

    # Reverse so the oldest message comes first — natural reading order.
    payload: dict[str, Any] = {
        "messages": [
            {"role": r[0], "content": r[1], "at": str(r[2])}
            for r in reversed(rows)
        ],
    }
    if not rows:
        payload["reason"] = "no prior messages in this session"
    return _serialize(payload)


# ────────────────────────────────────────────────────────────────────────────
# 16. get_page_image — multimodal page render (Option C)
# ────────────────────────────────────────────────────────────────────────────

class GetPageImageArgs(BaseModel):
    """Render one PDF page as a base64 PNG so the next agent hop can read
    the chart / figure / visual element directly via Gemini multimodal.

    The agent SHOULD reach for this tool only when the user is asking about
    something that text from search_chunks / get_page can't answer — exact
    data points off a chart, color of a series, contents of a legend, etc.
    For most factual questions the text tools are cheaper and sufficient.
    """
    report_id: str = Field(..., description="The report's UUID.")
    page_number: int = Field(..., ge=1, description="1-based page number to render.")


# Module-level PDF cache. Bytes cached for the lifetime of one Cloud Run
# instance. FIFO eviction once the total cached size exceeds the cap so a
# burst of requests against many reports can't OOM the container.
from collections import OrderedDict  # noqa: E402 — local to this section

_PDF_BYTES_CACHE: "OrderedDict[str, bytes]" = OrderedDict()
_PDF_CACHE_MAX_BYTES = 200 * 1024 * 1024  # 200 MB
_PDF_CACHE_LOCK = asyncio.Lock()


async def _fetch_pdf_bytes(file_url: str) -> bytes:
    """Fetch the source PDF for a report, cached in memory.

    Three supported URL shapes — the .NET side has historically stored each
    of these at different times, so we handle them all here rather than
    forcing a backfill:

        gs://<bucket>/<blob_path>      — explicit GCS URI (preferred)
        https://... / http://...       — direct HTTPS download (some imports)
        <bucket>/<blob_path>           — bare GCS bucket+path (no scheme)

    The bare form is what bulk-import / admin-UI flows currently emit; the
    leading bucket segment is unambiguous because GCS bucket names cannot
    contain '/' and our schema never stores a relative blob path without
    a bucket. Calls run in a thread for the blocking GCS / httpx work so
    the event loop stays free for other chats.
    """
    async with _PDF_CACHE_LOCK:
        cached = _PDF_BYTES_CACHE.get(file_url)
        if cached is not None:
            _PDF_BYTES_CACHE.move_to_end(file_url)
            return cached

    if file_url.startswith(("http://", "https://")):
        import httpx  # noqa: E402 — lazy import

        async with httpx.AsyncClient(timeout=60) as client:
            r = await client.get(file_url)
            r.raise_for_status()
            pdf_bytes = r.content
    else:
        # GCS — the bucket is ALWAYS `settings.gcs_bucket`. The stored
        # FileUrl is the full blob path within that bucket, including
        # any environment-prefix folder (e.g. `taqreerk-uploads-dev/...`
        # is a FOLDER inside the live bucket, not a separate bucket).
        # If the URL already has a `gs://` scheme we honour the bucket
        # encoded in it, but the .NET writer doesn't emit that form today.
        from google.cloud import storage  # noqa: E402 — lazy import

        if file_url.startswith("gs://"):
            gs_uri = file_url
        else:
            gs_uri = f"gs://{settings.gcs_bucket}/{file_url}"

        def _dl() -> bytes:
            client = storage.Client()
            blob = storage.Blob.from_string(gs_uri, client=client)
            return blob.download_as_bytes()

        pdf_bytes = await asyncio.to_thread(_dl)

    async with _PDF_CACHE_LOCK:
        _PDF_BYTES_CACHE[file_url] = pdf_bytes
        # FIFO evict until total cached bytes ≤ cap.
        current = sum(len(v) for v in _PDF_BYTES_CACHE.values())
        while current > _PDF_CACHE_MAX_BYTES and len(_PDF_BYTES_CACHE) > 1:
            _, evicted = _PDF_BYTES_CACHE.popitem(last=False)
            current -= len(evicted)
    return pdf_bytes


async def _get_page_image_impl(ctx: ToolContext, args: GetPageImageArgs) -> str:
    """Render one PDF page → base64 PNG, return as JSON with `image_b64`.

    The agent loop's `_tools_node_with_counter` post-processes the result,
    strips the b64 out of the ToolMessage content, and injects it as a
    multimodal HumanMessage so the next Gemini call can see the image
    natively. The b64 never leaves agent-internal state — it is not echoed
    back to the user or persisted to chat history.
    """
    if not settings.page_image_tool_enabled:
        return _no_results("page image tool is disabled")
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")

    # 1. Look up the file URL + page count for bounds checking.
    async with conn_ctx() as conn:
        cur = await conn.execute(
            'SELECT "FileUrl", "PageCount" FROM reports WHERE "Id" = %s',
            [args.report_id],
        )
        row = await cur.fetchone()

    if not row or not row[0]:
        return _no_results(
            "this report has no stored PDF on file; get_page_image is unavailable"
        )
    file_url: str = row[0]
    page_count: int | None = int(row[1]) if row[1] is not None else None
    if page_count is not None and args.page_number > page_count:
        return _no_results(
            f"page_number {args.page_number} exceeds the report's page_count {page_count}"
        )

    # 2. Fetch (and cache) the PDF bytes.
    try:
        pdf_bytes = await _fetch_pdf_bytes(file_url)
    except Exception as exc:
        logger.warning(
            "get_page_image: PDF fetch failed report=%s url=%s err=%s",
            args.report_id, file_url[:80], exc,
        )
        return _no_results(f"could not fetch source PDF: {exc}")

    # 3. Render the requested page via PyMuPDF — sync, run in thread.
    import base64  # noqa: E402 — local to this function
    import fitz    # noqa: E402 — PyMuPDF, already a dep

    def _render() -> bytes | None:
        try:
            with fitz.open(stream=pdf_bytes, filetype="pdf") as doc:
                if args.page_number < 1 or args.page_number > len(doc):
                    return None
                page = doc[args.page_number - 1]
                mat = fitz.Matrix(150 / 72, 150 / 72)  # DPI=150 matches ingest renderer
                pix = page.get_pixmap(matrix=mat, colorspace=fitz.csRGB)
                return pix.tobytes("png")
        except Exception as exc:
            logger.warning("get_page_image: render failed: %s", exc)
            return None

    png_bytes = await asyncio.to_thread(_render)
    if not png_bytes:
        return _no_results(f"could not render page {args.page_number}")

    b64 = base64.b64encode(png_bytes).decode("ascii")
    # Hard cap so a pathological page can't blow up agent state. 5 MB b64 ≈
    # 3.6 MB PNG — well above any reasonable single page at 150 DPI.
    if len(b64) > 5 * 1024 * 1024:
        return _no_results(
            f"rendered image for page {args.page_number} is too large to attach"
        )

    logger.info(
        "get_page_image: report=%s page=%d rendered=%d KB",
        args.report_id, args.page_number, len(png_bytes) // 1024,
    )
    # NOTE: this JSON is intercepted by the agent loop. `image_b64` is
    # stripped from the ToolMessage and re-attached as a multimodal
    # HumanMessage in the same turn. `report_id` + `page_number` are also
    # captured by the chat handler so the (report, page) pair can be
    # persisted with the assistant message and the image re-injected on
    # subsequent turns. Do NOT change these field names without updating
    # pipelines/agent.py._tools_node_with_counter AND api/chat.py.
    return json.dumps(
        {
            "report_id":   args.report_id,
            "page_number": args.page_number,
            "mime_type":   "image/png",
            "image_b64":   b64,
            "note": (
                f"Page {args.page_number} has been rendered and attached as "
                "an image. Read it directly to answer the question."
            ),
        },
        ensure_ascii=False,
    )


# ── Public render helper (used by api/chat.py to re-inject historical images)
# ────────────────────────────────────────────────────────────────────────────

async def render_page_to_b64(report_id: str, page_number: int) -> str | None:
    """Fetch the report's PDF (cached), render `page_number` at 150 DPI,
    return base64-encoded PNG. Returns None on any failure.

    Exposed for api/chat.py so it can re-attach pages from prior turns
    without going through the full agent + tool flow. Shares the same
    `_fetch_pdf_bytes` cache as `get_page_image` so a hot turn pays
    near-zero IO cost on repeat (report, page) pairs.
    """
    if not settings.page_image_tool_enabled:
        return None

    async with conn_ctx() as conn:
        cur = await conn.execute(
            'SELECT "FileUrl" FROM reports WHERE "Id" = %s',
            [report_id],
        )
        row = await cur.fetchone()
    if not row or not row[0]:
        return None
    file_url: str = row[0]

    try:
        pdf_bytes = await _fetch_pdf_bytes(file_url)
    except Exception as exc:
        logger.warning(
            "render_page_to_b64: PDF fetch failed report=%s url=%s err=%s",
            report_id, file_url[:80], exc,
        )
        return None

    import base64  # noqa: E402 — local to this function
    import fitz    # noqa: E402

    def _render() -> bytes | None:
        try:
            with fitz.open(stream=pdf_bytes, filetype="pdf") as doc:
                if page_number < 1 or page_number > len(doc):
                    return None
                page = doc[page_number - 1]
                mat = fitz.Matrix(150 / 72, 150 / 72)
                pix = page.get_pixmap(matrix=mat, colorspace=fitz.csRGB)
                return pix.tobytes("png")
        except Exception as exc:
            logger.warning("render_page_to_b64: render failed: %s", exc)
            return None

    png_bytes = await asyncio.to_thread(_render)
    if not png_bytes:
        return None
    b64 = base64.b64encode(png_bytes).decode("ascii")
    if len(b64) > 5 * 1024 * 1024:
        return None
    return b64


# ── Tool registry — used by the agent to bind to ChatVertexAI ──────────────


def build_tools(ctx: ToolContext) -> list[StructuredTool]:
    """Return the 16 tools bound to a per-turn ToolContext.

    Two boundary concerns are handled here so the inner `_xxx_impl(ctx, args)`
    bodies can stay simple:

      1. **Kwargs ↔ Pydantic adapter.** langchain-core's StructuredTool
         validates the LLM input against `args_schema` and then invokes
         the coroutine via `await coroutine(**validated_fields)`. Without
         the adapter, an inner function signed `(ctx, args)` would crash
         with "got unexpected keyword argument 'report_id'" on every
         call — symptom we hit in production was the agent retrying the
         same tool 3× before giving up.

      2. **Per-turn dedup guard.** Even with valid signatures an agent can
         loop on a legitimately empty result. We track `(name, args_sig)`
         in a closure-scoped cache and short-circuit a second identical
         call with a stop-message. The cache lives for one ToolContext
         (one chat turn); next turn starts fresh.

    Auth identity stays captured in the closure over `ctx` — never an arg
    the LLM controls.
    """
    # Per-turn dedup cache. Key: (tool_name, json-serialised args).
    call_cache: dict[tuple[str, str], str] = {}

    def _wrap(name: str, schema_cls: type[BaseModel], impl_async):
        """Adapter coroutine called by StructuredTool's _arun."""

        async def wrapped(**kwargs: Any) -> str:
            try:
                args = schema_cls(**kwargs)
            except Exception as exc:
                # StructuredTool already runs Pydantic validation upstream,
                # so this branch only fires on truly malformed input. Return
                # a stable shape rather than letting the exception propagate
                # as an opaque "Error: …" string.
                logger.warning("[tool] %s args repack failed: %s", name, exc)
                return _serialize({
                    "results": [],
                    "reason": f"invalid arguments to {name}: {exc}",
                })

            sig = (name, json.dumps(
                args.model_dump(mode="json"),
                sort_keys=True, ensure_ascii=False, default=str,
            ))
            if sig in call_cache:
                logger.info("[tool] %s dedup hit — short-circuiting retry", name)
                return _serialize({
                    "results": [],
                    "reason": (
                        f"You already called {name} with these arguments in "
                        "this turn — the result is unchanged. Do not call "
                        "again. Try different arguments, a different tool, "
                        "or answer with what you already have."
                    ),
                })

            try:
                result = await impl_async(ctx, args)
            except Exception as exc:
                logger.exception("[tool] %s raised: %s", name, exc)
                result = _serialize({
                    "results": [],
                    "reason": f"{name} failed with an internal error: {exc}",
                })
            call_cache[sig] = result
            return result

        return wrapped

    # (name, description, args schema, async impl(ctx, args) → str)
    table: list[tuple[str, str, type[BaseModel], Any]] = [
        ("search_chunks",
         "Hybrid (dense + BM25) retrieval over a single report — or all "
         "accessible reports if report_id is omitted — with cross-encoder "
         "rerank for top-1 accuracy. Use this for any content or factual "
         "question that isn't covered by the structured tools (summary, "
         "indicators, trends, recommendations).",
         SearchChunksArgs,
         _search_chunks_impl),

        ("get_page",
         "Fetch one PDF page's full text from a report. Use when the user asks for a specific page.",
         GetPageArgs,
         _get_page_impl),

        ("list_reports",
         "Filtered metadata search across reports the user can access. Use for 'list all', 'show me', 'how many' questions or to find report ids before asking content questions.",
         ListReportsArgs,
         _list_reports_impl),

        ("get_report_metadata",
         "Title, organization, sector, country, year, page count, ratings, and status of one report. Use to answer 'what is this report about?' at a structural level.",
         GetReportMetadataArgs,
         _get_report_metadata_impl),

        ("get_report_summary",
         "AI-generated executive summary + key findings for a report (per-language).",
         GetAiContentArgs,
         lambda c, a: _get_ai_content_field(c, a, "Summary")),

        ("get_report_indicators",
         "AI-extracted structured KPIs / indicators (numbers, percentages, ratios) for a report.",
         GetAiContentArgs,
         lambda c, a: _get_ai_content_field(c, a, "Indicators")),

        ("get_report_trends",
         "AI-extracted trends (rising / falling topics) for a report.",
         GetAiContentArgs,
         lambda c, a: _get_ai_content_field(c, a, "Trends")),

        ("get_report_recommendations",
         "AI-extracted recommendations / action items for a report.",
         GetAiContentArgs,
         lambda c, a: _get_ai_content_field(c, a, "Recommendations")),

        ("get_report_keywords",
         "Auto-tagged keywords (Arabic + English) for a report.",
         GetKeywordsArgs,
         _get_report_keywords_impl),

        ("get_report_topics",
         "High-level topics / sectors covered by a report (per-language). "
         "Use for 'what is this report about?' overview questions.",
         GetAiContentArgs,
         lambda c, a: _get_ai_content_field(c, a, "Topics")),

        ("get_translation",
         "Translated title / description / AI summary for a report. RETURNS TEXT ONLY — never a download link.",
         GetTranslationArgs,
         _get_translation_impl),

        ("list_saved_reports",
         "Reports the current user has bookmarked (Published only).",
         ListSavedReportsArgs,
         _list_saved_reports_impl),

        ("list_user_interests",
         "The current user's followed sectors / organizations / countries.",
         ListUserInterestsArgs,
         _list_user_interests_impl),

        ("find_similar_reports",
         "Reports semantically similar to the given one, ranked by chunk-embedding centroid cosine. Use to recommend follow-up reading.",
         FindSimilarReportsArgs,
         _find_similar_reports_impl),

        ("get_session_history",
         "Recent messages in the current chat session, oldest-first. Use to recall earlier context the user is referring back to.",
         GetSessionHistoryArgs,
         _get_session_history_impl),

        ("get_page_image",
         "Render one PDF page as an image so you can read it directly via multimodal vision. "
         "Use ONLY for VISUAL questions where text from search_chunks / get_page is insufficient: "
         "exact data points off a chart, color of a series, contents of a legend, fine layout details. "
         "Do NOT use it for questions whose answer is in the captioned text — that's wasteful. "
         "Costs more than text tools; spend the hop deliberately.",
         GetPageImageArgs,
         _get_page_image_impl),
    ]

    return [
        StructuredTool.from_function(
            coroutine=_wrap(name, schema, impl_async),
            name=name,
            description=desc,
            args_schema=schema,
        )
        for (name, desc, schema, impl_async) in table
    ]
