"""Page / document extraction with primary/fallback strategy.

Three extraction paths, layered so any one of them failing degrades cleanly:

  1. `extract_document(pdf_bytes)`  — preferred. Calls doc-processor
     /v1/extract_document with the full PDF (Docling reads the text layer
     natively, no rasterisation loss for digital PDFs).
  2. `extract_page(png, page_num)`  — fallback. Renders one page to PNG and
     calls /v1/extract. Used when the PDF is over the size threshold or
     when extract_document() failed.
  3. Gemini Vision                   — final fallback inside extract_page().
     Used when doc-processor is disabled or unreachable.

Each layer falls through to the next on any error so ingest is never
blocked by a single failure mode. Which path ran is logged via the
`extractor` field for A/B comparison.

Configuration
=============
DOC_PROCESSOR_ENABLED      — gate. False (default) keeps Gemini Vision behaviour.
DOC_PROCESSOR_URL          — the Cloud Run service URL.
DOC_PROCESSOR_TOKEN        — optional shared secret for X-Internal-Token.
DOC_PROCESSOR_TIMEOUT      — request timeout (s). Cold-start can take 60s+.
DOC_PROCESSOR_MAX_PDF_MB   — max PDF size to send whole; larger → per-page.

Auth
====
The wrapper uses google.auth ID tokens so Cloud Run IAM is the primary auth
mechanism in production: each request carries a Google-signed JWT for the
doc-processor's audience. ai-service's runtime SA needs `roles/run.invoker`
on the doc-processor service.
"""
from __future__ import annotations

import base64
import logging
import time

import httpx

from core.config import settings
from services.gemini import describe_page_image

logger = logging.getLogger(__name__)


# ── Cached HTTP client + auth token ─────────────────────────────────────────
# httpx.Client is process-global so connection pooling survives across
# requests. ID tokens are short-lived (1h) — we re-fetch lazily on each
# call but rely on google.auth's internal caching, which keeps overhead
# under 1 ms per request after the first.

_http: httpx.Client | None = None


def _client() -> httpx.Client:
    global _http
    if _http is None:
        _http = httpx.Client(timeout=settings.doc_processor_timeout_seconds)
    return _http


def _id_token() -> str | None:
    """Fetch a Google-signed ID token for the doc-processor's URL.

    Returns None if google.auth isn't reachable (local dev without ADC, or
    metadata server unavailable). Without IAM the X-Internal-Token header
    — if configured — is the remaining auth layer.

    On Cloud Run this works unmodified: the metadata server hands out a
    signed JWT for any audience the runtime SA is allowed to invoke. Locally
    it works after `gcloud auth application-default login` provided the
    user has run.invoker on the doc-processor service.
    """
    if not settings.doc_processor_url:
        return None

    try:
        from google.auth.transport.requests import Request as AuthRequest
        from google.oauth2 import id_token as oauth_id_token

        return oauth_id_token.fetch_id_token(AuthRequest(), settings.doc_processor_url)
    except Exception as exc:
        logger.debug("doc_extractor: ID token fetch failed (%s); proceeding without", exc)
        return None


# ── Public API ──────────────────────────────────────────────────────────────

def extract_document(pdf_bytes: bytes) -> list[dict] | None:
    """Try the full-PDF extraction path. Return None to signal "skip me".

    Returns a list of `{page_number, content, metadata}` dicts in page order
    when the doc-processor accepted the PDF and produced content. Returns
    None when:
        • doc-processor is disabled
        • PDF exceeds the configured size threshold (caller should fall back
          to per-page rendering, which doesn't have the request-size limit)
        • doc-processor returned an error or empty pages

    Caller should treat None as "use the per-page path" rather than a hard
    failure — the per-page path will itself fall back to Gemini Vision.
    """
    if not (settings.doc_processor_enabled and settings.doc_processor_url):
        return None

    pdf_mb = len(pdf_bytes) / (1024 * 1024)
    if pdf_mb > settings.doc_processor_max_pdf_mb:
        logger.info(
            "doc_extractor: PDF is %.1f MB > %.1f MB cap; using per-page path",
            pdf_mb, settings.doc_processor_max_pdf_mb,
        )
        return None

    try:
        return _call_doc_processor_document(pdf_bytes)
    except Exception as exc:
        # Surface the failure but let the per-page path try — that has its
        # own Gemini-Vision fallback.
        logger.warning(
            "doc-processor /v1/extract_document failed (%s); will fall back to per-page",
            exc,
        )
        return None


