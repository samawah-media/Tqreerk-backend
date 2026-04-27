"""Gemini wrapper supporting both AI Studio (API key) and Vertex AI (ADC).

Auth selection:
  - If GEMINI_API_KEY is set in the environment → use AI Studio with the key
  - Otherwise → use Vertex AI with Application Default Credentials

Both modes go through the unified `google-genai` SDK so the rest of the code
is identical regardless of which auth path is active.
"""
import json

from google import genai
from google.genai import types
from pydantic import BaseModel

from core.config import settings

_initialized = False
_client: genai.Client | None = None
_mode: str = ""  # "api_key" or "vertex" — set during _init for diagnostics


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


# ── Structured output schemas (JSON-Schema dicts) ────────────────────────────

PAGE_DESCRIPTION_SCHEMA = {
    "type": "object",
    "properties": {
        "text": {"type": "string"},
        "visual_elements": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["text", "visual_elements"],
}

REPORT_SUMMARY_SCHEMA = {
    "type": "object",
    "properties": {
        "summary": {"type": "string"},
        "key_findings": {"type": "array", "items": {"type": "string"}},
        "topics": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["summary", "key_findings", "topics"],
}


class ReportSummary(BaseModel):
    summary: str
    key_findings: list[str]
    topics: list[str]


# ── Public functions ─────────────────────────────────────────────────────────

def describe_page_image(png_bytes: bytes) -> str:
    """Send a PDF page rendered as PNG to Gemini; returns combined text for embedding."""
    _init()
    image_part = types.Part.from_bytes(data=png_bytes, mime_type="image/png")
    prompt = (
        "You are processing a page from a research report.\n"
        "Return a JSON object with two fields:\n"
        "  text: full transcription of ALL visible text, preserving the original language and script.\n"
        "  visual_elements: list of detailed descriptions for every chart, graph, table, or image "
        "(capture data, trends, labels, key takeaways). Empty list if none."
    )
    response = _client.models.generate_content(
        model="gemini-1.5-flash",
        contents=[prompt, image_part],
        config=types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema=PAGE_DESCRIPTION_SCHEMA,
        ),
    )
    parsed = json.loads(response.text)
    parts = [parsed.get("text", "")] + parsed.get("visual_elements", [])
    return "\n\n".join(p for p in parts if p)


def embed_text(text: str) -> list[float]:
    """Return a 768-dimensional embedding for the given text."""
    _init()
    response = _client.models.embed_content(
        model="text-embedding-004",
        contents=text,
    )
    return response.embeddings[0].values


def chat_with_context(
    history: list[dict],
    user_message: str,
    context_chunks: list[str],
    source_pages: list[int],
) -> tuple[str, list[int]]:
    """Answer a user question given retrieved page chunks."""
    _init()
    context_text = "\n\n---\n\n".join(context_chunks)
    system_prompt = (
        "You are an AI assistant for the Taqreerk platform. "
        "Answer ONLY based on the provided report context. "
        "If the answer is not in the context, say so clearly. "
        "Respond in the same language the user used.\n\n"
        f"REPORT CONTEXT:\n{context_text}"
    )

    contents = []
    for msg in history:
        role = "user" if msg["role"] == "user" else "model"
        contents.append({"role": role, "parts": [{"text": msg["content"]}]})

    full_message = f"{system_prompt}\n\nUSER QUESTION: {user_message}"
    contents.append({"role": "user", "parts": [{"text": full_message}]})

    response = _client.models.generate_content(
        model="gemini-1.5-flash",
        contents=contents,
    )
    return response.text.strip(), source_pages


def summarize_report(pages_content: list[str]) -> ReportSummary:
    """Generate a structured summary + key findings for a full report."""
    _init()
    combined = "\n\n".join(f"[Page {i+1}]\n{c}" for i, c in enumerate(pages_content))
    prompt = (
        "You are analyzing a research report. Based on the full text below, produce:\n"
        "- summary: a concise executive summary (3-5 paragraphs).\n"
        "- key_findings: 5-10 most important findings as a list.\n"
        "- topics: main topics/sectors covered as a list.\n"
        "Respond in the same language as the report text.\n\n"
        f"REPORT TEXT:\n{combined}"
    )
    response = _client.models.generate_content(
        model="gemini-1.5-flash",
        contents=prompt,
        config=types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema=REPORT_SUMMARY_SCHEMA,
        ),
    )
    return ReportSummary.model_validate_json(response.text)
