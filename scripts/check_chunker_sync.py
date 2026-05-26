"""CI guard: verify that the two chunker files stay in sync.

Run from the repo root:
    python scripts/check_chunker_sync.py

Exits 0 if the implementation bodies match, 1 (with diff) if they've drifted.
The module docstrings are intentionally different so we compare everything
after the first blank line that follows the closing triple-quote.
"""
import sys
from pathlib import Path

CANONICAL = Path("ai-service/core/chunking.py")
COPY      = Path("doc-processor/pipeline/chunker.py")


def _body(path: Path) -> str:
    """Return the file content after the module docstring."""
    src = path.read_text(encoding="utf-8")
    # Skip past the closing triple-quote of the module docstring.
    end = src.find('"""', src.find('"""') + 3) + 3
    return src[end:].lstrip("\n")


canonical_body = _body(CANONICAL)
copy_body      = _body(COPY)

if canonical_body == copy_body:
    print("chunker sync OK")
    sys.exit(0)

import difflib
diff = list(difflib.unified_diff(
    canonical_body.splitlines(keepends=True),
    copy_body.splitlines(keepends=True),
    fromfile=str(CANONICAL),
    tofile=str(COPY),
))
print("ERROR: chunker files have drifted — edit both and re-run this check.\n")
print("".join(diff))
sys.exit(1)