def extract_page(
    png_bytes: bytes, page_number: int, force_gemini: bool = False,
) -> dict:
    """Return `{content, metadata}` for one rendered PDF page.

    Tries doc-processor /v1/extract when enabled, falls back to Gemini Vision
    on any failure. The shape is identical to gemini.describe_page_image()
    so the ingest pipeline can swap pipelines via a single env flag.

    `force_gemini=True` skips doc-processor for this page — used by the
    ingest extractor toggle to A/B compare against the GPU pipeline.
    """
    if (
        not force_gemini
        and settings.doc_processor_enabled
        and settings.doc_processor_url
    ):
        try:
            return _call_doc_processor(png_bytes, page_number)
        except Exception as exc:
            logger.warning(
                "doc-processor /v1/extract failed for page=%d: %s — falling back to Gemini Vision",
                page_number, exc,
            )

    # Default / fallback path. The Gemini result already matches the
    # {content, metadata} contract.
    result = describe_page_image(png_bytes)
    result.setdefault("metadata", {}).setdefault("extractor", "gemini-vision")
    return result


# ── doc-processor calls ─────────────────────────────────────────────────────

def _call_doc_processor_document(pdf_bytes: bytes) -> list[dict] | None:
    """POST to doc-processor /v1/extract_document and translate the response
    into the same `{page_number, content, metadata}` shape ai-service uses.

    Returns None when the response had no pages — caller treats that as
    "fall back" rather than persisting an empty ingest.
    """
    url = settings.doc_processor_url.rstrip("/") + "/v1/extract_document"
    headers = _build_headers()

    payload = {
        "pdf_b64": base64.b64encode(pdf_bytes).decode("ascii"),
        "options": {
            "extract_tables":     True,
            "extract_figures":    True,
            "extract_formulas":   True,
            "ocr_fallback":       True,
            "figure_captioning":  True,
        },
    }

    started = time.perf_counter()
    response = _client().post(url, headers=headers, json=payload)
    response.raise_for_status()
    body = response.json()

    pages = body.get("pages") or []
    if not pages:
        logger.warning("doc-processor /v1/extract_document returned 0 pages")
        return None

    elapsed_ms = int((time.perf_counter() - started) * 1000)
    doc_meta = body.get("document_metadata") or {}
    logger.info(
        "doc-processor document latency=%dms pages=%d tables=%s figures=%s formulas=%s",
        elapsed_ms,
        doc_meta.get("page_count", len(pages)),
        doc_meta.get("has_tables"),
        doc_meta.get("has_figures"),
        doc_meta.get("has_formulas"),
    )

    extractor_tag = body.get("extractor") or "doc-processor-v1"
    return [_normalize_page_response(p, extractor_tag) for p in pages]


def _call_doc_processor(png_bytes: bytes, page_number: int) -> dict:
    """POST to doc-processor /v1/extract and translate the response back into
    the contract the ingest pipeline already understands."""
    url = settings.doc_processor_url.rstrip("/") + "/v1/extract"
    headers = _build_headers()

    payload = {
        "image_b64": base64.b64encode(png_bytes).decode("ascii"),
        "page_number": page_number,
        "options": {
            "extract_tables":     True,
            "extract_figures":    True,
            "extract_formulas":   True,
            "ocr_fallback":       True,
            "figure_captioning":  True,
        },
    }

    started = time.perf_counter()
    response = _client().post(url, headers=headers, json=payload)
    response.raise_for_status()
    body = response.json()

    elapsed_ms = int((time.perf_counter() - started) * 1000)
    logger.info(
        "doc-processor page=%d latency=%dms blocks=%d",
        page_number, elapsed_ms, len(body.get("blocks", []) or []),
    )

    return _normalize_page_response(
        body, body.get("extractor") or "doc-processor-v1",
    )


# ── Combined extract + chunk + embed (Option B) ─────────────────────────────


def ingest_full(pdf_bytes: bytes) -> dict | None:
    """Call doc-processor /v1/ingest_full and return chunks-with-vectors.

    Returns a dict shaped:
        {
          "document_metadata": {page_count, has_tables, ...},
          "chunks": [
              {page_number, chunk_index, content, section_title,
               block_types, embedding},
              ...
          ],
          "stats": {extract_latency_ms, chunk_latency_ms, embed_latency_ms,
                    total_latency_ms, pages_processed, chunks_emitted},
          "extractor": str,
          "embed_model": str,
          "embed_dim": int,
        }

    Returns None when:
      • doc-processor is disabled or unconfigured
      • PDF exceeds the configured size threshold (caller falls back to
        per-page extraction, which itself falls back to Gemini Vision)

    Raises on any HTTP / network error so the caller's try/except in the
    job runner marks the job Failed. We intentionally don't fall back to
    /v1/extract_document here — that path was the OOM-prone one we're
    moving off; reintroducing it as a fallback would defeat the point.
    """
    if not (settings.doc_processor_enabled and settings.doc_processor_url):
        return None

    pdf_mb = len(pdf_bytes) / (1024 * 1024)
    if pdf_mb > settings.doc_processor_max_pdf_mb:
        logger.info(
            "doc_extractor: PDF is %.1f MB > %.1f MB cap; using per-page path",
            pdf_mb, settings.doc_processor_max_pdf_mb,
        )
        return None

    url = settings.doc_processor_url.rstrip("/") + "/v1/ingest_full"
    headers = _build_headers()
    payload = {
        "pdf_b64": base64.b64encode(pdf_bytes).decode("ascii"),
        "options": {
            "extract_tables":     True,
            "extract_figures":    True,
            "extract_formulas":   True,
            "ocr_fallback":       True,
            "figure_captioning":  True,
            "embed_chunks":       True,
        },
    }

    started = time.perf_counter()
    response = _client().post(url, headers=headers, json=payload)
    response.raise_for_status()
    body = response.json()
    elapsed_ms = int((time.perf_counter() - started) * 1000)

    chunks = body.get("chunks") or []
    stats = body.get("stats") or {}
    doc_meta = body.get("document_metadata") or {}
    logger.info(
        "doc-processor ingest_full latency=%dms chunks=%d pages=%d "
        "extract_ms=%s chunk_ms=%s embed_ms=%s",
        elapsed_ms, len(chunks), doc_meta.get("page_count", 0),
        stats.get("extract_latency_ms"),
        stats.get("chunk_latency_ms"),
        stats.get("embed_latency_ms"),
    )
    return body


