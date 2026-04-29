"""Doc-processor settings — env-driven so the same image can run staging /
production with different model variants and thresholds without rebuilds."""
from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    # ── Runtime ──────────────────────────────────────────────────────────────
    environment: str = "staging"                 # tags Sentry events
    sentry_dsn: str  = ""                        # optional
    port: int = 8080                             # Cloud Run injects $PORT

    # ── Models ───────────────────────────────────────────────────────────────
    # Florence-2 variants: "base-ft" (~1GB, faster) or "large-ft" (~3GB, better
    # captions). base-ft is the default — a 30s extra cold-start of large-ft
    # rarely earns its keep on research-report figures.
    florence_model_id: str = "microsoft/Florence-2-base-ft"

    # EasyOCR languages — ['ar', 'en'] downloads ~120MB per language at first
    # run. Add more via env (CSV) without rebuilding the image.
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
    # Docling + EasyOCR — total ~9 GB on the L4's 24 GB headroom.
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

    model_config = {"env_file": ".env", "env_file_encoding": "utf-8"}


settings = Settings()  # type: ignore[call-arg]
