"""PDF document translation with Gemini verification + fallback.

Pipeline:
  1. Translate via Google Cloud Translation v3 Document Translation. The
     sync `translate_document` API caps PDFs at 20 pages; longer documents
     are split into <=20-page chunks, each is translated independently,
     and the translated chunks are merged back into a single PDF.
  2. Find the new PDF Google created in each chunk's output prefix
     (filename pattern varies — we list and pick freshly created blobs).
  3. Send BOTH the original and the Google-translated PDF to Gemini, asking
     whether the second is actually translated into the target language.
     This catches the path-rendered case where Google produces a near-copy of
     the input (size barely changes, so a size check is unreliable).
  4. If Gemini says "not translated", fall back: send the original PDF to
     Gemini → receive translated text per page → render a new PDF + upload.
  5. Return the URI of whichever translated PDF the pipeline produced.
"""
import datetime
import logging
import time
import uuid

import fitz  # PyMuPDF
from google.cloud import storage, translate_v3

from core.config import settings
from services.gemini import translate_pdf_content, verify_translation

logger = logging.getLogger(__name__)

# Google Cloud Translation v3 sync `translate_document` rejects PDFs with
# more than this many pages with 400 InvalidArgument. Larger PDFs are split
# into chunks of this size, translated separately, and merged.
_GOOGLE_TRANSLATE_MAX_PAGES = 20

_translate_client: translate_v3.TranslationServiceClient | None = None
_storage_client: storage.Client | None = None
_parent: str | None = None


def _init():
    global _translate_client, _storage_client, _parent
    if _translate_client is None:
        _translate_client = translate_v3.TranslationServiceClient()
        _storage_client = storage.Client()
        _parent = f"projects/{settings.gcp_project_id}/locations/{settings.translate_location}"


# ── Language detection ───────────────────────────────────────────────────────

def detect_language(text: str) -> str:
    """Detect the language of a text snippet via Google's API.
    Returns a BCP-47 code e.g. 'ar'.

    Note: Google's detector miscalls glyph-form Arabic (U+FE70-FEFF, U+FB50-
    FDFF) as English because those codepoints fall outside the standard
    Arabic block. Use detect_source_language() for our own corpus where
    presentation forms are common.
    """
    _init()
    response = _translate_client.detect_language(
        request={"parent": _parent, "content": text[:1000], "mime_type": "text/plain"}
    )
    return response.languages[0].language_code if response.languages else "ar"


def _is_arabic_char(ch: str) -> bool:
    """Match any Arabic character — standard, supplement, extended, AND
    presentation forms. Critical for PDFs that embed Arabic as font glyphs:
    those codepoints are in U+FB50-FDFF / U+FE70-FEFF and a naive standard-
    only check misclassifies them as non-Arabic."""
    cp = ord(ch)
    return (
        0x0600 <= cp <= 0x06FF      # Arabic core
        or 0x0750 <= cp <= 0x077F   # Arabic Supplement
        or 0x08A0 <= cp <= 0x08FF   # Arabic Extended-A
        or 0xFB50 <= cp <= 0xFDFF   # Arabic Presentation Forms-A
        or 0xFE70 <= cp <= 0xFEFF   # Arabic Presentation Forms-B
    )


def detect_source_language(text: str) -> str:
    """Return 'ar' if the sample is dominantly Arabic, else 'en'.

    Counts Arabic vs Latin LETTERS only — digits, whitespace, punctuation
    are excluded so a heading like '8. الخصائص الرئيسية' counts as Arabic
    despite the digit. Threshold of 0.4 is intentionally permissive: we
    only need to choose a translation direction, and 'en' is the safer
    default for ambiguous cases.

    Use this instead of detect_language() whenever the sample comes from
    our extraction pipeline — it correctly recognises glyph-form Arabic
    that Google's detector treats as non-Arabic."""
    if not text:
        return "en"
    arabic = sum(1 for ch in text if _is_arabic_char(ch))
    latin = sum(1 for ch in text if ch.isascii() and ch.isalpha())
    total = arabic + latin
    if total == 0:
        return "en"
    return "ar" if arabic / total >= 0.4 else "en"


