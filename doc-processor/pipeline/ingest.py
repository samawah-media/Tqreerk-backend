"""Full GPU-side ingest pipeline for /v1/ingest.

Orchestrates five stages entirely within the doc-processor container:

  1. Download  — GCS (ADC) or HTTPS.
  2. Extract   — Docling layout + tables + figures + OCR (same orchestrator
                 as /v1/extract_document).
  3. Chunk     — heading-aware block packer or recursive text splitter
                 (same chunker as /v1/ingest_full).
  4. Embed     — Vertex gemini-embedding-001 via vertex_embedder (same as
                 /v1/ingest_full).
  5. Persist   — DELETE + INSERT into report_chunks via sync psycopg.

The ai-service worker only calls POST /v1/ingest and marks the job
Complete with the returned stats — no chunks-with-vectors payload ever
crosses the network.

All stages run synchronously (no asyncio) so callers use
loop.run_in_executor and the FastAPI event loop stays free.
"""
from __future__ import annotations

import json
import logging
import time

import httpx
import numpy as np
from google.cloud import storage

from core.config import settings
from core.db import get_conn
from models.schema import ExtractOptions, IngestOptions
from pipeline import chunker, vertex_embedder
from pipeline.orchestrator import process_document



logger = logging.getLogger(__name__)

# Retry policy applied to every I/O / GPU stage.
# 3 retries → 4 total attempts; delays between consecutive attempts (seconds).
_RETRY_DELAYS: tuple[int, ...] = (2, 4, 6)


def _retry(fn, stage: str, report_id: str):
    """Run fn() up to 4 times (1 + 3 retries) with 2 / 4 / 6 s back-off.

    Logs a WARNING on each failed attempt and re-raises the last exception
    once all attempts are exhausted so the caller can mark the job Failed.
    """
    last_exc: Exception = RuntimeError("unreachable")
    total = len(_RETRY_DELAYS) + 1  # 4
    for attempt in range(1, total + 1):
        try:
            return fn()
        except Exception as exc:
            last_exc = exc
            if attempt == total:
                logger.error(
                    "[ingest %s] stage=%s failed after %d attempts: %s",
                    report_id, stage, total, exc,
                )
                raise
            delay = _RETRY_DELAYS[attempt - 1]
            logger.warning(
                "[ingest %s] stage=%s attempt %d/%d failed: %s — retrying in %ds",
                report_id, stage, attempt, total, exc, delay,
            )
            time.sleep(delay)
    raise last_exc  # unreachable but satisfies type-checkers


# ── Job lifecycle helpers (used by /v1/ingest_job background task) ───────────

def claim_job(job_id: str) -> bool:
    """Atomically move a Pending job to Processing.

    Returns True if this call claimed the job, False if it was already
    claimed by another process (GPU trigger fired twice, or worker raced).
    Uses a plain UPDATE … WHERE Status='Pending' — no SKIP LOCKED needed
    because the worker's SQL already excludes step=ingest jobs from its
    claim query.
    """
    with get_conn() as conn:
        cur = conn.execute(
            """
            UPDATE ai_jobs
               SET "Status" = 'Processing', "StartedAt" = now()
             WHERE "Id" = %s AND "Status" = 'Pending'
            """,
            [job_id],
        )
        conn.commit()
        return (cur.rowcount or 0) > 0


def mark_job_completed(job_id: str, result: dict) -> None:
    with get_conn() as conn:
        conn.execute(
            """
            UPDATE ai_jobs
               SET "Status" = 'Completed',
                   "OutputData" = %s::jsonb,
                   "CompletedAt" = now()
             WHERE "Id" = %s
            """,
            [json.dumps(result), job_id],
        )
        conn.commit()


def mark_job_failed(job_id: str, error: str) -> None:
    with get_conn() as conn:
        conn.execute(
            """
            UPDATE ai_jobs
               SET "Status" = 'Failed',
                   "ErrorMessage" = %s,
                   "CompletedAt" = now()
             WHERE "Id" = %s
            """,
            [error[:1000], job_id],
        )
        conn.commit()


_gcs_client = None


def _gcs() -> storage.Client:
    global _gcs_client
    if _gcs_client is None:
        _gcs_client = storage.Client()
    return _gcs_client


def _download(file_url: str) -> bytes:
    """Download PDF bytes from a gs:// or https:// URL."""
    if file_url.startswith("gs://"):
        _, _, rest = file_url[5:].partition("/")
        bucket_name = file_url[5:].split("/")[0]
        blob_path   = "/".join(file_url[5:].split("/")[1:])
        logger.info("[ingest] GCS download gs://%s/%s", bucket_name, blob_path)
        return _gcs().bucket(bucket_name).blob(blob_path).download_as_bytes()

    logger.info("[ingest] HTTPS download %s", file_url)
    resp = httpx.get(file_url, timeout=120, follow_redirects=True)
    resp.raise_for_status()
    return resp.content


