"""PDF document translation with Gemini verification + fallback.

Pipeline:
  1. Try Google Cloud Translation v3 Document Translation directly
     (with rotation_correction + shadow_removal flags).
  2. Find the new PDF Google created in the output prefix (filename pattern
     varies — we list and pick the freshly created blob).
  3. Send BOTH the original and the Google-translated PDF to Gemini, asking
     whether the second is actually translated into the target language.
     This catches the path-rendered case where Google produces a near-copy of
     the input (size barely changes, so a size check is unreliable).
  4. If Gemini says "not translated", fall back: send the original PDF to
     Gemini → receive translated text per page → render a new PDF + upload.
  5. Return the URI of whichever translated PDF the pipeline produced.
"""
import datetime

import fitz  # PyMuPDF
from google.cloud import storage, translate_v3

from core.config import settings
from services.gemini import translate_pdf_content, verify_translation

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
    """Translate a PDF stored in GCS. Falls back to Gemini if Google Translate
    returns a copy of the input.
    """
    _init()

    # Step 1: direct Google Translate Document Translation
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

    # Step 2: discover the file Google actually wrote (filename varies)
    google_uri = _find_new_pdf_under(output_prefix, before)

    # Step 3: verify translation by sending BOTH PDFs to Gemini
    if google_uri:
        original_bytes = _download_pdf(gcs_input_uri)
        translated_bytes = _download_pdf(google_uri)
        if verify_translation(original_bytes, translated_bytes, target_language):
            return google_uri  # ✅ Gemini confirms translation succeeded

    # Step 4: fallback — Gemini reads the original PDF and renders a new one
    return _gemini_fallback(gcs_input_uri, output_prefix, target_language)
