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


def translate_pdf(
    gcs_input_uri: str,    # gs://bucket/reports/{report_id}/original.pdf
    output_prefix: str,    # gs://bucket/reports/{report_id}/translated/en/  — caller controls this
    target_language: str,  # "en" | "ar" | "fr" etc.
    source_language: str = "ar",
) -> str:
    """Translate a PDF stored in GCS. Returns the exact GCS URI of the translated PDF.

    Google appends the original filename to output_prefix, so:
      output_prefix = gs://bucket/reports/{id}/translated/en/
      result        = gs://bucket/reports/{id}/translated/en/original.pdf
    """
    _init()
    response = _client.translate_document(
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
        }
    )

    return response.document_translation.translated_documents[0].gcs_output_uri
