"""RapidOCR wrapper — Arabic + English text recognition.

Why RapidOCR over Surya (switched 2026-05-26)
=============================================
Surya uses a transformer encoder-decoder (autoregressive) architecture.
Its decoder is forced to emit some token — it can't express "there's no
text here." On non-text image crops (decorative photos, template pages) it
hallucinated repetitions of high-frequency Arabic words, flooding every chunk
with garbage text that corrupted summaries and search results.

RapidOCR uses a two-stage pipeline:
  1. DB text detector  → bounding boxes around text lines (or None)
  2. CRNN recognizer   → text + confidence per box ← only runs if boxes found

If the detector returns nothing, the recognizer never executes and the result
is definitively empty — no forced token emission, no hallucination.
A configurable confidence threshold further filters borderline detections on
degraded scan regions.

Architecture notes:
  • ONNX runtime — no PyTorch dependency for OCR; saves ~2 GB VRAM on the L4
    so Docling + bge-m3 + reranker have more headroom.
  • Arabic recognition model (arabic_PP-OCRv3_rec_infer.onnx) pre-downloaded
    at image build time into /app/ocr_models/ — cold start only loads from
    disk, not the network.
  • CPU inference is fast enough: RapidOCR on CPU is ~20-80ms per crop vs
    Surya's ~80-300ms on GPU. OCR is applied only to regions where Docling
    produced no text, so the total per-document overhead stays low.

Public API is unchanged from the Surya version:
  init(), is_ready(), ocr_image(), ocr_crop()
"""
from __future__ import annotations

import io
import logging
import threading
import time

import numpy as np
from PIL import Image

from core.config import settings

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_engine = None          # rapidocr_onnxruntime.RapidOCR instance
_init_lock = threading.Lock()


def init() -> None:
    """Load the RapidOCR detection + recognition models.

    The engine is a thin ONNX-runtime wrapper — no GPU needed, no PyTorch
    import. Weights are pre-baked into the image at build time (Dockerfile.gpu);
    this call only loads them from disk into RAM.
    """
    global _engine
    if _engine is not None:
        return

    with _init_lock:
        if _engine is not None:
            return

        from rapidocr_onnxruntime import RapidOCR

        # Build constructor kwargs from settings. When a path is empty the
        # argument is omitted and RapidOCR uses its bundled English/CJK
        # defaults — still no hallucination, just lower Arabic accuracy.
        kwargs: dict = {}
        if settings.ocr_det_model_path:
            kwargs["det_model_dir"] = settings.ocr_det_model_path
        if settings.ocr_rec_model_path:
            kwargs["rec_model_dir"] = settings.ocr_rec_model_path

        params: dict = {}
        if settings.ocr_rec_keys_path:
            params["Rec"] = {"keys_path": settings.ocr_rec_keys_path}
        if params:
            kwargs["params"] = params

        logger.info(
            "rapidocr: initialising (det=%s rec=%s)",
            settings.ocr_det_model_path or "<bundled>",
            settings.ocr_rec_model_path or "<bundled>",
        )
        _engine = RapidOCR(**kwargs)
        logger.info("rapidocr: ready")


def is_ready() -> bool:
    return _engine is not None


# ── Public API ──────────────────────────────────────────────────────────────

