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
from pipeline.errors import InvalidPdfError
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
        except InvalidPdfError as exc:
            # Deterministic input failure — the bytes will not change
            # between retries, so re-running burns ~40 s of GPU for
            # nothing. Fail fast so the job marks Failed quickly.
            logger.error(
                "[ingest %s] stage=%s aborting (non-retryable): %s",
                report_id, stage, exc,
            )
            raise
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


def _markdown_target_from_pdf_url(file_url: str) -> tuple[str, str] | None:
    """Co-locate the markdown next to the PDF: same bucket, same folder,
    same basename, `.md` extension.

    Returns `(bucket, blob_path)` for a gs:// URL, or None when the URL is
    not gs:// (HTTPS / arbitrary) — those callers don't have an unambiguous
    co-location target, so the upload stage is skipped.
    """
    if not file_url.startswith("gs://"):
        return None

    rest = file_url[len("gs://"):]
    bucket_name, _, blob_path = rest.partition("/")
    if not bucket_name or not blob_path or blob_path.endswith("/"):
        return None

    # Swap a trailing .pdf (case-insensitive) for .md, or append .md when the
    # filename had no extension. We don't touch any prefix directories.
    if blob_path.lower().endswith(".pdf"):
        md_path = blob_path[:-4] + ".md"
    else:
        md_path = blob_path + ".md"
    return bucket_name, md_path


def _upload_markdown(
    report_id: str,
    file_url: str,
    markdown_text: str,
) -> str | None:
    """Stage 1.5 — write the Docling markdown export beside the source PDF.

    Returns the gs:// URI on success, or None when the stage is skipped
    (disabled by settings, or source URL isn't gs://). Always uploads when
    we have a target, including for empty markdown: the file's existence is
    the signal that ingest got past extract. Deterministic key (same folder,
    same basename) means retries overwrite in place.

    Charset is declared explicitly — Arabic-heavy docs render as mojibake in
    browsers / gsutil cp without `charset=utf-8`.
    """
    if not settings.markdown_export_enabled:
        return None

    target = _markdown_target_from_pdf_url(file_url)
    if target is None:
        logger.info(
            "[ingest %s] markdown upload skipped: non-gs:// source url (%s)",
            report_id, file_url,
        )
        return None

    bucket_name, blob_path = target
    payload = (markdown_text or "").encode("utf-8")

    blob = _gcs().bucket(bucket_name).blob(blob_path)
    blob.upload_from_string(payload, content_type="text/markdown; charset=utf-8")

    uri = f"gs://{bucket_name}/{blob_path}"
    logger.info(
        "[ingest %s] uploaded markdown (%d chars, %d bytes) → %s",
        report_id, len(markdown_text or ""), len(payload), uri,
    )
    return uri


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

    # ── Stage 1.5: Upload markdown beside the PDF ────────────────────────────
    # Free byproduct of the extract stage — same parsed tree, exported as
    # Markdown. We upload BEFORE chunking/embedding so the readable artifact
    # survives even if a later stage fails. Co-located with the PDF (same
    # folder, same basename, .md extension) so it lists naturally next to
    # the source in storage browsers. No-op when the source URL isn't gs://
    # or when markdown_export_enabled is False. Retries via _retry().
    markdown_uri: str | None = None
    if settings.markdown_export_enabled:
        markdown_uri = _retry(
            lambda: _upload_markdown(
                report_id, file_url, extract_response.markdown or "",
            ),
            "markdown_upload", report_id,
        )

    # ── Stage 3: Chunk ────────────────────────────────────────────────────────
    # Pure in-memory — deterministic, no I/O; retry not applicable.
    #
    # Document-level: flatten every page's blocks into a single list tagged
    # with `page_number` and a globally-monotonic `reading_order`, then run
    # the chunker once. Lets headings stick to bodies that wrap onto the next
    # page, lets paragraphs that cross page breaks pack into one chunk, and
    # lets section_title flow across page boundaries. See ai-service/core/
    # chunking.py for the heading-merge rule and the bbox-citation contract.
    flat_blocks: list[dict] = []
    text_fallback_pages: list[tuple[int, str]] = []
    global_order = 0
    for page in extract_response.pages:
        page_num = page.page_number
        if not page_num:
            continue
        if page.blocks:
            for b in page.blocks:
                flat_blocks.append({
                    "type":          b.type,
                    "content":       b.content,
                    "reading_order": global_order,
                    "page_number":   page_num,
                    "bbox":          b.bbox.model_dump() if b.bbox else None,
                })
                global_order += 1
        elif (page.content or "").strip():
            text_fallback_pages.append((page_num, page.content))

    pending: list[dict] = []
    # Re-scope chunk_index to be sequential within each chunk's primary page
    # so the report_chunks UNIQUE (ReportId, PageNumber, ChunkIndex) constraint
    # and the per-page neighbour-expansion query both keep working unchanged.
    # A chunk that spans pages still gets one (page_number, chunk_index) — its
    # cross-page coverage is preserved in metadata.bboxes.
    per_page_counters: dict[int, int] = {}
    if flat_blocks:
        for ch in chunker.chunk_blocks_with_meta(flat_blocks):
            pg = int(ch.get("page_number") or 1)
            idx = per_page_counters.get(pg, 0)
            per_page_counters[pg] = idx + 1
            pending.append({
                "page_number":   pg,
                "chunk_index":   idx,
                "content":       ch["content"],
                "section_title": ch["section_title"],
                "block_types":   ch["block_types"],
                "bboxes":        ch.get("bboxes") or [],
            })

    # Pages where the layout pipeline produced no structured blocks (full-page
    # OCR fallback, image-only pages) still need chunking. Run the recursive
    # text splitter per page — these can't pack across pages anyway since
    # they have no structural context.
    for page_num, content in text_fallback_pages:
        base_idx = per_page_counters.get(page_num, 0)
        pieces = chunker.chunk_text(content)
        for offset, piece in enumerate(pieces):
            pending.append({
                "page_number":   page_num,
                "chunk_index":   base_idx + offset,
                "content":       piece,
                "section_title": "",
                "block_types":   ["text"],
                "bboxes":        [],
            })
        per_page_counters[page_num] = base_idx + len(pieces)
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
                    "bboxes":        p.get("bboxes") or [],
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


    result = {
        "report_id":        report_id,
        "pages_processed":  pages_with_chunks,
        "chunks_inserted":  len(pending),
        "pages_total":      pages_total,
        "extractor":        extractor_tag,
        "embed_model":      settings.embed_vertex_model,
        "embed_dim":        settings.embed_vertex_dim,
        "total_latency_ms": total_ms,
    }
    if markdown_uri:
        result["markdown_uri"] = markdown_uri
        result["markdown_chars"] = len(extract_response.markdown or "")
    return result
