"""Thin wrapper around the Gemini Flash API.

Authentication uses Application Default Credentials (ADC) — no API key needed.
On Cloud Run the service account credentials are picked up automatically.
"""
import base64

import google.generativeai as genai
import google.auth
import google.auth.transport.requests

# Use ADC — picks up the Cloud Run service account automatically
_credentials, _ = google.auth.default(
    scopes=["https://www.googleapis.com/auth/cloud-platform"]
)
genai.configure(credentials=_credentials)

_flash = genai.GenerativeModel("gemini-1.5-flash")
_embed_model = "models/text-embedding-004"


def describe_page_image(png_bytes: bytes) -> str:
    """Send a PDF page rendered as PNG to Gemini and get rich text back.

    The prompt instructs the model to transcribe text verbatim AND describe
    any charts/graphs/images in detail so they are searchable later.
    """
    image_part = {"mime_type": "image/png", "data": base64.b64encode(png_bytes).decode()}
    prompt = (
        "You are processing a page from an Arabic research report. "
        "Do two things in order:\n"
        "1. Transcribe ALL visible text exactly as written (preserve Arabic).\n"
        "2. For every chart, graph, table, or image on this page, write a detailed "
        "textual description that captures the data, trends, labels, and key takeaways. "
        "Label each description clearly, e.g. 'Chart: ...' or 'Table: ...'.\n"
        "Output plain text only — no markdown, no extra commentary."
    )
    response = _flash.generate_content([prompt, image_part])
    return response.text.strip()


def embed_text(text: str) -> list[float]:
    """Return a 768-dimensional embedding for the given text."""
    result = genai.embed_content(model=_embed_model, content=text)
    return result["embedding"]


def chat_with_context(
    history: list[dict],
    user_message: str,
    context_chunks: list[str],
    source_pages: list[int],
) -> tuple[str, list[int]]:
    """Answer a user question given retrieved page chunks.

    Returns (answer_text, source_page_numbers).
    """
    context_text = "\n\n---\n\n".join(context_chunks)
    system_prompt = (
        "You are an AI assistant for the Taqreerk platform. "
        "Answer ONLY based on the provided report context. "
        "If the answer is not in the context, say so clearly. "
        "Respond in the same language the user used.\n\n"
        f"REPORT CONTEXT:\n{context_text}"
    )

    # Build Gemini conversation history format
    gemini_history = []
    for msg in history:
        role = "user" if msg["role"] == "user" else "model"
        gemini_history.append({"role": role, "parts": [msg["content"]]})

    chat = _flash.start_chat(history=gemini_history)
    full_message = f"{system_prompt}\n\nUSER QUESTION: {user_message}"
    response = chat.send_message(full_message)
    return response.text.strip(), source_pages


def translate_text(text: str, target_language: str) -> str:
    """Translate text to the target language using Gemini Flash."""
    prompt = (
        f"Translate the following text to {target_language}. "
        "Return only the translated text, no explanations.\n\n"
        f"{text}"
    )
    response = _flash.generate_content(prompt)
    return response.text.strip()


def summarize_report(pages_content: list[str]) -> dict:
    """Generate a structured summary + key findings for a full report."""
    combined = "\n\n".join(f"[Page {i+1}]\n{c}" for i, c in enumerate(pages_content))
    prompt = (
        "You are analyzing an Arabic research report. Based on the full text below, produce:\n"
        "1. A concise executive summary (3-5 paragraphs).\n"
        "2. A numbered list of the 5-10 most important key findings.\n"
        "3. A list of the main topics/sectors covered.\n\n"
        "Format your response as JSON with keys: summary, key_findings (list), topics (list).\n\n"
        f"REPORT TEXT:\n{combined}"
    )
    response = _flash.generate_content(prompt)
    import json, re
    text = response.text.strip()
    # Strip markdown code fences if present
    text = re.sub(r"^```(?:json)?\s*", "", text)
    text = re.sub(r"\s*```$", "", text)
    return json.loads(text)
