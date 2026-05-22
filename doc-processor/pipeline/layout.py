"""Docling wrapper — layout detection + reading order + table structure.

Docling is the spine of this pipeline. A single `convert()` call gives us:
    • layout regions    (DocLayNet-trained model: text / table / figure / formula)
    • reading order     (deterministic ordering across multi-column layouts)
    • table structure   (TableFormer cell extraction)
    • text extraction   (PyMuPDF for digital PDFs, no OCR involvement)

We do NOT use Docling for OCR — its built-in OCR backend is slower than
Surya for our use case and doesn't ship Arabic-tuned recognition. We post-
process Docling's output and run Surya in `pipeline.ocr` on regions that
came back empty.

Two entry points
================
`analyze_page(png_bytes)`     — single rendered page (kept for fallback /
                                small-image inputs).
`analyze_document(pdf_bytes)` — full PDF, preferred path. Avoids rasterising
                                the text layer, gives reading order across
                                page breaks, and runs Docling once per
                                document instead of once per page.
"""
from __future__ import annotations

import io
import logging
from dataclasses import dataclass
from typing import Iterable

import fitz  # PyMuPDF
from docling.datamodel.accelerator_options import (
    AcceleratorDevice,
    AcceleratorOptions,
)
from docling.datamodel.base_models import InputFormat
from docling.datamodel.pipeline_options import PdfPipelineOptions
from docling.document_converter import DocumentConverter, PdfFormatOption
from docling_core.types.doc import CoordOrigin

from core.config import settings
from pipeline.errors import InvalidPdfError

logger = logging.getLogger(__name__)

# Minimum plausible PDF size — anything below this is either empty or a
# truncated upload. Real PDFs are >1 KB; 32 B is just a sanity floor.
_MIN_PDF_BYTES = 32

# DPI at which the orchestrator renders pages for figure / OCR crops. Must
# match _RENDER_DPI in pipeline.orchestrator: Docling returns bboxes in PDF
# points (72/inch) but consumers slice page_img as a pixel array, so we
# scale here once at layout time. Duplicated rather than imported to avoid
# pulling orchestrator → layout → orchestrator dep cycle.
_RENDER_DPI = 150
_POINTS_TO_PIXELS = _RENDER_DPI / 72.0


# ── Output dataclasses ──────────────────────────────────────────────────────
# We don't return Docling's native types because they're an unstable API
# surface — pinning to ours protects the orchestrator from breaking on
# Docling minor version bumps.

@dataclass
class LayoutRegion:
    """A single detected region on the page."""
    kind: str                  # "text" | "table" | "figure" | "formula" | "heading" | "footer"
    bbox: tuple[float, float, float, float]   # (x_min, y_min, x_max, y_max), pixel space
    text: str                  # raw text Docling extracted; "" if Docling has no text layer here
    reading_order: int         # 0-based ordinal within its page
    table_data: list[list[str]] | None = None  # row-major, only for kind="table"


@dataclass
class DocumentLayout:
    """Both outputs of a single Docling convert() call.

    One parse → two artifacts:
      • `regions_by_page` drives chunking / OCR / captioning.
      • `markdown` is the human-readable export that we upload alongside the
        raw PDF (Stage 1.5 in pipeline.ingest) for manual QA and downstream
        consumers that just want plain text.

    Empty `markdown` is normal — Docling occasionally fails to export
    (e.g. on PDFs with unusual structure). The chunks are still good, so we
    log and keep going rather than failing the ingest.
    """
    regions_by_page: dict[int, list[LayoutRegion]]
    markdown: str


# Mapping from Docling item class names to the small fixed kind vocabulary
# the orchestrator uses. Centralised so both entry points stay consistent.
_KIND_MAP = {
    "TextItem":          "text",
    "SectionHeaderItem": "heading",
    "TitleItem":         "heading",
    "TableItem":         "table",
    "PictureItem":       "figure",
    "FormulaItem":       "formula",
    "ListItem":          "text",
    "CodeItem":          "text",
    "FootnoteItem":      "footer",
}


# ── Module state ────────────────────────────────────────────────────────────
# Docling's converter is heavy to instantiate (loads layout + table models).
# Build once at process startup; reuse across requests.
_converter: DocumentConverter | None = None


