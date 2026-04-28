"""Chat endpoints — per-session Q&A with RAG (streaming)."""
import asyncio
import json
from uuid import UUID, uuid4

import numpy as np
from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import StreamingResponse
from psycopg import AsyncConnection

from core.db import conn_ctx, get_conn
from models.chat import (
    CreateSessionRequest,
    CreateSessionResponse,
    SendMessageRequest,
    SessionHistoryResponse,
    SessionMessage,
)
from services.gemini import chat_with_context_stream, embed_text

router = APIRouter(prefix="/chat", tags=["chat"])

TOP_K = 5  # number of page chunks to retrieve per question


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


@router.post("/sessions/{session_id}/messages")
async def send_message(
    session_id: UUID,
    body: SendMessageRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Stream the assistant's answer back as Server-Sent Events.

    Event format (one event per line, blank-line separated):
      data: {"type": "sources", "pages": [3, 7, 12]}      ← sent first
      data: {"type": "token", "text": "Hello"}            ← repeated
      data: {"type": "token", "text": " there"}
      data: {"type": "done"}                              ← sent last

    Frontend should consume via EventSource or fetch + ReadableStream.
    """
    # ─── Validate session ───────────────────────────────────────────────────
    row = await conn.execute(
        'SELECT "ReportId" FROM chat_sessions WHERE "Id" = %s',
        [str(session_id)],
    )
    session = await row.fetchone()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    report_id = session[0]

    # ─── Load conversation history (last 10 messages → 5 turns) ────────────
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

    # ─── Hybrid RAG retrieval (dense vector + sparse tsvector) ─────────────
    question_vec = np.array(embed_text(body.message), dtype=np.float32)
    pages_cur = await conn.execute(
        """
        WITH dense AS (
            SELECT "PageNumber", "Content",
                   1 - (embedding <=> %s) AS score
            FROM report_pages
            WHERE "ReportId" = %s AND embedding IS NOT NULL
            ORDER BY embedding <=> %s
            LIMIT 20
        ),
        sparse AS (
            SELECT "PageNumber", "Content",
                   ts_rank(search_vector,
                           plainto_tsquery('arabic', %s) ||
                           plainto_tsquery('english', %s)) AS score
            FROM report_pages
            WHERE "ReportId" = %s
              AND search_vector @@ (plainto_tsquery('arabic', %s) ||
                                    plainto_tsquery('english', %s))
            ORDER BY score DESC
            LIMIT 20
        )
        SELECT "PageNumber", "Content",
               COALESCE(d.score, 0) * 0.7 + COALESCE(s.score, 0) * 0.3 AS final_score
        FROM dense d
        FULL OUTER JOIN sparse s USING ("PageNumber", "Content")
        ORDER BY final_score DESC
        LIMIT %s
        """,
        [
            question_vec, str(report_id), question_vec,
            body.message, body.message, str(report_id),
            body.message, body.message,
            TOP_K,
        ],
    )
    page_rows = await pages_cur.fetchall()
    page_rows = [(p, c) for p, c, _ in page_rows]
    context_chunks = [c for _, c in page_rows]
    source_pages = [p for p, _ in page_rows]

    # ─── Persist the user message before streaming starts ───────────────────
    await conn.execute(
        """
        INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
        VALUES (gen_random_uuid(), %s, 'user', %s, NULL, now())
        """,
        [str(session_id), body.message],
    )
    await conn.commit()

    # ─── Streaming generator ────────────────────────────────────────────────
    user_message = body.message
    sid = str(session_id)

    async def event_stream():
        full_answer = ""
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
                full_answer += item
                yield f"data: {json.dumps({'type': 'token', 'text': item})}\n\n"
                await asyncio.sleep(0)

            yield f"data: {json.dumps({'type': 'done'})}\n\n"
        except Exception as exc:
            yield f"data: {json.dumps({'type': 'error', 'message': str(exc)})}\n\n"
            return

        # ─── After the stream, persist the full assistant message ─────────
        # The dep-injected `conn` was already returned to its pool when the
        # handler returned, so open a fresh one for the save step.
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
        except Exception:
            # Don't fail the response if persistence fails; the stream already
            # delivered the answer to the user.
            pass

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
