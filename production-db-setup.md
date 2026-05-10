# Production Database Setup — Manual Steps

This file lists every SQL operation we performed on the **staging** database during AI service integration. Each step must also be performed on the **production** database (`taqreerk_production`) before the AI features (ingest, chat, summarize, translate) will work in production.

## How to run these

GCP Console → **SQL** → click your production instance → **Cloud SQL Studio** → sign in as **`postgres`** (the superuser, not the app user) → database **`taqreerk_production`**.

Run each step in order. Verify after each one before moving on.

---

## Step 1 — Enable the `pgvector` extension

The `report_chunks.embedding` and `chat_cache.question_emb` columns use `vector(768)`. The extension is built into Cloud SQL but must be activated per-database.

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

### Verify
```sql
SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';
```
Expected: one row with `vector` and a version number.

---

## Step 2 — Apply the chunking migration (`ReplaceReportPagesWithReportChunks`)

The EF migration `20260429000000_ReplaceReportPagesWithReportChunks` drops the legacy `report_pages` table and creates `report_chunks`. The vector / tsvector / metadata columns and the bilingual search-vector trigger live inside that migration via raw SQL, so you do not need to run them manually — `dotnet ef database update` handles them.

If for any reason the migration cannot run as-is (e.g. the table already exists from an older manual run), apply the equivalent SQL by hand:

```sql
-- 2a. Drop the old report_pages table + its trigger / function
DROP TRIGGER IF EXISTS report_pages_search_vector_trigger ON report_pages;
DROP FUNCTION IF EXISTS report_pages_search_vector_update();
DROP TABLE IF EXISTS report_pages;

-- 2b. Create report_chunks
CREATE TABLE report_chunks (
    "Id"          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "ReportId"    uuid NOT NULL REFERENCES reports("Id") ON DELETE CASCADE,
    "PageNumber"  integer NOT NULL,
    "ChunkIndex"  integer NOT NULL,
    "Content"     text NOT NULL,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX "IX_report_chunks_ReportId_PageNumber_ChunkIndex"
    ON report_chunks ("ReportId", "PageNumber", "ChunkIndex");

CREATE INDEX "IX_report_chunks_ReportId_PageNumber"
    ON report_chunks ("ReportId", "PageNumber");

-- 2c. DB-managed columns
ALTER TABLE report_chunks ADD COLUMN embedding     vector(768);
ALTER TABLE report_chunks ADD COLUMN metadata      jsonb NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE report_chunks ADD COLUMN search_vector tsvector;

-- 2d. Bilingual search trigger
CREATE OR REPLACE FUNCTION report_chunks_search_vector_update()
RETURNS trigger AS $$
BEGIN
    NEW.search_vector :=
        to_tsvector('arabic',  coalesce(NEW."Content", '')) ||
        to_tsvector('english', coalesce(NEW."Content", ''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS report_chunks_search_vector_trigger ON report_chunks;
CREATE TRIGGER report_chunks_search_vector_trigger
BEFORE INSERT OR UPDATE OF "Content" ON report_chunks
FOR EACH ROW EXECUTE FUNCTION report_chunks_search_vector_update();

-- 2e. GIN index on search_vector
CREATE INDEX IF NOT EXISTS "IX_report_chunks_search_vector"
    ON report_chunks USING GIN (search_vector);
```

### Verify
```sql
SELECT
  EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_name = 'report_chunks' AND column_name = 'embedding')      AS embedding_column,
  EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_name = 'report_chunks' AND column_name = 'metadata')       AS metadata_column,
  EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_name = 'report_chunks' AND column_name = 'search_vector')  AS search_vector_column,
  EXISTS (SELECT 1 FROM pg_indexes
          WHERE indexname = 'IX_report_chunks_search_vector')                    AS gin_index,
  EXISTS (SELECT 1 FROM pg_trigger
          WHERE tgname   = 'report_chunks_search_vector_trigger')                AS trigger_active;
```
All five columns must be `true`.

---

## Step 3 — Apply the chat-cache migration (`AddChatCache`)

The EF migration `20260429000100_AddChatCache` creates `chat_cache` for the two-tier semantic cache used by the chat endpoint. Same as Step 2, `dotnet ef database update` runs it for you.

Manual fallback if needed:

