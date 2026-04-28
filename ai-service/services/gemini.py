"""Gemini wrapper — thin client for vision, embedding, chat, summarization.

Concerns split:
  - Auth + client init   → here
  - Model names          → core.config
  - Prompts + schemas    → core.prompts

Auth selection:
  - GEMINI_API_KEY set  → AI Studio
  - otherwise           → Vertex AI via ADC
"""
import json

from google import genai
from google.genai import types
from pydantic import BaseModel

from core import prompts
from core.config import settings

_initialized = False
_client: genai.Client | None = None
_mode: str = ""  # "api_key" | "vertex" — for diagnostics


def _init():
    global _initialized, _client, _mode
    if _initialized:
        return
    if settings.gemini_api_key:
        _client = genai.Client(api_key=settings.gemini_api_key)
        _mode = "api_key"
    else:
        _client = genai.Client(
            vertexai=True,
            project=settings.gcp_project_id,
            location=settings.vertex_location,
        )
        _mode = "vertex"
    _initialized = True


class ReportSummary(BaseModel):
    summary: str
    key_findings: list[str]
    topics: list[str]


# ── Public API ───────────────────────────────────────────────────────────────

def describe_page_image(png_bytes: bytes) -> str:
    """Send a PDF page rendered as PNG to Gemini; returns combined text for embedding."""
    _init()
    image_part = types.Part.from_bytes(data=png_bytes, mime_type="image/png")
    response = _client.models.generate_content(
        model=settings.gemini_vision_model,
        contents=[prompts.PAGE_DESCRIPTION, image_part],
        config=types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema=prompts.PAGE_DESCRIPTION_SCHEMA,
        ),
    )
    parsed = json.loads(response.text)
    parts = [parsed.get("text", "")] + parsed.get("visual_elements", [])
    return "\n\n".join(p for p in parts if p)


def embed_text(text: str) -> list[float]:
    """Return a 768-dimensional embedding for the given text."""
    _init()
    response = _client.models.embed_content(
        model=settings.gemini_embed_model,
        contents=text,
    )
    return response.embeddings[0].values


def chat_with_context(
    history: list[dict],
    user_message: str,
    context_chunks: list[str],
    source_pages: list[int],
) -> tuple[str, list[int]]:
    """Answer a user question given retrieved page chunks (non-streaming)."""
    _init()
    system_prompt = prompts.chat_system_prompt("\n\n---\n\n".join(context_chunks))

    contents = []
    for msg in history:
        role = "user" if msg["role"] == "user" else "model"
        contents.append({"role": role, "parts": [{"text": msg["content"]}]})

    contents.append({
        "role": "user",
        "parts": [{"text": prompts.chat_user_message(system_prompt, user_message)}],
    })

    response = _client.models.generate_content(
        model=settings.gemini_chat_model,
        contents=contents,
    )
    return response.text.strip(), source_pages


def chat_with_context_stream(
    history: list[dict],
    user_message: str,
    context_chunks: list[str],
):
    """Same as chat_with_context but yields text chunks as they arrive.

    Use for streaming endpoints (SSE / WebSocket). Yields plain string deltas;
    join them to reconstruct the full answer.
    """
    _init()
    system_prompt = prompts.chat_system_prompt("\n\n---\n\n".join(context_chunks))

    contents = []
    for msg in history:
        role = "user" if msg["role"] == "user" else "model"
        contents.append({"role": role, "parts": [{"text": msg["content"]}]})

    contents.append({
        "role": "user",
        "parts": [{"text": prompts.chat_user_message(system_prompt, user_message)}],
    })

    stream = _client.models.generate_content_stream(
        model=settings.gemini_chat_model,
        contents=contents,
    )
    for chunk in stream:
        if chunk.text:
            yield chunk.text


def verify_translation(
    original_pdf: bytes,
    translated_pdf: bytes,
    target_language: str,
) -> bool:
    """Send both PDFs to Gemini and ask whether the second is a real translation
    of the first into ``target_language``.

    Returns True if Gemini judges the second PDF to contain the document content
    actually rendered in ``target_language`` — i.e., the translation succeeded.
    Returns False if the second PDF looks like a copy of the first (same source
    language, no real translation happened).
    """
    _init()
    prompt = (
        "Two PDFs are attached. The first is the ORIGINAL document. The second is "
        f"supposed to be its translation into {target_language}.\n\n"
        "Decide: does the second PDF actually contain the document text rendered "
        f"in {target_language}? If the second is essentially a copy of the first "
        "(same source language, no real translation took place), answer false. "
        "Otherwise answer true.\n\n"
        "Return ONLY a JSON object: {\"translated\": boolean}"
    )
    response = _client.models.generate_content(
        model=settings.gemini_summary_model,
        contents=[
            prompt,
            types.Part.from_bytes(data=original_pdf, mime_type="application/pdf"),
            types.Part.from_bytes(data=translated_pdf, mime_type="application/pdf"),
        ],
        config=types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema={
                "type": "object",
                "properties": {"translated": {"type": "boolean"}},
                "required": ["translated"],
            },
        ),
    )
    parsed = json.loads(response.text)
    return bool(parsed.get("translated", False))


def translate_pdf_content(pdf_bytes: bytes, target_language: str) -> list[str]:
    """Send a PDF to Gemini and get back the translated text, one entry per page.

    Used as a fallback when Google Translate's Document Translation produces
    a copy of the input (e.g. path-rendered Arabic forms with no real text layer).
    """
    _init()
    pdf_part = types.Part.from_bytes(data=pdf_bytes, mime_type="application/pdf")
    prompt = (
        f"Translate every page of this PDF to {target_language}. "
        "Return a JSON object with key 'pages': an array of strings, one per page, "
        "in original order. Preserve paragraph breaks within each page. "
        "Output the translation only — no commentary, no explanation."
    )
    response = _client.models.generate_content(
        model=settings.gemini_summary_model,
        contents=[prompt, pdf_part],
        config=types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema={
                "type": "object",
                "properties": {
                    "pages": {"type": "array", "items": {"type": "string"}},
                },
                "required": ["pages"],
            },
        ),
    )
    parsed = json.loads(response.text)
    return parsed.get("pages", [])


def summarize_report(pages_content: list[str]) -> ReportSummary:
    """Generate a structured summary + key findings for a full report."""
    _init()
    combined = "\n\n".join(f"[Page {i+1}]\n{c}" for i, c in enumerate(pages_content))
    response = _client.models.generate_content(
        model=settings.gemini_summary_model,
        contents=prompts.summarize_prompt(combined),
        config=types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema=prompts.REPORT_SUMMARY_SCHEMA,
        ),
    )
    return ReportSummary.model_validate_json(response.text)
