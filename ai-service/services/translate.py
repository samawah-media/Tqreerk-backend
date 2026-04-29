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
    """Detect the language of a text snippet. Returns a BCP-47 code e.g. 'ar'."""
    _init()
    response = _translate_client.detect_language(
        request={"parent": _parent, "content": text[:1000], "mime_type": "text/plain"}
    )
    return response.languages[0].language_code if response.languages else "ar"


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
    candidates = [
        b for b in bucket.list_blobs(prefix=prefix)
        if b.name.lower().endswith(".pdf") and b.time_created and b.time_created > after
    ]
    if not candidates:
        return None
    candidates.sort(key=lambda b: b.time_created, reverse=True)
    return f"gs://{bucket_name}/{candidates[0].name}"


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
    pdf_bytes = _download_pdf(gcs_input_uri)
    pages = translate_pdf_content(pdf_bytes, target_language)
    rendered = _render_translated_pdf(pages)

    filename = gcs_input_uri.rsplit("/", 1)[-1]
    base, _, ext = filename.rpartition(".")
    new_name = f"{base}.gemini.{ext or 'pdf'}"
    output_uri = f"{output_prefix}{new_name}"
    _upload_pdf(output_uri, rendered)
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

    # Read once: we need page count to decide chunking and the bytes to
    # construct chunks if needed. For a small PDF this is cheap; for a 100MB
    # PDF it's still well under Cloud Run's memory cap.
    pdf_bytes = _download_pdf(gcs_input_uri)
    with fitz.open(stream=pdf_bytes, filetype="pdf") as src:
        page_count = len(src)
    logger.info(
        "translate_pdf: %d pages, max_per_call=%d → %s",
        page_count, _GOOGLE_TRANSLATE_MAX_PAGES,
        "single-call" if page_count <= _GOOGLE_TRANSLATE_MAX_PAGES else "chunked",
    )

    # Step 1: produce a Google-translated PDF (chunked when oversized)
    if page_count <= _GOOGLE_TRANSLATE_MAX_PAGES:
        google_uri = _translate_single(
            gcs_input_uri, output_prefix, source_language, target_language,
        )
    else:
        google_uri = _translate_chunked(
            pdf_bytes, gcs_input_uri, output_prefix,
            source_language, target_language,
        )

    # Step 2: verify translation by sending BOTH PDFs to Gemini
    if google_uri:
        translated_bytes = _download_pdf(google_uri)
        if verify_translation(pdf_bytes, translated_bytes, target_language):
            return google_uri  # ✅ Gemini confirms translation succeeded

    # Step 3: last-resort fallback — Gemini reads the original PDF and
    # renders a new one. Used only when Google produced a copy of the input
    # (font-rendered Arabic with no real text layer is the common case).
    logger.warning(
        "translate_pdf: Google output did not verify as %s, falling back to Gemini",
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
    before = datetime.datetime.now(datetime.timezone.utc)
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
    job_id = uuid.uuid4().hex[:12]
    chunk_prefix = f"{output_prefix}_chunks_{job_id}/"
    chunk_bucket, _ = _parse_gs(chunk_prefix)
    bucket = _storage_client.bucket(chunk_bucket)

    translated_chunk_bytes: list[bytes] = []
    temp_blobs: list[str] = []  # paths under chunk_bucket to delete at the end

    try:
        with fitz.open(stream=pdf_bytes, filetype="pdf") as src:
            n_pages = len(src)
            for chunk_idx, start in enumerate(range(0, n_pages, _GOOGLE_TRANSLATE_MAX_PAGES)):
                end = min(start + _GOOGLE_TRANSLATE_MAX_PAGES, n_pages) - 1

                # Slice [start..end] into a fresh PDF document.
                chunk_doc = fitz.open()
                chunk_doc.insert_pdf(src, from_page=start, to_page=end)
                chunk_pdf_bytes = chunk_doc.tobytes()
                chunk_doc.close()

                # Upload the chunk under its own prefix so the translated
                # output blob can be discovered without ambiguity.
                this_prefix = f"{chunk_prefix}{chunk_idx:03d}/"
                input_blob_path = f"{this_prefix}input.pdf"
                input_uri = f"gs://{chunk_bucket}/{input_blob_path}"
                bucket.blob(input_blob_path).upload_from_string(
                    chunk_pdf_bytes, content_type="application/pdf",
                )
                temp_blobs.append(input_blob_path)

                logger.info(
                    "translate_chunked: chunk=%d pages=%d-%d uri=%s",
                    chunk_idx, start + 1, end + 1, input_uri,
                )

                # Translate the chunk.
                translated_chunk_uri = _translate_single(
                    input_uri, this_prefix, source_language, target_language,
                )
                if not translated_chunk_uri:
                    raise RuntimeError(
                        f"chunk {chunk_idx} ({start+1}-{end+1}): Google produced no output"
                    )

                # Track translated blob for cleanup.
                _, translated_blob_path = _parse_gs(translated_chunk_uri)
                temp_blobs.append(translated_blob_path)

                # Buffer translated chunk bytes for merging.
                translated_chunk_bytes.append(_download_pdf(translated_chunk_uri))

        # Merge translated chunks in order into a single PDF.
        merged = fitz.open()
        for chunk_pdf in translated_chunk_bytes:
            with fitz.open(stream=chunk_pdf, filetype="pdf") as part:
                merged.insert_pdf(part)
        merged_bytes = merged.tobytes()
        merged.close()

        # Final upload — use the original filename so downstream code can
        # find the translated PDF by predictable suffix.
        original_name = gcs_input_uri.rsplit("/", 1)[-1]
        base, _, ext = original_name.rpartition(".")
        final_name = f"{base}.{target_language}.{ext or 'pdf'}"
        final_uri = f"{output_prefix}{final_name}"
        _upload_pdf(final_uri, merged_bytes)
        logger.info(
            "translate_chunked: merged %d chunks -> %s (%d bytes)",
            len(translated_chunk_bytes), final_uri, len(merged_bytes),
        )
        return final_uri

    finally:
        # Cleanup temp chunk files (best-effort; ignore individual failures
        # so a half-broken cleanup never masks a successful translation).
        for blob_path in temp_blobs:
            try:
                bucket.blob(blob_path).delete()
            except Exception as exc:
                logger.debug("translate_chunked: temp cleanup failed for %s: %s", blob_path, exc)
