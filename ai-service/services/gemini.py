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
import logging
import threading
import time

from google import genai
from google.genai import types
from pydantic import BaseModel

from core import prompts
from core.config import settings

logger = logging.getLogger(__name__)

_initialized = False
_client: genai.Client | None = None
_mode: str = ""  # "api_key" | "vertex" — for diagnostics
# Guards mutations to (_initialized, _client). Held only briefly; the actual
# Gemini calls run outside the lock so concurrent embeddings don't serialise.
_client_lock = threading.Lock()


def _build_client() -> genai.Client:
    """Construct a fresh genai client. Caller holds _client_lock."""
    global _mode
    if settings.gemini_api_key:
        _mode = "api_key"
        logger.info(
            "[gemini] building client mode=api_key (AI Studio)"
        )
        return genai.Client(api_key=settings.gemini_api_key)
    _mode = "vertex"
    logger.info(
        "[gemini] building client mode=vertex project=%s location=%s",
        settings.gcp_project_id, settings.vertex_location,
    )
    return genai.Client(
        vertexai=True,
        project=settings.gcp_project_id,
        location=settings.vertex_location,
    )


def reset_client() -> None:
    """Drop the cached genai client. Next get_client() rebuilds it.

    Call this proactively when you know the connection pool is stale (e.g.
    after a long-running doc-processor extract, where the keep-alive
    connection would otherwise sit idle past the LB timeout)."""
    global _initialized, _client
    with _client_lock:
        _initialized = False
        _client = None


def get_client() -> genai.Client:
    """Return a usable client. Thread-safe — never returns None."""
    global _initialized, _client
    with _client_lock:
        if not _initialized or _client is None:
            _client = _build_client()
            _initialized = True
        return _client


def _replace_if_same(stale: genai.Client) -> None:
    """If the current client is still `stale`, replace it. No-op if another
    thread already replaced it. Avoids the (None) window that would let a
    concurrent caller see `_client = None`."""
    global _initialized, _client
    with _client_lock:
        if _client is stale:
            _client = _build_client()
            _initialized = True


def _init():
    """Back-compat wrapper around get_client() for code that still calls _init()."""
    get_client()


class ReportSummary(BaseModel):
    summary: str
    key_findings: list[str]
    topics: list[str]


# ── Public API ───────────────────────────────────────────────────────────────

_VALID_PAGE_TYPES = {"cover", "toc", "text", "table", "chart", "mixed", "empty"}
_VALID_LANGUAGES = {"ar", "en", "mixed"}


def describe_page_image(png_bytes: bytes) -> dict:
    """Send a PDF page rendered as PNG to Gemini.

    Returns a dict shaped:
        {
          "content": str,               # combined text + visual descriptions
          "metadata": {
              "section_title": str,      # may be ""
              "page_type":     str,      # cover|toc|text|table|chart|mixed|empty
              "language":      str,      # ar|en|mixed
          }
        }
    """
    _init()
    image_part = types.Part.from_bytes(data=png_bytes, mime_type="image/png")
    response = _client.models.generate_content(
        model=settings.gemini_vision_model,
        contents=[prompts.PAGE_DESCRIPTION, image_part],
        config=types.GenerateContentConfig(
            temperature=0.2,
            response_mime_type="application/json",
            response_schema=prompts.PAGE_DESCRIPTION_SCHEMA,
        ),
    )
    parsed = json.loads(response.text)
    parts = [parsed.get("text", "")] + parsed.get("visual_elements", [])
    content = "\n\n".join(p for p in parts if p)

    # Coerce model-returned values to the schema we promised callers. If Gemini
    # returns something off-list (e.g. "diagram"), we fall back to "mixed" rather
    # than poisoning the index with arbitrary strings.
    page_type = (parsed.get("page_type") or "").strip().lower()
    if page_type not in _VALID_PAGE_TYPES:
        page_type = "mixed"

    language = (parsed.get("language") or "").strip().lower()
    if language not in _VALID_LANGUAGES:
        language = "mixed"

    return {
        "content": content,
        "metadata": {
            "section_title": (parsed.get("section_title") or "").strip(),
            "page_type": page_type,
            "language": language,
        },
    }


