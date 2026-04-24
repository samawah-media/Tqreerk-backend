from contextlib import asynccontextmanager
from typing import AsyncGenerator

import psycopg
from pgvector.psycopg import register_vector_async

from core.config import settings


async def get_conn() -> AsyncGenerator[psycopg.AsyncConnection, None]:
    async with await psycopg.AsyncConnection.connect(settings.database_url) as conn:
        await register_vector_async(conn)
        yield conn