# ── GCS helpers ──────────────────────────────────────────────────────────────

def _parse_gs(uri: str) -> tuple[str, str]:
    """gs://bucket/path/to/file.pdf → ('bucket', 'path/to/file.pdf')"""
    assert uri.startswith("gs://")
    bucket, _, path = uri[5:].partition("/")
    return bucket, path


def _download_pdf(uri: str) -> bytes:
    bucket_name, blob_path = _parse_gs(uri)
    return _storage_client.bucket(bucket_name).blob(blob_path).download_as_bytes()


def _upload_pdf(uri: str, data: bytes) -> None:
    bucket_name, blob_path = _parse_gs(uri)
    _storage_client.bucket(bucket_name).blob(blob_path).upload_from_string(
        data, content_type="application/pdf"
    )


# ── Output discovery ────────────────────────────────────────────────────────

def _find_new_pdf_under(prefix_uri: str, after: datetime.datetime) -> str | None:
    """Find a PDF blob under prefix_uri that was created after `after`.

    Google's translate_document with a GCS destination doesn't return the
    output URI; it generates a filename like
    `<input-path>_<target_lang>_translations.pdf`. List + filter is the
    reliable way to discover it.
    """
    bucket_name, prefix = _parse_gs(prefix_uri)
    bucket = _storage_client.bucket(bucket_name)
    all_under_prefix = list(bucket.list_blobs(prefix=prefix))
    candidates = [
        b for b in all_under_prefix
        if b.name.lower().endswith(".pdf") and b.time_created and b.time_created > after
    ]
    logger.debug(
        "[translate] discover under %s after=%s → %d total, %d fresh PDFs",
        prefix_uri, after.isoformat(), len(all_under_prefix), len(candidates),
    )
    if not candidates:
        logger.warning(
            "[translate] no fresh PDF found under %s (saw %d total blobs); "
            "Google Translate may have produced no output",
            prefix_uri, len(all_under_prefix),
        )
        return None
    candidates.sort(key=lambda b: b.time_created, reverse=True)
    chosen = candidates[0]
    logger.info(
        "[translate] found translated PDF: gs://%s/%s (created %s, %d bytes)",
        bucket_name, chosen.name,
        chosen.time_created.isoformat() if chosen.time_created else "?",
        chosen.size or 0,
    )
    return f"gs://{bucket_name}/{chosen.name}"


# ── Gemini fallback PDF rendering ───────────────────────────────────────────

def _render_translated_pdf(pages: list[str]) -> bytes:
    """Build a plain-text PDF from translated page strings.

    Layout from the original is lost, but content is correct. The output is a
    clean A4 PDF with one source page = one new page.
    """
    new_pdf = fitz.open()
    margin = 50.0
    width, height = 595.0, 842.0  # A4 in points
    for page_text in pages:
        page = new_pdf.new_page(width=width, height=height)
        rect = fitz.Rect(margin, margin, width - margin, height - margin)
        page.insert_textbox(rect, page_text or "", fontsize=10, align=0)
    out_bytes = new_pdf.tobytes()
    new_pdf.close()
    return out_bytes


def _gemini_fallback(
    gcs_input_uri: str, output_prefix: str, target_language: str
) -> str:
    """Use Gemini to translate the PDF, build a new PDF, upload, return URI."""
    logger.info(
        "[translate] gemini fallback: input=%s target=%s",
        gcs_input_uri, target_language,
    )
    pdf_bytes = _download_pdf(gcs_input_uri)
    logger.info("[translate] gemini fallback: downloaded %d bytes", len(pdf_bytes))
    pages = translate_pdf_content(pdf_bytes, target_language)
    logger.info(
        "[translate] gemini fallback: produced %d translated pages", len(pages),
    )
    rendered = _render_translated_pdf(pages)

    filename = gcs_input_uri.rsplit("/", 1)[-1]
    base, _, ext = filename.rpartition(".")
    new_name = f"{base}.gemini.{ext or 'pdf'}"
    output_uri = f"{output_prefix}{new_name}"
    _upload_pdf(output_uri, rendered)
    logger.info(
        "[translate] gemini fallback: uploaded %s (%d bytes)",
        output_uri, len(rendered),
    )
    return output_uri


