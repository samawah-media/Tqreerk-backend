"""EasyOCR wrapper — Arabic + English text recognition.

Why EasyOCR (switched 2026-05-27 from RapidOCR)
================================================
RapidOCR had no working Arabic ONNX models on HuggingFace. The SWHL/RapidOCR
repository contains only CJK+English models (21 files); the recommended
PP-OCRv3/multilingual/ subfolder does not exist and the build was failing
with 404 errors. Third-party alternatives (monkt/paddleocr-onnx) ship a Latin
character dictionary — the CRNN maps Arabic feature vectors to Latin codepoints
and produces garbled output like "心 NHRC mg6m".

EasyOCR uses a two-stage pipeline:
  1. CRAFT text detector → bounding boxes around text lines (or empty list)
  2. CRNN recognizer     → text + confidence per box ← only runs if boxes found

If the detector returns nothing, the recognizer never executes — no forced
token emission, no hallucination on blank / decorative image crops.

Architecture notes:
  • PyTorch-native, runs on GPU — leverages the L4's VRAM that was previously
    wasted (RapidOCR ran on CPU). Detection is substantially faster on GPU.
  • Arabic + English models pre-downloaded at image build time into
    /app/easyocr_models/ — cold start loads from disk only, not the network.
  • CER ~0.58 on Arabic benchmarks (vs. a broken alternative that produced
    garbled Latin characters).

Public API is unchanged from the RapidOCR version:
  init(), is_ready(), ocr_image(), ocr_crop()
"""
from __future__ import annotations

import io
import logging
import os
import threading
import time

import numpy as np
from PIL import Image

from core.config import settings

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_reader = None          # easyocr.Reader instance
_init_lock = threading.Lock()


def init() -> None:
    """Load the EasyOCR Arabic + English models.

    Downloads are disabled at runtime (download_enabled=False) — weights must
    be pre-baked into the image at build time (Dockerfile.gpu). This call only
    loads them from disk into VRAM.
    """
    global _reader
    if _reader is not None:
        return

    with _init_lock:
        if _reader is not None:
            return

        # Tell EasyOCR where to find its model cache.
        os.environ.setdefault("EASYOCR_MODULE_PATH", "/app/easyocr_models")

        import easyocr  # noqa: PLC0415 — deferred so the module loads fast

        use_gpu = settings.device == "cuda"
        logger.info(
            "easyocr: initialising Arabic+English reader (gpu=%s)", use_gpu
        )
        _reader = easyocr.Reader(
            ["ar", "en"],
            gpu=use_gpu,
            download_enabled=False,
            verbose=False,
        )
        logger.info("easyocr: ready")


def is_ready() -> bool:
    return _reader is not None


# ── Public API ──────────────────────────────────────────────────────────────

def ocr_image(img: bytes | np.ndarray | Image.Image) -> str:
    """Run OCR on an image and return concatenated text in reading order.

    Accepts bytes (any PIL-supported format), a numpy array (H, W, C), or a
    PIL Image. Returns "" if no text was detected — never raises on empty input.

    Results are sorted top-to-bottom (primary) and right-to-left (secondary)
    so Arabic RTL text is read in the natural order before left-to-right labels.
    Lines below the minimum confidence threshold are dropped.
    """
    started = time.perf_counter()
    if not is_ready():
        init()

    arr = _coerce_to_numpy(img)
    if arr is None:
        logger.warning(
            "easyocr: input rejected — could not coerce %s to numpy",
            type(img).__name__,
        )
        return ""

    h, w = arr.shape[:2]
    if h == 0 or w == 0:
        logger.warning("easyocr: input rejected — zero-size image (w=%d h=%d)", w, h)
        return ""

    try:
        # detail=1 → [[bbox, text, confidence], ...]
        result = _reader.readtext(arr, detail=1)
    except Exception as exc:
        logger.warning("easyocr: inference failed: %s", exc)
        return ""

    elapsed_ms = int((time.perf_counter() - started) * 1000)

    if not result:
        logger.debug(
            "easyocr: no text detected (image=%dx%d, %d ms)", w, h, elapsed_ms,
        )
        return ""

    # Sort detections: top-to-bottom first, then right-to-left (RTL Arabic).
    # Each bbox is [[x0,y0],[x1,y0],[x1,y1],[x0,y1]]; centroid_y is the
    # average of the top-left and bottom-left y coords.
    def _sort_key(item):
        bbox = item[0]  # 4×2 points
        centroid_y = (bbox[0][1] + bbox[3][1]) / 2.0
        centroid_x = (bbox[0][0] + bbox[1][0]) / 2.0
        return (centroid_y, -centroid_x)   # y asc, x desc (RTL)

    result_sorted = sorted(result, key=_sort_key)

    min_conf = settings.ocr_min_confidence
    lines = []
    for item in result_sorted:
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
            "easyocr: ok — %d/%d line(s) kept (conf≥%.2f), %d chars, %d ms — "
            "preview: %s%s",
            len(lines), len(result), min_conf, len(text), elapsed_ms,
            preview, "…" if len(text) > 80 else "",
        )
    else:
        logger.debug(
            "easyocr: %d line(s) detected but all below conf threshold %.2f "
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
        logger.warning("easyocr: ocr_crop got empty page_img — skipping")
        return ""

    x0_raw, y0_raw, x1_raw, y1_raw = (int(round(v)) for v in bbox)
    h, w = page_img.shape[:2]
    x0, x1 = max(0, x0_raw), min(w, x1_raw)
    y0, y1 = max(0, y0_raw), min(h, y1_raw)
    if x1 <= x0 or y1 <= y0:
        logger.warning(
            "easyocr: bbox clamped to empty — raw=(%d,%d,%d,%d) page=%dx%d "
            "clamped=(%d,%d,%d,%d). If this fires on a real text region, "
            "_resolve_bbox upstream is producing bad geometry.",
            x0_raw, y0_raw, x1_raw, y1_raw, w, h, x0, y0, x1, y1,
        )
        return ""

    crop = page_img[y0:y1, x0:x1]
    logger.debug(
        "easyocr: ocr_crop bbox=(%d,%d,%d,%d) page=%dx%d → crop=%dx%d",
        x0, y0, x1, y1, w, h, crop.shape[1], crop.shape[0],
    )
    return ocr_image(crop)


# ── Helpers ─────────────────────────────────────────────────────────────────

def _coerce_to_numpy(img: bytes | np.ndarray | Image.Image) -> np.ndarray | None:
    """Normalise the various accepted input forms into a uint8 RGB numpy array.

    EasyOCR accepts numpy arrays directly (H, W, 3). Passing numpy avoids
    one copy for the common caller path.
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
            logger.warning("easyocr: failed to decode image bytes: %s", exc)
            return None
    logger.warning("easyocr: unsupported input type %s", type(img).__name__)
    return None
