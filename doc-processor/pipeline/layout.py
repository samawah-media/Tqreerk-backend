"""Docling wrapper — layout detection + reading order + table structure.

Docling is the spine of this pipeline. A single `convert()` call gives us:
    • layout regions    (DocLayNet-trained model: text / table / figure / formula)
    • reading order     (deterministic ordering across multi-column layouts)
    • table structure   (TableFormer cell extraction)
    • text extraction   (PyMuPDF for digital PDFs, no OCR involvement)

We do NOT use Docling for OCR — its built-in OCR backend is slower than
EasyOCR for our use case and doesn't have an Arabic-tuned model. We post-
process Docling's output and run EasyOCR in `pipeline.ocr` on regions that
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
from docling.datamodel.base_models import InputFormat
from docling.datamodel.pipeline_options import PdfPipelineOptions
from docling.document_converter import DocumentConverter, PdfFormatOption

from pipeline.errors import InvalidPdfError

logger = logging.getLogger(__name__)

# Minimum plausible PDF size — anything below this is either empty or a
# truncated upload. Real PDFs are >1 KB; 32 B is just a sanity floor.
_MIN_PDF_BYTES = 32


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
    """
    pipeline_opts = PdfPipelineOptions()
    pipeline_opts.do_ocr = False
    pipeline_opts.do_table_structure = True
    pipeline_opts.table_structure_options.do_cell_matching = True

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
    by_page = analyze_document(pdf_bytes)
    if not by_page:
        return []
    # The synthetic PDF has exactly one page. Whichever index Docling gives
    # us, just return the only bucket.
    return next(iter(by_page.values()))


# ── Public API: full PDF ────────────────────────────────────────────────────

def analyze_document(pdf_bytes: bytes) -> dict[int, list[LayoutRegion]]:
    """Run Docling on a full PDF.

    Returns `{page_no: [LayoutRegion, ...]}` with 1-based page numbers. Each
    page's regions are ordered by Docling's global reading-order traversal
    (so multi-column layouts and Arabic right-to-left columns flow correctly
    within their page).

    An unparseable PDF returns an empty dict — callers should treat that the
    same as "no content extracted" rather than a hard error, so a single bad
    document doesn't crash batch ingest.
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
        return {}

    return _bucket_regions_by_page(result.document)


# ── Region extraction (shared) ──────────────────────────────────────────────

def _bucket_regions_by_page(doc) -> dict[int, list[LayoutRegion]]:
    """Walk Docling's items, classify them, and bucket by page number.

    Docling's `iterate_items()` yields items in global reading order. We
    keep a per-page counter so each region carries a stable ordinal within
    its own page — the orchestrator joins on that to flow content
    page-by-page even when callers process pages independently.
    """
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

        bbox = _resolve_bbox(item)
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


def _resolve_bbox(item) -> tuple[float, float, float, float]:
    """Best-effort bbox extraction. Docling items vary; falls back to (0,0,0,0)
    so callers never have to None-check."""
    try:
        prov = item.prov[0] if getattr(item, "prov", None) else None
        if prov and getattr(prov, "bbox", None):
            b = prov.bbox
            return (float(b.l), float(b.t), float(b.r), float(b.b))
    except Exception:
        pass
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