# ── Public translate API ─────────────────────────────────────────────────────

def translate_pdf(
    gcs_input_uri: str,
    output_prefix: str,
    source_language: str,
    target_language: str,
) -> str:
    """Translate a PDF stored in GCS. Splits over the Google Translate page
    limit and translates per-chunk; falls back to Gemini if Google Translate
    returns a copy of the input.
    """
    _init()
    logger.info(
        "[translate] start: input=%s output_prefix=%s %s→%s",
        gcs_input_uri, output_prefix, source_language, target_language,
    )

    # Read once: we need page count to decide chunking and the bytes to
    # construct chunks if needed. For a small PDF this is cheap; for a 100MB
    # PDF it's still well under Cloud Run's memory cap.
    t_dl = time.perf_counter()
    pdf_bytes = _download_pdf(gcs_input_uri)
    with fitz.open(stream=pdf_bytes, filetype="pdf") as src:
        page_count = len(src)
    logger.info(
        "[translate] downloaded %.1f MB (%d pages) in %dms",
        len(pdf_bytes) / (1024 * 1024), page_count,
        int((time.perf_counter() - t_dl) * 1000),
    )
    logger.info(
        "[translate] strategy: %s (max_per_call=%d)",
        "single-call" if page_count <= _GOOGLE_TRANSLATE_MAX_PAGES else "chunked",
        _GOOGLE_TRANSLATE_MAX_PAGES,
    )

    # Step 1: produce a Google-translated PDF (chunked when oversized)
    t_translate = time.perf_counter()
    if page_count <= _GOOGLE_TRANSLATE_MAX_PAGES:
        google_uri = _translate_single(
            gcs_input_uri, output_prefix, source_language, target_language,
        )
    else:
        google_uri = _translate_chunked(
            pdf_bytes, gcs_input_uri, output_prefix,
            source_language, target_language,
        )
    logger.info(
        "[translate] Google translate phase: %dms → %s",
        int((time.perf_counter() - t_translate) * 1000),
        google_uri or "(no output)",
    )

    # Step 2: verify translation by sending BOTH PDFs to Gemini
    if google_uri:
        t_verify = time.perf_counter()
        translated_bytes = _download_pdf(google_uri)
        verified = verify_translation(pdf_bytes, translated_bytes, target_language)
        logger.info(
            "[translate] Gemini verify=%s in %dms (translated %d bytes)",
            verified, int((time.perf_counter() - t_verify) * 1000),
            len(translated_bytes),
        )
        if verified:
            return google_uri  # ✅ Gemini confirms translation succeeded

    # Step 3: last-resort fallback — Gemini reads the original PDF and
    # renders a new one. Used only when Google produced a copy of the input
    # (font-rendered Arabic with no real text layer is the common case).
    logger.warning(
        "[translate] Google output did not verify as %s, falling back to Gemini",
        target_language,
    )
    return _gemini_fallback(gcs_input_uri, output_prefix, target_language)


# ── Google Translate execution paths ────────────────────────────────────────

