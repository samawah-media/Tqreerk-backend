using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taqreerk.Infrastructure.Data;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds HNSW indexes for cosine-similarity search on every pgvector column
    /// that participates in retrieval / cache lookup.
    ///
    /// Why
    /// ===
    /// Today retrieval relies on sequential scans of `report_chunks.embedding`,
    /// kept fast only because each query filters by `ReportId = ANY(...)`
    /// first. As soon as a user has cross-report scope (Published reports +
    /// own-org reports = hundreds of report ids), the scan touches the full
    /// per-org chunk count and latency climbs linearly. HNSW pivots that to
    /// sublinear ANN — typical 50-200x speedup on cross-report queries with
    /// 95-98% recall at the default ef_search.
    ///
    /// Tuning notes
    /// ============
    ///   • m = 16, ef_construction = 64 are pgvector defaults — good balance
    ///     of build time / index size / recall for general-purpose retrieval.
    ///   • Default ef_search is 40 (per-session). Bump to 100+ at runtime via
    ///     `SET LOCAL hnsw.ef_search = 100;` if you need higher recall on a
    ///     specific query.
    ///   • For pgvector ≥ 0.7, set `hnsw.iterative_scan = 'relaxed_order'` at
    ///     the connection level for better performance under WHERE filters
    ///     like our `ReportId = ANY(...)`. Older versions will still work,
    ///     just with somewhat slower filtered queries.
    ///
    /// Build cost
    /// ==========
    /// CONCURRENTLY keeps the table readable + writable while the index
    /// builds (~1-2 min for 100K chunks; ~10-15 min for 1M). CONCURRENTLY
    /// cannot run inside a transaction, so each statement is suppressed via
    /// suppressTransaction: true — same pattern as the metadata GIN index.
    ///
    /// Requires pgvector ≥ 0.5.0 (HNSW operator class). Verify with:
    ///   SELECT extversion FROM pg_extension WHERE extname = 'vector';
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260504100003_AddHnswVectorIndexes")]
    public partial class AddHnswVectorIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── report_chunks.embedding ──────────────────────────────────────
            // Drives every dense retrieval call in tools.py (the inner
            // `<=>` cosine distance under both _hybrid_retrieve_one and
            // find_similar_reports).
            migrationBuilder.Sql(
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_report_chunks_embedding_hnsw
                  ON report_chunks USING hnsw (embedding vector_cosine_ops)
                  WITH (m = 16, ef_construction = 64);",
                suppressTransaction: true
            );

            // ── chunk_embedding_cache.embedding ──────────────────────────────
            // The cache table is keyed by sha256 (PK lookup), so the HNSW
            // here is for cache-miss-batch lookups by similarity if we ever
            // add fuzzy cache-key matching. Cheap to maintain at expected row
            // counts; future-proof.
            migrationBuilder.Sql(
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_chunk_embedding_cache_embedding_hnsw
                  ON chunk_embedding_cache USING hnsw (embedding vector_cosine_ops)
                  WITH (m = 16, ef_construction = 64);",
                suppressTransaction: true
            );

            // ── chat_cache.question_emb ──────────────────────────────────────
            // Drives Layer 2 of the chat answer cache (semantic match by
            // cosine on stored question embeddings). Today the lookup uses
            // sequential scan over recent rows for one report — fine while
            // small, but the index makes it scale across the whole table
            // when Layer 2 gets fully wired in.
            migrationBuilder.Sql(
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_chat_cache_question_emb_hnsw
                  ON chat_cache USING hnsw (question_emb vector_cosine_ops)
                  WITH (m = 16, ef_construction = 64)
                  WHERE question_emb IS NOT NULL;",
                suppressTransaction: true
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"DROP INDEX CONCURRENTLY IF EXISTS idx_chat_cache_question_emb_hnsw;",
                suppressTransaction: true
            );
            migrationBuilder.Sql(
                @"DROP INDEX CONCURRENTLY IF EXISTS idx_chunk_embedding_cache_embedding_hnsw;",
                suppressTransaction: true
            );
            migrationBuilder.Sql(
                @"DROP INDEX CONCURRENTLY IF EXISTS idx_report_chunks_embedding_hnsw;",
                suppressTransaction: true
            );
        }
    }
}
