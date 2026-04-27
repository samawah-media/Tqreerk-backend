"""PDF document translation using Google Cloud Translation API v3.

Pipeline (always-on Document AI preprocess):
  1. Download the PDF from GCS.
  2. Send to Google Document AI for high-accuracy OCR (much better Arabic
     than Tesseract — handles stylized fonts, government forms, etc.).
  3. Use PyMuPDF to embed an INVISIBLE Unicode text layer at each token's
     bounding box, producing a "searchable" PDF that Google Translate can read.
  4. Upload the searchable PDF to a sibling GCS path (.ocr.pdf).
  5. Send that PDF to Google Translate Document Translation.
  6. Return the translated GCS URI.

Why always preprocess: native PDFs already have a text layer and Document AI
agrees with it cheaply; path-rendered/scanned PDFs absolutely need it. Doing it
on every call keeps the pipeline simple and predictable.
"""
import os
import tempfile

import fitz  # PyMuPDF
from google.cloud import documentai_v1 as documentai
from google.cloud import storage, translate_v3

from core.config import settings

_translate_client: translate_v3.TranslationServiceClient | None = None
_storage_client: storage.Client | None = None
_docai_client: documentai.DocumentProcessorServiceClient | None = None
_parent: str | None = None
_docai_processor: str | None = None


def _init():
    global _translate_client, _storage_client, _docai_client, _parent, _docai_processor
    if _translate_client is None:
        _translate_client = translate_v3.TranslationServiceClient()
        _storage_client = storage.Client()
        _docai_client = documentai.DocumentProcessorServiceClient()
        _parent = f"projects/{settings.gcp_project_id}/locations/{settings.translate_location}"
        _docai_processor = (
            f"projects/{settings.gcp_project_id}"
            f"/locations/{settings.document_ai_location}"
            f"/processors/{settings.document_ai_processor_id}"
        )


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


# ── Document AI OCR + invisible text layer ──────────────────────────────────

def _layout_text(full_text: str, layout) -> str:
    """Pull the substring(s) from document.text that this layout points at."""
    if not layout.text_anchor.text_segments:
        return ""
    parts = []
    for seg in layout.text_anchor.text_segments:
        start = int(seg.start_index) if seg.start_index else 0
        end = int(seg.end_index) if seg.end_index else len(full_text)
        parts.append(full_text[start:end])
    return "".join(parts).strip()


def _preprocess_with_ocr(input_gcs_uri: str) -> str:
    """Run Document AI OCR, embed an invisible text layer with PyMuPDF,
    upload the searchable PDF to a sibling path. Returns its gs:// URI.
    """
    pdf_bytes = _download_pdf(input_gcs_uri)

    # 1) OCR via Document AI
    raw_doc = documentai.RawDocument(content=pdf_bytes, mime_type="application/pdf")
    request = documentai.ProcessRequest(name=_docai_processor, raw_document=raw_doc)
    result = _docai_client.process_document(request=request)
    document = result.document
    full_text = document.text

    # 2) Embed invisible text layer with PyMuPDF
    pdf = fitz.open(stream=pdf_bytes, filetype="pdf")
    for page_idx, da_page in enumerate(document.pages):
        if page_idx >= pdf.page_count:
            break
        page = pdf[page_idx]
        pw, ph = page.rect.width, page.rect.height

        for token in da_page.tokens:
            text = _layout_text(full_text, token.layout)
            if not text:
                continue
            verts = list(token.layout.bounding_poly.normalized_vertices)
            if len(verts) < 4:
                continue
            # Document AI: top-left origin, normalized 0..1
            x0 = verts[0].x * pw
            y0 = verts[0].y * ph
            y1 = verts[2].y * ph
            font_size = max(4.0, (y1 - y0) * 0.85)
            try:
                page.insert_text(
                    point=(x0, y1),         # PyMuPDF baseline = bottom-left
                    text=text,
                    fontsize=font_size,
                    render_mode=3,          # invisible (still selectable + translatable)
                )
            except Exception:
                # Skip problematic tokens; the rest of the page still gets indexed.
                continue

    # 3) Save and upload
    out_fd, out_path = tempfile.mkstemp(suffix=".pdf")
    os.close(out_fd)
    try:
        pdf.save(out_path, deflate=True, garbage=3)
        pdf.close()
        with open(out_path, "rb") as f:
            ocr_bytes = f.read()
    finally:
        try:
            os.unlink(out_path)
        except OSError:
            pass

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
    """OCR (Document AI) then translate. Returns the GCS URI of the translated PDF."""
    _init()

    # Step 1: always preprocess with Document AI OCR — guarantees a usable text layer
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