def _translate_single(
    gcs_input_uri: str,
    output_prefix: str,
    source_language: str,
    target_language: str,
) -> str | None:
    """Translate a PDF that fits within Google's per-call page limit.
    Returns the translated PDF's gs:// URI, or None if discovery failed."""
    logger.info(
        "[translate] _translate_single: %s → prefix=%s (%s→%s)",
        gcs_input_uri, output_prefix, source_language, target_language,
    )
    before = datetime.datetime.now(datetime.timezone.utc)
    t_call = time.perf_counter()
    try:
        _translate_client.translate_document(
            request={
                "parent": _parent,
                "source_language_code": source_language,
                "target_language_code": target_language,
                "document_input_config": {
                    "gcs_source": {"input_uri": gcs_input_uri},
                    "mime_type": "application/pdf",
                },
                "document_output_config": {
                    "gcs_destination": {"output_uri_prefix": output_prefix},
                },
                "enable_rotation_correction": True,
                "enable_shadow_removal_native_pdf": True,
            }
        )
    except Exception as exc:
        logger.exception(
            "[translate] _translate_single FAILED for %s after %dms: %s",
            gcs_input_uri, int((time.perf_counter() - t_call) * 1000), exc,
        )
        raise
    logger.info(
        "[translate] translate_document call returned in %dms",
        int((time.perf_counter() - t_call) * 1000),
    )
    return _find_new_pdf_under(output_prefix, before)


