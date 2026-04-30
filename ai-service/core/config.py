from pydantic_settings import BaseSettings


# Maps .NET / Npgsql connection-string keys to libpq keys.
# Used so the Python service can accept the SAME DATABASE_URL secret the .NET API uses.
_DOTNET_TO_LIBPQ = {
    "host":            "host",
    "server":          "host",
    "port":            "port",
    "database":        "dbname",
    "username":        "user",
    "user id":         "user",
    "password":        "password",
    "ssl mode":        "sslmode",
    "sslmode":         "sslmode",
    "search path":     "options",  # rarely used, mapped if present
}


def _normalize_database_url(value: str) -> str:
    """Accept either a libpq URI (postgres://...) / keyword string, or a .NET
    Npgsql connection string (Host=...;Database=...;Username=...;Password=...).

    .NET strings get converted into libpq keyword format which psycopg understands.
    """
    v = value.strip()
    if v.startswith(("postgres://", "postgresql://")):
        return v
    if "=" in v and ";" in v:  # looks like a .NET connection string
        parts = []
        for chunk in v.split(";"):
            if "=" not in chunk:
                continue
            k, _, val = chunk.partition("=")
            key = _DOTNET_TO_LIBPQ.get(k.strip().lower())
            if not key:
                continue
            val = val.strip()
            # quote values containing spaces or special chars
            if any(c in val for c in " '\\"):
                val = "'" + val.replace("\\", "\\\\").replace("'", "\\'") + "'"
            parts.append(f"{key}={val}")
        return " ".join(parts)
    return v


