using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class UserInterestConfiguration : IEntityTypeConfiguration<UserInterest>
{
    public void Configure(EntityTypeBuilder<UserInterest> builder)
    {
        builder.ToTable("user_interests");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.HasIndex(e => e.UserId);
    }
}
