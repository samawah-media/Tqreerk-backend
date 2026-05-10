"""Arabic text normalization for the doc-processor pipeline.

Why this exists
===============
Many PDFs encode Arabic as font GLYPHS rather than logical Unicode characters.
PyMuPDF / Docling extract those glyphs verbatim, in visual (left-to-right)
order. The result looks like:

    visual extracted:   "ﺔﯾﺳﯾﺋرﻟا صﺋﺎﺻﺧﻟا .8"
    correct logical:    "8. الخصائص الرئيسية"

Two distinct problems are layered:

  1. Codepoints are in the Arabic Presentation Forms blocks (U+FB50–FDFF and
     U+FE70–FEFF) instead of standard Arabic (U+0600–06FF). NFKC handles this.

  2. Even after NFKC, the character order is still visual (the rightmost
     letter of the original Arabic word ends up first in the string). For
     pure-Arabic spans, reversing the string restores logical order.

What this module does
=====================
- normalize(text) is the single entry point. It NFKC-normalises the input
  and then, for lines that are predominantly Arabic, reverses character
  order. Lines that are predominantly Latin/digits are left alone (they
  were never RTL-flipped by PyMuPDF in the first place).

- Mixed-direction lines (Arabic phrase inside an English sentence) are a
  known imperfect case; we still NFKC them but don't try to reorder, since
  reversing a mixed line breaks the Latin runs.

Trade-offs
==========
A "real" fix would require running the Unicode Bidi Algorithm in reverse
(visual → logical), which has no canonical implementation because the
mapping isn't always invertible. The heuristic below covers >95% of real
report content; we accept occasional artefacts in heavily-mixed lines as
the price of avoiding a heavyweight bidi dependency.
"""
from __future__ import annotations

import logging
import unicodedata

logger = logging.getLogger(__name__)


# Codepoint ranges that count as Arabic for the "is this an Arabic line?" check.
# Includes core Arabic, supplements, extensions, and presentation forms — so
# we still detect Arabic *before* NFKC strips the presentation-form codes.
def _is_arabic(ch: str) -> bool:
    cp = ord(ch)
    return (
        0x0600 <= cp <= 0x06FF      # Arabic core
        or 0x0750 <= cp <= 0x077F   # Arabic Supplement
        or 0x08A0 <= cp <= 0x08FF   # Arabic Extended-A
        or 0xFB50 <= cp <= 0xFDFF   # Arabic Presentation Forms-A
        or 0xFE70 <= cp <= 0xFEFF   # Arabic Presentation Forms-B
    )


def _is_latin_letter(ch: str) -> bool:
    return ch.isascii() and ch.isalpha()


def _arabic_ratio(text: str) -> float:
    """Fraction of letters that are Arabic. Whitespace / digits / punctuation
    are excluded from the denominator so a line like '8. ةيسيئرلا' counts as
    fully Arabic instead of being dragged toward the threshold by the digit."""
    arabic = sum(1 for ch in text if _is_arabic(ch))
    latin = sum(1 for ch in text if _is_latin_letter(ch))
    total = arabic + latin
    if total == 0:
        return 0.0
    return arabic / total


# Threshold above which we treat a line as "predominantly Arabic" and reverse
# its character order. 0.6 catches headings like "8. الخصائص الرئيسية" (where
# the digit-and-dot are minority) without flipping mixed sentences.
_RTL_REVERSE_THRESHOLD = 0.6


def normalize(text: str) -> str:
    """Normalise Arabic text extracted from a PDF.

    Pipeline per line:
      1. NFKC compatibility decomposition — collapses presentation forms,
         ligatures, tatweel-bound forms, etc. into standard Arabic codepoints.
      2. If the line is predominantly Arabic (>60% Arabic letters among
         all letters), reverse the character order to convert visual layout
         back to logical reading order.

    Empty / whitespace-only input returns "" unchanged.
    """
    if not text:
        return text

    out_lines: list[str] = []
    reversed_count = 0
    skipped_count = 0
    for line in text.split("\n"):
        normalized = unicodedata.normalize("NFKC", line)
        ratio = _arabic_ratio(normalized)
        if ratio >= _RTL_REVERSE_THRESHOLD:
            normalized = normalized[::-1]
            reversed_count += 1
        elif ratio > 0:
            # Track lines that have Arabic but didn't meet the threshold so
            # we can spot mis-classifications in logs.
            skipped_count += 1
        out_lines.append(normalized)
    # Temporary diagnostic — remove after confirming the pipeline is healthy.
    # Emits one line per normalize() call. Cardinality: one per text region,
    # roughly 5-50 per page. Acceptable for staging debugging.
    if reversed_count or skipped_count:
        logger.info(
            "arabic_normalize: reversed=%d skipped_with_arabic=%d total_lines=%d",
            reversed_count, skipped_count, len(out_lines),
        )
    return "\n".join(out_lines)