def _build_converter() -> DocumentConverter:
    """Configure Docling for image-as-page input.

    do_ocr=False because we run our own Arabic-aware OCR downstream;
    do_table_structure=True turns on TableFormer (cell-level table extraction).

    accelerator_options pins Docling's layout + table models to the same
    CUDA device the rest of the pipeline uses. Without this Docling auto-
    detects the device and has been observed to fall back to CPU silently
    in some image variants — which inflates layout from ~150 ms/page to
    ~1.5 s/page. Pinning explicitly removes that variance.
    """
    pipeline_opts = PdfPipelineOptions()
    pipeline_opts.do_ocr = False
    pipeline_opts.do_table_structure = True
    pipeline_opts.table_structure_options.do_cell_matching = True

    # Pin accelerator to CUDA so Docling's layout + table models run on
    # the GPU instead of silently falling back to CPU on some image
    # variants. num_threads=4 is for CPU-side preprocessing (PDF →
    # raster) and matches the Cloud Run --cpu=4 we deploy with.
    device = (
        AcceleratorDevice.CUDA
        if settings.device.lower() == "cuda"
        else AcceleratorDevice.CPU
    )
    pipeline_opts.accelerator_options = AcceleratorOptions(
        num_threads=4,
        device=device,
    )
    logger.info(
        "docling: pinned accelerator device=%s num_threads=4", device,
    )

    return DocumentConverter(
        format_options={
            InputFormat.PDF: PdfFormatOption(pipeline_options=pipeline_opts),
        }
    )


def init() -> None:
    """Eagerly build the Docling converter so the first request doesn't
    pay layout-model initialisation latency on top of cold-start."""
    global _converter
    if _converter is None:
        logger.info("docling: initialising converter")
        _converter = _build_converter()
        logger.info("docling: ready")


def is_ready() -> bool:
    return _converter is not None


# ── Public API: per-page (rendered PNG) ─────────────────────────────────────

def analyze_page(png_bytes: bytes) -> list[LayoutRegion]:
    """Run Docling on a single page rendered as PNG.

    The PNG is wrapped in a synthetic single-page PDF so Docling's document
    API can ingest it. Returns regions in reading order; an empty list means
    Docling produced no detected blocks (very blank or weird PDF).

    Prefer `analyze_document()` when you have the original PDF — it avoids
    the synthetic-PDF wrapping AND gives Docling access to the real PDF text
    layer (no rasterisation loss for digital PDFs).
    """
    pdf_bytes = _png_to_single_page_pdf(png_bytes)
    layout = analyze_document(pdf_bytes)
    if not layout.regions_by_page:
        return []
    # The synthetic PDF has exactly one page. Whichever index Docling gives
    # us, just return the only bucket.
    return next(iter(layout.regions_by_page.values()))


# ── Public API: full PDF ────────────────────────────────────────────────────

