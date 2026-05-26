"""Daily quotas for AI operations.

Why this exists
===============
The system has several paid surfaces that one enthusiastic user (or one
buggy retry loop) can run up real bills on:

  • Ingest         → Gemini Vision per page + GPU time on doc-processor
                     (per-org — orgs upload their own reports)
  • Translation    → Google Translation API per character
                     (per-org — translation is owner-of-report initiated)
  • Chat           → Gemini Flash + Vertex Ranking + tool-loop fan-out
                     (per-user — chat sessions are owned by an individual,
                      not an org; the agent's accessible scope is
                      Published-OR-own-org-membership, so capping chat per
                      org would punish quiet orgs whose one chatty user
                      ate everyone's allowance)

A daily count check is the cheapest form of cost insurance: one indexed
SQL count against `ai_jobs` (or `chat_messages` joined to `chat_sessions`
for the chat cap) before each new operation. Costs ~1 ms, blocks the
obvious abuse vectors, and stays out of the way of real usage at the
configured caps.

Failure mode
============
Any exception in the quota check itself (DB hiccup, missing id, etc.)
is logged and TREATED AS UNDER-QUOTA. We refuse to accidentally lock real
users out because of an observability bug. Real abuse will still be caught
on the next attempt; one slipped request is acceptable.
"""
from __future__ import annotations

import logging
from uuid import UUID

from fastapi import HTTPException
from psycopg import AsyncConnection

from core.config import settings

logger = logging.getLogger(__name__)


# Human-readable labels for the 429 response so the client knows which
# quota was hit (and the user knows what action they over-did).
_LABELS = {
    "Ingestion":   "ingest",
    "Translation": "translate",
    "Chat":        "chat message",
}


def _limit_for(kind: str) -> int:
    """Resolve the configured daily cap for a quota kind. Returns 0 when
    the cap is disabled (treated as unlimited)."""
    if kind == "Ingestion":
        return settings.quota_daily_ingest_per_org
    if kind == "Translation":
        return settings.quota_daily_translate_per_org
    if kind == "Chat":
        return settings.quota_daily_chat_per_user
    # Unknown kinds (e.g. Evaluation, internal) are uncapped — chat already
    # rate-limits eval indirectly because each chat enqueues one eval.
    return 0


async def assert_under_job_quota(
    conn: AsyncConnection,
    organization_id: UUID | str | None,
    kind: str,
) -> None:
    """Raise HTTPException(429) if `organization_id` has already enqueued
    >= cap jobs of `kind` in the last 24 hours.

    `kind` is the ai_jobs.JobType value: "Ingestion" | "Translation".
    Pass `organization_id=None` to skip the check (uncapped) — used by
    internal jobs like Evaluation that aren't user-initiated."""
    if not settings.quota_enabled or organization_id is None:
        return

    cap = _limit_for(kind)
    if cap <= 0:
        return  # cap disabled

    try:
        cur = await conn.execute(
            """
            SELECT COUNT(*) FROM ai_jobs
            WHERE "OrganizationId" = %s
              AND "JobType"        = %s
              AND "CreatedAt"      > now() - interval '24 hours'
            """,
            [str(organization_id), kind],
        )
        row = await cur.fetchone()
        count = int(row[0]) if row else 0
    except Exception as exc:
        # Fail-open: a DB hiccup must never lock a real user out.
        logger.warning(
            "[quota] count failed for org=%s kind=%s: %s — allowing through",
            organization_id, kind, exc,
        )
        return

    if count >= cap:
        label = _LABELS.get(kind, kind.lower())
        logger.warning(
            "[quota] org=%s hit daily %s cap (%d/%d) — returning 429",
            organization_id, label, count, cap,
        )
        raise HTTPException(
            status_code=429,
            detail=(
                f"Daily {label} quota reached ({cap}/24h). "
                f"Try again after midnight UTC."
            ),
            headers={"Retry-After": "3600"},  # generic hint; client can ignore
        )


async def assert_under_chat_quota(
    conn: AsyncConnection,
    user_id: UUID | str | None,
) -> None:
    """Per-user cap on chat_messages — counts user-role inserts in the
    last 24 hours across all sessions owned by `user_id`.

    Why per-user: chat_sessions.UserId is the natural owner of chat traffic
    (the agent's accessible scope is per-user, the costs are driven by one
    person). The query joins chat_messages → chat_sessions on UserId,
    which is the indexed path."""
    if not settings.quota_enabled or user_id is None:
        return

    cap = settings.quota_daily_chat_per_user
    if cap <= 0:
        return

    try:
        cur = await conn.execute(
            """
            SELECT COUNT(*)
            FROM chat_messages m
            JOIN chat_sessions s ON s."Id" = m."SessionId"
            WHERE s."UserId"   = %s
              AND m."Role"     = 'user'
              AND m."CreatedAt" > now() - interval '24 hours'
            """,
            [str(user_id)],
        )
        row = await cur.fetchone()
        count = int(row[0]) if row else 0
    except Exception as exc:
        logger.warning(
            "[quota] chat count failed for user=%s: %s — allowing through",
            user_id, exc,
        )
        return

    if count >= cap:
        logger.warning(
            "[quota] user=%s hit daily chat cap (%d/%d) — returning 429",
            user_id, count, cap,
        )
        raise HTTPException(
            status_code=429,
            detail=(
                f"Daily chat quota reached ({cap}/24h). "
                f"Try again after midnight UTC."
            ),
            headers={"Retry-After": "3600"},
        )