def embed_text(text: str) -> list[float]:
    """Return a 768-dimensional embedding for the given text.

    Resilient to SSL EOF / connection-reset errors that happen when the
    underlying HTTP keep-alive connection has gone stale (typically after a
    multi-minute doc-processor extract sat between two embedding bursts).
    On any connection-shaped error we drop the cached client so the next
    attempt opens a fresh connection pool, then retry with backoff.
    """
    last_exc = None
    max_attempts = 5
    for attempt in range(max_attempts):
        # Snapshot the current client. We pass this exact instance to the
        # embed call AND to _replace_if_same on failure, so we never mutate
        # state another thread already fixed.
        client = get_client()
        try:
            response = client.models.embed_content(
                model=settings.gemini_embed_model,
                contents=text,
            )
            return response.embeddings[0].values
        except Exception as exc:
            last_exc = exc
            err = str(exc).lower()
            connection_error = any(s in err for s in (
                "ssl", "eof", "connection reset", "broken pipe",
                "remote end closed", "connection aborted", "max retries",
            ))
            logger.warning(
                "embed_text attempt %d/%d failed (connection_error=%s): %s",
                attempt + 1, max_attempts, connection_error, exc,
            )
            if connection_error:
                # Atomically swap the stale client for a fresh one — but only
                # if no other thread has already replaced it. This avoids the
                # transient `_client = None` window that previously caused
                # 'NoneType has no attribute models' in concurrent callers.
                _replace_if_same(client)
            if attempt < max_attempts - 1:
                time.sleep(2 ** attempt)  # 1s, 2s, 4s, 8s
    raise last_exc


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
        config=types.GenerateContentConfig(temperature=0.2),
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
        config=types.GenerateContentConfig(temperature=0.2),
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
            temperature=0.2,
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
            temperature=0.2,
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


def extract_insights(pages_content: list[str]) -> dict:
    """Extract structured indicators + trends from a report's page content.

    Returns a dict with keys 'indicators' and 'trends'. Each is a list of dicts
    matching the schema in core/prompts.INSIGHTS_SCHEMA.
    """
    _init()
    combined = "\n\n".join(f"[Page {i+1}]\n{c}" for i, c in enumerate(pages_content))
    response = _client.models.generate_content(
        model=settings.gemini_summary_model,
        contents=prompts.insights_prompt(combined),
        config=types.GenerateContentConfig(
            temperature=0.2,
            response_mime_type="application/json",
            response_schema=prompts.INSIGHTS_SCHEMA,
        ),
    )
    return json.loads(response.text)


def compare_reports(reports: list[dict]) -> dict:
    """Compare multiple reports.

    `reports` is a list of {"report_id": "...", "summary": "...", "key_findings": [...]}.
    Returns a dict matching core/prompts.COMPARE_SCHEMA.
    """
    _init()
    sections = []
    for i, rep in enumerate(reports, start=1):
        kf = rep.get("key_findings") or []
        kf_block = "\n".join(f"  - {f}" for f in kf) if kf else "  (no key findings)"
        sections.append(
            f"[Report {i}] id={rep['report_id']}\n"
            f"Summary:\n{rep.get('summary') or '(no summary available)'}\n"
            f"Key findings:\n{kf_block}"
        )
    reports_section = "\n\n".join(sections)

    response = _client.models.generate_content(
        model=settings.gemini_summary_model,
        contents=prompts.compare_prompt(reports_section),
        config=types.GenerateContentConfig(
            temperature=0.2,
            response_mime_type="application/json",
            response_schema=prompts.COMPARE_SCHEMA,
        ),
    )
    return json.loads(response.text)


def summarize_report(pages_content: list[str]) -> ReportSummary:
    """Generate a structured summary + key findings for a full report."""
    _init()
    combined = "\n\n".join(f"[Page {i+1}]\n{c}" for i, c in enumerate(pages_content))
    response = _client.models.generate_content(
        model=settings.gemini_summary_model,
        contents=prompts.summarize_prompt(combined),
        config=types.GenerateContentConfig(
            temperature=0.2,
            response_mime_type="application/json",
            response_schema=prompts.REPORT_SUMMARY_SCHEMA,
        ),
    )
    return ReportSummary.model_validate_json(response.text)
