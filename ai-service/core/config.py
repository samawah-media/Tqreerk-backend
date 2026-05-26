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
    vertex_location: str = "global"  # Vertex AI endpoint — `global` auto-routes every request to the nearest available region. Removes the cross-region read-timeout failures we saw from me-central1 Cloud Run → us-central1 Vertex, and gives the highest aggregate quota of any single endpoint. NOTE: requests can be processed in any Google datacenter worldwide; if a customer contract pins data residency to a specific region, override per-deploy via env var (e.g. VERTEX_LOCATION=asia-south1).
    gemini_api_key: str = ""           # optional: AI Studio key — if set, used INSTEAD of Vertex AI
    internal_api_key: str = ""         # optional: shared secret for .NET → Python calls

    # ── Observability ────────────────────────────────────────────────────────
    sentry_dsn: str = ""               # optional — if set, errors + perf traces go to Sentry
    environment: str = "staging"       # "staging" | "production" — Sentry environment tag

    # ── Model names (override per env without code changes) ──────────────────
    gemini_vision_model: str  = "gemini-2.5-flash"        # PDF page → text + chart descriptions
    gemini_chat_model: str    = "gemini-2.5-flash"        # RAG chat answers (fast, small context)
    gemini_summary_model: str = "gemini-2.5-flash"        # full-report summarization (deeper analysis)
    # Fallback model used when gemini_summary_model returns 429 RESOURCE_EXHAUSTED.
    # Lite has a separate Vertex quota pool, so a primary-quota burst
    # spills over here instead of failing the user-visible job. Quality
    # is slightly lower but still good for summary/insights/compare.
    gemini_summary_model_fallback: str = "gemini-2.5-flash-lite"
    # Ragas eval judge — Lite tier so the eval pipeline doesn't burn the
    # chat-tier quota every time we score a chat trace.
    ragas_judge_model: str    = "gemini-2.5-flash-lite"
    gemini_embed_model: str   = "gemini-embedding-001"    # multilingual embedder via Vertex/AI Studio
    # Output dimension. gemini-embedding-001 supports Matryoshka so we can
    # request 768/1536/3072 natively. 768 keeps DB storage compact and matches
    # the report_chunks.embedding vector(768) column. Bump only with a schema
    # migration.
    gemini_embed_dim: int     = 768

    # ── Reranker (Vertex AI Ranking API) ─────────────────────────────────────
    # Replaces the doc-processor /v1/rerank path; cross-encoder over the same
    # candidate pool returned by hybrid retrieval. Multilingual (incl. Arabic)
    # and ~150ms warm. Behind a flag so we can A/B compare or disable on
    # outage; the chat path falls back to retrieval order on errors.
    reranker_vertex_model: str = "semantic-ranker-default-004"

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

    # ── Multimodal page-image tool ───────────────────────────────────────────
    # Exposes `get_page_image(report_id, page)` to the chat agent. The tool
    # renders one PDF page (PyMuPDF, 150 DPI), base64-encodes it, and the
    # agent loop injects it as a multimodal HumanMessage so the next Gemini
    # call can read the chart / figure directly. Use case: visual questions
    # the text retrieval path can't answer (exact data points off a chart,
    # legend colors, fine layout details).
    #
    # Disabled flips the tool into a stub that returns "tool disabled" —
    # safe instant rollback without redeploy.
    page_image_tool_enabled: bool = True

    # When enabled, every assistant turn records (report_id, page) pairs
    # for any get_page_image calls into chat_messages.ImagesAttached. On
    # the next turn, those pages are re-rendered and re-injected as
    # multimodal HumanMessages so the agent doesn't have to re-call the
    # tool for pages it already analysed. Limits the number of recent
    # assistant turns we rehydrate from to keep multimodal-token cost
    # bounded on long conversations.
    page_image_persist_enabled: bool = True
    page_image_persist_lookback_turns: int = 2

    # ── Fuzzy / trigram retrieval arm ────────────────────────────────────────
    # Third RRF arm using pg_trgm against arabic_normalize("Content"). Catches
    # typos, OCR errors, and partial-name lookups that dense + BM25 both miss.
    # Backed by the GIN expression index created in
    # Feature_ArabicSearchTuning. Disable to fall back to two-arm hybrid.
    #
    # The trigram filter uses pg_trgm's default similarity_threshold (0.3),
    # which is empirically a reasonable floor for Arabic text — tighter
    # cuts off legitimate stem/spelling variants, looser pulls in noise.
    fuzzy_retrieval_enabled: bool = True

    # ── Query rewriter (bilingual + decomposition) ───────────────────────────
    # One Gemini Flash call before retrieval that produces up to N variants
    # of the user's question — typically the cross-language counterpart
    # (Arabic↔English) plus optional decomposition for multi-part questions.
    # The original query is always kept; variants are added on top.
    # Disable with query_rewriter_enabled=false to A/B compare retrieval
    # quality without the extra API call.
    query_rewriter_enabled: bool      = True
    # Auxiliary models — moved off gemini-2.5-flash 2026-05-08 to a
    # cheaper / faster Lite tier so they don't contend for the same
    # quota pool as user-facing chat & summarization.
    query_rewriter_model: str         = "gemini-2.5-flash-lite"
    query_rewriter_max_variants: int  = 2     # variants beyond the original
    query_rewriter_timeout_seconds: float = 4.0  # hard cap so it never blocks chat for long

    # ── Hybrid retrieval — BM25 vector floor ─────────────────────────────────
    # BM25 alone happily promotes keyword-heavy noise (page numbers, footers,
    # boilerplate) that has no semantic relevance. With this floor active, a
    # BM25 candidate only contributes to RRF if its vector cosine similarity
    # to the query is at least `hybrid_bm25_vector_floor`. Set 0.0 to disable
    # the gate (revert to legacy behaviour). Cosine of 0.3 keeps real Arabic
    # / English keyword hits while filtering pure-keyword junk.
    hybrid_bm25_vector_floor: float = 0.3

    # ── HyQE (Hypothetical-Question Embeddings, ingest-time) ─────────────────
    # For each real chunk, ask a fast LLM for N questions the chunk answers,
    # embed those questions, and persist them as additional rows linked to
    # the parent chunk via ParentChunkId. At retrieval time the SQL swaps the
    # parent's content in for any hypothetical hit, so the agent only sees
    # real prose. Cost: 1 Flash call per chunk at ingest, plus extra
    # embeddings + storage. Recall lift on Q-style queries: ~15-30% in
    # practice. Disable with hyqe_enabled=false to revert to chunk-only embeds.
    hyqe_enabled: bool                = True
    # Same Lite-tier reasoning as query_rewriter — HyQE generates short
    # paraphrase questions per chunk; quality-cost ratio favours flash-lite
    # heavily, especially during bulk ingest where this fires once per
    # chunk (= hundreds of calls per report).
    hyqe_model: str                   = "gemini-2.5-flash-lite"
    hyqe_questions_per_chunk: int     = 3
    hyqe_max_concurrency: int         = 5     # parallel Flash calls during ingest
    hyqe_timeout_seconds: float       = 10.0  # per-chunk hard cap

    # ── Groundedness check (inline post-stream) ──────────────────────────────
    # One Gemini Flash call after the answer streams: scores 0..1 whether the
    # answer's factual claims appear in the retrieved chunks. If the score
    # drops below `groundedness_warn_threshold`, the chat endpoint emits an
    # extra SSE `warning` event AFTER `done` and logs to Sentry. Adds ~600ms
    # AFTER the user already received the answer — never blocks first token.
    groundedness_check_enabled: bool          = True
    # Lite tier — groundedness is a binary-ish "do these claims appear
    # in these chunks" check, not a creative task. Saves quota for chat.
    groundedness_check_model: str             = "gemini-2.5-flash-lite"
    groundedness_check_timeout_seconds: float = 5.0
    groundedness_warn_threshold: float        = 0.7   # below → emit warning + Sentry

    # ── Parent-section retrieval ─────────────────────────────────────────────
    # When a chunk hits, also fetch every other chunk on the same page that
    # shares its section_title and concatenate them as `section_content`.
    # This gives the LLM the full section context (charts + their caption,
    # bullet list + its intro, table + its surrounding paragraphs) without
    # blowing up top_k. Capped at `parent_section_max_chars` so a giant
    # section doesn't poison the context window.
    parent_section_enabled: bool      = True
    parent_section_max_chars: int     = 4000   # ~1000 tokens
    parent_section_max_chunks: int    = 8      # absolute cap on chunks merged

    # ── Worker mode ──────────────────────────────────────────────────────────
    # "api"    → FastAPI server (the default Cloud Run service)
    # "worker" → background loop that polls ai_jobs and runs ingest/translate
    # Set per-service in deployment so the same image can fan out into two
    # Cloud Run services with separate autoscaling.
    worker_mode: str               = "api"
    worker_url: str                = ""   # worker Cloud Run URL — used to wake scaled-to-zero instances
    worker_poll_interval_seconds: float = 3.0
    worker_stale_job_minutes: int  = 30   # mark Processing > N min as Failed
    # How many jobs to claim and run concurrently per worker instance.
    # Each job is a Gemini network call (I/O-bound); asyncio.gather runs them
    # in parallel threads via asyncio.to_thread, so the only limit is Gemini
    # RPM quota (Gemini 2.5 Flash = 1000 RPM >> 5 concurrent jobs).
    worker_concurrency: int        = 5

    # ── doc-processor (GPU pipeline) ─────────────────────────────────────────
    # Optional alternative extractor for ingest. When enabled, ingest calls
    # the doc-processor Cloud Run service for layout-aware extraction; on
    # any failure we transparently fall back to Gemini Vision so chat is
    # never blocked. Default is OFF — flip when the new pipeline is proven.
    doc_processor_enabled: bool   = False
    doc_processor_url: str        = ""    # e.g. https://doc-processor-xxx.run.app
    doc_processor_token: str      = ""    # optional X-Internal-Token shared secret
    doc_processor_timeout_seconds: float = 600.0  # full-PDF can take minutes

    # Per-job HTTP read timeout for /v1/ingest_job. Since 2026-05-08 the
    # doc-processor processes ingestion synchronously (request stays open
    # for the whole pipeline), so this is effectively the wall-clock cap
    # per job — anything taking longer is treated as failed and the
    # _pending_job_watcher retries. 600 s = 10 min covers typical reports.
    doc_processor_ingest_timeout_seconds: float = 600.0

    # Bounded concurrency for bulk-ingest dispatch. Should match the
    # doc-processor's --max-instances so we saturate available pods without
    # creating queue depth Cloud Run can't scale into. Each in-flight
    # trigger holds an HTTP connection open until that job completes.
    doc_processor_max_concurrency: int = 2
    # Above this size we render pages individually instead of sending the
    # whole PDF — Cloud Run's 32 MB request body limit applies, and base64
    # adds ~33% overhead. 22 MB raw → ~29 MB on the wire, comfortably under.
    doc_processor_max_pdf_mb: float = 22.0

    # ── ingest_full path (Option B) ──────────────────────────────────────────
    # When True, ingest calls doc-processor /v1/ingest_full which returns
    # chunks-with-embeddings in one round-trip (~2-3 MB for 134 pages) instead
    # of the legacy /v1/extract_document path (~100 MB of structured blocks).
    # This eliminates the OOM that killed the worker during JSON parse of the
    # large extraction response. Off by default for safe rollout — flip via
    # env var when ready. When True, the per-page Gemini Vision fallback no
    # longer runs on doc-processor failures (failed jobs go straight to
    # Failed); flip back to False if you need that fallback behaviour.
    doc_processor_ingest_full_enabled: bool = True

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
    # Per-metric timeout — used as both:
    #   • Ragas RunConfig.timeout (per individual LLM call inside evaluate)
    #   • a multiplier for the outer asyncio.wait_for that hard-caps the
    #     whole evaluate() wallclock at timeout * (n_metrics + 2).
    # 90 s gives each Gemini judge call enough headroom for retries while
    # keeping the worst-case eval bounded around 7-8 min for the 3-metric
    # set (faithfulness, answer_relevancy, context_precision). Was 60 s,
    # which routinely blew up under retry storms.
    eval_metric_timeout_seconds: float = 90.0

    # ── Daily quotas (cost protection) ───────────────────────────────────────
    # Counts rolling 24-hour windows over ai_jobs (per-org) and chat_messages
    # (per-user — the chat path is owned by an individual, not an org). When
    # the cap is hit, the relevant API endpoint returns 429. A value of 0
    # disables that specific cap. Tune via env var without redeploy. Defaults
    # err on the side of "generous for real users, blocks only obvious abuse
    # loops."
    #
    # Why chat is per-user: chat sessions are bound to a single UserId, and
    # the agent's accessible-report scope is computed per-user (Published OR
    # own-org membership). Capping chat per-org would punish quiet orgs whose
    # one chatty user overwhelmed everyone else's allowance.
    quota_enabled: bool                 = False
    quota_daily_ingest_per_org: int     = 20      # /reports/ingest, EnqueueIngest
    quota_daily_translate_per_org: int  = 10      # /reports/translate
    quota_daily_chat_per_user: int      = 200     # /chat/.../messages

    model_config = {"env_file": ".env", "env_file_encoding": "utf-8"}

    def model_post_init(self, __context) -> None:
        self.database_url = _normalize_database_url(self.database_url)


settings = Settings()  # type: ignore[call-arg]
