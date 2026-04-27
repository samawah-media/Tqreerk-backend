using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Fixes the reports search-vector trigger to reference the actual column
    /// name. The original migration declared `NEW.search_vector` (snake_case),
    /// but EF Core maps the property to a quoted "SearchVector" column. As a
    /// result every INSERT/UPDATE on `reports` failed with
    ///     42703: record "new" has no field "search_vector"
    /// This migration drops & recreates the function with NEW."SearchVector".
    /// </summary>
    public partial class Feature6_FixSearchVectorTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reports_search_vector_update()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."SearchVector" :=
                        setweight(to_tsvector('arabic', coalesce(NEW."Title", '')), 'A') ||
                        setweight(to_tsvector('arabic', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('arabic', coalesce(NEW."ExtractedText", '')), 'C') ||
                        setweight(to_tsvector('english', coalesce(NEW."Title", '')), 'A') ||
                        setweight(to_tsvector('english', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('english', coalesce(NEW."ExtractedText", '')), 'C');
                    RETURN NEW;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the original (broken) trigger body for symmetry. We don't
            // expect anyone to roll back to this state, but keeping Down honest
            // means `dotnet ef migrations remove` produces a coherent diff.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reports_search_vector_update()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.search_vector :=
                        setweight(to_tsvector('arabic', coalesce(NEW."Title", '')), 'A') ||
                        setweight(to_tsvector('arabic', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('arabic', coalesce(NEW."ExtractedText", '')), 'C') ||
                        setweight(to_tsvector('english', coalesce(NEW."Title", '')), 'A') ||
                        setweight(to_tsvector('english', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('english', coalesce(NEW."ExtractedText", '')), 'C');
                    RETURN NEW;
                END;
                $$;
                """);
        }
    }
}