def ocr_image(img: bytes | np.ndarray | Image.Image) -> str:
    """Run OCR on an image and return concatenated text in reading order.

    Accepts bytes (any PIL-supported format), a numpy array (H, W, C), or a
    PIL Image. Returns "" if no text was detected — never raises on empty input.

    Key difference from Surya: when the detector finds no text regions it
    returns None before recognition runs, so the result is always genuinely
    empty on non-text crops.
    """
    started = time.perf_counter()
    if not is_ready():
        init()

    arr = _coerce_to_numpy(img)
    if arr is None:
        logger.warning(
            "rapidocr: input rejected — could not coerce %s to numpy",
            type(img).__name__,
        )
        return ""

    h, w = arr.shape[:2]
    if h == 0 or w == 0:
        logger.warning("rapidocr: input rejected — zero-size image (w=%d h=%d)", w, h)
        return ""

    try:
        result, _elapse = _engine(arr)
    except Exception as exc:
        logger.warning("rapidocr: inference failed: %s", exc)
        return ""

    elapsed_ms = int((time.perf_counter() - started) * 1000)

    if not result:
        # Detection found no text regions — this is the correct answer for
        # decorative images, photos, and template-only pages.
        logger.debug(
            "rapidocr: no text detected (image=%dx%d, %d ms)", w, h, elapsed_ms,
        )
        return ""

    # Each result item: [bbox_points, text, confidence]
    min_conf = settings.ocr_min_confidence
    lines = []
    for item in result:
        if len(item) < 3:
            continue
        text_str = (item[1] or "").strip()
        conf = float(item[2] or 0.0)
        if text_str and conf >= min_conf:
            lines.append(text_str)

    text = "\n".join(lines)
    if text:
        preview = text.replace("\n", " ⏎ ")[:80]
        logger.info(
            "rapidocr: ok — %d/%d line(s) kept (conf≥%.2f), %d chars, %d ms — "
            "preview: %s%s",
            len(lines), len(result), min_conf, len(text), elapsed_ms,
            preview, "…" if len(text) > 80 else "",
        )
    else:
        logger.debug(
            "rapidocr: %d line(s) detected but all below conf threshold %.2f "
            "(image=%dx%d, %d ms)",
            len(result), min_conf, w, h, elapsed_ms,
        )
    return text


def ocr_crop(
    page_img: np.ndarray,
    bbox: tuple[float, float, float, float],
) -> str:
    """OCR a sub-region of a page image. Used to fill in text for layout
    regions where Docling didn't return any (scanned PDFs, image-only pages).
    """
    if page_img is None or page_img.size == 0:
        logger.warning("rapidocr: ocr_crop got empty page_img — skipping")
        return ""

    x0_raw, y0_raw, x1_raw, y1_raw = (int(round(v)) for v in bbox)
    h, w = page_img.shape[:2]
    x0, x1 = max(0, x0_raw), min(w, x1_raw)
    y0, y1 = max(0, y0_raw), min(h, y1_raw)
    if x1 <= x0 or y1 <= y0:
        logger.warning(
            "rapidocr: bbox clamped to empty — raw=(%d,%d,%d,%d) page=%dx%d "
            "clamped=(%d,%d,%d,%d). If this fires on a real text region, "
            "_resolve_bbox upstream is producing bad geometry.",
            x0_raw, y0_raw, x1_raw, y1_raw, w, h, x0, y0, x1, y1,
        )
        return ""

    crop = page_img[y0:y1, x0:x1]
    logger.debug(
        "rapidocr: ocr_crop bbox=(%d,%d,%d,%d) page=%dx%d → crop=%dx%d",
        x0, y0, x1, y1, w, h, crop.shape[1], crop.shape[0],
    )
    return ocr_image(crop)


# ── Helpers ─────────────────────────────────────────────────────────────────

def _coerce_to_numpy(img: bytes | np.ndarray | Image.Image) -> np.ndarray | None:
    """Normalise the various accepted input forms into a uint8 RGB numpy array.

    RapidOCR accepts numpy arrays directly (H, W, 3) — no PIL intermediary
    needed, which avoids one copy for the common numpy caller path.
    """
    if isinstance(img, np.ndarray):
        if img.size == 0 or img.ndim not in (2, 3):
            return None
        return img
    if isinstance(img, Image.Image):
        return np.array(img.convert("RGB"))
    if isinstance(img, (bytes, bytearray)):
        try:
            pil = Image.open(io.BytesIO(img)).convert("RGB")
            return np.array(pil)
        except Exception as exc:
            logger.warning("rapidocr: failed to decode image bytes: %s", exc)
            return None
    logger.warning("rapidocr: unsupported input type %s", type(img).__name__)
    return None