def run_ingest(
    report_id: str,
    file_url: str,
    options: IngestOptions,
    job_id: str | None = None,
) -> dict:
    """Run the full ingest pipeline and return stats.

    Called from the /v1/ingest_job background task via loop.run_in_executor
    so it always runs on a thread pool worker, never on the event loop.

    Any failure in any stage raises immediately so the caller can mark the
    job Failed without retrying.

    Returns a dict that mark_job_completed persists verbatim as OutputData.
    """
    overall_started = time.perf_counter()

    # ── Stage 1: Download ─────────────────────────────────────────────────────
    pdf_bytes: bytes = _retry(
        lambda: _download(file_url),
        "download", report_id,
    )
    logger.info(
        "[ingest %s] downloaded %.2f MB from %s",
        report_id, len(pdf_bytes) / 1024 / 1024, file_url,
    )

    # ── Stage 2: Extract ──────────────────────────────────────────────────────
    extract_options = ExtractOptions(
        extract_tables=options.extract_tables,
        extract_figures=options.extract_figures,
        extract_formulas=options.extract_formulas,
        ocr_fallback=options.ocr_fallback,
        figure_captioning=options.figure_captioning,
    )
    extract_response = _retry(
        lambda: process_document(pdf_bytes, extract_options, None),
        "extract", report_id,
    )
    pages_total   = extract_response.document_metadata.page_count
    extractor_tag = extract_response.extractor
    languages     = extract_response.document_metadata.detected_languages
    language      = languages[0] if languages else "mixed"
    logger.info(
        "[ingest %s] extracted %d pages (extractor=%s)",
        report_id, pages_total, extractor_tag,
    )

    # ── Stage 3: Chunk ────────────────────────────────────────────────────────
    # Pure in-memory — deterministic, no I/O; retry not applicable.
    pending: list[dict] = []
    for page in extract_response.pages:
        page_num = page.page_number
        if not page_num:
            continue
        block_dicts = [
            {
                "type":          b.type,
                "content":       b.content,
                "reading_order": b.reading_order,
            }
            for b in page.blocks
        ]
        if block_dicts:
            for idx, ch in enumerate(chunker.chunk_blocks_with_meta(block_dicts)):
                pending.append({
                    "page_number":   page_num,
                    "chunk_index":   idx,
                    "content":       ch["content"],
                    "section_title": ch["section_title"],
                    "block_types":   ch["block_types"],
                })
        elif (page.content or "").strip():
            for idx, piece in enumerate(chunker.chunk_text(page.content)):
                pending.append({
                    "page_number":   page_num,
                    "chunk_index":   idx,
                    "content":       piece,
                    "section_title": "",
                    "block_types":   ["text"],
                })
    logger.info("[ingest %s] chunked → %d chunks", report_id, len(pending))

    # ── Stage 4: Embed ───────────────────────────────────────────────────────
    vectors: list[list[float]] = []
    if pending:
        vectors = _retry(
            lambda: vertex_embedder.embed_passages([p["content"] for p in pending]),
            "embed", report_id,
        )
        if len(vectors) != len(pending):
            raise RuntimeError(
                f"Vertex returned {len(vectors)} vectors for {len(pending)} chunks"
            )
    logger.info(
        "[ingest %s] embedded %d chunks (model=%s)",
        report_id, len(vectors), settings.embed_vertex_model,
    )

    # ── Stage 5: Persist ─────────────────────────────────────────────────────
    def _persist() -> int:
        with get_conn() as conn:
            cur = conn.execute(
                'DELETE FROM report_chunks WHERE "ReportId" = %s',
                [report_id],
            )
            deleted = cur.rowcount or 0
            for i, p in enumerate(pending):
                vec = np.array(vectors[i], dtype=np.float32)
                metadata = {
                    "section_title": p["section_title"],
                    "block_types":   p["block_types"],
                    "language":      language,
                    "extractor":     extractor_tag,
                }
                conn.execute(
                    """
                    INSERT INTO report_chunks
                        ("ReportId", "PageNumber", "ChunkIndex", "Content",
                         embedding, metadata, "CreatedAt")
                    VALUES (%s, %s, %s, %s, %s, %s::jsonb, now())
                    """,
                    [
                        report_id,
                        p["page_number"],
                        p["chunk_index"],
                        p["content"],
                        vec,
                        json.dumps(metadata),
                    ],
                )
            conn.commit()
        return deleted

    deleted = _retry(_persist, "persist", report_id)

    pages_with_chunks = len({p["page_number"] for p in pending})
    total_ms = int((time.perf_counter() - overall_started) * 1000)

    logger.info(
        "[ingest %s] persisted: deleted=%d inserted=%d pages=%d/%d "
        "extractor=%s latency=%dms",
        report_id, deleted, len(pending),
        pages_with_chunks, pages_total, extractor_tag, total_ms,
    )


    return {
        "report_id":        report_id,
        "pages_processed":  pages_with_chunks,
        "chunks_inserted":  len(pending),
        "pages_total":      pages_total,
        "extractor":        extractor_tag,
        "embed_model":      settings.embed_vertex_model,
        "embed_dim":        settings.embed_vertex_dim,
        "total_latency_ms": total_ms,
    }
