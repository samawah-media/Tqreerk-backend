# Production Database Setup — Manual Steps

This file lists every SQL operation we performed on the **staging** database during AI service integration. Each step must also be performed on the **production** database (`taqreerk_production`) before the AI features (ingest, chat, summarize, translate) will work in production.

## How to run these

GCP Console → **SQL** → click your production instance → **Cloud SQL Studio** → sign in as **`postgres`** (the superuser, not the app user) → database **`taqreerk_production`**.

Run each step in order. Verify after each one before moving on.

---

## Step 1 — Enable the `pgvector` extension

The `report_pages.embedding` column uses `vector(768)`. The extension is built into Cloud SQL but must be activated per-database.

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

### Verify
```sql
SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';
```
Expected: one row with `vector` and a version number.

---

## Step 2 — Add the `embedding` column to `report_pages`

The `AddAiServiceTables` migration is supposed to add this, but on staging it didn't apply cleanly (the `CREATE EXTENSION` line ran without superuser the first time). To make production deterministic, add it manually:

```sql
ALTER TABLE report_pages ADD COLUMN IF NOT EXISTS embedding vector(768);
```

### Verify
```sql
SELECT column_name, data_type, udt_name
FROM information_schema.columns
WHERE table_name = 'report_pages' AND column_name = 'embedding';
```
Expected: one row showing `embedding | USER-DEFINED | vector`.

---

## Step 3 — Add `search_vector` (full-text search) + GIN index

The `AddReportPagesFullTextSearch` migration adds bilingual (Arabic + English) full-text search to `report_pages.Content` so the chatbot can do hybrid retrieval (dense vector + keyword).

```sql
-- 3a. tsvector column
ALTER TABLE report_pages ADD COLUMN IF NOT EXISTS search_vector tsvector;

-- 3b. Trigger function — bilingual (Arabic + English combined)
CREATE OR REPLACE FUNCTION report_pages_search_vector_update()
RETURNS trigger AS $$
BEGIN
    NEW.search_vector :=
        to_tsvector('arabic',  coalesce(NEW."Content", '')) ||
        to_tsvector('english', coalesce(NEW."Content", ''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- 3c. Trigger
DROP TRIGGER IF EXISTS report_pages_search_vector_trigger ON report_pages;
CREATE TRIGGER report_pages_search_vector_trigger
BEFORE INSERT OR UPDATE OF "Content" ON report_pages
FOR EACH ROW EXECUTE FUNCTION report_pages_search_vector_update();

-- 3d. Backfill any existing rows by triggering an UPDATE
UPDATE report_pages SET "Content" = "Content";

-- 3e. GIN index for fast keyword lookup
CREATE INDEX IF NOT EXISTS "IX_report_pages_search_vector"
ON report_pages USING GIN (search_vector);
```

### Verify
```sql
-- Column exists
SELECT column_name FROM information_schema.columns
WHERE table_name = 'report_pages' AND column_name = 'search_vector';

-- Index exists
SELECT indexname FROM pg_indexes
WHERE tablename = 'report_pages' AND indexname = 'IX_report_pages_search_vector';

-- Trigger exists
SELECT tgname FROM pg_trigger WHERE tgname = 'report_pages_search_vector_trigger';

-- Existing rows have populated tsvectors
SELECT COUNT(*) AS total,
       COUNT(*) FILTER (WHERE search_vector IS NOT NULL) AS populated
FROM report_pages;
```
Expected: `populated` should equal `total`.

---

## Step 4 — Mark these migrations as applied in EF history

So EF Core's `dotnet ef database update` doesn't try to re-run these migrations and crash on "already exists" errors:

```sql
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
  ('20260424000000_AddAiServiceTables',           '8.0.0'),
  ('20260426000000_AddReportPagesFullTextSearch', '8.0.0')
ON CONFLICT DO NOTHING;
```

### Verify
```sql
SELECT "MigrationId" FROM "__EFMigrationsHistory"
WHERE "MigrationId" LIKE '%AiService%'
   OR "MigrationId" LIKE '%FullTextSearch%';
```
Expected: both migration IDs listed.

---

## Step 5 — Final sanity check (full pipeline)

After all four steps, run this single query to confirm everything is in place:

```sql
SELECT
  EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')                                AS pgvector_enabled,
  EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_name = 'report_pages' AND column_name = 'embedding')                    AS embedding_column,
  EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_name = 'report_pages' AND column_name = 'search_vector')                AS search_vector_column,
  EXISTS (SELECT 1 FROM pg_indexes
          WHERE indexname = 'IX_report_pages_search_vector')                                  AS gin_index,
  EXISTS (SELECT 1 FROM pg_trigger
          WHERE tgname = 'report_pages_search_vector_trigger')                                AS trigger_active;
```

All five columns must be `true`.

---

## Notes

- These steps require **superuser/`postgres` user** access. Your application user (the one in `DATABASE_URL_PRODUCTION`) typically does not have permission to `CREATE EXTENSION` or `CREATE TRIGGER`.
- After the production database is set up, deploy the AI service to production by pushing to the `production` branch (auto-triggers the deploy workflow).
- If the production deploy still fails on a migration, check the error — it's likely an EF "object already exists" message, fix by inserting the migration row in `__EFMigrationsHistory` (Step 4 pattern).
- **GitHub secrets** required for production AI service: `DATABASE_URL_PRODUCTION`, `GCP_PROJECT_ID`, `CLOUD_STORAGE_BUCKET`, `GEMINI_API_KEY` (optional — falls back to Vertex AI if absent).
