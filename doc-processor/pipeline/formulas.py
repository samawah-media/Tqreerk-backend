"""pix2tex wrapper — formula image → LaTeX.

pix2tex (LaTeX-OCR) is a transformer model trained on the IM2LaTeX dataset.
For inline / display math regions Docling has classified as "formula", we
crop the image and run it through pix2tex to recover the original LaTeX.

The output gets wrapped in `$$ … $$` by the orchestrator so that downstream
chunking / embedding sees recognisable math, and any LLM answering questions
about the report can re-render or reason about the equations directly.

Caveats
=======
pix2tex was trained mainly on English-source papers. It still works on
formulas in Arabic-language reports (math notation is universal) but is not
designed for Arabic text inside formulas. For Taqreerk's typical content
(macroeconomic / policy reports) formulas are rare — this module is best
viewed as a polish step rather than a critical one.
"""
from __future__ import annotations

import io
import logging
from typing import Optional

import numpy as np
from PIL import Image

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_model: Optional["LatexOCR"] = None  # type: ignore[name-defined]


def init() -> None:
    """Lazily build the pix2tex model. ~200MB weights pre-downloaded in the
    Dockerfile, so this is a CPU-side init plus a CUDA warmup."""
    global _model
    if _model is not None:
        return

    from pix2tex.cli import LatexOCR

    logger.info("pix2tex: initialising")
    _model = LatexOCR()
    logger.info("pix2tex: ready")


def is_ready() -> bool:
    return _model is not None


# ── Public API ──────────────────────────────────────────────────────────────

def to_latex(img: bytes | np.ndarray | Image.Image) -> str:
    """Convert a formula image into a LaTeX string.

    Returns "" on any decoding / inference error rather than propagating —
    formula extraction is a quality booster, not a hard pipeline dependency.
    """
    if _model is None:
        init()

    pil = _coerce_to_pil(img)
    if pil is None:
        return ""

    try:
        latex = _model(pil) or ""
    except Exception as exc:
        logger.warning("pix2tex: inference failed: %s", exc)
        return ""

    return latex.strip()


def to_latex_crop(
    page_img: np.ndarray,
    bbox: tuple[float, float, float, float],
) -> str:
    """Crop a region off the page and run pix2tex on it."""
    x0, y0, x1, y1 = (int(round(v)) for v in bbox)
    h, w = page_img.shape[:2]
    x0, x1 = max(0, x0), min(w, x1)
    y0, y1 = max(0, y0), min(h, y1)
    if x1 <= x0 or y1 <= y0:
        return ""

    crop = page_img[y0:y1, x0:x1]
    return to_latex(crop)


# ── Helpers ─────────────────────────────────────────────────────────────────

def _coerce_to_pil(img: bytes | np.ndarray | Image.Image) -> Image.Image | None:
    """pix2tex expects a PIL.Image — normalise the accepted inputs."""
    if isinstance(img, Image.Image):
        return img.convert("RGB")
    if isinstance(img, np.ndarray):
        try:
            return Image.fromarray(img).convert("RGB")
        except Exception as exc:
            logger.warning("pix2tex: failed to convert ndarray: %s", exc)
            return None
    if isinstance(img, (bytes, bytearray)):
        try:
            return Image.open(io.BytesIO(img)).convert("RGB")
        except Exception as exc:
            logger.warning("pix2tex: failed to decode bytes: %s", exc)
            return None
    return None