```sql
CREATE TABLE chat_cache (
    cache_key      text PRIMARY KEY,
    report_id      uuid NOT NULL REFERENCES reports ("Id") ON DELETE CASCADE,
    question       text NOT NULL,
    question_emb   vector(768),
    answer         text NOT NULL,
    source_pages   jsonb NOT NULL DEFAULT '[]'::jsonb,
    hit_count      integer NOT NULL DEFAULT 0,
    created_at     timestamptz NOT NULL DEFAULT now(),
    expires_at     timestamptz NOT NULL
);

CREATE INDEX "IX_chat_cache_report_expires"
    ON chat_cache (report_id, expires_at);
```

### Verify
```sql
SELECT
  EXISTS (SELECT 1 FROM information_schema.tables
          WHERE table_name = 'chat_cache')                              AS table_exists,
  EXISTS (SELECT 1 FROM pg_indexes
          WHERE indexname = 'IX_chat_cache_report_expires')             AS index_exists;
```
Both must be `true`.

---

## Step 3.5 — Apply Arabic search tuning (`Feature_ArabicSearchTuning`)

Migration `20260510000000_Feature_ArabicSearchTuning` enables `pg_trgm`, replaces the `report_chunks` FTS trigger with an Arabic-normalizing version, adds a trigram GIN index, and backfills existing rows. `dotnet ef database update` runs it for you.

If you need to apply it by hand (e.g. running ahead of the .NET deploy), use this idempotent block. **Run during off-peak — the final `UPDATE` rebuilds `search_vector` for every existing chunk row.**

```sql
-- 3.5a. pg_trgm extension (fuzzy / typo-tolerant matching)
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- 3.5b. arabic_normalize(text) — canonical form for FTS + trigram
-- IMMUTABLE + PARALLEL SAFE so it can back an expression index.
CREATE OR REPLACE FUNCTION arabic_normalize(input text)
RETURNS text
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
    SELECT lower(
        translate(
            regexp_replace(
                COALESCE(input, ''),
                E'[ً-ٟـٰ]',  -- harakat + tatweel + superscript alef
                '',
                'g'
            ),
            E'أإآٱىةؤئ',  -- variants
            E'اااايهوي'   -- canonical
        )
    )
$$;

-- 3.5c. Rewrite the FTS trigger to use arabic_normalize on the arabic arm
CREATE OR REPLACE FUNCTION report_chunks_search_vector_update()
RETURNS trigger AS $$
BEGIN
    NEW.search_vector :=
        to_tsvector('arabic',  arabic_normalize(NEW."Content")) ||
        to_tsvector('english', COALESCE(NEW."Content", ''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- 3.5d. Trigram GIN index (used by the fuzzy arm in services/tools.py)
CREATE INDEX IF NOT EXISTS "IX_report_chunks_content_trgm"
ON report_chunks
USING GIN (arabic_normalize("Content") gin_trgm_ops);

-- 3.5e. Backfill — fires the trigger on every existing row to rebuild
-- search_vector under the new normalization rule. No-op UPDATE.
UPDATE report_chunks SET "Content" = "Content";
```

### Verify
```sql
SELECT
  EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm')                          AS pg_trgm_enabled,
  EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'arabic_normalize')                      AS normalize_function,
  EXISTS (SELECT 1 FROM pg_indexes
          WHERE indexname = 'IX_report_chunks_content_trgm')                             AS trigram_index,
  -- The trigger body now references arabic_normalize — proves Step 3.5c ran.
  EXISTS (SELECT 1 FROM pg_proc
          WHERE proname = 'report_chunks_search_vector_update'
            AND pg_get_functiondef(oid) LIKE '%arabic_normalize%')                       AS trigger_updated;
```
All four must be `true`. Sanity check the normalizer behaves:
```sql
SELECT arabic_normalize('السَّعُوْدِيَّةِ') = arabic_normalize('السعوديه') AS folds_match;
-- expected: t
```

> **Heads-up on the backfill duration.** Cost scales with `report_chunks` row count. Staging (~100k rows) finishes in seconds; production may take longer. The trigger is `BEFORE INSERT OR UPDATE OF "Content"`, so unrelated columns can be updated concurrently without re-firing it. Run this step inside a maintenance window if your `report_chunks` table is large.

---

## Step 4 — Enable the Vertex AI Discovery Engine API (reranker)

