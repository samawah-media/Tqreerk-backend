"""Sub-page chunking — canonical copy lives in ai-service/core/chunking.py.

SYNC RULE: keep this file byte-identical to ai-service/core/chunking.py
(below the module docstring). The two services run in separate containers
so they can't share a package, but chunk boundaries MUST be identical —
any divergence makes existing report_chunks rows incompatible with newly
ingested ones. Edit both files together, then re-ingest affected reports.

A CI check (scripts/check_chunker_sync.py) enforces this at pull-request
time so drift is caught before it reaches production.
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
    """Pack a page's structured blocks into chunk strings (text only).

    Convenience wrapper around `chunk_blocks_with_meta` for callers that
    don't need per-chunk metadata.
    """
    return [c["content"] for c in chunk_blocks_with_meta(blocks)]


def chunk_blocks_with_meta(blocks: list[dict]) -> list[dict]:
    """Same as chunk_blocks but each result is `{content, block_types,
    section_title}` so the persist layer can attach per-chunk metadata.

    `block_types` lists the kinds of blocks that contributed to this chunk,
    in order. `section_title` is the most recent heading seen at or before
    the start of this chunk (empty string if none yet).
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

    def flush() -> None:
        nonlocal current_text, current_types
        if current_text.strip():
            chunks.append({
                "content": current_text.strip(),
                "block_types": list(current_types),
                "section_title": current_section,
            })
        current_text = ""
        current_types = []

    for block in sorted_blocks:
        btype = block.get("type") or "text"
        text = (block.get("content") or "").strip()
        if not text:
            continue

        # Atomic blocks: emit any pending chunk, then emit this block as its
        # own chunk regardless of size. Splitting a table mid-row or a
        # formula mid-LaTeX is worse than an oversized chunk.
        if btype in _ATOMIC_BLOCK_TYPES:
            flush()
            chunks.append({
                "content": text,
                "block_types": [btype],
                "section_title": last_section,
            })
            continue

        # Headings start a new chunk and become the running section title.
        if btype == "heading":
            flush()
            last_section = text[:300]
            current_section = last_section
            current_text = text
            current_types = ["heading"]
            continue

        # Plain text / footer block. If it alone exceeds the chunk size,
        # split it with the recursive splitter and emit each piece as its
        # own chunk (the section_title still applies).
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
                })
            continue

        # Greedy pack: append to the current chunk if it fits, else flush
        # and start a new one with this block.
        joined = (current_text + "\n\n" + text) if current_text else text
        if len(joined) <= DEFAULT_CHUNK_CHARS:
            current_text = joined
            current_types.append(btype)
            if not current_section:
                current_section = last_section
        else:
            flush()
            current_text = text
            current_types = [btype]
            current_section = last_section

    flush()
    return chunks
