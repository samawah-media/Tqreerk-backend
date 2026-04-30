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

logger = logging.getLogger(__name__)

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import StreamingResponse
from langchain_core.messages import AIMessage, HumanMessage
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
from services import observability as obs
from services.access import accessible_report_ids
from services.quota import assert_under_chat_quota
from services.tools import ToolContext

router = APIRouter(prefix="/chat", tags=["chat"])


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

    # ── Load conversation history (last 10 → 5 turns) ──────────────────────
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
    history_msgs = [
        HumanMessage(content=c) if r == "user" else AIMessage(content=c)
        for r, c in history_rows
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
    lf_handler = make_langfuse_handler(
        user_id=user_id,
        session_id=session_id,
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
                            # No Langfuse trace_id available from the LangChain
                            # callback handler at this layer; the worker will
                            # post scores against session_id + question instead
                            # and Langfuse joins them via session.
                            "trace_id":   "",
                            "question":   body.message,
                            "contexts":   tool_contexts,
                            "answer":     full_answer,
                            "session_id": sid,
                        },
                    )
                    await eval_conn.commit()
                logger.info("chat[%s] enqueued eval job=%s", sid_short, eval_job_id)
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
