"""Doc-processor settings — env-driven so the same image can run staging /
production with different model variants and thresholds without rebuilds."""
from pydantic_settings import BaseSettings


# Maps .NET / Npgsql connection-string keys to libpq keys so the doc-processor
# can accept the SAME DATABASE_URL secret the .NET API and ai-service use.
_DOTNET_TO_LIBPQ = {
    "host":        "host",
    "server":      "host",
    "port":        "port",
    "database":    "dbname",
    "username":    "user",
    "user id":     "user",
    "password":    "password",
    "ssl mode":    "sslmode",
    "sslmode":     "sslmode",
}


def _normalize_database_url(value: str) -> str:
    """Accept a libpq URI (postgres://...), libpq keyword string, or a .NET
    Npgsql connection string (Host=...;Database=...;Username=...;Password=...).
    .NET strings are converted to libpq keyword format that psycopg3 understands.
    """
    v = value.strip()
    if not v:
        return v
    if v.startswith(("postgres://", "postgresql://")):
        return v
    if "=" in v and ";" in v:   # .NET semicolon-separated format
        parts = []
        for chunk in v.split(";"):
            if "=" not in chunk:
                continue
            k, _, val = chunk.partition("=")
            key = _DOTNET_TO_LIBPQ.get(k.strip().lower())
            if not key:
                continue
            val = val.strip()
            if any(c in val for c in " '\\"):
                val = "'" + val.replace("\\", "\\\\").replace("'", "\\'") + "'"
            parts.append(f"{key}={val}")
        return " ".join(parts)
    return v


