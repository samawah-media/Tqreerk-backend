"""Chat endpoints — per-session Q&A with RAG (streaming).

Pipeline (each step skips when its predecessor short-circuits):
    1. Page-direct lookup ("give me page 5") — bypasses retrieval entirely
       when the user explicitly asks for a numbered page.
    2. Chat cache — exact-match on a SHA256 cache_key, then semantic match on
       cached question embeddings (only after we've embedded the question
       anyway for retrieval).
    3. Hybrid retrieval — dense (cosine) + sparse (tsvector) candidates fused
       via Reciprocal Rank Fusion (RRF). Replaces the older weighted-sum
       approach which was dominated by the dense score because the two scales
       were so different.
    4. Cross-encoder rerank — Vertex AI Ranking API picks top-K from the RRF
       pool. Behind a feature flag (settings.reranker_enabled).
    5. Gemini stream — final answer, streamed to the client over SSE.

Storage: all retrieval is now over `report_chunks` (sub-page chunks ~500
tokens each), not the previous `report_pages` table. Page-level features that
the frontend still expects (e.g. "show me page 5 verbatim") are reconstructed
by aggregating chunks for that page in chunk_index order.
"""
import asyncio
import json
import logging
import random
import re
import time
from uuid import UUID, uuid4

logger = logging.getLogger(__name__)

import numpy as np
from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import StreamingResponse
from psycopg import AsyncConnection

from core.config import settings
from core.db import conn_ctx, get_conn
from models.chat import (
    CreateSessionRequest,
    CreateSessionResponse,
    SendMessageRequest,
    SessionHistoryResponse,
    SessionMessage,
)
from pipelines.jobs import insert_job
from services import chat_cache, observability as obs, reranker
from services.doc_extractor import embed_texts as embed_via_gpu
from services.gemini import chat_with_context_stream

router = APIRouter(prefix="/chat", tags=["chat"])

# Recognises explicit page requests in either Arabic or English, accepting both
# Western (0-9) and Arabic-Indic (٠-٩) digit forms.
_PAGE_PATTERN = re.compile(
    r"(?:page|pg\.?|p\.?|الصفحة|صفحة|الصفحه|صفحه)\s*[#:\-]?\s*(\d+|[٠-٩]+)",
    re.IGNORECASE,
)
_ARABIC_TO_LATIN = str.maketrans("٠١٢٣٤٥٦٧٨٩", "0123456789")


def _detect_page_request(message: str) -> int | None:
    """Return the page number if the message explicitly asks for a page, else None.

    Examples that match: "give me page 2", "what is on page 5?",
    "ما هو محتوى الصفحة 3", "صفحة ٧".
    """
    m = _PAGE_PATTERN.search(message)
    if not m:
        return None
    try:
        return int(m.group(1).translate(_ARABIC_TO_LATIN))
    except ValueError:
        return None


