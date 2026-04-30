using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taqreerk.Infrastructure.Data;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Switches embedding columns from pgvector(1024) to pgvector(768) so the
    /// chat path can run entirely against managed APIs (Google Vertex AI's
    /// gemini-embedding-001 + Vertex AI Ranking) instead of bge-m3 +
    /// bge-reranker-v2-m3 on the doc-processor GPU.
    ///
    /// Why we're moving off GPU:
    ///   • Chat first-query latency hits 30-90s on cold doc-processor instances.
    ///     For a production product that's a real UX bug, not a tuning issue.
    ///   • gemini-embedding-001 is always warm, ~150ms per call, region-flexible.
    ///   • bge-m3 has a modest Arabic quality edge (~75 vs ~62 MIRACL) but the
    ///     latency win is worth more for a paying customer than the quality
    ///     gap nobody will notice in real chat traffic.
    ///
    /// Existing 1024-dim vectors (from bge-m3) are unrecoverable under the new
    /// model — different embedding spaces, cosine across them is noise. We
    /// drop and re-add the columns rather than try to preserve data, and
    /// truncate both tables so re-ingest is the only path to repopulate.
    /// chat_cache also gets wiped because cached question vectors live in the
    /// old 1024-dim space and would never match freshly-embedded queries.
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260430210000_VertexEmbeddingDimensions")]
    public partial class VertexEmbeddingDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── report_chunks.embedding: vector(1024) → vector(768) ─────────────
            migrationBuilder.Sql(@"TRUNCATE TABLE report_chunks;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks DROP COLUMN IF EXISTS embedding;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks ADD COLUMN embedding vector(768);");

            // ── chat_cache.question_emb: vector(1024) → vector(768) ────────────
            migrationBuilder.Sql(@"TRUNCATE TABLE chat_cache;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache DROP COLUMN IF EXISTS question_emb;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache ADD COLUMN question_emb vector(768);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: vectors at 768 dims aren't dimensionally compatible with
            // 1024 either, so the down path is also destructive.
            migrationBuilder.Sql(@"TRUNCATE TABLE report_chunks;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks DROP COLUMN IF EXISTS embedding;");
            migrationBuilder.Sql(@"ALTER TABLE report_chunks ADD COLUMN embedding vector(1024);");

            migrationBuilder.Sql(@"TRUNCATE TABLE chat_cache;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache DROP COLUMN IF EXISTS question_emb;");
            migrationBuilder.Sql(@"ALTER TABLE chat_cache ADD COLUMN question_emb vector(1024);");
        }
    }
}
