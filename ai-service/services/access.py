"""Per-user report access scope.

Every agent tool that touches reports filters by `accessible_report_ids` to
enforce auth at the tool boundary, not in the LLM. The agent never sees
user_id; the wrapper passes it through and tools call this helper.

Access rule (locked-in 2026-04-30, "Published only"):

    A user U can read report R if EITHER
      • R.Status = 'Published'                          (public catalogue)
      • U is an active member of R.OrganizationId       (their own org's
                                                          reports, any status)

Saved reports / featured reports / subscription tiers do NOT grant
additional access — they're filters / hints applied on top of the set
above.

Soft-deleted reports (DeletedAt IS NOT NULL) are always excluded.

Why a single helper
===================
Every tool calls this once at the start of its query. Cached on the
LangGraph state for the duration of one chat turn so we don't re-run the
SQL per tool call. Cache invalidation = end of turn.
"""
from __future__ import annotations

import logging
from uuid import UUID

from psycopg import AsyncConnection

logger = logging.getLogger(__name__)


async def accessible_report_ids(
    conn: AsyncConnection,
    user_id: UUID | str,
) -> list[str]:
    """Return the set of report ids `user_id` is allowed to read, as
    string-form UUIDs (psycopg returns UUID instances; we stringify for
    direct ANY(...) parameter binding).

    Empty list if the user has no accessible reports — every tool short-
    circuits to an empty result in that case rather than crashing.
    """
    try:
        cur = await conn.execute(
            """
            SELECT DISTINCT r."Id"
            FROM reports r
            LEFT JOIN organization_members om
                   ON om."OrganizationId" = r."OrganizationId"
                  AND om."UserId" = %s
                  AND om."IsActive" = true
            WHERE r."DeletedAt" IS NULL
              AND (
                r."Status" = 'Published'
                OR om."Id" IS NOT NULL
              )
            """,
            [str(user_id)],
        )
        rows = await cur.fetchall()
        return [str(r[0]) for r in rows]
    except Exception as exc:
        # Fail-closed: on a DB error we'd rather the agent see an empty
        # access set (tools return "no results") than accidentally widen
        # access. Logged so the failure is investigable.
        logger.exception(
            "[access] accessible_report_ids failed for user=%s: %s",
            user_id, exc,
        )
        return []
