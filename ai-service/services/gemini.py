"""Thin wrapper around the Gemini Flash API.

Authentication uses Application Default Credentials (ADC) — no API key needed.
Clients are initialized lazily on first use so import failures don't block startup.
"""
import base64

import google.auth
import google.generativeai as genai
from pydantic import BaseModel

_initialized = False
_flash = None
_embed_model = "models/text-embedding-004"


def _init():
    global _initialized, _flash
    if _initialized:
        return
    credentials, _ = google.auth.default(
        scopes=["https://www.googleapis.com/auth/cloud-platform"]
    )
    genai.configure(credentials=credentials)
    _flash = genai.GenerativeModel("gemini-1.5-flash")
    _initialized = True


# ── Structured output schemas ────────────────────────────────────────────────

class PageDescription(BaseModel):
    text: str                        # full transcription of visible text
    visual_elements: list[str]       # one entry per chart / table / image


class ReportSummary(BaseModel):
    summary: str
    key_findings: list[str]
    topics: list[str]


# ── Public functions ─────────────────────────────────────────────────────────

def describe_page_image(png_bytes: bytes) -> str:
    """Send a PDF page rendered as PNG to Gemini; returns combined text for embedding."""
    _init()
    image_part = {"mime_type": "image/png", "data": base64.b64encode(png_bytes).decode()}
    prompt = (
        "You are processing a page from a research report.\n"
        "Return a JSON object with two fields:\n"
        "  text: full transcription of ALL visible text, preserving the original language and script.\n"
        "  visual_elements: list of detailed descriptions for every chart, graph, table, or image "
        "(capture data, trends, labels, key takeaways). Empty list if none."
    )
    response = _flash.generate_content(
        [prompt, image_part],
        generation_config=genai.GenerationConfig(
            response_mime_type="application/json",
            response_schema=PageDescription,
        ),
    )
    parsed = PageDescription.model_validate_json(response.text)
    # Combine into a single string for downstream embedding
    parts = [parsed.text] + parsed.visual_elements
    return "\n\n".join(p for p in parts if p)


def embed_text(text: str) -> list[float]:
    """Return a 768-dimensional embedding for the given text."""
    _init()
    result = genai.embed_content(model=_embed_model, content=text)
    return result["embedding"]


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

    gemini_history = []
    for msg in history:
        role = "user" if msg["role"] == "user" else "model"
        gemini_history.append({"role": role, "parts": [msg["content"]]})

    chat = _flash.start_chat(history=gemini_history)
    full_message = f"{system_prompt}\n\nUSER QUESTION: {user_message}"
    response = chat.send_message(full_message)
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
    response = _flash.generate_content(
        prompt,
        generation_config=genai.GenerationConfig(
            response_mime_type="application/json",
            response_schema=ReportSummary,
        ),
    )
    return ReportSummary.model_validate_json(response.text)
