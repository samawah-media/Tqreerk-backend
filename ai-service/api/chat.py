"""Chat endpoints — per-session Q&A driven by the LangGraph agent.

What changed (2026-04-30): the single-shot retrieval-then-stream pipeline
that lived here was replaced by `pipelines.agent.build_agent`. Every
chat turn now runs through a tool-using ReAct-style loop instead of one
fixed `embed → hybrid retrieve → rerank → Gemini` chain.

SSE protocol
============
The wire format is a superset of the legacy stream so existing clients
keep working — they just need to ignore unknown event types. The new
events report the agent's intermediate decisions so the UI can show
"searching reports…" / "reading page 5 of report X…" hints:

    data: {"type": "tool_call", "name": "search_chunks", "args": {...}}
    data: {"type": "tool_result", "name": "search_chunks", "ok": true}
    data: {"type": "sources", "pages": [3, 7]}     ← emitted once tools have run
    data: {"type": "token", "text": "Hello"}       ← repeated during final answer
    data: {"type": "done"}

`tool_call` and `tool_result` events are best-effort observability;
clients that don't care about them can drop them on the floor and still
get a usable stream of tokens.

Source pages
============
With agent-style retrieval the chunks the model used can come from
multiple `search_chunks` / `get_page` tool runs. We collect every
`page_number` referenced in tool results and emit them as a single
`sources` event right before the first token. Order is "first-seen,"
not relevance-ranked — relevance ranking belongs in the model's prose.
"""
import asyncio
import json
import logging
import random
import time
from uuid import UUID, uuid4

import sentry_sdk

logger = logging.getLogger(__name__)

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import StreamingResponse
from langchain_core.messages import AIMessage, HumanMessage, SystemMessage
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
from pipelines.agent import (
    build_agent,
    initial_state,
    make_langfuse_handler,
)
from pipelines.jobs import insert_job
from services import chat_cache, groundedness, observability as obs
from services.access import accessible_report_ids
from services.quota import assert_under_chat_quota
from services.tools import ToolContext

router = APIRouter(prefix="/chat", tags=["chat"])

# ── Session summary cache ────────────────────────────────────────────────────
# Key: (session_id, older_msg_count // 10 * 10) — regenerates every ~5 new
# turns so the summary stays fresh without a Gemini call on every single turn.
# Bounded at 500 entries to prevent unbounded memory growth in long-running pods.
_SUMMARY_CACHE_MAX = 500
_summary_cache: dict[tuple[str, int], str] = {}


async def _build_session_summary(
    older_rows: list[tuple[str, str]],
    session_id: str,
) -> str:
    """Summarise older conversation turns into 3-5 sentences with Gemini Flash.

    Runs as a one-shot non-streaming call — no tool loop, no state. Offloaded
    to a thread so the async handler isn't blocked by the sync SDK call.
    """
    from langchain_core.messages import HumanMessage as LCHumanMessage
    from langchain_google_vertexai import ChatVertexAI

    transcript = "\n".join(
        f"{'User' if role == 'user' else 'Assistant'}: {content[:400]}"
        for role, content in older_rows[:20]  # cap at 20 messages input
    )
    prompt = (
        "Summarize the following conversation in 3–5 concise sentences. "
        "Capture: which reports were discussed, the key questions asked, "
        "and any important facts or conclusions that were established. "
        "Reply in the same language as the conversation.\n\n"
        f"CONVERSATION:\n{transcript}"
    )

    def _call() -> str:
        model = ChatVertexAI(
            model=settings.gemini_summary_model,
            project=settings.gcp_project_id,
            location=settings.vertex_location,
            temperature=0.1,
            max_output_tokens=512,
        )
        resp = model.invoke([LCHumanMessage(content=prompt)])
        return getattr(resp, "content", "") or ""

    return await asyncio.to_thread(_call)


