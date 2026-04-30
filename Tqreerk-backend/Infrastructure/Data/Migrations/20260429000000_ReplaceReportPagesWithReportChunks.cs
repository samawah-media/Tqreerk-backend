using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taqreerk.Infrastructure.Data;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Replaces page-level RAG storage (report_pages) with sub-page chunking
    /// (report_chunks). Each PDF page is now split into ~500-token chunks with
    /// overlap, enabling tighter retrieval and per-chunk metadata
    /// (section_title, page_type, language) for filtered search.
    ///
    /// Existing report_pages data is dropped. Reports must be re-ingested by the
    /// Python ai-service to repopulate report_chunks.
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260429000000_ReplaceReportPagesWithReportChunks")]
    public partial class ReplaceReportPagesWithReportChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Drop report_pages ────────────────────────────────────────────
            // The trigger / function / index were added in AddReportPagesFullTextSearch.
            // CASCADE on the table drop also clears the trigger and dependent indexes;
            // we still drop the function explicitly because functions are not table-bound.
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS report_pages_search_vector_trigger ON report_pages;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS report_pages_search_vector_update();");
            migrationBuilder.DropTable(name: "report_pages");

            // ── 2. Create report_chunks (EF-managed columns only) ───────────────
            migrationBuilder.CreateTable(
                name: "report_chunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_chunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_chunks_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_chunks_ReportId_PageNumber_ChunkIndex",
                table: "report_chunks",
                columns: new[] { "ReportId", "PageNumber", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_chunks_ReportId_PageNumber",
                table: "report_chunks",
                columns: new[] { "ReportId", "PageNumber" });

            // ── 3. DB-managed columns: embedding, search_vector, metadata ───────
            // Same pattern as report_pages: managed exclusively by the Python
            // ai-service via psycopg / raw SQL. EF Core never maps these.

            // pgvector embedding — Gemini text-embedding-004 (768 dims).
            migrationBuilder.Sql(@"
                ALTER TABLE report_chunks ADD COLUMN embedding vector(768);
            ");

            // jsonb metadata — {section_title, page_type, language}
            // Defaulted to '{}' so existing-row updates never produce NULL.
            migrationBuilder.Sql(@"
                ALTER TABLE report_chunks
                ADD COLUMN metadata jsonb NOT NULL DEFAULT '{}'::jsonb;
            ");

            // tsvector search_vector — bilingual Arabic + English, mirrors the
            // pattern used previously on report_pages.
            migrationBuilder.Sql(@"
                ALTER TABLE report_chunks ADD COLUMN search_vector tsvector;
            ");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION report_chunks_search_vector_update()
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
                CREATE TRIGGER report_chunks_search_vector_trigger
                BEFORE INSERT OR UPDATE OF ""Content"" ON report_chunks
                FOR EACH ROW EXECUTE FUNCTION report_chunks_search_vector_update();
            ");

            // GIN index on search_vector — keyword lookup stays milliseconds at scale.
            migrationBuilder.Sql(@"
                CREATE INDEX ""IX_report_chunks_search_vector""
                ON report_chunks USING GIN (search_vector);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: drop chunks (and its trigger/function/indexes), recreate
            // report_pages exactly as Feature2_LockoutAndAiTables left it after
            // AddReportPagesFullTextSearch (so down-stack stays consistent).

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_report_chunks_search_vector"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS report_chunks_search_vector_trigger ON report_chunks;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS report_chunks_search_vector_update();");
            migrationBuilder.DropTable(name: "report_chunks");

            migrationBuilder.CreateTable(
                name: "report_pages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_pages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_pages_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_pages_ReportId_PageNumber",
                table: "report_pages",
                columns: new[] { "ReportId", "PageNumber" },
                unique: true);

            migrationBuilder.Sql(@"ALTER TABLE report_pages ADD COLUMN embedding vector(768);");
            migrationBuilder.Sql(@"ALTER TABLE report_pages ADD COLUMN search_vector tsvector;");
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
            migrationBuilder.Sql(@"
                CREATE INDEX ""IX_report_pages_search_vector""
                ON report_pages USING GIN (search_vector);
            ");
        }
    }
}
