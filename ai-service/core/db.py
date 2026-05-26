from contextlib import asynccontextmanager
from typing import AsyncGenerator

import psycopg
from pgvector.psycopg import register_vector_async

from core.config import settings


@asynccontextmanager
async def conn_ctx() -> AsyncGenerator[psycopg.AsyncConnection, None]:
    """Async context manager — use this from pipeline / background code:
        async with conn_ctx() as conn: ...
    """
    async with await psycopg.AsyncConnection.connect(settings.database_url) as conn:
        await register_vector_async(conn)
        yield conn


async def get_conn() -> AsyncGenerator[psycopg.AsyncConnection, None]:
    """FastAPI dependency — use with Depends(get_conn) in route signatures."""
    async with conn_ctx() as conn:
        yield conn
