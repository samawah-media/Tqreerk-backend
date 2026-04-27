"""Centralized prompt library + structured-output schemas.

Keeping prompts here (not inline in services) lets us:
  - Tune wording without touching call sites.
  - Reuse the same prompt across services / variants.
  - Diff prompt history clearly in version control.

Static prompts are module-level constants. Dynamic prompts that interpolate
runtime data are exposed as small builder functions.
"""

# ── Page description (vision) ────────────────────────────────────────────────

PAGE_DESCRIPTION = (
    "You are processing a page from a research report.\n"
    "Return a JSON object with two fields:\n"
    "  text: full transcription of ALL visible text, preserving the original "
    "language and script.\n"
    "  visual_elements: list of detailed descriptions for every chart, graph, "
    "table, or image (capture data, trends, labels, key takeaways). "
    "Empty list if none."
)

PAGE_DESCRIPTION_SCHEMA = {
    "type": "object",
    "properties": {
        "text": {"type": "string"},
        "visual_elements": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["text", "visual_elements"],
}


# ── Chat (RAG) ───────────────────────────────────────────────────────────────

def chat_system_prompt(context_text: str) -> str:
    """Built per-message — embeds the retrieved page chunks for THIS question."""
    return (
        "You are an AI assistant for the Taqreerk platform. "
        "Answer ONLY based on the provided report context. "
        "If the answer is not in the context, say so clearly. "
        "Respond in the same language the user used.\n\n"
        f"REPORT CONTEXT:\n{context_text}"
    )


def chat_user_message(system_prompt: str, user_question: str) -> str:
    """Combine the system prompt + user question into a single user turn."""
    return f"{system_prompt}\n\nUSER QUESTION: {user_question}"


# ── Summarization ────────────────────────────────────────────────────────────

def summarize_prompt(combined_text: str) -> str:
    return (
        "You are analyzing a research report. Based on the full text below, produce:\n"
        "- summary: a concise executive summary (3-5 paragraphs).\n"
        "- key_findings: 5-10 most important findings as a list.\n"
        "- topics: main topics/sectors covered as a list.\n"
        "Respond in the same language as the report text.\n\n"
        f"REPORT TEXT:\n{combined_text}"
    )


REPORT_SUMMARY_SCHEMA = {
    "type": "object",
    "properties": {
        "summary": {"type": "string"},
        "key_findings": {"type": "array", "items": {"type": "string"}},
        "topics": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["summary", "key_findings", "topics"],
}
