using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Switches report_ai_contents.Summary from text to jsonb so it can hold
    /// the new 3-7 bullet-point array produced by the summarize pipeline.
    ///
    /// The EF AlterColumn helper can't emit a custom USING clause, and Postgres
    /// won't implicitly cast text → jsonb, so we drive the conversion via raw
    /// SQL. Existing non-null paragraphs are wrapped into a single-element
    /// JSON array so legacy rows render as one bullet (better than dropping
    /// them); NULL stays NULL.
    ///
    /// The companion Designer file carries the [DbContext] / [Migration]
    /// attributes — matching the project's generated-migration convention.
    /// </summary>
    public partial class Feature_SummaryAsBulletPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE report_ai_contents
                ALTER COLUMN ""Summary"" TYPE jsonb
                USING CASE
                    WHEN ""Summary"" IS NULL THEN NULL
                    ELSE to_jsonb(ARRAY[""Summary""])
                END;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Flatten the bullet array back into a paragraph (joined with
            // double newlines). Lossy when the array had >1 item, but the
            // alternative is dropping data outright on rollback.
            migrationBuilder.Sql(@"
                ALTER TABLE report_ai_contents
                ALTER COLUMN ""Summary"" TYPE text
                USING CASE
                    WHEN ""Summary"" IS NULL THEN NULL
                    WHEN jsonb_typeof(""Summary"") = 'array'
                        THEN array_to_string(
                            ARRAY(SELECT jsonb_array_elements_text(""Summary"")),
                            E'\n\n'
                        )
                    ELSE ""Summary"" #>> '{}'
                END;
            ");
        }
    }
}
