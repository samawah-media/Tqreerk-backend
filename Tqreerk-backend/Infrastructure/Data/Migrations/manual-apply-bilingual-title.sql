-- Manual application of Feature_ReportBilingualTitle migration.
-- Apply when `dotnet ef database update` is blocked by unrelated pending
-- migrations (e.g. pgvector). Idempotent: safe to re-run.

BEGIN;

-- 0) Disable the existing FTS trigger before renaming.
--    The old trigger body references NEW."Title" — once we rename it
--    away, any subsequent UPDATE on reports (including step 6 below)
--    blows up with "record new has no field Title". Recreating the
--    trigger in step 5 reinstates it against the new column names.
DROP TRIGGER IF EXISTS reports_search_vector_trigger ON reports;

-- 1) Rename Title -> TitleAr (only if it still exists)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'reports' AND column_name = 'Title'
    ) AND NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'reports' AND column_name = 'TitleAr'
    ) THEN
        ALTER TABLE reports RENAME COLUMN "Title" TO "TitleAr";
    END IF;
END $$;

-- 2) Add TitleEn (nullable initially)
ALTER TABLE reports ADD COLUMN IF NOT EXISTS "TitleEn" varchar(500);

-- 3) Backfill TitleEn from TitleAr
UPDATE reports SET "TitleEn" = "TitleAr" WHERE "TitleEn" IS NULL;

-- 4) Lock TitleEn as NOT NULL
ALTER TABLE reports ALTER COLUMN "TitleEn" SET NOT NULL;

-- 5) Rewrite FTS trigger function + re-attach the trigger to reports.
CREATE OR REPLACE FUNCTION reports_search_vector_update()
RETURNS trigger LANGUAGE plpgsql AS $func$
BEGIN
    NEW."SearchVector" :=
        setweight(to_tsvector('arabic',  coalesce(NEW."TitleAr", '')), 'A') ||
        setweight(to_tsvector('arabic',  coalesce(NEW."Description", '')), 'B') ||
        setweight(to_tsvector('arabic',  coalesce(NEW."ExtractedText", '')), 'C') ||
        setweight(to_tsvector('english', coalesce(NEW."TitleEn", '')), 'A') ||
        setweight(to_tsvector('english', coalesce(NEW."Description", '')), 'B') ||
        setweight(to_tsvector('english', coalesce(NEW."ExtractedText", '')), 'C');
    RETURN NEW;
END;
$func$;

CREATE TRIGGER reports_search_vector_trigger
BEFORE INSERT OR UPDATE ON reports
FOR EACH ROW EXECUTE FUNCTION reports_search_vector_update();

-- 6) Rebuild SearchVector for every existing row via no-op UPDATE
UPDATE reports SET "TitleAr" = "TitleAr";

-- 7) Record the migration as applied so EF doesn't try to re-run it.
--    Only if the history table exists — staging hand-bootstrapped without
--    EF migrations historically, so it may not be there. Once any EF
--    migration runs this table appears automatically.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_name = '__EFMigrationsHistory'
    ) THEN
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('20260519100000_Feature_ReportBilingualTitle', '8.0.0')
        ON CONFLICT ("MigrationId") DO NOTHING;
    END IF;
END $$;

COMMIT;

-- Verify:
-- SELECT column_name FROM information_schema.columns
-- WHERE table_name = 'reports' AND column_name IN ('TitleAr','TitleEn','Title');