def analyze_document(pdf_bytes: bytes) -> DocumentLayout:
    """Run Docling on a full PDF.

    Returns a `DocumentLayout` carrying both:
      • `regions_by_page`: `{page_no: [LayoutRegion, ...]}` with 1-based page
        numbers. Each page's regions are ordered by Docling's global
        reading-order traversal (so multi-column layouts and Arabic
        right-to-left columns flow correctly within their page).
      • `markdown`: the full document exported as Markdown — the same parsed
        tree we already paid for, surfaced for storage / manual QA.

    An unparseable PDF returns an empty layout — callers should treat that
    the same as "no content extracted" rather than a hard error, so a single
    bad document doesn't crash batch ingest.

    Markdown export failures are caught and downgraded to an empty string:
    the regions are the actual retrieval payload, and a docling export bug
    must not be allowed to fail the whole ingest.
    """
    if _converter is None:
        init()

    # Pre-validate before handing to Docling. Docling's own failure mode
    # is a generic RuntimeError("... is not valid.") that doesn't tell you
    # WHY (empty? encrypted? wrong format?). Catching the obvious cases
    # here gives ai_jobs.error_message useful detail.
    if len(pdf_bytes) < _MIN_PDF_BYTES:
        raise InvalidPdfError(
            "PDF too small (likely empty or truncated upload)",
            size=len(pdf_bytes), header=pdf_bytes[:8],
        )
    if not pdf_bytes.startswith(b"%PDF-"):
        raise InvalidPdfError(
            "Not a PDF (missing %PDF- header — wrong content-type or HTML error page?)",
            size=len(pdf_bytes), header=pdf_bytes[:8],
        )

    from docling.datamodel.base_models import DocumentStream

    stream = DocumentStream(name="document.pdf", stream=io.BytesIO(pdf_bytes))
    try:
        result = _converter.convert(stream)
    except RuntimeError as exc:
        # Docling's PDF backend (PyPdfium2) flips in_doc.valid=False on
        # encrypted, corrupted, or otherwise-unreadable PDFs and surfaces
        # it as a plain RuntimeError. Re-raise as our typed error so the
        # retry layer knows not to retry.
        if "is not valid" in str(exc):
            raise InvalidPdfError(
                f"Docling rejected PDF (likely encrypted or corrupted): {exc}",
                size=len(pdf_bytes), header=pdf_bytes[:8],
            ) from exc
        raise

    if result.document is None:
        logger.warning("docling: convert() returned no document")
        return DocumentLayout(regions_by_page={}, markdown="")

    regions_by_page = _bucket_regions_by_page(result.document)

    # Belt-and-braces: don't let an export bug fail the ingest.
    try:
        markdown = result.document.export_to_markdown() or ""
    except Exception as exc:
        logger.warning("docling: export_to_markdown() failed: %s", exc)
        markdown = ""

    return DocumentLayout(regions_by_page=regions_by_page, markdown=markdown)


# ── Region extraction (shared) ──────────────────────────────────────────────

def _bucket_regions_by_page(doc) -> dict[int, list[LayoutRegion]]:
    """Walk Docling's items, classify them, and bucket by page number.

    Docling's `iterate_items()` yields items in global reading order. We
    keep a per-page counter so each region carries a stable ordinal within
    its own page — the orchestrator joins on that to flow content
    page-by-page even when callers process pages independently.
    """
    # Pre-build {page_no: page_height_pt} so _resolve_bbox can flip
    # BOTTOMLEFT-origin bboxes to TOPLEFT (the orientation page_img uses).
    # Empty / malformed page entries fall back to 0.0 — _resolve_bbox treats
    # 0 as "skip the flip", which is safe for TOPLEFT pages.
    page_heights: dict[int, float] = {}
    try:
        for page_no_key, page in (getattr(doc, "pages", {}) or {}).items():
            try:
                page_heights[int(page_no_key)] = float(page.size.height)
            except Exception:
                continue
    except Exception:
        pass

    by_page: dict[int, list[LayoutRegion]] = {}
    page_orders: dict[int, int] = {}

    for item, _level in doc.iterate_items():
        cls_name = type(item).__name__
        kind = _KIND_MAP.get(cls_name)
        if kind is None:
            continue   # unknown / paginated container — skip rather than guess

        page_no = _resolve_page_no(item)
        if page_no is None:
            continue

        bbox = _resolve_bbox(item, page_heights.get(page_no, 0.0))
        text = _resolve_text(item, kind)
        table_data = _resolve_table(item) if kind == "table" else None

        order_idx = page_orders.get(page_no, 0)
        page_orders[page_no] = order_idx + 1

        by_page.setdefault(page_no, []).append(LayoutRegion(
            kind=kind,
            bbox=bbox,
            text=text,
            reading_order=order_idx,
            table_data=table_data,
        ))
    return by_page


# ── Helpers ─────────────────────────────────────────────────────────────────

def _png_to_single_page_pdf(png_bytes: bytes) -> bytes:
    """Wrap a PNG into a one-page PDF so Docling can ingest it.

    Used only by the per-page entry point. PyMuPDF handles this in-memory
    — no temp files. The wrapping is lossy for digital PDFs (because we
    started from a 150-dpi raster, not the source PDF), which is exactly
    why `analyze_document()` is the preferred path.
    """
    img_doc = fitz.open(stream=png_bytes, filetype="png")
    pix = img_doc[0].get_pixmap()
    width, height = pix.width, pix.height

    pdf = fitz.open()
    page = pdf.new_page(width=width, height=height)
    page.insert_image(fitz.Rect(0, 0, width, height), stream=png_bytes)
    pdf_bytes = pdf.tobytes()
    pdf.close()
    img_doc.close()
    return pdf_bytes