# ── Session CRUD ───────────────────────────────────────────────────────────

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
            source_pages=(json.loads(sp) if isinstance(sp, (str, bytes, bytearray)) else sp) if sp else None,
        )
        for r, c, sp in await msgs_cur.fetchall()
    ]

    return SessionHistoryResponse(
        session_id=session_id,
        report_id=session[1],
        title=session[2],
        messages=messages,
    )


# ── Helpers ─────────────────────────────────────────────────────────────────

# Tool result fields that look like page numbers we want to surface as
# sources. Tools return JSON-encoded strings (see services/tools.py); we
# parse and walk for `page` / `page_number` keys.
_PAGE_KEYS = ("page", "page_number")


def _extract_pages(tool_output: str) -> list[int]:
    """Best-effort: pull page numbers out of one tool's JSON result.

    The tool wrappers in services/tools.py serialize with `json.dumps`
    (or return a `_no_results(...)` shape), so we can decode and walk
    for keys named `page` / `page_number`. Anything that doesn't decode
    as JSON is silently ignored — sources are observability, not load-
    bearing for the answer.
    """
    try:
        parsed = json.loads(tool_output)
    except Exception:
        return []

    out: list[int] = []

    def walk(node) -> None:
        if isinstance(node, dict):
            for k, v in node.items():
                if k in _PAGE_KEYS and isinstance(v, int):
                    out.append(v)
                else:
                    walk(v)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(parsed)
    return out


def _dedup_in_order(values: list[int]) -> list[int]:
    seen: set[int] = set()
    out: list[int] = []
    for v in values:
        if v not in seen:
            seen.add(v)
            out.append(v)
    return out


def _summarise_tool_args(args: dict | None) -> dict:
    """Trim huge tool args (long queries, full embeddings) before pushing
    them onto the wire. Helps the client UI render `tool_call` events
    without exploding when a query is paragraph-length."""
    if not args:
        return {}
    out: dict = {}
    for k, v in args.items():
        if isinstance(v, str) and len(v) > 200:
            out[k] = v[:200] + "…"
        else:
            out[k] = v
    return out


# ── Streaming endpoint ───────────────────────────────────────────────────────

