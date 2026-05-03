using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportPersonalNoteConfiguration : IEntityTypeConfiguration<ReportPersonalNote>
{
    public void Configure(EntityTypeBuilder<ReportPersonalNote> builder)
    {
        builder.ToTable("report_personal_notes");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(n => n.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(n => n.Body).IsRequired().HasMaxLength(50_000);

        // Enforces the one-note-per-(user,report) invariant. Upsert path
        // in NotesService relies on this.
        builder.HasIndex(n => new { n.UserId, n.ReportId })
            .IsUnique()
            .HasDatabaseName("ix_report_personal_notes_user_report");

        builder.HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(n => n.Report)
            .WithMany()
            .HasForeignKey(n => n.ReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
