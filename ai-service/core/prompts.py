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
    "Return a JSON object with these fields:\n"
    "  text: full transcription of ALL visible text, preserving the original "
    "language and script.\n"
    "  visual_elements: list of detailed descriptions for every chart, graph, "
    "table, or image (capture data, trends, labels, key takeaways). "
    "Empty list if none.\n"
    "  section_title: the most prominent heading on the page (e.g. chapter or "
    "section title), or an empty string if none is visible.\n"
    "  page_type: one of 'cover', 'toc', 'text', 'table', 'chart', 'mixed', "
    "or 'empty'. Pick the dominant content type.\n"
    "  language: BCP-47 short tag for the dominant text language: 'ar', 'en', "
    "or 'mixed'."
)

# JSON schema for Gemini's structured output. Required fields keep the response
# stable so downstream code can index without defensive checks.
PAGE_DESCRIPTION_SCHEMA = {
    "type": "object",
    "properties": {
        "text": {"type": "string"},
        "visual_elements": {"type": "array", "items": {"type": "string"}},
        "section_title": {"type": "string"},
        "page_type": {"type": "string"},
        "language": {"type": "string"},
    },
    "required": ["text", "visual_elements", "section_title", "page_type", "language"],
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

# ISO 639-1 → human-readable name. Gemini honours the language-name directive
# more reliably than the code, especially when the source text is mixed
# Arabic/English chrome (header/footer in EN, body in AR) and the soft
# "respond in the same language" hint isn't enough.
_LANGUAGE_NAMES = {"ar": "Arabic", "en": "English"}


def _language_name(code: str | None) -> str:
    """Map a 2-letter language code to an English name. Falls back to Arabic
    because that's the project's default — Saudi-market-focused content with
    occasional English uploads."""
    return _LANGUAGE_NAMES.get((code or "").lower(), "Arabic")


def summarize_prompt(combined_text: str, language: str = "ar") -> str:
    """Combined summary + insights prompt — one Gemini call returns 4 fields:
    summary, key_findings, topics, indicators.

    `language` is the report's OriginalLanguage (ISO code) — used to pin
    the output language with a hard directive instead of trusting Gemini's
    own language detection on mixed text."""
    lang_name = _language_name(language)
    return (
        f"You are analyzing a research report. The output language is {lang_name}.\n"
        "Based on the full text below, produce a JSON object with these fields:\n"
        "\n"
        "- summary: a list of 5 to 7 concise bullet points capturing the report's "
        "main takeaways. Each item is a single self-contained sentence, maximum "
        "25 words. Always produce at least 5 items; use 7 only when the material "
        "genuinely needs that breadth — do not pad.\n"
        "- key_findings: 5 to 8 most important findings as a list of strings, "
        "maximum 30 words each.\n"
        "- topics: main topics/sectors covered as a list of 5 to 10 short labels, "
        "maximum 5 words each.\n"
        "- indicators: up to 15 of the most important quantitative figures "
        "(headline totals and key breakdowns only — skip granular row-level data). "
        "Each item: name (max 10 words), value (numeric), unit (max 5 words, if any), "
        "time_period (max 10 words, if any), context (max 10 words, if any).\n"
        "\n"
        f"CRITICAL — output language: every string value in the JSON MUST be "
        f"written in {lang_name}. Do not mix languages. Numeric values stay "
        f"numeric; only prose and labels are translated.\n"
        f"Output JSON only — no commentary, no markdown fences.\n\n"
        f"REPORT TEXT:\n{combined_text}"
    )


REPORT_SUMMARY_SCHEMA = {
    "type": "object",
    "properties": {
        "summary": {
            "type": "array",
            "items": {"type": "string"},
            "minItems": 5,
            "maxItems": 7,
        },
        "key_findings": {"type": "array", "items": {"type": "string"}},
        "topics":       {"type": "array", "items": {"type": "string"}},
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
    },
    "required": ["summary", "key_findings", "topics", "indicators"],
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

def compare_prompt(reports_section: str, language: str = "ar") -> str:
    """`language` is the chosen output language (ISO code). The caller decides
    the rule (typically: same language for all reports → that language; mixed
    → Arabic) and we just enforce it on Gemini with a hard directive."""
    lang_name = _language_name(language)
    return (
        f"You are comparing multiple research reports. The output language is {lang_name}.\n"
        "Each report below is labelled [Report N] with its summary and key findings. "
        "Produce a structured comparison:\n\n"
        "- common_topics: themes/sectors that appear in two or more reports.\n"
        "- key_differences: notable disagreements or differing emphases. Each item should "
        "say which reports diverge and how.\n"
        "- shared_indicators: quantitative indicators that appear in two or more reports, "
        "with the values per report so the reader can compare directly.\n"
        "- overall_summary: 2-3 sentences summarizing the relationship between the reports.\n\n"
        f"CRITICAL — output language: every string value (common_topics entries, "
        f"key_differences entries, shared_indicator names/units, and overall_summary) "
        f"MUST be written in {lang_name}. Do not mix languages. Output JSON only.\n\n"
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