# ── Embeddings ──────────────────────────────────────────────────────────────

def embed_texts(texts: list[str], kind: str = "passage") -> list[list[float]]:
    """POST a list of strings to doc-processor /v1/embed and return one
    1024-dim vector per input.

    Replaces services.gemini.embed_text — embeddings now run on the GPU
    service alongside extraction, so we don't pay a Vertex round-trip per
    chunk and we don't depend on cross-region Vertex availability. Backed
    by BAAI/bge-m3 in the doc-processor; it uses raw text for both queries
    and passages so the `kind` parameter is reserved for future embedder
    swaps but currently has no effect on output.

    Raises on any HTTP / network error so the caller's existing retry logic
    kicks in. Returns [] only if `texts` is empty.
    """
    if not texts:
        return []
    if not (settings.doc_processor_enabled and settings.doc_processor_url):
        raise RuntimeError(
            "doc_processor not configured — cannot embed; set DOC_PROCESSOR_ENABLED=true "
            "and DOC_PROCESSOR_URL"
        )

    url = settings.doc_processor_url.rstrip("/") + "/v1/embed"
    headers = _build_headers()
    payload = {"texts": texts, "kind": kind}

    started = time.perf_counter()
    response = _client().post(url, headers=headers, json=payload)
    response.raise_for_status()
    body = response.json()
    vectors = body.get("embeddings") or []
    if len(vectors) != len(texts):
        raise RuntimeError(
            f"doc-processor /v1/embed returned {len(vectors)} vectors for {len(texts)} inputs"
        )

    elapsed_ms = int((time.perf_counter() - started) * 1000)
    logger.info(
        "doc-processor embed kind=%s n=%d latency=%dms model=%s",
        kind, len(texts), elapsed_ms, body.get("model"),
    )
    return vectors


# ── Shared helpers ──────────────────────────────────────────────────────────

def _build_headers() -> dict[str, str]:
    """Common headers for doc-processor calls: content-type + IAM bearer +
    optional defence-in-depth shared secret."""
    headers: dict[str, str] = {"Content-Type": "application/json"}
    if settings.doc_processor_token:
        headers["X-Internal-Token"] = settings.doc_processor_token
    token = _id_token()
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return headers


def _normalize_page_response(page_body: dict, extractor_tag: str) -> dict:
    """Trim a doc-processor page response to the shape the ingest pipeline
    consumes. We now also preserve the structured `blocks` list so the
    chunker can pack along block boundaries (heading-aware, never splitting
    a table or figure) instead of doing blind character splits on `content`.

    Per-block keys we keep:
        type           — text / heading / table / figure / formula / footer
        content        — the chunk-ready text the block emits
        reading_order  — global ordinal so we can sort if anything reorders

    The structured payloads (table.markdown, figure.caption, formula.latex)
    are already inlined into block.content by the orchestrator, so we don't
    re-flatten them here.
    """
    metadata_in = page_body.get("metadata") or {}

    raw_blocks = page_body.get("blocks") or []
    blocks: list[dict] = []
    for b in raw_blocks:
        if not isinstance(b, dict):
            continue
        text = (b.get("content") or "").strip()
        if not text:
            continue
        blocks.append({
            "type":          b.get("type") or "text",
            "content":       text,
            "reading_order": int(b.get("reading_order") or 0),
        })

    return {
        "page_number":  page_body.get("page_number"),  # absent on /v1/extract; harmless
        "content":      page_body.get("content", "") or "",
        "blocks":       blocks,                        # empty for non-doc-processor extractors
        "metadata": {
            "section_title": (metadata_in.get("section_title") or "")[:300],
            "page_type":     metadata_in.get("page_type") or "mixed",
            "language":      metadata_in.get("language") or "mixed",
            "extractor":     extractor_tag,
        },
    }
