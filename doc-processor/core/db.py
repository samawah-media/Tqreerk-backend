"""Sync psycopg3 connection helper for /v1/ingest.

The ingest pipeline runs entirely in a thread pool (process_document,
vertex_embedder, and now persistence are all blocking), so we use sync
psycopg rather than the async variant. Callers use run_in_executor and
never touch this from the event loop directly.
"""
import psycopg
from pgvector.psycopg import register_vector

from core.config import settings


def get_conn() -> psycopg.Connection:
    """Open a new sync connection with pgvector registered.

    Callers are responsible for closing / using as a context manager:
        with get_conn() as conn:
            conn.execute(...)
            conn.commit()
    """
    if not settings.database_url:
        raise RuntimeError(
            "DATABASE_URL is not set — cannot connect to the database. "
            "Set the DATABASE_URL env var on the doc-processor Cloud Run service."
        )
    conn = psycopg.connect(settings.database_url)
    register_vector(conn)
    return conn
