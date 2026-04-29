"""Recursive text splitter for sub-page chunking.

Why we chunk
============
Embedding a whole page (1500-3000 tokens) blurs everything together: a single
vector has to represent a chart caption, two paragraphs of analysis, and a
boilerplate footer at once. Retrieval becomes coarse — every page either fully
matches or fully misses, even if only one paragraph is relevant.

Sub-page chunking (~500 tokens with overlap) gives retrieval a tighter unit:
top-K returns only the relevant paragraph(s), the LLM sees less noise, and
hybrid retrieval (dense + BM25) ranks more meaningfully.

Implementation
==============
Uses LangChain's `RecursiveCharacterTextSplitter` (battle-tested, the de-facto
default for RAG chunking). We pull only the `langchain-text-splitters`
sub-package — no `langchain` core, no chains, no LLM clients — to keep the
dependency surface small.

The splitter walks a list of separators in priority order: paragraph break →
single newline → sentence terminator (Latin + Arabic) → word break → hard
character cut. Whichever separator first produces parts <= chunk_size is used.

Token sizing
============
We size in CHARACTERS, not tokens. Empirically, mixed Arabic + English content
runs ~3-4 chars per token, so chunk_size=2000 yields ~500-650 tokens per
chunk — squarely in the sweet spot for text-embedding-004 and Gemini Flash.
"""
from langchain_text_splitters import RecursiveCharacterTextSplitter

DEFAULT_CHUNK_CHARS = 2000   # ~500 tokens
DEFAULT_OVERLAP_CHARS = 200  # ~50 tokens of context bleed

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