@router.post("/sessions/{session_id}/messages")
async def send_message(
    session_id: UUID,
    body: SendMessageRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Run one chat turn through the LangGraph agent and stream events as SSE.

    See module docstring for the wire format. This handler is the single
    entry point for production chat — there is no longer a non-agent
    fast-path."""
    t0 = time.perf_counter()
    sid = str(session_id)
    sid_short = sid[:8]

    def _mark(label: str, since: float) -> float:
        now = time.perf_counter()
        logger.info("chat[%s] %s: %.0f ms", sid_short, label, (now - since) * 1000)
        return now

    # ── Validate session — pull user + report in one round-trip ───────────
    # Sessions are owned by a UserId; ReportId is the optional "currently
    # viewing" hint. We no longer need the report's OrganizationId because
    # the chat quota is now per-user (see services/quota.py).
    row = await conn.execute(
        """
        SELECT "UserId", "ReportId"
        FROM chat_sessions
        WHERE "Id" = %s
        """,
        [sid],
    )
    session = await row.fetchone()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    user_id, report_id = session[0], session[1]
    t1 = _mark("validate_session", t0)

    # ── Quota gate (per-user daily chat cap) ───────────────────────────────
    await assert_under_chat_quota(conn, user_id)

    # ── Load conversation history + optional summary of older turns ──────────
    # Count total messages so we know whether a summary is needed.
    count_cur = await conn.execute(
        'SELECT COUNT(*) FROM chat_messages WHERE "SessionId" = %s',
        [sid],
    )
    total_msg_count = (await count_cur.fetchone())[0]

    # Always load the last 10 messages as the hot context window.
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
        [sid],
    )
    history_rows = await hist_cur.fetchall()

    # If the session is longer than 10 messages, summarise the older turns
    # and prepend as a SystemMessage so the agent retains earlier context.
    # Cache key rounds down to nearest 10 so we only rebuild every ~5 new turns.
    history_msgs: list = [
        HumanMessage(content=c) if r == "user" else AIMessage(content=c)
        for r, c in history_rows
    ]
    if total_msg_count > 10:
        older_count = total_msg_count - 10
        cache_key = (sid, (older_count // 10) * 10)
        cached_summary = _summary_cache.get(cache_key)
        if cached_summary is None:
            try:
                older_cur = await conn.execute(
                    """
                    SELECT "Role", "Content"
                    FROM chat_messages
                    WHERE "SessionId" = %s
                    ORDER BY "CreatedAt" ASC
                    LIMIT %s
                    """,
                    [sid, older_count],
                )
                older_rows = await older_cur.fetchall()
                if older_rows:
                    cached_summary = await _build_session_summary(list(older_rows), sid)
                    if cached_summary:
                        if len(_summary_cache) >= _SUMMARY_CACHE_MAX:
                            _summary_cache.pop(next(iter(_summary_cache)))
                        _summary_cache[cache_key] = cached_summary
            except Exception as exc:
                logger.warning("chat[%s] session_summary failed: %s", sid_short, exc)
        if cached_summary:
            history_msgs = [
                SystemMessage(content=f"[Summary of earlier conversation]\n{cached_summary}"),
                *history_msgs,
            ]

    t2 = _mark("load_history", t1)

    # ── Resolve the user's accessible report set (Published OR own org) ───
    accessible_ids = await accessible_report_ids(conn, user_id)
    _mark("accessible_report_ids", t2)

    # Two short-circuit paths, each producing a single user-visible token
    # and a `done` event so the client UI handles them like any normal
    # answer instead of crashing on a missing stream.
    early_msg: str | None = None
    if not accessible_ids:
        early_msg = (
            "You don't have access to any reports yet. Sign in or wait for "
            "your organization to publish reports, then try again."
        )
    elif report_id is not None and str(report_id) not in accessible_ids:
        # The session was created against a report this user can no longer
        # read — either the report was un-Published, the user was removed
        # from the org, or the session was created without an access check
        # in place. Fail fast: handing this to the agent would cause every
        # report-scoped tool to return "outside accessible scope" and the
        # model to flail.
        logger.warning(
            "chat[%s] session report=%s is not in user=%s accessible scope "
            "(accessible_count=%d) — aborting before agent",
            sid_short, report_id, user_id, len(accessible_ids),
        )
        early_msg = (
            "Your access to this report has changed and you can no longer "
            "view it in this chat. Please open a new chat from a report "
            "you currently have access to."
        )

    if early_msg is not None:
        early_token_evt = "data: " + json.dumps(
            {"type": "token", "text": early_msg}, ensure_ascii=False
        ) + "\n\n"
        early_done_evt = "data: " + json.dumps({"type": "done"}) + "\n\n"

        async def early_stream():
            yield early_token_evt
            yield early_done_evt
        return StreamingResponse(early_stream(), media_type="text/event-stream")

    # ── Persist user message before streaming starts ───────────────────────
    await conn.execute(
        """
        INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
        VALUES (gen_random_uuid(), %s, 'user', %s, NULL, now())
        """,
        [sid, body.message],
    )
    await conn.commit()
    t3 = _mark("save_user_msg", t2)
    logger.info("chat[%s] pre_stream_total: %.0f ms", sid_short, (t3 - t0) * 1000)

    # ── Chat-cache short-circuit (Layer 1: exact match) ───────────────────
    # Check for a cached answer for this exact question on this report. Hits
    # skip the entire agent loop — no rewriter, no retrieval, no LLM call —
    # and stream the cached answer back as SSE so the client UI handles it
    # identically to a fresh response. Layer 2 (semantic match) is intentionally
    # skipped here: it requires the question embedding, which we don't yet
    # compute outside the agent. Layer 1 alone catches exact verbatim repeats
    # — the bulk of repeated traffic.
    cache_hit: chat_cache.CacheHit | None = None
    if report_id is not None and settings.chat_cache_enabled:
        try:
            cache_hit = await chat_cache.lookup(conn, report_id, body.message)
        except Exception as exc:
            logger.warning("chat[%s] cache_lookup_failed: %s", sid_short, exc)

    if cache_hit is not None:
        logger.info(
            "chat[%s] cache_hit tier=%s — skipping agent",
            sid_short, cache_hit.tier,
        )
        # Persist the assistant message before streaming so the row exists
        # even if the client disconnects mid-stream.
        try:
            await conn.execute(
                """
                INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
                VALUES (gen_random_uuid(), %s, 'assistant', %s, %s, now())
                """,
                [sid, cache_hit.answer, json.dumps(cache_hit.source_pages)],
            )
            await conn.commit()
        except Exception as exc:
            logger.warning("chat[%s] cache_hit_save_msg_failed: %s", sid_short, exc)

        cached_answer = cache_hit.answer
        cached_pages = cache_hit.source_pages
        cached_tier = cache_hit.tier

        async def cached_stream():
            yield "data: " + json.dumps(
                {"type": "sources", "pages": cached_pages}
            ) + "\n\n"
            yield "data: " + json.dumps(
                {"type": "token", "text": cached_answer}, ensure_ascii=False,
            ) + "\n\n"
            yield "data: " + json.dumps(
                {"type": "done", "cache_tier": cached_tier}
            ) + "\n\n"

        return StreamingResponse(
            cached_stream(),
            media_type="text/event-stream",
            headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
        )

    # ── Build the agent ────────────────────────────────────────────────────
    ctx = ToolContext(
        user_id=user_id if isinstance(user_id, UUID) else UUID(str(user_id)),
        session_id=session_id,
        accessible_ids=accessible_ids,
    )
    graph = build_agent(ctx)

    # If the session is bound to a specific report, hint the agent: "the
    # user is currently viewing report X — prefer it unless they ask
    # something cross-report." We push this as a leading user message
    # instead of a system prompt mutation so the per-turn graph state
    # stays clean of construction-time dependencies.
    user_text = body.message
    if report_id is not None:
        user_text = (
            f"[context] The user is currently viewing report id={report_id}. "
            "Prefer it as the default scope unless the question is clearly "
            "about a different report.\n\n"
            f"{body.message}"
        )

    state0 = initial_state(
        user_id=user_id,
        session_id=session_id,
        history=history_msgs,
        user_message=HumanMessage(content=user_text),
    )

    # Langfuse: one trace per chat turn, all model + tool spans nested.
    # We mint the trace_id ourselves rather than letting the CallbackHandler
    # invent one, because the asynchronous Ragas eval job (enqueued at the
    # bottom of this handler, run later by the worker) needs the SAME id to
    # post `score` events against. Without a shared id, eval runs but the
    # scores get dropped on the floor at obs.score(trace_id="") — see
    # services/observability.py.
    trace_id = str(uuid4())
    lf_handler = make_langfuse_handler(
        user_id=user_id,
        session_id=session_id,
        trace_id=trace_id,
        trace_name="chat_agent",
    )
    callbacks = [lf_handler] if lf_handler is not None else []

    # ── Stream agent events to SSE ─────────────────────────────────────────
    async def event_stream():
        full_answer_parts: list[str] = []
        all_pages: list[int] = []
        sources_emitted = False
        token_count = 0
        first_token_at: float | None = None
        stream_start = time.perf_counter()
        # Aggregated tool outputs — fed to the Ragas eval job below as the
        # "contexts" the answer was grounded on. Bounded to avoid OOM on a
        # chatty agent that ran a half-dozen search_chunks.
        tool_contexts: list[str] = []

        try:
            async for ev in graph.astream_events(
                state0,
                config={"callbacks": callbacks} if callbacks else None,
                version="v2",
            ):
                etype = ev.get("event")
                name = ev.get("name", "")

                # ── Tool start: surface it to the client ───────────────
                if etype == "on_tool_start":
                    args = ev.get("data", {}).get("input", {})
                    if isinstance(args, dict):
                        # LangChain wraps the args under a single key for
                        # StructuredTool; unwrap if so.
                        if set(args.keys()) == {"input"} and isinstance(args["input"], dict):
                            args = args["input"]
                    yield (
                        "data: " + json.dumps({
                            "type": "tool_call",
                            "name": name,
                            "args": _summarise_tool_args(args if isinstance(args, dict) else {}),
                        }, ensure_ascii=False) + "\n\n"
                    )
                    await asyncio.sleep(0)

                # ── Tool end: collect pages, emit ack ──────────────────
                elif etype == "on_tool_end":
                    output = ev.get("data", {}).get("output")
                    output_text = ""
                    if hasattr(output, "content"):
                        # ToolMessage
                        output_text = str(output.content)
                    elif isinstance(output, str):
                        output_text = output
                    elif output is not None:
                        output_text = str(output)

                    pages = _extract_pages(output_text)
                    if pages:
                        all_pages.extend(pages)
                    if output_text:
                        tool_contexts.append(output_text[:4000])

                    yield (
                        "data: " + json.dumps({
                            "type": "tool_result",
                            "name": name,
                            "ok": "error" not in output_text.lower()[:50] if output_text else True,
                            "n_pages": len(pages),
                        }, ensure_ascii=False) + "\n\n"
                    )
                    await asyncio.sleep(0)

                # ── Token chunk from the model ─────────────────────────
                elif etype == "on_chat_model_stream":
                    chunk = ev.get("data", {}).get("chunk")
                    text = ""
                    if chunk is not None:
                        # AIMessageChunk.content is usually a string; with
                        # tool-calling Vertex sometimes returns a list of
                        # parts — flatten to a string.
                        c = getattr(chunk, "content", "")
                        if isinstance(c, list):
                            text = "".join(
                                p.get("text", "") if isinstance(p, dict) else str(p)
                                for p in c
                            )
                        else:
                            text = str(c or "")
                    if not text:
                        continue

                    # Emit `sources` once, right before the first user-visible
                    # token. Pages collected so far are the ones grounding
                    # this answer.
                    if not sources_emitted and all_pages:
                        deduped = _dedup_in_order(all_pages)
                        yield (
                            "data: " + json.dumps({"type": "sources", "pages": deduped})
                            + "\n\n"
                        )
                        sources_emitted = True

                    if first_token_at is None:
                        first_token_at = time.perf_counter()
                        logger.info(
                            "chat[%s] agent_first_token: %.0f ms",
                            sid_short, (first_token_at - stream_start) * 1000,
                        )
                    token_count += 1
                    full_answer_parts.append(text)
                    yield (
                        "data: " + json.dumps({"type": "token", "text": text}, ensure_ascii=False)
                        + "\n\n"
                    )
                    await asyncio.sleep(0)

            # ── Stream done — make sure we emitted sources at least once ──
            if not sources_emitted:
                deduped = _dedup_in_order(all_pages)
                yield "data: " + json.dumps({"type": "sources", "pages": deduped}) + "\n\n"

            # ── Inline groundedness check ─────────────────────────────────
            # One Flash call after the answer streams. The user has already
            # seen the full response, so this only delays the `done` event by
            # ~600ms. On low-faithfulness, emit a `warning` event the UI can
            # display next to the answer and log to Sentry for dashboarding.
            full_answer_so_far = "".join(full_answer_parts).strip()
            if full_answer_so_far and tool_contexts:
                grounding = await groundedness.check(
                    answer=full_answer_so_far,
                    contexts=tool_contexts,
                )
                if grounding is not None and grounding.is_warning:
                    logger.warning(
                        "chat[%s] low_groundedness score=%.2f unsupported=%s",
                        sid_short, grounding.score, grounding.unsupported,
                    )
                    try:
                        sentry_sdk.capture_message(
                            "chat: low_groundedness",
                            level="warning",
                            extras={
                                "session_id":  sid,
                                "score":       grounding.score,
                                "unsupported": grounding.unsupported,
                                "question":    body.message[:500],
                            },
                        )
                    except Exception:
                        pass
                    yield "data: " + json.dumps({
                        "type":        "warning",
                        "kind":        "low_groundedness",
                        "score":       round(grounding.score, 2),
                        "unsupported": grounding.unsupported,
                        "message":     (
                            "بعض الادعاءات في هذه الإجابة قد لا تكون مدعومة "
                            "بشكل كامل بمحتوى التقارير المسترجَعة. يُنصح بالتحقق "
                            "من المصادر."
                        ),
                    }, ensure_ascii=False) + "\n\n"

            yield "data: " + json.dumps({"type": "done"}) + "\n\n"

            stream_end = time.perf_counter()
            logger.info(
                "chat[%s] stream_total: %.0f ms, tokens: %d, total_request: %.0f ms",
                sid_short,
                (stream_end - stream_start) * 1000,
                token_count,
                (stream_end - t0) * 1000,
            )
        except Exception as exc:
            logger.exception("chat[%s] agent_stream_error: %s", sid_short, exc)
            yield "data: " + json.dumps({"type": "error", "message": str(exc)}) + "\n\n"
            return

        # ── Persist the assistant message ──────────────────────────────────
        full_answer = "".join(full_answer_parts).strip()
        source_pages = _dedup_in_order(all_pages)
        try:
            async with conn_ctx() as save_conn:
                await save_conn.execute(
                    """
                    INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
                    VALUES (gen_random_uuid(), %s, 'assistant', %s, %s, now())
                    """,
                    [sid, full_answer, json.dumps(source_pages)],
                )
                await save_conn.commit()
        except Exception as exc:
            logger.warning("chat[%s] save_assistant_msg failed: %s", sid_short, exc)

        # ── Cache the answer (best-effort) ────────────────────────────────
        # Runs after the user has already received the full streamed response,
        # so a slow store call adds zero perceived latency. We pass
        # question_embedding=None — see chat_cache.lookup above for the
        # rationale on why Layer 2 is currently disabled.
        if (report_id is not None
                and full_answer
                and settings.chat_cache_enabled):
            try:
                async with conn_ctx() as cache_conn:
                    await chat_cache.store(
                        cache_conn,
                        report_id=report_id,
                        question=body.message,
                        answer=full_answer,
                        source_pages=source_pages,
                    )
            except Exception as exc:
                logger.warning("chat[%s] cache_store_failed: %s", sid_short, exc)

        # ── Flush Langfuse + enqueue eval ─────────────────────────────────
        try:
            obs.flush()
        except Exception:
            pass

        try:
            if (settings.eval_enabled
                    and settings.eval_sample_rate > 0.0
                    and full_answer
                    and tool_contexts
                    and random.random() < settings.eval_sample_rate):
                async with conn_ctx() as eval_conn:
                    eval_job_id = uuid4()
                    await insert_job(
                        eval_conn,
                        job_id=eval_job_id,
                        job_type="Evaluation",
                        report_id=report_id,
                        input_data={
                            # Same trace id we pinned on the CallbackHandler
                            # above. The worker passes it to obs.score() so
                            # Ragas metrics land directly on the chat trace
                            # the user just produced.
                            "trace_id":   trace_id,
                            "question":   body.message,
                            "contexts":   tool_contexts,
                            "answer":     full_answer,
                            "session_id": sid,
                        },
                    )
                    await eval_conn.commit()
                logger.info(
                    "chat[%s] enqueued eval job=%s trace=%s",
                    sid_short, eval_job_id, trace_id,
                )
        except Exception as exc:
            logger.warning("chat[%s] enqueue_eval failed: %s", sid_short, exc)

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
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
