"""Document-level chunking for RAG — canonical source; doc-processor/pipeline/chunker.py must stay in sync.

SYNC RULE: keep doc-processor/pipeline/chunker.py byte-identical to this file
(below its module docstring). Edit both together. A CI check
(scripts/check_chunker_sync.py) enforces this at pull-request time.

Two entry points
================
- `chunk_text(text)` — character-recursive splitter (LangChain). Used when
  the extractor only produced a flat content string (Gemini Vision path) or
  when a single block is too long to fit a chunk on its own.

- `chunk_blocks(blocks)` — structure-aware packer. Consumes the
  `blocks: list[{type, content, reading_order, page_number?, bbox?}]` that
  doc-processor returns and packs them into chunks along structural
  boundaries:

    • Atomic blocks (table / figure / formula) are NEVER split — they go
      into a chunk by themselves so the markdown/caption/LaTeX stays
      intact for the embedder and the LLM.

    • Headings are sticky: a heading starts a new chunk and stays glued to
      the first text block(s) that follow, so retrieval surfaces section
      titles next to their content.

    • Heading-merge rule: when the pending chunk is heading-only and the
      next block is also a heading, fold the new heading in with an em-dash
      separator instead of flushing a lone-heading chunk. Avoids one-line
      junk chunks that pollute BM25 and embed poorly (TOCs, section
      dividers, multi-level heading runs like "Program — Session 1").

    • Text blocks are greedy-packed up to ~CHUNK_CHARS. A text block that
      exceeds the limit is split with the recursive character splitter
      (preserving sentence / word boundaries when possible).

Document-level vs per-page
==========================
Callers should flatten every page's blocks into one list before calling,
tagging each block with its source `page_number` and a globally-monotonic
`reading_order`. Run the chunker once over the flat list. The chunker
naturally packs across page breaks — headings whose body lives on the next
page no longer emit a lone heading, paragraphs that wrap across pages no
longer get cut mid-sentence, and `section_title` stays correct across the
boundary.

Each emitted chunk reports:
  - `page_number`  — the page of its first contributing block (primary
    citation page; what we store in report_chunks."PageNumber").
  - `bboxes`       — list of `{"page": int, "bbox": [x0, y0, x1, y1]}` so
    multi-page chunks still cite every region.

The output of both functions is a list of strings — same downstream
contract, so the embed + persist phases don't care which path produced
the chunks. `chunk_blocks_with_meta` is a richer variant that returns
per-chunk metadata (block_types, section_title, page_number, bboxes) for
observability, filtered retrieval, and citation rendering.

Token sizing
============
We size in CHARACTERS, not tokens. Empirically, mixed Arabic + English content
runs ~3-4 chars per token, so chunk_size=2000 yields ~500-650 tokens per
chunk — squarely in the sweet spot for bge-m3 (8k limit) and Gemini Flash.
"""
from __future__ import annotations

from langchain_text_splitters import RecursiveCharacterTextSplitter

DEFAULT_CHUNK_CHARS = 2000   # ~500 tokens
DEFAULT_OVERLAP_CHARS = 800  # ~200 tokens of context bleed
# Why 200 tokens (not 50): a sentence split across a chunk boundary is
# invisible to a 50-token overlap — the next chunk starts halfway through
# the next paragraph and BM25 / dense retrieval miss the half that lives
# upstream. 200 tokens guarantees any sentence under ~150 tokens appears
# fully in two adjacent chunks, so retrieval recovers either side. Storage
# grows ~10% (each chunk shares 800/2000 chars with its neighbour).
# Re-ingest required for chunks already in the DB to pick up the new size.

# Block types that must never be split mid-content. Their `content` is
# already chunk-friendly (GFM markdown for tables, "[Figure: caption]\n
# extracted text" for figures, "$$ ... $$" for formulas) and splitting
# would break either the markup or the semantic unit.
_ATOMIC_BLOCK_TYPES = frozenset({"table", "figure", "formula"})

# Separator priority: largest semantic unit first. Arabic punctuation (؟ ۔)
# included alongside Latin so sentence-aware splits work for both scripts.
_SEPARATORS = [
    "\n\n",   # paragraph break
    "\n",     # line break
    ". ",     # English sentence end
    "؟ ",     # Arabic question mark
    "۔ ",     # Urdu / Arabic full stop
    "! ",
    "? ",
    "؛ ",     # Arabic semicolon
    "; ",
    " ",      # word break
    "",       # hard char cut (last resort)
]

_splitter = RecursiveCharacterTextSplitter(
    chunk_size=DEFAULT_CHUNK_CHARS,
    chunk_overlap=DEFAULT_OVERLAP_CHARS,
    separators=_SEPARATORS,
    keep_separator=True,
    length_function=len,
)


def chunk_text(text: str) -> list[str]:
    """Split `text` into ~500-token chunks with ~50-token overlap.

    Empty / whitespace-only input → []. Single short text → [text].
    """
    text = (text or "").strip()
    if not text:
        return []
    return _splitter.split_text(text)


# ── Structure-aware chunker ─────────────────────────────────────────────────


def chunk_blocks(blocks: list[dict]) -> list[str]:
    """Pack structured blocks into chunk strings (text only).

    Convenience wrapper around `chunk_blocks_with_meta` for callers that
    don't need per-chunk metadata.
    """
    return [c["content"] for c in chunk_blocks_with_meta(blocks)]


