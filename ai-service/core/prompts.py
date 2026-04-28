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


# ── Insights (indicators + trends) ───────────────────────────────────────────

def insights_prompt(combined_text: str) -> str:
    return (
        "You are analyzing a research report. Extract two things from the full text:\n"
        "1. INDICATORS — concrete quantitative figures with their unit and time/place context. "
        "Each indicator must include: name, value, unit (if any), time_period (if any), "
        "context (country/sector/scope if any).\n"
        "2. TRENDS — qualitative directional patterns over time. Each trend must include: "
        "topic, direction (one of: increasing/decreasing/stable/volatile/mixed), time_span, "
        "magnitude (e.g. 'from 5% to 2%'), and a 1-2 sentence explanation.\n\n"
        "Respond in the same language as the report text.\n"
        "Output JSON only — no commentary.\n\n"
        f"REPORT TEXT:\n{combined_text}"
    )


INSIGHTS_SCHEMA = {
    "type": "object",
    "properties": {
        "indicators": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "name":        {"type": "string"},
                    "value":       {"type": "string"},
                    "unit":        {"type": "string"},
                    "time_period": {"type": "string"},
                    "context":     {"type": "string"},
                },
                "required": ["name", "value"],
            },
        },
        "trends": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "topic":       {"type": "string"},
                    "direction":   {"type": "string"},
                    "time_span":   {"type": "string"},
                    "magnitude":   {"type": "string"},
                    "explanation": {"type": "string"},
                },
                "required": ["topic", "direction"],
            },
        },
    },
    "required": ["indicators", "trends"],
}


# ── Comparison (multi-report) ────────────────────────────────────────────────

def compare_prompt(reports_section: str) -> str:
    return (
        "You are comparing multiple research reports. Each report below is labelled "
        "[Report N] with its summary and key findings. Produce a structured comparison:\n\n"
        "- common_topics: themes/sectors that appear in two or more reports.\n"
        "- key_differences: notable disagreements or differing emphases. Each item should "
        "say which reports diverge and how.\n"
        "- shared_indicators: quantitative indicators that appear in two or more reports, "
        "with the values per report so the reader can compare directly.\n"
        "- overall_summary: 2-3 sentences summarizing the relationship between the reports.\n\n"
        "Respond in the language used in the report summaries. Output JSON only.\n\n"
        f"REPORTS:\n{reports_section}"
    )


COMPARE_SCHEMA = {
    "type": "object",
    "properties": {
        "common_topics":    {"type": "array", "items": {"type": "string"}},
        "key_differences":  {"type": "array", "items": {"type": "string"}},
        "shared_indicators": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "name":             {"type": "string"},
                    "values_per_report": {"type": "string"},
                },
                "required": ["name", "values_per_report"],
            },
        },
        "overall_summary":  {"type": "string"},
    },
    "required": ["common_topics", "key_differences", "shared_indicators", "overall_summary"],
}
