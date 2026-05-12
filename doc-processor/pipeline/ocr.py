"""Surya OCR wrapper — Arabic + English text recognition.

We run OCR only on regions where Docling's text-layer extraction came back
empty (or suspiciously short). Two reasons:
    1. OCR is the slowest step per region (~80-300ms each on GPU).
    2. Native PDF text is always more accurate than OCR'd text — never
       overwrite real text with an OCR guess.

Why Surya over EasyOCR (swapped 2026-05-12)
===========================================
Surya is a transformer-based OCR stack (detection + recognition) that
benchmarks ahead of EasyOCR on Arabic line-level accuracy and is markedly
better on multi-column / RTL layouts — which is the bulk of Taqreerk's
input. It's PyTorch-native (no second DL framework), uses the same CUDA
driver as Docling, and pulls weights from Hugging Face via `transformers`
so it slots into our existing HF_HOME cache. The trade-off is ~2 GB extra
VRAM and a GPL-3 / commercial dual license — see ops notes.
"""
from __future__ import annotations

import io
import logging
import os
import threading
from typing import Iterable

import numpy as np
from PIL import Image

from core.config import settings

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_rec_predictor = None       # surya.recognition.RecognitionPredictor
_det_predictor = None       # surya.detection.DetectionPredictor
_init_lock = threading.Lock()


def init() -> None:
    """Load Surya detection + recognition predictors.

    Surya picks its device from the `TORCH_DEVICE` env var; we set it here
    from settings.device so the same code path works on CPU dev machines and
    CUDA Cloud Run instances. Weights are pulled from Hugging Face into
    HF_HOME on first call — pre-baked into the image at build time (see
    Dockerfile.gpu) so cold start only pays for VRAM transfer.
    """
    global _rec_predictor, _det_predictor
    if _rec_predictor is not None and _det_predictor is not None:
        return

    with _init_lock:
        if _rec_predictor is not None and _det_predictor is not None:
            return

        # Surya reads TORCH_DEVICE at import time on some versions, so set
        # it before importing the predictors.
        os.environ.setdefault("TORCH_DEVICE", settings.device)

        from surya.detection import DetectionPredictor
        from surya.recognition import RecognitionPredictor

        logger.info(
            "surya: initialising detection + recognition (device=%s, langs hint=%s)",
            settings.device, settings.ocr_languages,
        )
        _det_predictor = DetectionPredictor()
        _rec_predictor = RecognitionPredictor()
        logger.info("surya: ready")


def is_ready() -> bool:
    return _rec_predictor is not None and _det_predictor is not None


# ── Public API ──────────────────────────────────────────────────────────────

def ocr_image(img: bytes | np.ndarray | Image.Image) -> str:
    """Run OCR on an image and return concatenated text in reading order.

    Accepts bytes (any PIL-supported format), a numpy array (H, W, C), or a
    PIL Image. Returns "" if no text was detected — never raises on empty
    input.
    """
    if not is_ready():
        init()

    pil_img = _coerce_to_pil(img)
    if pil_img is None:
        return ""

    langs = _configured_langs()

    try:
        # Surya predictor signature: rec([images], [langs_per_image], det_predictor)
        # Recent versions accept langs=None and auto-detect script per line;
        # we still pass the configured hint for slightly better accuracy on
        # mixed AR/EN text where script switches mid-line are rare.
        predictions = _rec_predictor(
            [pil_img],
            [langs] if langs else [None],
            _det_predictor,
        )
    except Exception as exc:
        logger.warning("surya: recognition failed: %s", exc)
        return ""

    if not predictions:
        return ""

    result = predictions[0]
    lines = getattr(result, "text_lines", None) or []
    return "\n".join(
        (line.text or "").strip()
        for line in lines
        if (line.text or "").strip()
    )


def ocr_crop(
    page_img: np.ndarray,
    bbox: tuple[float, float, float, float],
) -> str:
    """OCR a sub-region of a page image. Used to fill in text for layout
    regions where Docling didn't return any (scanned PDFs, image-only pages).
    """
    if page_img is None or page_img.size == 0:
        return ""

    x0, y0, x1, y1 = (int(round(v)) for v in bbox)
    h, w = page_img.shape[:2]
    x0, x1 = max(0, x0), min(w, x1)
    y0, y1 = max(0, y0), min(h, y1)
    if x1 <= x0 or y1 <= y0:
        return ""

    crop = page_img[y0:y1, x0:x1]
    return ocr_image(crop)


# ── Helpers ─────────────────────────────────────────────────────────────────

def _configured_langs() -> list[str] | None:
    """Parse OCR_LANGUAGES env into the list Surya expects. Returns None when
    the setting is empty/whitespace so Surya falls back to auto-detect."""
    raw = (settings.ocr_languages or "").strip()
    if not raw:
        return None
    return [s.strip() for s in raw.split(",") if s.strip()]


def _coerce_to_pil(img: bytes | np.ndarray | Image.Image) -> Image.Image | None:
    """Normalise the various accepted input forms into a PIL RGB image.

    Surya predictors only accept PIL Images, so callers passing numpy arrays
    or raw bytes get converted here.
    """
    if isinstance(img, Image.Image):
        return img.convert("RGB")
    if isinstance(img, np.ndarray):
        if img.size == 0 or img.ndim not in (2, 3):
            return None
        try:
            if img.ndim == 2:
                return Image.fromarray(img).convert("RGB")
            return Image.fromarray(img).convert("RGB")
        except Exception as exc:
            logger.warning("ocr: failed to convert ndarray to PIL: %s", exc)
            return None
    if isinstance(img, (bytes, bytearray)):
        try:
            return Image.open(io.BytesIO(img)).convert("RGB")
        except Exception as exc:
            logger.warning("ocr: failed to decode image bytes: %s", exc)
            return None
    return None