def _normalize_bbox_entry(block: dict) -> dict | None:
    """Build a `{"page", "bbox"}` citation entry from a block.

    Accepts `bbox` as either a 4-coord dict (`{x_min, y_min, x_max, y_max}`,
    matching the pydantic BoundingBox shape) or a 4-element sequence. Returns
    None when the block has no usable bbox so the caller can simply skip it.
    """
    bbox = block.get("bbox")
    if bbox is None:
        return None
    page = int(block.get("page_number") or 1)
    if isinstance(bbox, dict):
        coords = [
            bbox.get("x_min"), bbox.get("y_min"),
            bbox.get("x_max"), bbox.get("y_max"),
        ]
        if any(c is None for c in coords):
            return None
        return {"page": page, "bbox": [float(c) for c in coords]}
    try:
        x0, y0, x1, y1 = bbox
    except (TypeError, ValueError):
        return None
    return {"page": page, "bbox": [float(x0), float(y0), float(x1), float(y1)]}


def chunk_blocks_with_meta(blocks: list[dict]) -> list[dict]:
    """Pack structured blocks into chunks with rich per-chunk metadata.

    Input block shape: `{type, content, reading_order, page_number?, bbox?}`.
    `page_number` defaults to 1 when missing (single-page callers). `bbox` is
    optional. Callers that want cross-page packing pass a flat list with a
    globally-monotonic `reading_order` (see the module docstring).

    Output (per chunk):
      - `content`       — the chunk text (heading line(s) glued to packed body).
      - `block_types`   — kinds of blocks that contributed, in order.
      - `section_title` — most recent heading seen at/before this chunk.
      - `page_number`   — the page of the first contributing block (primary
        citation page).
      - `bboxes`        — list of `{"page": int, "bbox": [x0,y0,x1,y1]}` so
        chunks that span pages still cite every region they came from.
    """
    if not blocks:
        return []

    # Sort by reading_order so multi-column or RTL layouts stay coherent.
    # The orchestrator already returns blocks in reading order, but be
    # defensive — the cost is negligible.
    sorted_blocks = sorted(
        (b for b in blocks if (b.get("content") or "").strip()),
        key=lambda b: int(b.get("reading_order") or 0),
    )
    if not sorted_blocks:
        return []

    chunks: list[dict] = []
    current_text = ""
    current_types: list[str] = []
    current_section = ""
    last_section = ""  # tracks heading-as-section for chunks AFTER the heading too
    current_page: int | None = None
    current_bboxes: list[dict] = []

    def flush() -> None:
        nonlocal current_text, current_types, current_page, current_bboxes
        if current_text.strip():
            chunks.append({
                "content": current_text.strip(),
                "block_types": list(current_types),
                "section_title": current_section,
                "page_number": current_page or 1,
                "bboxes": list(current_bboxes),
            })
        current_text = ""
        current_types = []
        current_page = None
        current_bboxes = []

    for block in sorted_blocks:
        btype = block.get("type") or "text"
        text = (block.get("content") or "").strip()
        if not text:
            continue
        page_num = int(block.get("page_number") or 1)
        bbox_entry = _normalize_bbox_entry(block)

        # Atomic blocks: emit any pending chunk, then emit this block as its
        # own chunk regardless of size. Splitting a table mid-row or a
        # formula mid-LaTeX is worse than an oversized chunk.
        if btype in _ATOMIC_BLOCK_TYPES:
            flush()
            chunks.append({
                "content": text,
                "block_types": [btype],
                "section_title": last_section,
                "page_number": page_num,
                "bboxes": [bbox_entry] if bbox_entry else [],
            })
            continue

        # Headings start a new chunk and become the running section title,
        # EXCEPT when the pending chunk is heading-only — then fold this
        # heading into it with an em-dash separator. Cascades naturally for
        # 3+ consecutive headings (H1 — H2 — H3) and works across page
        # boundaries when callers pass document-flat blocks.
        if btype == "heading":
            if current_types == ["heading"] and current_text.strip():
                current_text = current_text.strip() + " — " + text
                last_section = current_text[:300]
                current_section = last_section
                if bbox_entry:
                    current_bboxes.append(bbox_entry)
                # current_page stays on the FIRST heading's page — that's the
                # primary citation page for the merged chunk.
                continue
            flush()
            last_section = text[:300]
            current_section = last_section
            current_text = text
            current_types = ["heading"]
            current_page = page_num
            current_bboxes = [bbox_entry] if bbox_entry else []
            continue

        # Plain text / footer block. If it alone exceeds the chunk size,
        # split it with the recursive splitter and emit each piece as its
        # own chunk (the section_title still applies; every piece cites the
        # original block's bbox since we can't sub-divide it).
        if len(text) > DEFAULT_CHUNK_CHARS:
            flush()
            for piece in _splitter.split_text(text):
                piece = piece.strip()
                if not piece:
                    continue
                chunks.append({
                    "content": piece,
                    "block_types": [btype],
                    "section_title": last_section,
                    "page_number": page_num,
                    "bboxes": [bbox_entry] if bbox_entry else [],
                })
            continue

        # Greedy pack: append to the current chunk if it fits, else flush
        # and start a new one with this block.
        joined = (current_text + "\n\n" + text) if current_text else text
        if len(joined) <= DEFAULT_CHUNK_CHARS:
            current_text = joined
            current_types.append(btype)
            if current_page is None:
                current_page = page_num
            if not current_section:
                current_section = last_section
            if bbox_entry:
                current_bboxes.append(bbox_entry)
        else:
            flush()
            current_text = text
            current_types = [btype]
            current_section = last_section
            current_page = page_num
            current_bboxes = [bbox_entry] if bbox_entry else []

    flush()
    return chunks
