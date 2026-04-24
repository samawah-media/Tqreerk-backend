"""Chat endpoints — per-session Q&A with RAG."""
import json
from uuid import UUID, uuid4

import numpy as np
from fastapi import APIRouter, Depends, HTTPException
from psycopg import AsyncConnection

from core.db import get_conn
from models.chat import (
    CreateSessionRequest,
    CreateSessionResponse,
    SendMessageRequest,
    SendMessageResponse,
    SessionHistoryResponse,
    SessionMessage,
)
from services.gemini import chat_with_context, embed_text

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


@router.post("/sessions/{session_id}/messages", response_model=SendMessageResponse)
async def send_message(
    session_id: UUID,
    body: SendMessageRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    # Verify session exists and get report_id
    row = await conn.execute(
        'SELECT "ReportId" FROM chat_sessions WHERE "Id" = %s',
        [str(session_id)],
    )
    session = await row.fetchone()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    report_id = session[0]

    # Load conversation history
    # Send only the last 10 messages (5 turns) to Gemini to control token usage.
    # Full history is always available in the DB for display.
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

    # RAG: embed question and retrieve top-K similar pages
    question_vec = np.array(embed_text(body.message), dtype=np.float32)
    pages_cur = await conn.execute(
        """
        SELECT "PageNumber", "Content"
        FROM report_pages
        WHERE "ReportId" = %s
        ORDER BY embedding <=> %s
        LIMIT %s
        """,
        [str(report_id), question_vec, TOP_K],
    )
    page_rows = await pages_cur.fetchall()
    context_chunks = [c for _, c in page_rows]
    source_pages = [p for p, _ in page_rows]

    # Generate answer
    answer, used_pages = chat_with_context(history, body.message, context_chunks, source_pages)

    # Persist user message
    await conn.execute(
        """
        INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
        VALUES (gen_random_uuid(), %s, 'user', %s, NULL, now())
        """,
        [str(session_id), body.message],
    )

    # Persist assistant message
    await conn.execute(
        """
        INSERT INTO chat_messages ("Id", "SessionId", "Role", "Content", "SourcePages", "CreatedAt")
        VALUES (gen_random_uuid(), %s, 'assistant', %s, %s, now())
        """,
        [str(session_id), answer, json.dumps(used_pages)],
    )

    await conn.commit()
    return SendMessageResponse(session_id=session_id, answer=answer, source_pages=used_pages)


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
