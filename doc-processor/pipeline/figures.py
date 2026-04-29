"""Florence-2 wrapper — figure region → caption.

Florence-2 is Microsoft's lightweight multimodal model (~1 GB for `base-ft`).
Compared to BLIP-2 / CogVLM it loads faster and runs in fp16 on an L4 with
plenty of VRAM headroom, which matters because cold-start time is what the
user actually feels.

Task contract
=============
Florence-2 is task-conditioned: you pass a task token in the prompt, the
model produces output for that task. We use `<MORE_DETAILED_CAPTION>` for
charts / figures / diagrams — the longer captions describe trends and
labels rather than just shape, which is what RAG retrieval over reports
actually needs.


Failure mode
============
On any inference error we return "" so the orchestrator can still embed the
figure region with whatever text EasyOCR pulled out of it (axis labels,
legends). Captioning is a quality booster, not a hard dependency.
"""
from __future__ import annotations

import io
import logging
from typing import Optional

import numpy as np
import torch
from PIL import Image

from core.config import settings

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_processor = None
_model: Optional[torch.nn.Module] = None
_device: Optional[str] = None
_dtype: Optional[torch.dtype] = None

# Florence-2 task token that produces a richer description than the default
# caption — useful for charts / diagrams in research reports.
_TASK_PROMPT = "<MORE_DETAILED_CAPTION>"


def init() -> None:
    """Load Florence-2 processor + model. Weights are pre-cached at image
    build time so this is a CUDA-bound op (move to GPU + warmup)."""
    global _processor, _model, _device, _dtype
    if _model is not None:
        return

    from transformers import AutoProcessor, AutoModelForCausalLM

    _device = settings.device
    _dtype = torch.float16 if (settings.fp16 and _device == "cuda") else torch.float32

    logger.info(
        "florence-2: loading model=%s device=%s dtype=%s",
        settings.florence_model_id, _device, _dtype,
    )

    _processor = AutoProcessor.from_pretrained(
        settings.florence_model_id, trust_remote_code=True,
    )
    model = AutoModelForCausalLM.from_pretrained(
        settings.florence_model_id,
        trust_remote_code=True,
        torch_dtype=_dtype,
    )
    model.to(_device)
    model.eval()
    _model = model
    logger.info("florence-2: ready")


def is_ready() -> bool:
    return _model is not None


# ── Public API ──────────────────────────────────────────────────────────────

@torch.inference_mode()
def caption(img: bytes | np.ndarray | Image.Image) -> str:
    """Return a multi-sentence caption for a figure / chart / diagram.

    Returns "" on any failure (image decode, model error, OOM). The
    orchestrator can fall back to a generic placeholder + OCR'd text.
    """
    if _model is None:
        init()

    pil = _coerce_to_pil(img)
    if pil is None:
        return ""

    try:
        inputs = _processor(
            text=_TASK_PROMPT, images=pil, return_tensors="pt",
        ).to(_device, _dtype if _dtype == torch.float16 else None)

        # Cap output length so a degenerate generation can't run away.
        # 256 new tokens ≈ 2-3 sentences — plenty for a chart description.
        generated = _model.generate(
            input_ids=inputs["input_ids"],
            pixel_values=inputs["pixel_values"],
            max_new_tokens=256,
            num_beams=3,
            do_sample=False,
        )
        text = _processor.batch_decode(generated, skip_special_tokens=False)[0]

        # Florence-2 returns the prompt + raw markup; post_process_generation
        # turns it into the clean caption string.
        parsed = _processor.post_process_generation(
            text, task=_TASK_PROMPT, image_size=(pil.width, pil.height),
        )
        return (parsed.get(_TASK_PROMPT) or "").strip()
    except torch.cuda.OutOfMemoryError as exc:
        # OOM here usually means an unusually large figure crop. Free what we
        # can and bail out — better an empty caption than a dead worker.
        logger.warning("florence-2: OOM during captioning: %s", exc)
        torch.cuda.empty_cache()
        return ""
    except Exception as exc:
        logger.warning("florence-2: caption failed: %s", exc)
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
            logger.warning("florence-2: failed to convert ndarray: %s", exc)
            return None
    if isinstance(img, (bytes, bytearray)):
        try:
            return Image.open(io.BytesIO(img)).convert("RGB")
        except Exception as exc:
            logger.warning("florence-2: failed to decode bytes: %s", exc)
            return None
    return None