@router.post("/sessions", response_model=CreateSessionResponse, status_code=201)
async def create_session(
    body: CreateSessionRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    session_id = uuid4()
    await conn.execute(
        """
        INSERT INTO chat_sessions ("Id", "UserId", "ReportId", "Title", "CreatedAt")
        VALUES (%s, %s, %s, %s, now())
        """,
        [str(session_id), str(body.user_id), str(body.report_id), body.title],
    )
    await conn.commit()
    return CreateSessionResponse(session_id=session_id, title=body.title)


@router.get("/sessions/{session_id}", response_model=SessionHistoryResponse)
async def get_session(
    session_id: UUID,
    conn: AsyncConnection = Depends(get_conn),
):
    row = await conn.execute(
        'SELECT "Id", "ReportId", "Title" FROM chat_sessions WHERE "Id" = %s',
        [str(session_id)],
    )
    session = await row.fetchone()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")

    msgs_cur = await conn.execute(
        """
        SELECT "Role", "Content", "SourcePages"
        FROM chat_messages
        WHERE "SessionId" = %s
        ORDER BY "CreatedAt"
        """,
        [str(session_id)],
    )
    messages = [
        SessionMessage(
            role=r,
            content=c,
            source_pages=json.loads(sp) if sp else None,
        )
        for r, c, sp in await msgs_cur.fetchall()
    ]

    return SessionHistoryResponse(
        session_id=session_id,
        report_id=session[1],
        title=session[2],
        messages=messages,
    )


# ── Retrieval helpers ────────────────────────────────────────────────────────

async def _fetch_page_chunks(
    conn: AsyncConnection, report_id, page_number: int,
) -> list[dict]:
    """Return all chunks for an explicit page request, in chunk_index order."""
    cur = await conn.execute(
        """
        SELECT "PageNumber", "Content", "ChunkIndex"
        FROM report_chunks
        WHERE "ReportId" = %s AND "PageNumber" = %s
        ORDER BY "ChunkIndex"
        """,
        [str(report_id), page_number],
    )
    rows = await cur.fetchall()
    return [
        {"page_number": p, "content": c, "chunk_index": i}
        for p, c, i in rows
    ]


async def _hybrid_rrf_retrieve(
    conn: AsyncConnection,
    report_id,
    question: str,
    question_vec: np.ndarray,
    top_n: int,
) -> list[dict]:
    """Hybrid retrieval over report_chunks using Reciprocal Rank Fusion.

    Why RRF instead of weighted-sum: the dense cosine score and the tsvector
    rank live on completely different scales (cosine ∈ [0,1] tightly clustered
    around 0.7-0.9 vs ts_rank unbounded but typically 0.001-0.1). A linear
    combination is dominated by whichever scale has more dynamic range, which
    in practice means the BM25 contribution is noise. RRF ignores raw scores
    and uses ranks: rrf(d) = Σ 1/(k + rank_in_list_i). It's scale-invariant
    and is what production engines (Elasticsearch, Vespa) ship as default.

    `k = 60` is the canonical RRF constant; higher k flattens the curve, lower
    k weights the very top of each list more aggressively. 60 has been
    empirically robust across domains.
    """
    rrf_k = 60

    cur = await conn.execute(
        """
        WITH dense AS (
            SELECT "PageNumber", "Content", "ChunkIndex", metadata,
                   ROW_NUMBER() OVER (ORDER BY embedding <=> %s) AS rnk
            FROM report_chunks
            WHERE "ReportId" = %s AND embedding IS NOT NULL
            ORDER BY embedding <=> %s
            LIMIT %s
        ),
        sparse AS (
            SELECT "PageNumber", "Content", "ChunkIndex", metadata,
                   ROW_NUMBER() OVER (
                       ORDER BY ts_rank(search_vector,
                                        plainto_tsquery('arabic', %s) ||
                                        plainto_tsquery('english', %s)) DESC
                   ) AS rnk
            FROM report_chunks
            WHERE "ReportId" = %s
              AND search_vector @@ (plainto_tsquery('arabic', %s) ||
                                    plainto_tsquery('english', %s))
            LIMIT %s
        )
        SELECT COALESCE(d."PageNumber", s."PageNumber")  AS page_number,
               COALESCE(d."Content",    s."Content")     AS content,
               COALESCE(d."ChunkIndex", s."ChunkIndex")  AS chunk_index,
               COALESCE(d.metadata,     s.metadata)      AS metadata,
               COALESCE(1.0 / (%s + d.rnk), 0)
             + COALESCE(1.0 / (%s + s.rnk), 0)           AS rrf_score
        FROM dense d
        FULL OUTER JOIN sparse s
          ON  d."PageNumber" = s."PageNumber"
          AND d."ChunkIndex" = s."ChunkIndex"
        ORDER BY rrf_score DESC
        LIMIT %s
        """,
        [
            question_vec, str(report_id), question_vec, top_n,
            question, question, str(report_id), question, question, top_n,
            rrf_k, rrf_k,
            top_n,
        ],
    )
    rows = await cur.fetchall()
    return [
        {
            "page_number": p,
            "content": c,
            "chunk_index": ci,
            "metadata": m or {},
            "rrf_score": float(s),
        }
        for p, c, ci, m, s in rows
    ]


def _dedupe_pages_in_order(chunks: list[dict]) -> list[int]:
    """Return source page numbers, preserving rank order, no duplicates."""
    seen: set[int] = set()
    out: list[int] = []
    for ch in chunks:
        p = int(ch["page_number"])
        if p not in seen:
            seen.add(p)
            out.append(p)
    return out


# ── Streaming endpoint ───────────────────────────────────────────────────────

@router.post("/sessions/{session_id}/messages")
async def send_message(
    session_id: UUID,
    body: SendMessageRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Stream the assistant's answer back as Server-Sent Events.

    Event format (one event per line, blank-line separated):
      data: {"type": "sources", "pages": [3, 7, 12]}      ← sent first
      data: {"type": "cache_hit", "tier": "exact|semantic"}  ← only on cache hit
      data: {"type": "token", "text": "Hello"}            ← repeated
      data: {"type": "token", "text": " there"}
      data: {"type": "done"}                              ← sent last

    Frontend should consume via EventSource or fetch + ReadableStream.
    """
    # ── Timing instrumentation — each step logs its duration ───────────────
    t0 = time.perf_counter()
    def _mark(label: str, since: float) -> float:
        now = time.perf_counter()
        logger.info("chat[%s] %s: %.0f ms", str(session_id)[:8], label, (now - since) * 1000)
        return now

    # ── Langfuse trace ─────────────────────────────────────────────────────
    # One trace per chat request, grouped by session_id so the dashboard
    # shows the full conversation under a single Langfuse "session". The
    # trace is a no-op when Langfuse is disabled or sampling drops it.
    chat_trace = obs.trace(
        name="chat",
        session_id=str(session_id),
        input={"message": body.message},
        tags=["chat"],
    )

    # ─── Validate session ───────────────────────────────────────────────────
    with obs.span(chat_trace, name="validate_session"):
        row = await conn.execute(
            'SELECT "ReportId" FROM chat_sessions WHERE "Id" = %s',
            [str(session_id)],
        )
        session = await row.fetchone()
        if not session:
            raise HTTPException(status_code=404, detail="Session not found")
        report_id = session[0]
    chat_trace.update(metadata={"report_id": str(report_id)})
    t1 = _mark("validate_session", t0)

    # ─── Load conversation history (last 10 messages → 5 turns) ────────────
    with obs.span(chat_trace, name="load_history") as sp:
        hist_cur = await conn.execute(
            """
            SELECT "Role", "Content" FROM (
                SELECT "Role", "Content", "CreatedAt"
                FROM chat_messages
                WHERE "SessionId" = %s
                ORDER BY "CreatedAt" DESC
                LIMIT 10
            ) recent
            ORDER BY "CreatedAt" ASC
            """,
            [str(session_id)],
        )
        history = [{"role": r, "content": c} for r, c in await hist_cur.fetchall()]
        sp.update(output={"turn_count": len(history)})
    t2 = _mark("load_history", t1)

    # ─── Cache lookup (layer 1: exact match — no embedding cost) ────────────
    # Only consult the cache for stateless questions: in a multi-turn chat,
    # the same surface text can mean different things depending on context.
    # Treating the first message of a session as cacheable is a safe default;
    # later turns flow through retrieval.
    cache_eligible = len(history) == 0
    cache_hit = None
    if cache_eligible:
        cache_hit = await chat_cache.lookup(conn, report_id, body.message)

    # ─── Page-number short-circuit (skips retrieval entirely) ───────────────
    requested_page = _detect_page_request(body.message)
    direct_chunks: list[dict] = []
    if cache_hit is None and requested_page is not None:
        direct_chunks = await _fetch_page_chunks(conn, report_id, requested_page)

    # ─── Hybrid RAG retrieval + rerank (only if no shortcut applied) ───────
    retrieved_chunks: list[dict] = []
    question_vec: np.ndarray | None = None
    if cache_hit is None and not direct_chunks:
        t_embed_start = time.perf_counter()
        with obs.span(chat_trace, name="embed_question") as sp:
            question_vec = np.array(
                embed_via_gpu([body.message], kind="query")[0],
                dtype=np.float32,
            )
            sp.update(output={"dim": int(question_vec.shape[0])})
        t_embed_done = _mark("embed_question", t_embed_start)

        # Layer 2 cache lookup is cheap once we have the embedding — try it
        # before paying for retrieval and the LLM.
        if cache_eligible:
            cache_hit = await chat_cache.lookup(
                conn, report_id, body.message, question_embedding=question_vec,
            )

        if cache_hit is None:
            candidate_pool = max(
                settings.reranker_candidate_pool,
                settings.reranker_top_k,
            ) if settings.reranker_enabled else settings.reranker_top_k

            with obs.span(chat_trace, name="hybrid_retrieve",
                          input={"query": body.message, "top_n": candidate_pool}) as sp:
                candidates = await _hybrid_rrf_retrieve(
                    conn, report_id, body.message, question_vec, top_n=candidate_pool,
                )
                sp.update(output={"n_candidates": len(candidates)})
            t_retrieve_done = _mark("hybrid_rrf", t_embed_done)

            # Cross-encoder rerank picks the final top-K from the candidate pool.
            # Falls back to retrieval order on /v1/rerank failure — handled
            # inside the wrapper.
            with obs.span(chat_trace, name="rerank",
                          input={"top_k": settings.reranker_top_k}) as sp:
                retrieved_chunks = reranker.rerank(
                    body.message, candidates, top_k=settings.reranker_top_k,
                )
                sp.update(output={
                    "n_kept": len(retrieved_chunks),
                    "top_score": (retrieved_chunks[0].get("rerank_score")
                                  if retrieved_chunks else None),
                })
            _mark("rerank", t_retrieve_done)

    # ─── Persist the user message before streaming starts ───────────────────
    await conn.execute(
        """
        INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
        VALUES (gen_random_uuid(), %s, 'user', %s, NULL, now())
        """,
        [str(session_id), body.message],
    )
    await conn.commit()
    t4 = _mark("save_user_msg", t2)
    logger.info("chat[%s] pre_stream_total: %.0f ms", str(session_id)[:8], (t4 - t0) * 1000)

    # ─── Cache hit: return the stored answer as a synthetic stream ──────────
    # Same SSE shape as a normal answer, plus a `cache_hit` event so the client
    # / observability stack can tell them apart.
    if cache_hit is not None:
        sid_short = str(session_id)[:8]
        # Tag the trace as a cache hit so the dashboard can filter cache vs
        # full-pipeline requests and so per-tier hit-rates fall out for free.
        try:
            chat_trace.update(
                tags=["chat", "cache_hit", f"tier:{cache_hit.tier}"],
                metadata={"cache_tier": cache_hit.tier},
            )
        except Exception:
            pass

        async def cached_stream():
            try:
                yield f"data: {json.dumps({'type': 'cache_hit', 'tier': cache_hit.tier})}\n\n"
                yield f"data: {json.dumps({'type': 'sources', 'pages': cache_hit.source_pages})}\n\n"
                yield f"data: {json.dumps({'type': 'token', 'text': cache_hit.answer})}\n\n"
                yield f"data: {json.dumps({'type': 'done'})}\n\n"

                # Persist the cached answer as a real assistant message so
                # session history stays consistent regardless of cache state.
                async with conn_ctx() as save_conn:
                    await save_conn.execute(
                        """
                        INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
                        VALUES (gen_random_uuid(), %s, 'assistant', %s, %s, now())
                        """,
                        [str(session_id), cache_hit.answer, json.dumps(cache_hit.source_pages)],
                    )
                    await save_conn.commit()
            except Exception as exc:
                logger.error("chat[%s] cached_stream_error: %s", sid_short, exc)
                yield f"data: {json.dumps({'type': 'error', 'message': str(exc)})}\n\n"
            finally:
                # Close the trace with the cached answer so dashboards see
                # the same shape regardless of cache vs full-pipeline path.
                try:
                    chat_trace.update(output={
                        "answer": cache_hit.answer,
                        "source_pages": cache_hit.source_pages,
                        "cache_tier": cache_hit.tier,
                    })
                    obs.flush()
                except Exception:
                    pass

        return StreamingResponse(
            cached_stream(),
            media_type="text/event-stream",
            headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
        )

    # ─── Build the context list passed to Gemini ────────────────────────────
    context_chunks_objs = direct_chunks or retrieved_chunks
    context_chunks = [c["content"] for c in context_chunks_objs]
    source_pages = _dedupe_pages_in_order(context_chunks_objs)

    # ─── Streaming generator ────────────────────────────────────────────────
    user_message = body.message
    sid = str(session_id)
    sid_short = sid[:8]
    cacheable = cache_eligible and question_vec is not None

    # Generation event for the streaming Gemini call. We open it now (so it
    # appears in the trace before the first token) and update with the full
    # output + token usage after the stream ends.
    generation = obs.generation(
        chat_trace,
        name="gemini_chat",
        model=settings.gemini_chat_model,
        input={
            "question": user_message,
            "context_chunks": context_chunks,
            "history_turns": len(history),
        },
        metadata={
            "source_pages": source_pages,
            "n_chunks": len(context_chunks),
        },
    )

    async def event_stream():
        full_answer = ""
        stream_start = time.perf_counter()
        first_token_at: float | None = None
        token_count = 0
        try:
            # Tell the client which pages we're answering from before the first token
            yield f"data: {json.dumps({'type': 'sources', 'pages': source_pages})}\n\n"
            await asyncio.sleep(0)  # let uvicorn flush the bytes to the client

            # Run the SYNC Gemini stream in a thread so we don't block the event loop.
            # Each chunk gets pushed onto an async queue → consumed and yielded here.
            queue: asyncio.Queue = asyncio.Queue()
            SENTINEL = object()
            loop = asyncio.get_running_loop()

            def producer():
                try:
                    for delta in chat_with_context_stream(history, user_message, context_chunks):
                        loop.call_soon_threadsafe(queue.put_nowait, delta)
                finally:
                    loop.call_soon_threadsafe(queue.put_nowait, SENTINEL)

            asyncio.get_event_loop().run_in_executor(None, producer)

            while True:
                item = await queue.get()
                if item is SENTINEL:
                    break
                if first_token_at is None:
                    first_token_at = time.perf_counter()
                    logger.info(
                        "chat[%s] gemini_first_token: %.0f ms",
                        sid_short, (first_token_at - stream_start) * 1000,
                    )
                token_count += 1
                full_answer += item
                yield f"data: {json.dumps({'type': 'token', 'text': item})}\n\n"
                await asyncio.sleep(0)

            yield f"data: {json.dumps({'type': 'done'})}\n\n"
            stream_end = time.perf_counter()
            logger.info(
                "chat[%s] stream_total: %.0f ms, tokens: %d, total_request: %.0f ms",
                sid_short,
                (stream_end - stream_start) * 1000,
                token_count,
                (stream_end - t0) * 1000,
            )
            # Close the Langfuse generation with the full answer + counts.
            # `usage.output` is approximate (we count delta events, not real
            # tokens) but is consistent across requests for relative cost
            # tracking. Real token counts would need Gemini's usage_metadata
            # which only ships on the LAST chunk of generate_content_stream.
            try:
                generation.end(
                    output=full_answer,
                    usage={"output": token_count},
                    metadata={
                        "first_token_ms": int((first_token_at - stream_start) * 1000)
                                          if first_token_at else None,
                        "stream_ms": int((stream_end - stream_start) * 1000),
                    },
                )
            except Exception:
                pass
        except Exception as exc:
            logger.error("chat[%s] stream_error: %s", sid_short, exc)
            yield f"data: {json.dumps({'type': 'error', 'message': str(exc)})}\n\n"
            try:
                generation.end(
                    output="",
                    metadata={"error": str(exc)[:500]},
                    level="ERROR",
                )
            except Exception:
                pass
            return

        # ─── After the stream, persist the full assistant message ─────────
        # The dep-injected `conn` was already returned to its pool when the
        # handler returned, so open a fresh one for the save step.
        try:
            save_start = time.perf_counter()
            async with conn_ctx() as save_conn:
                await save_conn.execute(
                    """
                    INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
                    VALUES (gen_random_uuid(), %s, 'assistant', %s, %s, now())
                    """,
                    [sid, full_answer, json.dumps(source_pages)],
                )
                await save_conn.commit()

                # Cache the answer so future asks for the same question on the
                # same report short-circuit retrieval and Gemini. Skipped for
                # follow-up turns (cache_eligible=False) and for empty answers.
                if cacheable and full_answer.strip():
                    await chat_cache.store(
                        save_conn, report_id, user_message, full_answer,
                        source_pages, question_embedding=question_vec,
                    )
            logger.info(
                "chat[%s] save_assistant_msg: %.0f ms",
                sid_short, (time.perf_counter() - save_start) * 1000,
            )
        except Exception as exc:
            logger.warning("chat[%s] save_assistant_msg failed: %s", sid_short, exc)

        # ─── Close the Langfuse trace ─────────────────────────────────────
        # `output` carries the final answer + the source pages we cited so
        # the dashboard shows them inline. Flush ensures the trace lands in
        # Langfuse even if Cloud Run recycles this instance shortly after.
        try:
            chat_trace.update(
                output={"answer": full_answer, "source_pages": source_pages},
            )
            obs.flush()
        except Exception:
            pass

        # ─── Enqueue async Ragas eval (sample-rate gated) ─────────────────
        # The worker reads input_data, runs ragas.evaluate, and posts one
        # Langfuse score per metric back onto this trace within ~30-60s.
        # We never block on it — eval is best-effort observability, not
        # part of the chat contract.
        try:
            if (settings.eval_enabled
                    and settings.eval_sample_rate > 0.0
                    and getattr(chat_trace, "id", "")
                    and full_answer.strip()):
                if random.random() < settings.eval_sample_rate:
                    async with conn_ctx() as eval_conn:
                        eval_job_id = uuid4()
                        await insert_job(
                            eval_conn,
                            job_id=eval_job_id,
                            job_type="Evaluation",
                            report_id=report_id,
                            input_data={
                                "trace_id":   chat_trace.id,
                                "question":   user_message,
                                "contexts":   context_chunks,
                                "answer":     full_answer,
                                "session_id": sid,
                            },
                        )
                        await eval_conn.commit()
                    logger.info(
                        "chat[%s] enqueued eval job=%s trace=%s",
                        sid_short, eval_job_id, chat_trace.id,
                    )
        except Exception as exc:
            logger.warning("chat[%s] enqueue_eval failed: %s", sid_short, exc)

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "X-Accel-Buffering": "no",  # disable proxy buffering for real streaming
        },
    )


@router.get("/reports/{report_id}/sessions", response_model=list[dict])
async def list_sessions(
    report_id: UUID,
    user_id: UUID,
    conn: AsyncConnection = Depends(get_conn),
):
    """List all chat sessions for a user+report combination."""
    cur = await conn.execute(
        """
        SELECT "Id", "Title", "CreatedAt"
        FROM chat_sessions
        WHERE "ReportId" = %s AND "UserId" = %s
        ORDER BY "CreatedAt" DESC
        """,
        [str(report_id), str(user_id)],
    )
    rows = await cur.fetchall()
    return [{"session_id": r[0], "title": r[1], "created_at": r[2].isoformat()} for r in rows]
