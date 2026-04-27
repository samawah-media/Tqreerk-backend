"""PDF document translation using Google Cloud Translation API v3.

Pipeline (always-on OCR preprocess):
  1. Download PDF from GCS.
  2. Run OCRmyPDF (Tesseract Arabic+English) — adds a real Unicode text layer.
     This handles path-rendered Arabic government forms, scanned PDFs, and
     guarantees Google Translate can read the text on every input.
  3. Upload the OCR'd PDF to a sibling GCS path.
  4. Send the OCR'd PDF to Google Translate Document Translation.
  5. Return the translated GCS URI.

Trade-off: ~30–60 sec extra per PDF for the OCR step, and the output PDF
loses the original font styling (text is rendered via Tesseract's text layer).
The win is reliability — every PDF translates successfully.
"""
import os
import tempfile

import ocrmypdf
from google.cloud import storage, translate_v3

from core.config import settings

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
    """Detect the language of a text snippet. Returns a BCP-47 language code e.g. 'ar'."""
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


# ── OCR preprocessing ────────────────────────────────────────────────────────

def _preprocess_with_ocr(input_gcs_uri: str) -> str:
    """Download → run OCRmyPDF (Arabic+English) → upload to a sibling path.

    Returns the gs:// URI of the OCR-processed PDF, ready to send to Translate.
    """
    pdf_bytes = _download_pdf(input_gcs_uri)

    in_fd, in_path = tempfile.mkstemp(suffix=".pdf")
    out_fd, out_path = tempfile.mkstemp(suffix=".pdf")
    os.close(in_fd)
    os.close(out_fd)

    try:
        with open(in_path, "wb") as f:
            f.write(pdf_bytes)

        # force_ocr=True: re-OCR even pages that already have a text layer
        # (path-rendered PDFs have garbage text layers that need replacing)
        ocrmypdf.ocr(
            in_path, out_path,
            language="ara+eng",
            force_ocr=True,
            deskew=True,
            rotate_pages=True,
            clean=True,
            progress_bar=False,
        )

        with open(out_path, "rb") as f:
            ocr_bytes = f.read()
    finally:
        for p in (in_path, out_path):
            try:
                os.unlink(p)
            except OSError:
                pass

    # Save next to the original: .../original.pdf → .../original.ocr.pdf
    bucket, path = _parse_gs(input_gcs_uri)
    base, _, ext = path.rpartition(".")
    ocr_uri = f"gs://{bucket}/{base}.ocr.{ext}"
    _upload_pdf(ocr_uri, ocr_bytes)
    return ocr_uri


# ── Public translate API ─────────────────────────────────────────────────────

def translate_pdf(
    gcs_input_uri: str,    # gs://bucket/reports/{report_id}/original.pdf
    output_prefix: str,    # gs://bucket/reports/{report_id}/translated/en/
    source_language: str,
    target_language: str,
) -> str:
    """OCR-preprocess a PDF then translate it. Returns the GCS URI of the translated PDF."""
    _init()

    # Step 1: always preprocess with OCR — guarantees a usable text layer
    ocr_uri = _preprocess_with_ocr(gcs_input_uri)

    # Step 2: translate the OCR'd PDF
    _translate_client.translate_document(
        request={
            "parent": _parent,
            "source_language_code": source_language,
            "target_language_code": target_language,
            "document_input_config": {
                "gcs_source": {"input_uri": ocr_uri},
                "mime_type": "application/pdf",
            },
            "document_output_config": {
                "gcs_destination": {"output_uri_prefix": output_prefix},
            },
            "enable_rotation_correction": True,
            "enable_shadow_removal_native_pdf": True,
        }
    )

    # Google writes to: output_prefix + filename-of-input
    filename = ocr_uri.rsplit("/", 1)[-1]
    return f"{output_prefix}{filename}"