class Settings(BaseSettings):
    database_url: str                  # libpq URI, libpq keyword, OR .NET-style — auto-normalized
    gcp_project_id: str                # e.g. taqrrerk
    gcs_bucket: str                    # taqreerk-uploads (me-central1, Doha)
    translate_location: str = "global" # Google Translate API location
    vertex_location: str = "europe-west3"  # Vertex AI region
    gemini_api_key: str = ""           # optional: AI Studio key — if set, used INSTEAD of Vertex AI
    internal_api_key: str = ""         # optional: shared secret for .NET → Python calls

    # ── Observability ────────────────────────────────────────────────────────
    sentry_dsn: str = ""               # optional — if set, errors + perf traces go to Sentry
    environment: str = "staging"       # "staging" | "production" — Sentry environment tag

    # ── Model names (override per env without code changes) ──────────────────
    gemini_vision_model: str  = "gemini-2.5-flash"        # PDF page → text + chart descriptions
    gemini_chat_model: str    = "gemini-2.5-flash"        # RAG chat answers (fast, small context)
    gemini_summary_model: str = "gemini-2.5-flash"        # full-report summarization (deeper analysis)
    gemini_embed_model: str   = "text-embedding-004"      # 768-dim embeddings — must match DB vector(768)

    # ── Chat cache (Postgres-backed, two-tier) ───────────────────────────────
    # Exact-match cache hits skip retrieval AND the LLM call; semantic-match hits
    # also skip both, but require an embedding round-trip first. TTLs are in
    # seconds. Set chat_cache_enabled=False to bypass the cache entirely (useful
    # for A/B testing or debugging stale answers).
    chat_cache_enabled: bool         = True
    chat_cache_exact_ttl_seconds: int    = 86400   # 24 h
    chat_cache_semantic_ttl_seconds: int = 3600    # 1 h
    chat_cache_semantic_threshold: float = 0.95    # cosine similarity floor for semantic hit

    # ── Reranker (Vertex AI Ranking API) ─────────────────────────────────────
    # When enabled, retrieval pulls top-N candidates and the reranker prunes
    # them to top-K before the LLM call. Behind a flag so we can A/B compare.
    reranker_enabled: bool       = True
    reranker_model: str          = "semantic-ranker-default-004"  # multilingual incl. Arabic
    reranker_candidate_pool: int = 20    # how many to fetch from hybrid before rerank
    reranker_top_k: int          = 5     # how many to keep after rerank

    # ── Worker mode ──────────────────────────────────────────────────────────
    # "api"    → FastAPI server (the default Cloud Run service)
    # "worker" → background loop that polls ai_jobs and runs ingest/translate
    # Set per-service in deployment so the same image can fan out into two
    # Cloud Run services with separate autoscaling.
    worker_mode: str               = "api"
    worker_url: str                = ""   # worker Cloud Run URL — used to wake scaled-to-zero instances
    worker_poll_interval_seconds: float = 3.0
    worker_stale_job_minutes: int  = 30   # mark Processing > N min as Failed

    # ── doc-processor (GPU pipeline) ─────────────────────────────────────────
    # Optional alternative extractor for ingest. When enabled, ingest calls
    # the doc-processor Cloud Run service for layout-aware extraction; on
    # any failure we transparently fall back to Gemini Vision so chat is
    # never blocked. Default is OFF — flip when the new pipeline is proven.
    doc_processor_enabled: bool   = False
    doc_processor_url: str        = ""    # e.g. https://doc-processor-xxx.run.app
    doc_processor_token: str      = ""    # optional X-Internal-Token shared secret
    doc_processor_timeout_seconds: float = 600.0  # full-PDF can take minutes
    # Above this size we render pages individually instead of sending the
    # whole PDF — Cloud Run's 32 MB request body limit applies, and base64
    # adds ~33% overhead. 22 MB raw → ~29 MB on the wire, comfortably under.
    doc_processor_max_pdf_mb: float = 22.0

    # ── Langfuse (self-hosted observability) ─────────────────────────────────
    # When `langfuse_enabled` is on AND keys are present, every chat / ingest
    # request emits a trace tree with spans for each phase and a generation
    # event for the LLM call. Async-flushed in batches; failure to reach the
    # Langfuse server never breaks the user request.
    langfuse_enabled: bool        = True
    langfuse_host: str            = ""    # e.g. https://langfuse.taqreerk.internal
    langfuse_public_key: str      = ""    # pk-lf-xxx
    langfuse_secret_key: str      = ""    # sk-lf-xxx
    # Trace sampling — fraction in [0.0, 1.0]. 1.0 = trace every request.
    # Tune down in production once we're confident the volume justifies it.
    langfuse_trace_sample_rate: float = 1.0

    # ── Ragas (online RAG eval) ──────────────────────────────────────────────
    # After each chat, an Evaluation job is enqueued and the worker runs
    # reference-free Ragas metrics, posting one Langfuse score per metric back
    # onto the chat trace. Judge LLM = Gemini Flash; embeddings = bge-m3 via
    # the doc-processor. EVAL_SAMPLE_RATE gates how many chats we evaluate
    # (1.0 = all). Disable with eval_enabled=False to skip eval entirely.
    eval_enabled: bool            = True
    eval_sample_rate: float       = 1.0
    # Per-metric timeout — protects the worker from a stuck judge LLM call.
    eval_metric_timeout_seconds: float = 60.0

    # ── Per-org daily quotas (cost protection) ───────────────────────────────
    # Counts rolling 24-hour windows over ai_jobs (and chat_messages for the
    # chat cap). When an org goes over, the relevant API endpoint returns 429.
    # A value of 0 disables that specific cap. Tune via env var without
    # redeploy. Defaults err on the side of "generous for real users, blocks
    # only obvious abuse loops."
    quota_enabled: bool                = True
    quota_daily_ingest_per_org: int    = 20      # /reports/ingest, EnqueueIngest
    quota_daily_translate_per_org: int = 10      # /reports/translate
    quota_daily_chat_per_org: int      = 500     # /chat/.../messages

    model_config = {"env_file": ".env", "env_file_encoding": "utf-8"}

    def model_post_init(self, __context) -> None:
        self.database_url = _normalize_database_url(self.database_url)


settings = Settings()  # type: ignore[call-arg]