The chat endpoint reranks retrieval candidates through the Vertex AI Ranking API. Enable it once per project:

```bash
gcloud services enable discoveryengine.googleapis.com --project=<PROJECT_ID>
```

The ai-service's existing service account needs the role `roles/discoveryengine.viewer` (or a custom role granting `discoveryengine.servingConfigs.rank`). Grant it via IAM if the chat endpoint logs `permission denied` from `RankServiceClient`.

If you need to disable the reranker for any reason, set `RERANKER_ENABLED=false` on the ai-service Cloud Run service — the chat endpoint falls back to RRF-ordered retrieval automatically.

---

## Step 5 — Mark these migrations as applied in EF history

So EF Core's `dotnet ef database update` doesn't try to re-run these migrations and crash on "already exists" errors:

```sql
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
  ('20260424000000_AddAiServiceTables',                   '8.0.0'),
  ('20260426000000_AddReportPagesFullTextSearch',         '8.0.0'),
  ('20260429000000_ReplaceReportPagesWithReportChunks',   '8.0.0'),
  ('20260429000100_AddChatCache',                         '8.0.0'),
  ('20260510000000_Feature_ArabicSearchTuning',           '8.0.0')
ON CONFLICT DO NOTHING;
```

### Verify
```sql
SELECT "MigrationId" FROM "__EFMigrationsHistory"
WHERE "MigrationId" LIKE '%AiService%'
   OR "MigrationId" LIKE '%FullTextSearch%'
   OR "MigrationId" LIKE '%ReportChunks%'
   OR "MigrationId" LIKE '%ChatCache%';
```
Expected: all four migration IDs listed.

---

## Step 6 — Deploy ai-service in two roles

The same image now serves two Cloud Run services with different `WORKER_MODE` env values, so ingest/translate work no longer blocks the chat hot path.

| Service           | `WORKER_MODE` | `min-instances` | Notes                                   |
| ----------------- | ------------- | --------------- | --------------------------------------- |
| ai-service        | `api`         | 1               | FastAPI; serves chat + REST.            |
| ai-service-worker | `worker`      | 1               | Polls ai_jobs; runs ingest + translate. |

Both services must have the same `DATABASE_URL`, `GCP_PROJECT_ID`, `GCS_BUCKET`, and Gemini credentials. The worker poll interval and stale-job threshold are tunable via `WORKER_POLL_INTERVAL_SECONDS` and `WORKER_STALE_JOB_MINUTES`.

---

## Step 7 — Final sanity check (full pipeline)

After all six steps, run this single query to confirm everything is in place:

```sql
SELECT
  EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')                              AS pgvector_enabled,
  EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'report_chunks')       AS report_chunks_table,
  EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_name = 'report_chunks' AND column_name = 'embedding')                 AS embedding_column,
  EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_name = 'report_chunks' AND column_name = 'metadata')                  AS metadata_column,
  EXISTS (SELECT 1 FROM pg_indexes
          WHERE indexname = 'IX_report_chunks_search_vector')                               AS gin_index,
  EXISTS (SELECT 1 FROM pg_trigger
          WHERE tgname   = 'report_chunks_search_vector_trigger')                           AS chunk_trigger,
  EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'chat_cache')          AS chat_cache_table,
  EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'report_pages')        AS legacy_report_pages_present;
```

All columns except the last must be `true`. `legacy_report_pages_present` should be `false` — if it's `true`, Step 2 didn't drop the old table.

---

## Notes

- These steps require **superuser/`postgres` user** access. Your application user (the one in `DATABASE_URL_PRODUCTION`) typically does not have permission to `CREATE EXTENSION` or `CREATE TRIGGER`.
- After the production database is set up, deploy the AI service to production by pushing to the `production` branch (auto-triggers the deploy workflow). Make sure both Cloud Run services (api + worker) are updated together.
- If the production deploy still fails on a migration, check the error — it's likely an EF "object already exists" message, fix by inserting the migration row in `__EFMigrationsHistory` (Step 5 pattern).
- **GitHub secrets** required for production AI service: `DATABASE_URL_PRODUCTION`, `GCP_PROJECT_ID`, `CLOUD_STORAGE_BUCKET`, `GEMINI_API_KEY` (optional — falls back to Vertex AI if absent).