def _translate_chunked(
    pdf_bytes: bytes,
    gcs_input_uri: str,
    output_prefix: str,
    source_language: str,
    target_language: str,
) -> str | None:
    """Split an oversized PDF into <=20-page chunks, translate each chunk via
    Google Translate, and merge the translated chunks into a single PDF.

    Each chunk gets its own temp prefix under `output_prefix` so listing for
    the freshly created blob is unambiguous. Temp chunks (input + output)
    are cleaned up after merge — only the merged final PDF stays in GCS.
    """
    if not output_prefix.endswith("/"):
        output_prefix = output_prefix + "/"

    # Per-job temp scratch space — colocate with the final output so we
    # don't need a second bucket and cleanup is a single prefix wipe.
    # `chunk_path_root` is a *bucket-relative* path (no gs:// prefix); we
    # build object keys against it and only re-attach gs:// at the API
    # boundary. Mixing the two is what caused the doubled-gs:// bug.
    job_id = uuid.uuid4().hex[:12]
    chunk_uri_prefix = f"{output_prefix}_chunks_{job_id}/"
    chunk_bucket, chunk_path_root = _parse_gs(chunk_uri_prefix)
    bucket = _storage_client.bucket(chunk_bucket)
    logger.info(
        "[translate] chunked job=%s scratch=%s",
        job_id, chunk_uri_prefix,
    )

    translated_chunk_bytes: list[bytes] = []
    temp_blobs: list[str] = []  # bucket-relative paths to delete at the end

    try:
        with fitz.open(stream=pdf_bytes, filetype="pdf") as src:
            n_pages = len(src)
            n_chunks = (n_pages + _GOOGLE_TRANSLATE_MAX_PAGES - 1) // _GOOGLE_TRANSLATE_MAX_PAGES
            logger.info(
                "[translate] chunked job=%s pages=%d → %d chunks of <=%d",
                job_id, n_pages, n_chunks, _GOOGLE_TRANSLATE_MAX_PAGES,
            )
            for chunk_idx, start in enumerate(range(0, n_pages, _GOOGLE_TRANSLATE_MAX_PAGES)):
                end = min(start + _GOOGLE_TRANSLATE_MAX_PAGES, n_pages) - 1
                t_chunk = time.perf_counter()

                # Slice [start..end] into a fresh PDF document.
                chunk_doc = fitz.open()
                chunk_doc.insert_pdf(src, from_page=start, to_page=end)
                chunk_pdf_bytes = chunk_doc.tobytes()
                chunk_doc.close()
                logger.debug(
                    "[translate] job=%s chunk=%d sliced %d-%d (%d bytes)",
                    job_id, chunk_idx, start + 1, end + 1, len(chunk_pdf_bytes),
                )

                # Per-chunk paths: bucket-relative for blob ops, full gs://
                # URI for the Translate API.
                this_path_prefix = f"{chunk_path_root}{chunk_idx:03d}/"
                this_uri_prefix = f"gs://{chunk_bucket}/{this_path_prefix}"
                input_blob_path = f"{this_path_prefix}input.pdf"
                input_uri = f"gs://{chunk_bucket}/{input_blob_path}"

                t_upload = time.perf_counter()
                bucket.blob(input_blob_path).upload_from_string(
                    chunk_pdf_bytes, content_type="application/pdf",
                )
                temp_blobs.append(input_blob_path)
                logger.info(
                    "[translate] job=%s chunk=%d/%d pages=%d-%d uploaded %s "
                    "(%d bytes, %dms)",
                    job_id, chunk_idx + 1, n_chunks, start + 1, end + 1,
                    input_uri, len(chunk_pdf_bytes),
                    int((time.perf_counter() - t_upload) * 1000),
                )

                # Translate the chunk. _translate_single uses gs:// URIs for
                # both input and output prefix — the API expects URIs, not
                # bucket-relative paths.
                translated_chunk_uri = _translate_single(
                    input_uri, this_uri_prefix, source_language, target_language,
                )
                if not translated_chunk_uri:
                    logger.error(
                        "[translate] job=%s chunk=%d/%d pages=%d-%d FAILED "
                        "to discover translated output under %s",
                        job_id, chunk_idx + 1, n_chunks, start + 1, end + 1,
                        this_uri_prefix,
                    )
                    raise RuntimeError(
                        f"chunk {chunk_idx} ({start+1}-{end+1}): Google produced no output"
                    )

                # Track translated blob (bucket-relative) for cleanup.
                _, translated_blob_path = _parse_gs(translated_chunk_uri)
                temp_blobs.append(translated_blob_path)

                # Buffer translated chunk bytes for merging.
                translated_chunk_bytes.append(_download_pdf(translated_chunk_uri))
                logger.info(
                    "[translate] job=%s chunk=%d/%d done in %dms → %s",
                    job_id, chunk_idx + 1, n_chunks,
                    int((time.perf_counter() - t_chunk) * 1000),
                    translated_chunk_uri,
                )

        # Merge translated chunks in order into a single PDF.
        t_merge = time.perf_counter()
        merged = fitz.open()
        merged_pages = 0
        for chunk_pdf in translated_chunk_bytes:
            with fitz.open(stream=chunk_pdf, filetype="pdf") as part:
                merged.insert_pdf(part)
                merged_pages += len(part)
        merged_bytes = merged.tobytes()
        merged.close()
        logger.info(
            "[translate] job=%s merged %d chunks → %d pages (%d bytes) in %dms",
            job_id, len(translated_chunk_bytes), merged_pages, len(merged_bytes),
            int((time.perf_counter() - t_merge) * 1000),
        )

        # Final upload — use the original filename so downstream code can
        # find the translated PDF by predictable suffix.
        original_name = gcs_input_uri.rsplit("/", 1)[-1]
        base, _, ext = original_name.rpartition(".")
        final_name = f"{base}.{target_language}.{ext or 'pdf'}"
        final_uri = f"{output_prefix}{final_name}"
        t_up = time.perf_counter()
        _upload_pdf(final_uri, merged_bytes)
        logger.info(
            "[translate] job=%s final upload → %s (%d bytes, %dms)",
            job_id, final_uri, len(merged_bytes),
            int((time.perf_counter() - t_up) * 1000),
        )
        return final_uri

    finally:
        # Cleanup temp chunk files (best-effort; ignore individual failures
        # so a half-broken cleanup never masks a successful translation).
        cleaned = 0
        for blob_path in temp_blobs:
            try:
                bucket.blob(blob_path).delete()
                cleaned += 1
            except Exception as exc:
                logger.debug(
                    "[translate] job=%s cleanup failed for %s: %s",
                    job_id, blob_path, exc,
                )
        logger.info(
            "[translate] job=%s cleanup: deleted %d/%d temp blobs",
            job_id, cleaned, len(temp_blobs),
        )