def _resolve_page_no(item) -> int | None:
    """Read the 1-based page number off a Docling item's provenance."""
    try:
        prov = item.prov[0] if getattr(item, "prov", None) else None
        if prov is not None and getattr(prov, "page_no", None) is not None:
            return int(prov.page_no)
    except Exception:
        return None
    return None


def _resolve_bbox(
    item,
    page_height_pt: float = 0.0,
) -> tuple[float, float, float, float]:
    """Return bbox in pixel space at _RENDER_DPI, top-left origin.

    Docling exposes BoundingBox with `coord_origin` in {TOPLEFT, BOTTOMLEFT}
    and coordinates in PDF points. The orchestrator slices page_img as a
    pixel array, so any consumer that uses this bbox needs pixel
    coordinates with top-left origin. Doing both normalisations here once
    eliminates a class of silent crop-mismatch bugs:

      • BOTTOMLEFT pages otherwise produce y0 > y1, yielding zero-row
        slices that Surya / Gemini Vision describe as empty without raising.
      • Even TOPLEFT pages were ~2× under-cropped because raw points (72/in)
        were used to index a 150-dpi pixel array.

    `page_height_pt` is needed only for BOTTOMLEFT → TOPLEFT flips; pass 0
    when unavailable and the function skips the flip (safe degradation for
    TOPLEFT pages, no-op for already-correct items).
    """
    try:
        prov = item.prov[0] if getattr(item, "prov", None) else None
        if not prov or not getattr(prov, "bbox", None):
            return (0.0, 0.0, 0.0, 0.0)
        b = prov.bbox

        if page_height_pt and getattr(b, "coord_origin", None) == CoordOrigin.BOTTOMLEFT:
            b = b.to_top_left_origin(page_height_pt)

        x0 = float(b.l) * _POINTS_TO_PIXELS
        x1 = float(b.r) * _POINTS_TO_PIXELS
        y0 = float(b.t) * _POINTS_TO_PIXELS
        y1 = float(b.b) * _POINTS_TO_PIXELS

        # Defensive: ensure ordered. After to_top_left_origin we expect
        # y0 < y1, but Docling occasionally emits reversed boxes on
        # degenerate items.
        if y0 > y1:
            y0, y1 = y1, y0
        if x0 > x1:
            x0, x1 = x1, x0
        return (x0, y0, x1, y1)
    except Exception:
        return (0.0, 0.0, 0.0, 0.0)


def _resolve_text(item, kind: str) -> str:
    """Return the textual content for the item, or '' if Docling didn't
    extract any. For tables we deliberately return '' — the orchestrator
    formats them via `pipeline.tables`."""
    if kind == "table":
        return ""
    text = getattr(item, "text", None) or ""
    return text.strip()


def _resolve_table(item) -> list[list[str]] | None:
    """Pull TableFormer's cell grid into a row-major 2-D list of strings.

    Docling exposes a `data` field on TableItem with row/col indices on each
    cell. We bucket them by row, sort each row by column index, and emit the
    raw text. Merged cells repeat their content into each spanned slot — that
    keeps downstream Markdown rendering trivial at the cost of slight
    duplication, which is the right trade-off for a chunk-and-embed
    consumer."""
    try:
        table_data = item.data
        if table_data is None:
            return None

        cells = list(getattr(table_data, "table_cells", []) or [])
        if not cells:
            return None

        n_rows = max(c.end_row_offset_idx for c in cells)
        n_cols = max(c.end_col_offset_idx for c in cells)
        grid: list[list[str]] = [["" for _ in range(n_cols)] for _ in range(n_rows)]

        for c in cells:
            text = (getattr(c, "text", "") or "").strip()
            for r in range(c.start_row_offset_idx, c.end_row_offset_idx):
                for col in range(c.start_col_offset_idx, c.end_col_offset_idx):
                    if 0 <= r < n_rows and 0 <= col < n_cols:
                        grid[r][col] = text
        return grid
    except Exception as exc:
        logger.warning("docling: failed to extract table cells: %s", exc)
        return None
