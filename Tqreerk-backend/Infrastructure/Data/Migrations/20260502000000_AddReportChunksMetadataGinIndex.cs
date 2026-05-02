using Microsoft.EntityFrameworkCore.Migrations;
using Taqreerk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds a GIN index on the report_chunks.metadata JSONB column so that
    /// block_types containment queries (metadata @> '{"block_types":["table"]}')
    /// used by the RAG search_chunks tool run in index time instead of full-scan.
    ///
    /// Uses jsonb_path_ops operator class, which is the correct choice for @>
    /// containment queries and produces a smaller index than the default
    /// jsonb_ops class (which also supports the ? key-existence operator we
    /// don't need here).
    ///
    /// Built CONCURRENTLY so the migration doesn't take an exclusive lock on
    /// report_chunks during index construction — safe to apply in production
    /// while the service is live. CONCURRENTLY cannot run inside a transaction,
    /// so suppressed via migrationBuilder.Sql with suppressTransaction: true.
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260502000000_AddReportChunksMetadataGinIndex")]
    public partial class AddReportChunksMetadataGinIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_report_chunks_metadata_gin
                  ON report_chunks USING GIN (metadata jsonb_path_ops);",
                suppressTransaction: true
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"DROP INDEX CONCURRENTLY IF EXISTS idx_report_chunks_metadata_gin;",
                suppressTransaction: true
            );
        }
    }
}
