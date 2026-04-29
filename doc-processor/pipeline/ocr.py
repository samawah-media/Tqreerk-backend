"""EasyOCR wrapper — Arabic + English text recognition.

We run OCR only on regions where Docling's text-layer extraction came back
empty (or suspiciously short). Two reasons:
    1. OCR is the slowest step per region (~50-200ms each on GPU).
    2. Native PDF text is always more accurate than OCR'd text — never
       overwrite real text with an OCR guess.

Why EasyOCR over PaddleOCR
==========================
EasyOCR is PyTorch-native (matches the rest of the stack — no second
deep-learning framework), works on the same CUDA driver as Docling and
Florence-2, and ships solid Arabic + English models out of the box. PaddleOCR
is marginally better on Arabic but pulls in paddlepaddle-gpu which doubles
the container image size and creates CUDA-version-pinning headaches.
"""
from __future__ import annotations

import io
import logging
from typing import Iterable

import numpy as np
from PIL import Image

from core.config import settings

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_reader = None  # easyocr.Reader


def init() -> None:
    """Load EasyOCR models for the configured languages.

    Languages are provided as a CSV via `OCR_LANGUAGES` env (default 'ar,en').
    The reader is GPU-bound when settings.device == 'cuda'; first call after
    init pre-warms the CUDA kernels.
    """
    global _reader
    if _reader is not None:
        return

    import easyocr

    langs = [s.strip() for s in settings.ocr_languages.split(",") if s.strip()]
    use_gpu = settings.device == "cuda"
    logger.info("easyocr: initialising languages=%s gpu=%s", langs, use_gpu)
    _reader = easyocr.Reader(langs, gpu=use_gpu, verbose=False)
    logger.info("easyocr: ready")


def is_ready() -> bool:
    return _reader is not None


# ── Public API ──────────────────────────────────────────────────────────────

def ocr_image(img: bytes | np.ndarray | Image.Image) -> str:
    """Run OCR on an image and return concatenated text in reading order.

    Accepts bytes (any PIL-supported format), a numpy array (H, W, C), or a
    PIL Image. Returns "" if no text was detected — never raises on empty
    input.
    """
    if _reader is None:
        init()

    arr = _coerce_to_ndarray(img)
    if arr is None or arr.size == 0:
        return ""

    try:
        # detail=0 → return list[str] only (no boxes / scores) since we only
        # need the text. paragraph=True merges adjacent boxes into reading
        # order — important for Arabic right-to-left columns.
        result = _reader.readtext(arr, detail=0, paragraph=True)
    except Exception as exc:
        logger.warning("easyocr: readtext failed: %s", exc)
        return ""

    return "\n".join(line.strip() for line in result if line and line.strip())


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

def _coerce_to_ndarray(img: bytes | np.ndarray | Image.Image) -> np.ndarray | None:
    """Normalise the various accepted input forms into an RGB ndarray.

    EasyOCR is happy with either RGB or BGR but we standardise on RGB for
    consistency with the rest of the pipeline (PIL is RGB by default).
    """
    if isinstance(img, np.ndarray):
        return img if img.ndim in (2, 3) else None
    if isinstance(img, Image.Image):
        return np.array(img.convert("RGB"))
    if isinstance(img, (bytes, bytearray)):
        try:
            return np.array(Image.open(io.BytesIO(img)).convert("RGB"))
        except Exception as exc:
            logger.warning("ocr: failed to decode image bytes: %s", exc)
            return None
    return None
