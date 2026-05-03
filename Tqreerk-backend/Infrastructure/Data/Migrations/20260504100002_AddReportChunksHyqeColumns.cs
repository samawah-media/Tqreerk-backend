using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taqreerk.Infrastructure.Data;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Hypothetical-Question Embeddings (HyQE) support on report_chunks.
    ///
    /// Why
    /// ===
    /// At ingest time, for each "real" chunk we generate 2-3 questions the
    /// chunk could answer (via Gemini Flash) and write those questions as
    /// EXTRA rows whose embedding is the question vector. At retrieval time
    /// these rows boost recall — a user asking "كم بلغ النمو؟" matches the
    /// question-embedding row directly even when the chunk itself never uses
    /// that exact phrasing. When such a row hits, the retrieval layer
    /// substitutes the PARENT chunk's content before handing it to the LLM.
    ///
    /// Schema additions:
    ///   • ParentChunkId — nullable FK to report_chunks.Id. NULL = real chunk;
    ///                     set = hypothetical question linked to a real parent.
    ///   • Index on ParentChunkId for the parent-substitution JOIN.
    ///
    /// Hypothetical rows reuse the existing (ReportId, PageNumber, ChunkIndex)
    /// unique index by offsetting their ChunkIndex into a high range
    /// (parent_idx * 1000 + question_offset), so no schema change there.
    ///
    /// Owned by the Python ai-service / doc-processor; .NET never reads or
    /// writes ParentChunkId.
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260504100002_AddReportChunksHyqeColumns")]
    public partial class AddReportChunksHyqeColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE report_chunks
                ADD COLUMN ""ParentChunkId"" uuid;
            ");

            // ON DELETE CASCADE so that deleting a real chunk drops every
            // hypothetical row that references it. Re-ingest of a report
            // already DELETEs all that report's chunks; this just makes the
            // cascade explicit at the DB layer.
            migrationBuilder.Sql(@"
                ALTER TABLE report_chunks
                ADD CONSTRAINT fk_report_chunks_parent_chunk
                FOREIGN KEY (""ParentChunkId"")
                REFERENCES report_chunks (""Id"")
                ON DELETE CASCADE;
            ");

            // Drives the parent-substitution JOIN at retrieval time. A
            // partial index keeps it small — most rows have a NULL parent.
            migrationBuilder.Sql(@"
                CREATE INDEX ""IX_report_chunks_ParentChunkId""
                ON report_chunks (""ParentChunkId"")
                WHERE ""ParentChunkId"" IS NOT NULL;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_report_chunks_ParentChunkId"";");
            migrationBuilder.Sql(@"
                ALTER TABLE report_chunks
                DROP CONSTRAINT IF EXISTS fk_report_chunks_parent_chunk;
            ");
            migrationBuilder.Sql(@"
                ALTER TABLE report_chunks DROP COLUMN IF EXISTS ""ParentChunkId"";
            ");
        }
    }
}
