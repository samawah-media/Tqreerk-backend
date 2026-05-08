"""Figure captioning — Vertex Gemini Vision wrapper.

Replaces the previous local Florence-2 implementation 2026-05-08. Reasons:

  • Florence-2's `trust_remote_code` config no longer resolves on
    transformers ≥ 4.50 (Florence2LanguageConfig isn't recognised by
    AutoModelForCausalLM), and we needed transformers 4.50 for the rt_detr_v2
    layout model that ships in docling 2.93.

  • A managed VLM (Gemini 2.5 Flash) gives equal or better caption quality
    than Florence-2 base-ft, doesn't add ~1-3 GB of GPU VRAM, and removes
    a category of cold-start cost. Same trade-off pattern we already use
    for embeddings (moved off bge-m3 → gemini-embedding-001 on 2026-04-30).

API contract is unchanged
=========================
`caption(img)` and `caption_crop(page_img, bbox)` keep their signatures
so the orchestrator doesn't need to change. Failure returns "" so the
caller can still embed the figure region with whatever text EasyOCR
pulled out (axis labels, legends).

Cost model
==========
Each call is one Vertex generate_content invocation with one image part
and a short prompt. Falls into the same per-minute quota as the rest of
gemini_vision_model. Captioning is OFF for ingest by default (see
ai-service/services/doc_extractor.py trigger payload) so this only
fires when an on-demand caller explicitly requests it.
"""
from __future__ import annotations

import io
import logging
import threading
from typing import Optional

import numpy as np
from PIL import Image

from google import genai
from google.genai import types

from core.config import settings

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_client: Optional[genai.Client] = None
_client_lock = threading.Lock()
_init_attempted: bool = False  # True once init() has run

# Long-form caption prompt — research-report figures are usually charts /
# tables / diagrams, so we ask for a description that surfaces axes,
# labels, and trend rather than just the visible shapes. Mirrors the
# intent of Florence-2's `<MORE_DETAILED_CAPTION>` task token.
_CAPTION_PROMPT = (
    "Describe this figure from a research report in 2-3 sentences. "
    "Focus on what the figure communicates: chart type, axes / labels, "
    "key trends or values, and the overall takeaway. Do not speculate "
    "beyond what is visible. Reply in the same language the figure uses; "
    "default to English when unclear."
)


def _build_client() -> genai.Client:
    """Construct a Vertex genai client with a 60s per-request timeout."""
    http_opts = {"timeout": 60_000}  # ms
    logger.info(
        "figures: building Vertex vision client project=%s location=%s model=%s",
        settings.gcp_project_id, settings.vertex_location, settings.gemini_vision_model,
    )
    return genai.Client(
        vertexai=True,
        project=settings.gcp_project_id,
        location=settings.vertex_location,
        http_options=http_opts,
    )


def init() -> None:
    """Pre-build the Vertex client so the first request doesn't pay
    cold-start auth/connection latency. Safe to call multiple times."""
    global _client, _init_attempted
    with _client_lock:
        if _init_attempted:
            return
        _init_attempted = True
        try:
            _client = _build_client()
            logger.info("figures: ready (Vertex Gemini Vision)")
        except Exception as exc:
            # Not fatal — calls will lazily retry the build via _get_client.
            logger.warning("figures: client build failed at init: %s", exc)


def is_ready() -> bool:
    """True once init() has run and a client was built."""
    return _client is not None


def _get_client() -> genai.Client:
    """Lazy-init: build the client on first call if init() hasn't been
    called yet (keeps tests / scripts that bypass FastAPI startup working)."""
    global _client
    with _client_lock:
        if _client is None:
            _client = _build_client()
        return _client


# ── Public API ──────────────────────────────────────────────────────────────

def caption(img: bytes | np.ndarray | Image.Image) -> str:
    """Return a multi-sentence caption for a figure / chart / diagram.

    Returns "" on any failure (image decode, Vertex error, quota). The
    orchestrator falls back to a generic placeholder + OCR'd text in
    that case.
    """
    pil = _coerce_to_pil(img)
    if pil is None:
        return ""

    png_bytes = _pil_to_png_bytes(pil)
    if not png_bytes:
        return ""

    try:
        client = _get_client()
        response = client.models.generate_content(
            model=settings.gemini_vision_model,
            contents=[
                types.Part.from_bytes(data=png_bytes, mime_type="image/png"),
                _CAPTION_PROMPT,
            ],
            config=types.GenerateContentConfig(temperature=0.2),
        )
        return (response.text or "").strip()
    except Exception as exc:
        logger.warning("figures: caption failed: %s", exc)
        return ""


def caption_crop(
    page_img: np.ndarray,
    bbox: tuple[float, float, float, float],
) -> str:
    """Crop the figure region off the page and run captioning."""
    x0, y0, x1, y1 = (int(round(v)) for v in bbox)
    h, w = page_img.shape[:2]
    x0, x1 = max(0, x0), min(w, x1)
    y0, y1 = max(0, y0), min(h, y1)
    if x1 <= x0 or y1 <= y0:
        return ""

    crop = page_img[y0:y1, x0:x1]
    return caption(crop)


# ── Helpers ─────────────────────────────────────────────────────────────────

def _coerce_to_pil(img: bytes | np.ndarray | Image.Image) -> Image.Image | None:
    if isinstance(img, Image.Image):
        return img.convert("RGB")
    if isinstance(img, np.ndarray):
        try:
            return Image.fromarray(img).convert("RGB")
        except Exception as exc:
            logger.warning("figures: failed to convert ndarray: %s", exc)
            return None
    if isinstance(img, (bytes, bytearray)):
        try:
            return Image.open(io.BytesIO(img)).convert("RGB")
        except Exception as exc:
            logger.warning("figures: failed to decode bytes: %s", exc)
            return None
    return None


def _pil_to_png_bytes(pil: Image.Image) -> bytes:
    """Encode the cropped PIL image as PNG for the Vertex request."""
    try:
        buf = io.BytesIO()
        pil.save(buf, format="PNG", optimize=False)
        return buf.getvalue()
    except Exception as exc:
        logger.warning("figures: PNG encode failed: %s", exc)
        return b""
