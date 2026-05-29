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
from typing import Any

from google import genai
from google.genai import types
from pydantic import BaseModel, Field

from core import prompts
from core.config import settings
from services import observability as obs

logger = logging.getLogger(__name__)


# ── Langfuse cost tracking helpers ──────────────────────────────────────────
# Direct google-genai SDK calls don't flow through LangChain's CallbackHandler,
# so without these helpers their token usage never reaches Langfuse and the
# cost dashboard undercounts the bill. We extract usage_metadata from each
# response and emit a stand-alone Langfuse generation event. Failures are
# swallowed inside obs.record_generation, so a Langfuse outage never breaks
# the user request.

def _extract_usage(response: Any) -> dict | None:
    """Pull token counts from a google-genai response into the dict shape
    Langfuse 2.x expects. Returns None when the SDK didn't include usage,
    which makes the caller skip the recording rather than emit zeros that
    would skew dashboards."""
    md = getattr(response, "usage_metadata", None)
    if md is None:
        return None
    in_tokens = getattr(md, "prompt_token_count", None)
    out_tokens = getattr(md, "candidates_token_count", None)
    total = getattr(md, "total_token_count", None)
    if in_tokens is None and out_tokens is None and total is None:
        return None
    in_tokens = in_tokens or 0
    out_tokens = out_tokens or 0
    return {
        "input": in_tokens,
        "output": out_tokens,
        "total": total if total is not None else (in_tokens + out_tokens),
        "unit": "TOKENS",
    }


def _log_genai_usage(
    response: Any,
    *,
    name: str,
    model: str,
    metadata: dict | None = None,
) -> None:
    """Record a Langfuse generation event for a google-genai response. No-op
    when usage is missing or Langfuse is disabled."""
    usage = _extract_usage(response)
    if usage is None:
        return
    obs.record_generation(
        name=name,
        model=model,
        usage=usage,
        metadata=metadata,
    )

_initialized = False
_client: genai.Client | None = None
_mode: str = ""  # "api_key" | "vertex" — for diagnostics
# Guards mutations to (_initialized, _client). Held only briefly; the actual
# Gemini calls run outside the lock so concurrent embeddings don't serialise.
_client_lock = threading.Lock()


