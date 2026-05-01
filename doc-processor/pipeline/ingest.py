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


_MAX_EMBED_RETRIES = 3


def reset_job_for_embed_retry(job_id: str, error: str) -> None:
    """Reset a job with a chunk checkpoint back to Pending, up to a limit.

    Increments embed_retry_count inside the checkpoint JSON on every call.
    Once the count reaches _MAX_EMBED_RETRIES the checkpoint is cleared and
    the job is marked Failed so it doesn't loop forever.
    """
    with get_conn() as conn:
        cur = conn.execute(
            'SELECT "OutputData" FROM ai_jobs WHERE "Id" = %s',
            [job_id],
        )
        row = cur.fetchone()
        if not row or not row[0] or row[0].get("checkpoint") != "chunks_ready":
            # Checkpoint disappeared — just fail the job normally.
            conn.execute(
                """
                UPDATE ai_jobs
                   SET "Status"       = 'Failed',
                       "ErrorMessage" = %s,
                       "CompletedAt"  = now()
                 WHERE "Id" = %s
                """,
                [error[:1000], job_id],
            )
            conn.commit()
            return

        checkpoint = row[0]
        retries = checkpoint.get("embed_retry_count", 0) + 1

        if retries >= _MAX_EMBED_RETRIES:
            # Exhausted retries — clear checkpoint and mark Failed.
            conn.execute(
                """
                UPDATE ai_jobs
                   SET "Status"       = 'Failed',
                       "OutputData"   = NULL,
                       "ErrorMessage" = %s,
                       "CompletedAt"  = now()
                 WHERE "Id" = %s
                """,
                [f"embed failed after {retries} retries: {error[:900]}", job_id],
            )
            conn.commit()
            logger.warning(
                "[ingest] job=%s embed failed %d/%d times — marking Failed",
                job_id, retries, _MAX_EMBED_RETRIES,
            )
        else:
            # Update retry count and reset to Pending.
            checkpoint["embed_retry_count"] = retries
            conn.execute(
                """
                UPDATE ai_jobs
                   SET "Status"       = 'Pending',
                       "StartedAt"    = NULL,
                       "OutputData"   = %s::jsonb,
                       "ErrorMessage" = %s
                 WHERE "Id" = %s
                """,
                [
                    json.dumps(checkpoint),
                    f"embed retry {retries}/{_MAX_EMBED_RETRIES}: {error[:800]}",
                    job_id,
                ],
            )
            conn.commit()
            logger.info(
                "[ingest] job=%s embed retry %d/%d — reset to Pending",
                job_id, retries, _MAX_EMBED_RETRIES,
            )


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


# ── Chunk checkpoint helpers ─────────────────────────────────────────────────
# Stored in ai_jobs.OutputData as:
#   {"checkpoint": "chunks_ready", "report_id": ..., "pages_total": ...,
#    "extractor": ..., "language": ..., "chunks": [...]}
#
# Written ONLY when embedding fails so the expensive download→extract→chunk
# work is not repeated on retry.  Cleared automatically when the job
# completes (mark_job_completed overwrites OutputData with final stats).

def _save_chunk_checkpoint(
    job_id: str,
    chunks: list[dict],
    pages_total: int,
    extractor_tag: str,
    language: str,
) -> None:
    payload = json.dumps({
        "checkpoint":  "chunks_ready",
        "pages_total": pages_total,
        "extractor":   extractor_tag,
        "language":    language,
        "chunks":      chunks,
    })
    with get_conn() as conn:
        conn.execute(
            'UPDATE ai_jobs SET "OutputData" = %s::jsonb WHERE "Id" = %s',
            [payload, job_id],
        )
        conn.commit()
    logger.info(
        "[ingest] saved chunk checkpoint for job=%s (%d chunks)",
        job_id, len(chunks),
    )


def _load_chunk_checkpoint(job_id: str) -> dict | None:
    """Return the saved checkpoint dict if one exists, else None."""
    with get_conn() as conn:
        cur = conn.execute(
            """
            SELECT "OutputData"
              FROM ai_jobs
             WHERE "Id" = %s
               AND "OutputData"->>'checkpoint' = 'chunks_ready'
            """,
            [job_id],
        )
        row = cur.fetchone()
    return row[0] if row else None


def run_ingest(
    report_id: str,
    file_url: str,
    options: IngestOptions,
    job_id: str | None = None,
) -> dict:
    """Run the full ingest pipeline and return stats.

    Called from the /v1/ingest_job background task via loop.run_in_executor
    so it always runs on a thread pool worker, never on the event loop.

    If a chunk checkpoint is found in ai_jobs.OutputData (written by a
    previous failed attempt) stages 1-3 are skipped and embedding resumes
    directly from the saved chunks.

    Returns a dict that mark_job_completed persists verbatim as OutputData.
    """
    overall_started = time.perf_counter()

    # ── Checkpoint resume: skip stages 1-3 if chunks were already saved ──────
    checkpoint = _load_chunk_checkpoint(job_id) if job_id else None
    if checkpoint:
        pending      = checkpoint["chunks"]
        pages_total  = checkpoint["pages_total"]
        extractor_tag = checkpoint["extractor"]
        language     = checkpoint["language"]
        logger.info(
            "[ingest %s] resuming from chunk checkpoint — "
            "skipping download/extract/chunk (%d chunks, %d pages)",
            report_id, len(pending), pages_total,
        )
    else:
        # ── Stage 1: Download ─────────────────────────────────────────────────
        pdf_bytes = _download(file_url)
        logger.info(
            "[ingest %s] downloaded %.2f MB from %s",
            report_id, len(pdf_bytes) / 1024 / 1024, file_url,
        )

        # ── Stage 2: Extract ──────────────────────────────────────────────────
        extract_options = ExtractOptions(
            extract_tables=options.extract_tables,
            extract_figures=options.extract_figures,
            extract_formulas=options.extract_formulas,
            ocr_fallback=options.ocr_fallback,
            figure_captioning=options.figure_captioning,
        )
        extract_response = process_document(pdf_bytes, extract_options, None)
        pages_total   = extract_response.document_metadata.page_count
        extractor_tag = extract_response.extractor
        languages     = extract_response.document_metadata.detected_languages
        language      = languages[0] if languages else "mixed"
        logger.info(
            "[ingest %s] extracted %d pages (extractor=%s)",
            report_id, pages_total, extractor_tag,
        )

        # ── Stage 3: Chunk ────────────────────────────────────────────────────
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
        texts = [p["content"] for p in pending]
        try:
            vectors = vertex_embedder.embed_passages(texts)
        except Exception as embed_exc:
            # Save chunks so the next retry skips download/extract/chunk.
            if job_id and not checkpoint:
                try:
                    _save_chunk_checkpoint(
                        job_id, pending, pages_total, extractor_tag, language,
                    )
                except Exception as save_exc:
                    logger.warning(
                        "[ingest %s] failed to save chunk checkpoint: %s",
                        report_id, save_exc,
                    )
            raise

        if len(vectors) != len(pending):
            raise RuntimeError(
                f"Vertex returned {len(vectors)} vectors for {len(pending)} chunks"
            )
    logger.info(
        "[ingest %s] embedded %d chunks (model=%s)",
        report_id, len(vectors), settings.embed_vertex_model,
    )

    # ── Stage 5: Persist ─────────────────────────────────────────────────────
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
