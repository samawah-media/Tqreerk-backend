using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Strengthens Arabic recall in hybrid retrieval without adding any model
    /// or external service. Three coordinated pieces:
    ///
    ///   1. arabic_normalize(text) — IMMUTABLE SQL function that strips harakat
    ///      + tatweel and folds alef/yaa/taa-marbutah/hamza variants. Used by
    ///      both the FTS trigger AND the trigram index so query-side and
    ///      index-side share one canonical form.
    ///
    ///   2. report_chunks search-vector trigger rewritten to call
    ///      arabic_normalize(Content) before to_tsvector('arabic', ...).
    ///      Also rebuilds search_vector for every existing row (cheap UPDATE
    ///      with the trigger doing the work).
    ///
    ///   3. pg_trgm extension + GIN index on arabic_normalize(Content) so the
    ///      ai-service can add a fuzzy/typo-tolerant arm to hybrid retrieval
    ///      (covers OCR errors, typos, partial-name lookups). Hooked up
    ///      separately in services/tools.py.
    ///
    /// Why ai-service shares this normalization in Python: query-side
    /// normalization in tools.py (_normalize_query_for_fts) must produce the
    /// SAME canonical form as arabic_normalize() here. They are kept byte-
    /// identical by design — if you change one, change the other.
    /// </summary>
    public partial class Feature_ArabicSearchTuning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. pg_trgm extension (fuzzy / typo-tolerant matching) ────────
            // Cloud SQL supports pg_trgm out of the box, no flag toggles needed.
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // ── 2. arabic_normalize(text) — canonical form for FTS + trigram ─
            // IMMUTABLE + PARALLEL SAFE so it can back an expression index.
            //
            // Codepoints are spelled as \uXXXX inside Postgres E-strings so
            // the regex character class is unambiguous. Writing the literal
            // chars in [a-b] form would silently widen the range to include
            // U+0660-U+0669 Arabic-Indic digits.
            //
            // Strip:
            //   U+064B .. U+065F  Arabic harakat (fathatan .. wavy hamza below)
            //   U+0670            Superscript Alef (the "khanjariya" diacritic)
            //   U+0640            Tatweel / Kashida (presentational stretch)
            //
            // Fold (translate single-char to single-char):
            //   U+0623 ALEF WITH HAMZA ABOVE        -> U+0627 ALEF
            //   U+0625 ALEF WITH HAMZA BELOW        -> U+0627 ALEF
            //   U+0622 ALEF WITH MADDA ABOVE        -> U+0627 ALEF
            //   U+0671 ALEF WASLA                   -> U+0627 ALEF
            //   U+0649 ALEF MAKSURA                 -> U+064A YEH
            //   U+0629 TEH MARBUTA                  -> U+0647 HEH
            //   U+0624 WAW WITH HAMZA ABOVE         -> U+0648 WAW
            //   U+0626 YEH WITH HAMZA ABOVE         -> U+064A YEH
            //
            // lower() is applied last so the english FTS arm gets case-folded
            // Latin and the trigram index sees one canonical form for mixed.
            migrationBuilder.Sql(@"
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
                                E'[ً-ٟـٰ]',
                                '',
                                'g'
                            ),
                            E'أإآٱىةؤئ',
                            E'اااايهوي'
                        )
                    )
                $$;
            ");

            // ── 3. Rewrite the FTS trigger to use arabic_normalize ───────────
            // The 'arabic' arm now sees the normalized form so user queries
            // for "Saudi" written with taa-marbutah match the same word
            // written with regular heh, alef-with-hamza matches plain alef,
            // and so on. The 'english' arm stays on raw Content so Latin
            // tokenization is unaffected.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION report_chunks_search_vector_update()
                RETURNS trigger AS $$
                BEGIN
                    NEW.search_vector :=
                        to_tsvector('arabic',  arabic_normalize(NEW.""Content"")) ||
                        to_tsvector('english', COALESCE(NEW.""Content"", ''));
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            // ── 4. Trigram GIN index on arabic_normalize(Content) ────────────
            // Expression index: matches what the application will pass to the
            // fuzzy CTE arm (it normalizes the user's query the same way).
            // GIN gin_trgm_ops supports both '%' (similarity threshold) and
            // '<->' (similarity-distance ORDER BY) operators.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_report_chunks_content_trgm""
                ON report_chunks
                USING GIN (arabic_normalize(""Content"") gin_trgm_ops);
            ");

            // ── 5. Backfill existing rows ─────────────────────────────────────
            // The trigger only fires on INSERT/UPDATE OF Content. Existing rows
            // were indexed under the old (un-normalized) rule. A no-op UPDATE
            // forces the trigger and rebuilds search_vector for every row.
            // For typical staging volumes (~100k chunks) this is a few seconds;
            // production may take longer. Run during off-peak.
            migrationBuilder.Sql(@"
                UPDATE report_chunks
                SET ""Content"" = ""Content"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop in reverse order. We restore the trigger to the
            // pre-migration body but DO NOT drop pg_trgm — other features
            // (e.g. ad-hoc admin search) may also be using it.

            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_report_chunks_content_trgm"";
            ");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION report_chunks_search_vector_update()
                RETURNS trigger AS $$
                BEGIN
                    NEW.search_vector :=
                        to_tsvector('arabic',  COALESCE(NEW.""Content"", '')) ||
                        to_tsvector('english', COALESCE(NEW.""Content"", ''));
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS arabic_normalize(text);");

            // Backfill again so search_vector reverts to old form on existing rows.
            migrationBuilder.Sql(@"UPDATE report_chunks SET ""Content"" = ""Content"";");
        }
    }
}
