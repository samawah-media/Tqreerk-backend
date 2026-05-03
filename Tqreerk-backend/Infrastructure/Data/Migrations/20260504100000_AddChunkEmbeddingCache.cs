using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taqreerk.Infrastructure.Data;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Content-addressable cache for chunk embeddings.
    ///
    /// Why this table exists
    /// ====================
    /// Re-ingesting a report (or ingesting another report that shares boilerplate
    /// with an existing one) recomputes the same chunk embeddings from scratch
    /// against Vertex AI, costing money and latency. With this cache, the
    /// embedding step looks up sha256(spec || normalized_text) first; only the
    /// misses go to the API. Same model + same text + same spec → cached vector.
    ///
    /// `spec` encodes (model || task_type || dim) so changing any of those three
    /// invalidates the cache automatically — there is no flush step.
    ///
    /// Owned by the Python ai-service / doc-processor. EF declares the schema
    /// here so the database structure stays in one place; .NET never touches it.
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260504100000_AddChunkEmbeddingCache")]
    public partial class AddChunkEmbeddingCache : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE chunk_embedding_cache (
                    cache_key      text PRIMARY KEY,
                    spec           text NOT NULL,
                    embedding      vector(768) NOT NULL,
                    text_preview   text,
                    created_at     timestamptz NOT NULL DEFAULT now(),
                    last_used_at   timestamptz NOT NULL DEFAULT now()
                );
            ");

            // Drives optional future LRU eviction. Cheap to maintain at the row
            // counts we expect and lets a janitor job DELETE rows older than N.
            migrationBuilder.Sql(@"
                CREATE INDEX ""IX_chunk_embedding_cache_last_used""
                ON chunk_embedding_cache (last_used_at);
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_chunk_embedding_cache_last_used"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS chunk_embedding_cache;");
        }
    }
}
