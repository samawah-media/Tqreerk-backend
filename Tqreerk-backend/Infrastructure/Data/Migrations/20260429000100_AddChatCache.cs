using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Postgres-backed semantic cache for chat answers.
    ///
    /// Layer 1 — exact match: SHA256 of (report_id + normalized question) is the
    /// primary key. Sub-millisecond lookup.
    /// Layer 2 — semantic match: each cache entry stores the question's
    /// embedding, so paraphrases of the same question can short-circuit
    /// retrieval AND the LLM call by cosine-matching against recent entries
    /// for the same report.
    ///
    /// The table is owned and managed exclusively by the Python ai-service.
    /// EF never reads or writes it; we only declare the schema here so all
    /// database structure stays in one place.
    /// </summary>
    public partial class AddChatCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE chat_cache (
                    cache_key      text PRIMARY KEY,
                    report_id      uuid NOT NULL,
                    question       text NOT NULL,
                    question_emb   vector(768),
                    answer         text NOT NULL,
                    source_pages   jsonb NOT NULL DEFAULT '[]'::jsonb,
                    hit_count      integer NOT NULL DEFAULT 0,
                    created_at     timestamptz NOT NULL DEFAULT now(),
                    expires_at     timestamptz NOT NULL,
                    CONSTRAINT fk_chat_cache_report
                        FOREIGN KEY (report_id) REFERENCES reports (""Id"") ON DELETE CASCADE
                );
            ");

            // Drives semantic lookup: search recent, non-expired entries scoped
            // to the report. Composite (report_id, expires_at) is the hottest
            // path so it gets a dedicated B-tree.
            migrationBuilder.Sql(@"
                CREATE INDEX ""IX_chat_cache_report_expires""
                ON chat_cache (report_id, expires_at);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_chat_cache_report_expires"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS chat_cache;");
        }
    }
}
