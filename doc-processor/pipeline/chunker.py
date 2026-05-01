"""Sub-page chunking — kept byte-identical to ai-service/core/chunking.py.

Why this exists here
====================
Option B of the ingest redesign moves chunking + embedding into the
doc-processor so we don't ship 100 MB of structured blocks back over HTTP.
The chunking algorithm MUST match what ai-service produced previously,
otherwise existing report_chunks rows would be incompatible with newly
ingested ones (different chunk boundaries → different retrieval).

If you change anything here, change it in ai-service/core/chunking.py too
and re-ingest — or move that file to call into here at import time.
"""
from __future__ import annotations

from langchain_text_splitters import RecursiveCharacterTextSplitter

DEFAULT_CHUNK_CHARS = 2000
DEFAULT_OVERLAP_CHARS = 200

_ATOMIC_BLOCK_TYPES = frozenset({"table", "figure", "formula"})

_SEPARATORS = [
    "\n\n",
    "\n",
    ". ",
    "؟ ",
    "۔ ",
    "! ",
    "? ",
    "؛ ",
    "; ",
    " ",
    "",
]

_splitter = RecursiveCharacterTextSplitter(
    chunk_size=DEFAULT_CHUNK_CHARS,
    chunk_overlap=DEFAULT_OVERLAP_CHARS,
    separators=_SEPARATORS,
    keep_separator=True,
    length_function=len,
)


def chunk_text(text: str) -> list[str]:
    text = (text or "").strip()
    if not text:
        return []
    return _splitter.split_text(text)


def chunk_blocks_with_meta(blocks: list[dict]) -> list[dict]:
    """Pack a page's structured blocks into chunks with per-chunk metadata.

    Returns: list of `{content, block_types, section_title}`.
    """
    if not blocks:
        return []

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
    last_section = ""

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

        if btype in _ATOMIC_BLOCK_TYPES:
            flush()
            chunks.append({
                "content": text,
                "block_types": [btype],
                "section_title": last_section,
            })
            continue

        if btype == "heading":
            flush()
            last_section = text[:300]
            current_section = last_section
            current_text = text
            current_types = ["heading"]
            continue

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
