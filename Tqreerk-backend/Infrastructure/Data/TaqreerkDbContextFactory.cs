using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Taqreerk.Infrastructure.Data;

/// Used only by `dotnet ef` tooling and EF migration bundles.
/// Keeps migration commands independent of the app's runtime services
/// (Jwt, Sentry, CORS) so CI can build/apply migrations without full config.
public class TaqreerkDbContextFactory : IDesignTimeDbContextFactory<TaqreerkDbContext>
{
    public TaqreerkDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=taqreerk_design;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<TaqreerkDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly(typeof(TaqreerkDbContext).Assembly.FullName)
        );

        return new TaqreerkDbContext(optionsBuilder.Options);
    }
}