def _build_client() -> genai.Client:
    """Construct a fresh genai client. Caller holds _client_lock."""
    global _mode
    # 300 s per-request timeout. Raised from 120 s because summarize_report
    # sends the full report text as one prompt — on large reports (100+ pages)
    # Vertex AI can take 2-3 min before the first token arrives, causing the
    # 120 s ceiling to fire and waste a retry slot. Embedding and chat calls
    # are always fast (<10 s) so they are unaffected by the higher ceiling.
    # _call_with_retry still rebuilds the client and retries on any timeout,
    # so this is not an "indefinite hang" — it's a 5-min hard cap per attempt.
    # NOTE: google-genai http_options timeout is in MILLISECONDS.
    http_opts = {"timeout": 300_000}
    if settings.gemini_api_key:
        _mode = "api_key"
        logger.info(
            "[gemini] building client mode=api_key (AI Studio)"
        )
        return genai.Client(api_key=settings.gemini_api_key, http_options=http_opts)
    _mode = "vertex"
    logger.info(
        "[gemini] building client mode=vertex project=%s location=%s",
        settings.gcp_project_id, settings.vertex_location,
    )
    return genai.Client(
        vertexai=True,
        project=settings.gcp_project_id,
        location=settings.vertex_location,
        http_options=http_opts,
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


# ── Shared retry wrapper ────────────────────────────────────────────────────

_QUOTA_ERROR_MARKERS = (
    "429", "resource_exhausted", "resource exhausted",
    "quota exceeded", "rate limit",
)


def _is_quota_error(exc: Exception) -> bool:
    """Detect Vertex / Gemini quota errors (HTTP 429 RESOURCE_EXHAUSTED).
    These need >60 s backoff to outlive Vertex's per-minute quota window —
    the conn-error path's 1/2/4/8 s backoff just burns retries inside a
    single window."""
    return any(m in str(exc).lower() for m in _QUOTA_ERROR_MARKERS)


def _is_token_overflow_error(exc: Exception) -> bool:
    """Detect 400 INVALID_ARGUMENT token-limit errors.
    These are deterministic — retrying the same content never helps.
    Surface immediately so the caller can truncate and retry."""
    s = str(exc)
    return "400" in s and "INVALID_ARGUMENT" in s and "token" in s.lower() and "exceeds" in s.lower()


_CONN_ERROR_MARKERS = (
    "ssl", "eof", "connection reset", "broken pipe",
    "remote end closed", "connection aborted", "max retries",
    "timed out", "timeout",
)


def _is_connection_error(exc: Exception) -> bool:
    err = str(exc).lower()
    return any(m in err for m in _CONN_ERROR_MARKERS)


def _call_with_retry(operation: str, fn):
    """Run `fn(client)` with retry on connection-shaped errors.

    Same pattern proven for embed_text:
        - up to 5 attempts with backoff 1s, 2s, 4s, 8s
        - on SSL/EOF/connection errors, atomically swap the cached client so
          the next attempt opens a fresh TLS pool
        - non-connection errors are logged and re-raised on the LAST attempt;
          earlier attempts still retry in case the error is transient

    `fn` receives the genai client snapshot for this attempt — use this exact
    instance for the call AND for `_replace_if_same` on failure to avoid
    racing other threads that may have already replaced it.
    """
    last_exc: Exception | None = None
    max_attempts = 5
    for attempt in range(max_attempts):
        client = get_client()
        try:
            return fn(client)
        except Exception as exc:
            last_exc = exc
            quota_error = _is_quota_error(exc)
            connection_error = _is_connection_error(exc)
            logger.warning(
                "%s attempt %d/%d failed (connection_error=%s, quota_error=%s): %s",
                operation, attempt + 1, max_attempts,
                connection_error, quota_error, exc,
            )
            # Quota errors are deterministic for the current window — retrying
            # in this loop just burns budget. Surface immediately so the
            # higher-level _call_with_model_fallback can switch to a model
            # with a separate quota pool (e.g. Flash → Flash-Lite). Callers
            # that don't use the fallback wrapper see the 429 directly.
            if quota_error:
                raise
            if _is_token_overflow_error(exc):
                raise
            if connection_error:
                _replace_if_same(client)
            if attempt < max_attempts - 1:
                time.sleep(2 ** attempt)
    assert last_exc is not None
    raise last_exc


def _call_with_model_fallback(
    operation: str,
    models: list[str],
    build_call,
):
    """Try `models` in order. Each gets the full _call_with_retry budget.

    On a 429 RESOURCE_EXHAUSTED from the primary, fall through to the next
    model — its quota is independent (e.g. gemini-2.5-flash and
    gemini-2.5-flash-lite live in separate Vertex pools). On non-quota
    errors, surface immediately rather than wasting the fallback budget.

    `build_call(model_name)` returns the lambda used by _call_with_retry.
    The double-lambda pattern lets us bind the model name per attempt
    without redeclaring the lambda body at each call site.
    """
    last_exc: Exception | None = None
    for i, model in enumerate(models):
        is_last = (i == len(models) - 1)
        try:
            response = _call_with_retry(operation, build_call(model))
            _log_genai_usage(response, name=operation, model=model)
            if i > 0:
                logger.info(
                    "[%s] succeeded on fallback model %s after %d primary attempts",
                    operation, model, i,
                )
            return response
        except Exception as exc:
            last_exc = exc
            if _is_quota_error(exc) and not is_last:
                logger.warning(
                    "[%s] quota exhausted on %s; falling back to %s",
                    operation, model, models[i + 1],
                )
                continue
            raise
    assert last_exc is not None
    raise last_exc


class IndicatorItem(BaseModel):
    name: str
    value: str
    unit: str | None = None
    time_period: str | None = None
    context: str | None = None


class ReportSummary(BaseModel):
    """Combined summarization + insights output. One Gemini call populates
    every report_ai_contents column (summary, key_findings, topics,
    indicators) so the C# finalizer can copy them all in one pass.

    trends removed: stored in DB but never returned by any API endpoint —
    was spending tokens/time for nothing.

    maxItems works on list[str] (primitive arrays); not supported on
    list[object] arrays by Gemini — those are capped via the prompt."""
    summary:      list[str]           = Field(min_length=5, max_length=7)
    key_findings: list[str]           = Field(max_length=8)
    topics:       list[str]           = Field(max_length=10)
    indicators:   list[IndicatorItem] = Field(default=[])


# ── Plain text completion (used by Ragas judge) ─────────────────────────────

def simple_completion(
    prompt: str,
    temperature: float = 0.0,
    model: str | None = None,
) -> str:
    """One-shot prompt → completion text. Goes through `_call_with_retry`
    so SSL EOFs and connection resets are handled the same as everywhere
    else. Used by the Ragas eval wrapper to avoid duplicating retry logic.

    `model` defaults to `gemini_chat_model`; Ragas passes
    `ragas_judge_model` so judge calls don't share quota with user chat."""
    chosen = model or settings.gemini_chat_model
    response = _call_with_retry(
        "simple_completion",
        lambda client: client.models.generate_content(
            model=chosen,
            contents=prompt,
            config=types.GenerateContentConfig(temperature=temperature),
        ),
    )
    _log_genai_usage(response, name="simple_completion", model=chosen)
    return (response.text or "").strip()


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
    image_part = types.Part.from_bytes(data=png_bytes, mime_type="image/png")
    response = _call_with_retry(
        "describe_page_image",
        lambda client: client.models.generate_content(
            model=settings.gemini_vision_model,
            contents=[prompts.PAGE_DESCRIPTION, image_part],
            config=types.GenerateContentConfig(
                temperature=0.2,
                response_mime_type="application/json",
                response_schema=prompts.PAGE_DESCRIPTION_SCHEMA,
            ),
        ),
    )
    _log_genai_usage(response, name="describe_page_image", model=settings.gemini_vision_model)
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

    Resilient to SSL EOF / connection-reset errors via _call_with_retry.
    """
    response = _call_with_retry(
        "embed_text",
        lambda client: client.models.embed_content(
            model=settings.gemini_embed_model,
            contents=text,
        ),
    )
    _log_genai_usage(response, name="embed_text", model=settings.gemini_embed_model)
    return response.embeddings[0].values


def chat_with_context(
    history: list[dict],
    user_message: str,
    context_chunks: list[str],
    source_pages: list[int],
) -> tuple[str, list[int]]:
    """Answer a user question given retrieved page chunks (non-streaming)."""
    system_prompt = prompts.chat_system_prompt("\n\n---\n\n".join(context_chunks))

    contents = []
    for msg in history:
        role = "user" if msg["role"] == "user" else "model"
        contents.append({"role": role, "parts": [{"text": msg["content"]}]})

    contents.append({
        "role": "user",
        "parts": [{"text": prompts.chat_user_message(system_prompt, user_message)}],
    })

    response = _call_with_retry(
        "chat_with_context",
        lambda client: client.models.generate_content(
            model=settings.gemini_chat_model,
            contents=contents,
            config=types.GenerateContentConfig(temperature=0.2),
        ),
    )
    _log_genai_usage(response, name="chat_with_context", model=settings.gemini_chat_model)
    return response.text.strip(), source_pages


def chat_with_context_stream(
    history: list[dict],
    user_message: str,
    context_chunks: list[str],
):
    """Same as chat_with_context but yields text chunks as they arrive.

    Use for streaming endpoints (SSE / WebSocket). Yields plain string deltas;
    join them to reconstruct the full answer.

    Retry semantics: we retry only on connection failures that surface BEFORE
    any tokens are emitted (i.e. the stream open itself failed). Once the
    stream has yielded its first token we can't safely restart — partial
    output would already be on the wire. Mid-stream connection drops are
    surfaced to the caller as-is.
    """
    system_prompt = prompts.chat_system_prompt("\n\n---\n\n".join(context_chunks))

    contents = []
    for msg in history:
        role = "user" if msg["role"] == "user" else "model"
        contents.append({"role": role, "parts": [{"text": msg["content"]}]})

    contents.append({
        "role": "user",
        "parts": [{"text": prompts.chat_user_message(system_prompt, user_message)}],
    })

    stream = _call_with_retry(
        "chat_with_context_stream.open",
        lambda client: client.models.generate_content_stream(
            model=settings.gemini_chat_model,
            contents=contents,
            config=types.GenerateContentConfig(temperature=0.2),
        ),
    )
    last_with_usage: Any = None
    for chunk in stream:
        if getattr(chunk, "usage_metadata", None) is not None:
            last_with_usage = chunk
        if chunk.text:
            yield chunk.text
    if last_with_usage is not None:
        _log_genai_usage(
            last_with_usage,
            name="chat_with_context_stream",
            model=settings.gemini_chat_model,
        )


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
    prompt = (
        "Two PDFs are attached. The first is the ORIGINAL document. The second is "
        f"supposed to be its translation into {target_language}.\n\n"
        "Decide: does the second PDF actually contain the document text rendered "
        f"in {target_language}? If the second is essentially a copy of the first "
        "(same source language, no real translation took place), answer false. "
        "Otherwise answer true.\n\n"
        "Return ONLY a JSON object: {\"translated\": boolean}"
    )
    response = _call_with_retry(
        "verify_translation",
        lambda client: client.models.generate_content(
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
        ),
    )
    _log_genai_usage(response, name="verify_translation", model=settings.gemini_summary_model)
    parsed = json.loads(response.text)
    return bool(parsed.get("translated", False))


def translate_pdf_content(pdf_bytes: bytes, target_language: str) -> list[str]:
    """Send a PDF to Gemini and get back the translated text, one entry per page.

    Used as a fallback when Google Translate's Document Translation produces
    a copy of the input (e.g. path-rendered Arabic forms with no real text layer).
    """
    pdf_part = types.Part.from_bytes(data=pdf_bytes, mime_type="application/pdf")
    prompt = (
        f"Translate every page of this PDF to {target_language}. "
        "Return a JSON object with key 'pages': an array of strings, one per page, "
        "in original order. Preserve paragraph breaks within each page. "
        "Output the translation only — no commentary, no explanation."
    )
    response = _call_with_retry(
        "translate_pdf_content",
        lambda client: client.models.generate_content(
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
        ),
    )
    _log_genai_usage(response, name="translate_pdf_content", model=settings.gemini_summary_model)
    parsed = json.loads(response.text)
    return parsed.get("pages", [])


def extract_insights(pages_content: list[str]) -> dict:
    """Extract structured indicators + trends from a report's page content.

    Returns a dict with keys 'indicators' and 'trends'. Each is a list of dicts
    matching the schema in core/prompts.INSIGHTS_SCHEMA.
    """
    combined = "\n\n".join(f"[Page {i+1}]\n{c}" for i, c in enumerate(pages_content))
    response = _call_with_model_fallback(
        operation="extract_insights",
        models=[settings.gemini_summary_model, settings.gemini_summary_model_fallback],
        build_call=lambda model: lambda client: client.models.generate_content(
            model=model,
            contents=prompts.insights_prompt(combined),
            config=types.GenerateContentConfig(
                temperature=0.2,
                response_mime_type="application/json",
                response_schema=prompts.INSIGHTS_SCHEMA,
            ),
        ),
    )
    return json.loads(response.text)


def compare_reports(reports: list[dict], language: str = "ar") -> dict:
    """Compare multiple reports.

    `reports` is a list of {"report_id": "...", "summary": "...", "key_findings": [...]}.
    `language` pins the output language (ISO code); see prompts.compare_prompt.
    Returns a dict matching core/prompts.COMPARE_SCHEMA.
    """
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

    response = _call_with_model_fallback(
        operation="compare_reports",
        models=[settings.gemini_summary_model, settings.gemini_summary_model_fallback],
        build_call=lambda model: lambda client: client.models.generate_content(
            model=model,
            contents=prompts.compare_prompt(reports_section, language=language),
            config=types.GenerateContentConfig(
                temperature=0.2,
                response_mime_type="application/json",
                response_schema=prompts.COMPARE_SCHEMA,
            ),
        ),
    )
    return json.loads(response.text)


def summarize_report(pages_content: list[str], language: str = "ar") -> ReportSummary:
    """Generate a structured summary + key findings for a full report.

    `language` is the report's OriginalLanguage (ISO code), pinning the
    output language with a hard directive in the prompt.

    Quota fallback: if `gemini_summary_model` (Flash) is over quota, we
    automatically retry with `gemini_summary_model_fallback` (Flash-Lite,
    different Vertex quota pool) before surfacing the error.
    """
    def _extract_json_object(text: str) -> str | None:
        """Best-effort JSON extraction when extra prose wraps an object."""
        if not text:
            return None
        start = text.find("{")
        end = text.rfind("}")
        if start == -1 or end == -1 or end <= start:
            return None
        candidate = text[start:end + 1].strip()
        return candidate if candidate.startswith("{") and candidate.endswith("}") else None

    # Start with the full content. If Gemini rejects with a 400 token overflow,
    # halve the char budget and retry — up to 4 halvings before giving up.
    _MAX_CONTENT_CHARS: int | None = None  # None = no limit on first pass
    response = None
    for trunc_pass in range(5):
        page_parts: list[str] = []
        total = 0
        for i, content in enumerate(pages_content):
            part = f"[Page {i + 1}]\n{content}"
            needed = (4 if page_parts else 0) + len(part)
            if _MAX_CONTENT_CHARS is not None and total + needed > _MAX_CONTENT_CHARS:
                logger.warning(
                    "summarize_report: truncated at page %d/%d (pass=%d max_chars=%d)",
                    i, len(pages_content), trunc_pass, _MAX_CONTENT_CHARS,
                )
                break
            page_parts.append(part)
            total += needed
        combined = "\n\n".join(page_parts)
        prompt_text = prompts.summarize_prompt(combined, language=language)
        try:
            response = _call_with_model_fallback(
                operation="summarize_report",
                models=[settings.gemini_summary_model, settings.gemini_summary_model_fallback],
                build_call=lambda model: lambda client: client.models.generate_content(
                    model=model,
                    contents=prompt_text,
                    config=types.GenerateContentConfig(
                        temperature=0.2,
                        max_output_tokens=65535,
                        response_mime_type="application/json",
                        response_schema=ReportSummary,
                    ),
                ),
            )
            break
        except Exception as exc:
            if _is_token_overflow_error(exc) and trunc_pass < 4:
                # Remove 10% of content on each overflow and retry.
                _MAX_CONTENT_CHARS = int((_MAX_CONTENT_CHARS or total) * 0.9)
                logger.warning(
                    "summarize_report: token overflow on pass %d, retrying with max_chars=%d",
                    trunc_pass, _MAX_CONTENT_CHARS,
                )
                continue
            raise
    assert response is not None
    # response.parsed is None when the SDK's internal parsing fails silently.
    # Fall back to manual parse so the error surface is the same as before.
    if response.parsed is not None:
        return response.parsed
    return ReportSummary.model_validate_json(response.text)
