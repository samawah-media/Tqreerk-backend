using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportChunkConfiguration : IEntityTypeConfiguration<ReportChunk>
{
    public void Configure(EntityTypeBuilder<ReportChunk> builder)
    {
        builder.ToTable("report_chunks");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(c => c.Content).IsRequired();
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");

        // embedding (vector(768)), search_vector (tsvector) and metadata (jsonb)
        // are all DB-managed and added via raw SQL in the AddReportChunks migration.
        // EF Core never maps them.

        builder.HasIndex(c => new { c.ReportId, c.PageNumber, c.ChunkIndex }).IsUnique();
        builder.HasIndex(c => new { c.ReportId, c.PageNumber });

        builder.HasOne(c => c.Report)
            .WithMany(r => r.Chunks)
            .HasForeignKey(c => c.ReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
