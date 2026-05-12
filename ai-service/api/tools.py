"""Utility AI endpoints — ad-hoc text translation via Gemini."""
import logging

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, Field

from services.gemini import simple_completion

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/tools", tags=["tools"])


class TranslateTextRequest(BaseModel):
    text: str = Field(..., min_length=1, max_length=5000)
    target_language: str = Field(default="en")


class TranslateTextResponse(BaseModel):
    translated_text: str
    target_language: str


@router.post("/translate-text", response_model=TranslateTextResponse)
async def translate_text(body: TranslateTextRequest):
    """Translate a short passage of text using Gemini.

    Designed for the reader selection-toolbar feature: user selects text,
    clicks Translate, frontend POSTs here, result appears in the pop-up bubble.
    temperature=0.2 keeps output deterministic while allowing natural phrasing.
    """
    lang_label = "English" if body.target_language == "en" else body.target_language

    prompt = (
        f"Translate the following text into {lang_label}. "
        "Return only the translated text, with no explanation, no quotes, "
        "and no commentary.\n\n"
        f"{body.text}"
    )

    try:
        import asyncio
        translated = await asyncio.to_thread(
            simple_completion,
            prompt=prompt,
            temperature=0.2,
        )
    except Exception as exc:
        logger.error("[translate-text] Gemini call failed: %s", exc, exc_info=True)
        raise HTTPException(status_code=502, detail="Translation service unavailable") from exc

    if not translated:
        raise HTTPException(status_code=502, detail="Empty response from translation model")

    logger.info(
        "[translate-text] %d chars → %s (%d chars)",
        len(body.text), body.target_language, len(translated),
    )

    return TranslateTextResponse(
        translated_text=translated,
        target_language=body.target_language,
    )
