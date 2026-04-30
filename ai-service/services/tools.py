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

Tool list (14)
==============
search_chunks            — semantic + BM25 retrieval over a single report
get_page                 — fetch one page's full text
list_reports             — filtered metadata search
get_report_metadata      — title, org, sector, country, year, status, page count
get_report_summary       — AI-generated summary + key findings
get_report_indicators    — extracted KPIs / numbers
get_report_trends        — extracted trends
get_report_recommendations — extracted recommendations
get_report_keywords      — keywords + language
get_translation          — translated TITLE / DESCRIPTION / SUMMARY (text only,
                            never a download URL — locked-in policy)
list_saved_reports       — current user's bookmarks
list_user_interests      — sectors / orgs / countries the user follows
find_similar_reports     — embedding-similarity search across reports
get_session_history      — previous turns in the current chat session
"""
from __future__ import annotations

import json
import logging
from dataclasses import dataclass
from typing import Any
from uuid import UUID

from langchain_core.tools import StructuredTool
from psycopg import AsyncConnection
from pydantic import BaseModel, Field

from core.chunking import DEFAULT_CHUNK_CHARS  # for size hints
from core.db import conn_ctx
from services import embed
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


async def _search_chunks_impl(ctx: ToolContext, args: SearchChunksArgs) -> str:
    if not ctx.accessible_ids:
        return _no_results("user has no accessible reports")

    # Embed the query in the same vector space as stored chunks.
    qvec = embed.embed_query(args.query)
    if not qvec:
        return _no_results("embedding failed")

    target_ids = ctx.accessible_ids
    if args.report_id:
        if args.report_id not in ctx.accessible_ids:
            return _no_results("report_id is outside accessible scope")
        target_ids = [args.report_id]

    async with conn_ctx() as conn:
        # Dense top-K via pgvector cosine.
        cur = await conn.execute(
            """
            SELECT rc."ReportId", rc."PageNumber", rc."ChunkIndex",
                   rc."Content", r."Title"
            FROM report_chunks rc
            JOIN reports r ON r."Id" = rc."ReportId"
            WHERE rc."ReportId" = ANY(%s)
              AND rc.embedding IS NOT NULL
            ORDER BY rc.embedding <=> %s::vector
            LIMIT %s
            """,
            [target_ids, qvec, args.top_k],
        )
        rows = await cur.fetchall()

    results = [
        {
            "report_id":   str(r[0]),
            "report_title": r[4],
            "page":        int(r[1]) if r[1] is not None else None,
            "chunk_index": int(r[2]) if r[2] is not None else None,
            "content":     r[3],
        }
        for r in rows
    ]
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
    return _serialize({"count": len(results), "results": results})


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

    return _serialize({
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
    })


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

    return _serialize({
        "interests": [
            {
                "sector":       (r[0] or r[1]) if (r[0] or r[1]) else None,
                "country":      (r[3] or r[4] or r[2]) if (r[2] or r[3] or r[4]) else None,
                "organization": (r[5] or r[6]) if (r[5] or r[6]) else None,
            }
            for r in rows
        ],
    })


# ────────────────────────────────────────────────────────────────────────────
# 13. find_similar_reports
# ────────────────────────────────────────────────────────────────────────────

class FindSimilarReportsArgs(BaseModel):
    report_id: str = Field(..., description="The reference report's UUID.")
    top_k: int = Field(5, ge=1, le=10)


async def _find_similar_reports_impl(ctx: ToolContext, args: FindSimilarReportsArgs) -> str:
    if args.report_id not in ctx.accessible_ids:
        return _no_results("report_id is outside accessible scope")
    if not ctx.accessible_ids:
        return _no_results("no accessible reports to compare against")

    # Source report's "centroid" embedding = average of its chunk vectors.
    # Cosine similarity to other reports' centroids is a coarse but useful
    # similarity signal, and it runs entirely in-DB.
    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            WITH src AS (
                SELECT AVG(embedding)::vector AS v
                FROM report_chunks
                WHERE "ReportId" = %s
                  AND embedding IS NOT NULL
            )
            SELECT r."Id", r."Title", r."PublicationYear",
                   o."NameAr", o."NameEn",
                   1 - (AVG(rc.embedding) <=> (SELECT v FROM src)) AS similarity
            FROM report_chunks rc
            JOIN reports r       ON r."Id" = rc."ReportId"
            LEFT JOIN organizations o ON o."Id" = r."OrganizationId"
            WHERE rc."ReportId" <> %s
              AND rc."ReportId" = ANY(%s)
              AND rc.embedding IS NOT NULL
              AND r."Status" = 'Published'        -- "Published only" rule
              AND r."DeletedAt" IS NULL
            GROUP BY r."Id", r."Title", r."PublicationYear", o."NameAr", o."NameEn"
            HAVING (SELECT v FROM src) IS NOT NULL
            ORDER BY similarity DESC
            LIMIT %s
            """,
            [args.report_id, args.report_id, ctx.accessible_ids, args.top_k],
        )
        rows = await cur.fetchall()

    return _serialize({
        "results": [
            {
                "report_id":    str(r[0]),
                "title":        r[1],
                "year":         r[2],
                "organization": r[3] or r[4],
                "similarity":   round(float(r[5]), 4) if r[5] is not None else None,
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
    return _serialize({
        "messages": [
            {"role": r[0], "content": r[1], "at": str(r[2])}
            for r in reversed(rows)
        ],
    })


# ── Tool registry — used by the agent to bind to ChatVertexAI ──────────────


def build_tools(ctx: ToolContext) -> list[StructuredTool]:
    """Return the 14 tools bound to a per-turn ToolContext.

    The wrapper shape (`async def fn(args) -> str`) is what LangGraph's
    ToolNode expects: each tool sees its typed Pydantic args, returns a
    string the agent can read. Auth identity is captured in the closure
    over `ctx` — never an arg the LLM controls.
    """

    # Each tuple: (tool name, description, args schema, impl, [optional extra])
    table: list[tuple[str, str, type[BaseModel], Any]] = [
        ("search_chunks",
         "Semantic + keyword retrieval over a single report (or all accessible reports if report_id is omitted). Use this when the user asks about content or facts.",
         SearchChunksArgs,
         lambda a: _search_chunks_impl(ctx, a)),

        ("get_page",
         "Fetch one PDF page's full text from a report. Use when the user asks for a specific page.",
         GetPageArgs,
         lambda a: _get_page_impl(ctx, a)),

        ("list_reports",
         "Filtered metadata search across reports the user can access. Use for 'list all', 'show me', 'how many' questions or to find report ids before asking content questions.",
         ListReportsArgs,
         lambda a: _list_reports_impl(ctx, a)),

        ("get_report_metadata",
         "Title, organization, sector, country, year, page count, ratings, and status of one report. Use to answer 'what is this report about?' at a structural level.",
         GetReportMetadataArgs,
         lambda a: _get_report_metadata_impl(ctx, a)),

        ("get_report_summary",
         "AI-generated executive summary + key findings for a report (per-language).",
         GetAiContentArgs,
         lambda a: _get_ai_content_field(ctx, a, "Summary")),

        ("get_report_indicators",
         "AI-extracted structured KPIs / indicators (numbers, percentages, ratios) for a report.",
         GetAiContentArgs,
         lambda a: _get_ai_content_field(ctx, a, "Indicators")),

        ("get_report_trends",
         "AI-extracted trends (rising / falling topics) for a report.",
         GetAiContentArgs,
         lambda a: _get_ai_content_field(ctx, a, "Trends")),

        ("get_report_recommendations",
         "AI-extracted recommendations / action items for a report.",
         GetAiContentArgs,
         lambda a: _get_ai_content_field(ctx, a, "Recommendations")),

        ("get_report_keywords",
         "Auto-tagged keywords (Arabic + English) for a report.",
         GetKeywordsArgs,
         lambda a: _get_report_keywords_impl(ctx, a)),

        ("get_translation",
         "Translated title / description / AI summary for a report. RETURNS TEXT ONLY — never a download link.",
         GetTranslationArgs,
         lambda a: _get_translation_impl(ctx, a)),

        ("list_saved_reports",
         "Reports the current user has bookmarked (Published only).",
         ListSavedReportsArgs,
         lambda a: _list_saved_reports_impl(ctx, a)),

        ("list_user_interests",
         "The current user's followed sectors / organizations / countries.",
         ListUserInterestsArgs,
         lambda a: _list_user_interests_impl(ctx, a)),

        ("find_similar_reports",
         "Reports semantically similar to the given one, ranked by chunk-embedding centroid cosine. Use to recommend follow-up reading.",
         FindSimilarReportsArgs,
         lambda a: _find_similar_reports_impl(ctx, a)),

        ("get_session_history",
         "Recent messages in the current chat session, oldest-first. Use to recall earlier context the user is referring back to.",
         GetSessionHistoryArgs,
         lambda a: _get_session_history_impl(ctx, a)),
    ]

    return [
        StructuredTool.from_function(
            coroutine=impl,
            name=name,
            description=desc,
            args_schema=schema,
        )
        for (name, desc, schema, impl) in table
    ]
