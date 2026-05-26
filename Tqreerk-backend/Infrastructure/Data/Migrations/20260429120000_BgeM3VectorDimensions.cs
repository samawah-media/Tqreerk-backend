using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taqreerk.Infrastructure.Data;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Switches embedding columns from pgvector(768) to pgvector(1024) so the
    /// doc-processor can host BAAI/bge-m3 instead of multilingual-e5-base.
    ///
    /// bge-m3 is the strongest publicly-available multilingual embedder for
    /// Arabic + English retrieval (MIRACL Arabic ~75 vs e5-base ~51) and
    /// removes the E5 query/passage prefix requirement, simplifying the API.
    ///
    /// Existing 768-dim vectors are unrecoverable under the new model (different
    /// embedding space), so we drop and re-add the columns rather than try to
    /// preserve data. Re-ingest is required afterwards. The chat_cache table
    /// is also wiped because cached question embeddings would never match
    /// freshly-embedded queries.
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260429120000_BgeM3VectorDimensions")]
    public partial class BgeM3VectorDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── report_chunks.embedding: vector(768) → vector(1024) ─────────────
            // Wipe rows AND drop+re-add the column. PostgreSQL can't auto-cast
            // pgvector dimensions, and the old text / vectors are useless under
            // the new model anyway — both the embedding space and the Arabic
            // normalization differ. Re-ingest is required afterwards.
            migrationBuilder.Sql(@"TRUNCATE TABLE report_chunks;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks DROP COLUMN IF EXISTS embedding;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks ADD COLUMN embedding vector(1024);");

            // ── chat_cache.question_emb: vector(768) → vector(1024) ────────────
            // Cached question embeddings live in the old 768-dim space and
            // would never match the new 1024-dim queries — clear them.
            migrationBuilder.Sql(@"TRUNCATE TABLE chat_cache;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache DROP COLUMN IF EXISTS question_emb;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache ADD COLUMN question_emb vector(1024);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric reverse: wipe rows, drop 1024-dim columns, recreate at 768.
            // Same data-loss caveat applies — the migration is destructive in
            // both directions because the underlying vectors aren't
            // dimensionally compatible.
            migrationBuilder.Sql(@"TRUNCATE TABLE report_chunks;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks DROP COLUMN IF EXISTS embedding;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks ADD COLUMN embedding vector(768);");

            migrationBuilder.Sql(@"TRUNCATE TABLE chat_cache;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache DROP COLUMN IF EXISTS question_emb;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache ADD COLUMN question_emb vector(768);");
        }
    }
}
