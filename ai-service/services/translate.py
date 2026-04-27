"""PDF document translation using Google Cloud Translation API v3.

Sends the original PDF from GCS → Google Translate → saves translated PDF back to GCS.
The output PDF preserves the exact layout, fonts, images, and structure of the original.
"""
from google.cloud import translate_v3

from core.config import settings

_client = None
_parent = None


def _init():
    global _client, _parent
    if _client is None:
        _client = translate_v3.TranslationServiceClient()
        _parent = f"projects/{settings.gcp_project_id}/locations/{settings.translate_location}"


def detect_language(text: str) -> str:
    """Detect the language of a text snippet. Returns a BCP-47 language code e.g. 'ar'."""
    _init()
    response = _client.detect_language(
        request={"parent": _parent, "content": text[:1000], "mime_type": "text/plain"}
    )
    return response.languages[0].language_code if response.languages else "ar"


def translate_pdf(
    gcs_input_uri: str,    # gs://bucket/reports/{report_id}/original.pdf
    output_prefix: str,    # gs://bucket/reports/{report_id}/translated/en/
    source_language: str,  # detected BCP-47 code e.g. "ar"
    target_language: str,  # flipped BCP-47 code e.g. "en"
) -> str:
    """Translate a PDF stored in GCS. Returns the exact GCS URI of the translated PDF.

    Google appends the original filename to output_prefix, so:
      output_prefix = gs://bucket/reports/{id}/translated/en/
      result        = gs://bucket/reports/{id}/translated/en/original.pdf
    """
    _init()
    _client.translate_document(
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
            # OCR helpers — improve translation quality on scanned / image-rendered PDFs
            # (e.g. older Arabic government forms where text is rendered as paths, not chars).
            "enable_rotation_correction": True,
            "enable_shadow_removal_native_pdf": True,
        }
    )
    # The response from translate_document with a GCS destination does not echo
    # back the output path, but Google writes to: output_prefix + source_filename
    filename = gcs_input_uri.rsplit("/", 1)[-1]
    return f"{output_prefix}{filename}"
