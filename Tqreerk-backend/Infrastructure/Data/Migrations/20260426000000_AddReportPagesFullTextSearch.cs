using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds a tsvector column + GIN index on report_pages.Content so the chatbot
    /// can run hybrid retrieval (dense vector + keyword) inside a single SQL query.
    ///
    /// Like the embedding column, search_vector is DB-managed only — populated by a
    /// trigger and queried directly via raw SQL with the @@ operator.
    /// </summary>
    public partial class AddReportPagesFullTextSearch : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. tsvector column
            migrationBuilder.Sql(@"
                ALTER TABLE report_pages ADD COLUMN search_vector tsvector;
            ");

            // 2. Trigger function — same bilingual approach as reports.search_vector
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION report_pages_search_vector_update()
                RETURNS trigger AS $$
                BEGIN
                    NEW.search_vector :=
                        to_tsvector('arabic',  coalesce(NEW.""Content"", '')) ||
                        to_tsvector('english', coalesce(NEW.""Content"", ''));
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql(@"
                CREATE TRIGGER report_pages_search_vector_trigger
                BEFORE INSERT OR UPDATE OF ""Content"" ON report_pages
                FOR EACH ROW EXECUTE FUNCTION report_pages_search_vector_update();
            ");

            // 3. Backfill existing rows by triggering an UPDATE
            migrationBuilder.Sql(@"
                UPDATE report_pages SET ""Content"" = ""Content"";
            ");

            // 4. GIN index — makes keyword search milliseconds even at 5M+ rows
            migrationBuilder.Sql(@"
                CREATE INDEX ""IX_report_pages_search_vector""
                ON report_pages USING GIN (search_vector);
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_report_pages_search_vector"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS report_pages_search_vector_trigger ON report_pages;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS report_pages_search_vector_update();");
            migrationBuilder.Sql(@"ALTER TABLE report_pages DROP COLUMN IF EXISTS search_vector;");
        }
    }
}
