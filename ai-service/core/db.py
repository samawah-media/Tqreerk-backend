from contextlib import asynccontextmanager
from typing import AsyncGenerator

import psycopg
from pgvector.psycopg import register_vector_async
from psycopg_pool import AsyncConnectionPool

from core.config import settings

# One pool per process — shared across all requests and background tasks.
# max_size=2 keeps the per-instance footprint small: with 6 AI service +
# 6 worker instances that's 24 connections max vs. one-per-call which could
# spike to hundreds under load. Connections are reused across calls; pgvector
# is registered once when each physical connection is first created.
_pool: AsyncConnectionPool | None = None


async def _configure_conn(conn: psycopg.AsyncConnection) -> None:
    await register_vector_async(conn)


async def get_pool() -> AsyncConnectionPool:
    global _pool
    if _pool is None:
        _pool = AsyncConnectionPool(
            settings.database_url,
            min_size=1,
            max_size=2,
            kwargs={"autocommit": False},
            configure=_configure_conn,
            open=False,
        )
        await _pool.open()
    return _pool


@asynccontextmanager
async def conn_ctx() -> AsyncGenerator[psycopg.AsyncConnection, None]:
    pool = await get_pool()
    async with pool.connection() as conn:
        yield conn


async def get_conn() -> AsyncGenerator[psycopg.AsyncConnection, None]:
    """FastAPI dependency — use with Depends(get_conn) in route signatures."""
    async with conn_ctx() as conn:
        yield conn