class Settings(BaseSettings):
    # ── Runtime ──────────────────────────────────────────────────────────────
    environment: str = "staging"                 # tags Sentry events
    sentry_dsn: str  = ""                        # optional
    port: int = 8080                             # Cloud Run injects $PORT

    # ── Models ───────────────────────────────────────────────────────────────
    # Florence-2 variants: "base-ft" (~1GB, faster) or "large-ft" (~3GB, better
    # captions). base-ft is the default — a 30s extra cold-start of large-ft
    # rarely earns its keep on research-report figures.
    # NOTE 2026-05-08: Florence-2 is no longer loaded at startup (see main.py).
    # Figure captioning is now served by Vertex Gemini Vision via figures.py.
    # This setting is kept for backwards compatibility with any external
    # tooling that reads it; flip back to local Florence-2 by re-enabling
    # the warmup branch in main.py if needed.
    florence_model_id: str = "microsoft/Florence-2-base-ft"

    # Gemini Vision model used for figure captioning. Lite tier so the
    # captioning burst from a single ingest (one call per figure × N
    # figures × M reports) doesn't hit the Flash quota that user-facing
    # chat / summary share. Override per-deploy via GEMINI_VISION_MODEL.
    gemini_vision_model: str = "gemini-2.5-flash-lite"

    # Drop recognition results whose confidence falls below this threshold.
    # RapidOCR's detection model already prevents hallucination on non-text
    # crops; this threshold removes borderline reads on degraded scan regions.
    ocr_min_confidence: float = 0.5

    # Kept for backwards compatibility — not used at runtime (EasyOCR language
    # selection is baked into the Reader(['ar','en']) constructor call).
    ocr_languages: str = "ar,en"

    # Multilingual sentence-transformer for /v1/embed.
    # BAAI/bge-m3 — 1024 dims, ~2.5 GB VRAM, top-of-class on Arabic retrieval
    # (MIRACL ~75 vs e5-base ~51) and strong cross-lingual AR↔EN. Unlike E5
    # it does NOT require "query: " / "passage: " prefixes — raw text is
    # what it was trained on. Schema column is vector(1024) accordingly.
    embed_model_id: str = "BAAI/bge-m3"
    embed_max_seq_length: int = 8192       # bge-m3 supports up to 8k tokens; chunks are well under
    embed_batch_size: int = 16             # bge-m3 is bigger than e5; smaller batch keeps VRAM safe

    # Cross-encoder reranker for /v1/rerank. Trained as a sibling to bge-m3
    # so query and chunk vectors live in compatible representation spaces;
    # joint scoring is meaningfully better than re-scoring with cosine.
    # ~568M params, ~2.5 GB VRAM in fp16 alongside bge-m3 + Florence-2 +
    # Docling + Surya OCR — total ~11 GB on the L4's 24 GB headroom.
    rerank_model_id: str = "BAAI/bge-reranker-v2-m3"
    rerank_max_seq_length: int = 1024      # 512 query + 512 passage is plenty for chunk-level rerank
    rerank_batch_size: int = 8             # bge-reranker-v2-m3 is heavier than the embedder; smaller batch

    # Hardware / inference toggles.
    device: str = "cuda"                         # "cuda" | "cpu" — set "cpu" for local dev
    fp16: bool  = True                           # half-precision for VLM speed

    # ── Pipeline behaviour ───────────────────────────────────────────────────
    # When Docling produces text directly (digital PDF), we trust it. When the
    # text is empty / suspiciously short, we run OCR on the region as fallback.
    ocr_fallback_min_chars: int = 20             # below this length → OCR

    # Skip OCR fallback on regions that are too small to contain meaningful
    # text or are visually empty (solid-color rectangles, decorative borders).
    # Each skipped region saves ~2-3 s of Surya OCR work; a typical bulk
    # ingest has 5-10 such regions per page.
    ocr_min_region_area_ratio: float = 0.005     # < 0.5% of page area → skip
    ocr_min_pixel_stddev: float = 8.0            # solid-color crops → skip

    # Skip Florence-2 captioning on figures that are too small to be
    # informative (logos, decoration, page-corner glyphs). Even when the
    # caller asks for captioning, anything below this threshold is treated
    # as a non-content figure and gets a "[Figure]" stub.
    figure_caption_min_area_ratio: float = 0.02  # < 2% of page area → no caption

    # Cap how many figures / tables / formulas we process per page so a
    # malformed PDF can't blow up GPU memory or runtime.
    max_blocks_per_page: int = 200
    max_table_chars: int     = 50000
    max_formula_chars: int   = 5000

    # ── Auth ─────────────────────────────────────────────────────────────────
    # When set, the API requires this token in the X-Internal-Token header.
    # Cloud Run IAM is the primary auth layer (--no-allow-unauthenticated +
    # ai-service SA has run.invoker); this is a defence-in-depth shared
    # secret so a misconfigured IAM doesn't expose the GPU pipeline publicly.
    internal_api_token: str = ""

    # ── Database (used by /v1/ingest to persist chunks directly) ────────────────
    # Same connection string as the ai-service. When set, /v1/ingest runs the
    # full pipeline — download → extract → chunk → embed → persist — so the
    # ai-service worker only needs to mark the job Complete. No chunks-with-
    # vectors payload crosses the network.
    database_url: str = ""

    # ── Vertex AI (used by /v1/ingest_full and /v1/ingest to embed chunks) ──────
    # The doc-processor calls Vertex gemini-embedding-001 directly so that
    # embeddings stay in the same vector space as the existing report_chunks
    # rows. Same model + dim as ai-service; no DB migration needed.
    gcp_project_id: str = ""
    # `global` auto-routes to the nearest available region — removes the
    # cross-region read-timeout failures and gives the highest aggregate
    # quota. Override via env var (VERTEX_LOCATION=asia-south1, etc.) if a
    # customer contract pins data residency to a specific region.
    vertex_location: str = "global"
    embed_vertex_model: str = "gemini-embedding-001"
    embed_vertex_dim: int   = 768

    # ── /v1/ingest_full chunk cap ────────────────────────────────────────────
    # Safety ceiling so a malformed PDF can't produce a 50 MB embedding payload.
    # 5000 chunks ≈ 15 MB of vectors + ~15 MB of text — still well under worker
    # memory limits. Calls that would exceed are rejected with HTTP 413; caller
    # falls back to per-page mode.
    ingest_full_max_chunks: int = 5000

    # ── Markdown export upload (Stage 1.5) ──────────────────────────────────
    # When enabled, /v1/ingest uploads the Docling markdown export to the
    # SAME folder as the source PDF, with the basename swapped to `.md`.
    # E.g. gs://bucket/reports/<id>/file.pdf → gs://bucket/reports/<id>/file.md
    # — co-located, deterministic, and listed naturally next to the PDF in
    # storage browsers. Retries overwrite in place. Empty markdown is uploaded
    # too: the file's existence is the "got past extract" signal.
    #
    # Skipped (with a log line) when the source URL is not gs:// — HTTPS-only
    # callers don't have a clear co-location target.
    markdown_export_enabled: bool = True

    model_config = {"env_file": ".env", "env_file_encoding": "utf-8"}

    def model_post_init(self, __context) -> None:
        self.database_url = _normalize_database_url(self.database_url)


settings = Settings()  # type: ignore[call-arg]
