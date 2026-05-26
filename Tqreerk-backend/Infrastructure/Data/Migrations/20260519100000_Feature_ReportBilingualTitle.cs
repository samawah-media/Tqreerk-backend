using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Splits the single-language Report.Title column into TitleAr +
    /// TitleEn so the SPA can render the right one per locale. Legacy
    /// rows have their existing Title copied into both columns; admins
    /// can refine the other language from the review/edit UI afterwards.
    ///
    /// The FTS trigger is rewritten so the Arabic arm indexes TitleAr
    /// and the English arm indexes TitleEn — keeping ts_rank meaningful
    /// when callers search in one specific language.
    /// </summary>
    public partial class Feature_ReportBilingualTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Rename Title → TitleAr (data preserved verbatim).
            migrationBuilder.RenameColumn(
                name: "Title",
                table: "reports",
                newName: "TitleAr");

            // 2) Add TitleEn, allow null initially so backfill can populate
            //    it from TitleAr without violating NOT NULL.
            migrationBuilder.AddColumn<string>(
                name: "TitleEn",
                table: "reports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // 3) Rewrite the FTS trigger BEFORE any UPDATE so the trigger
            //    body references the renamed column (TitleAr) and the new
            //    column (TitleEn) rather than the old "Title". If we run
            //    the backfill UPDATE first, the old trigger fires and crashes
            //    with "record 'new' has no field 'Title'".
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reports_search_vector_update()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."SearchVector" :=
                        setweight(to_tsvector('arabic',  coalesce(NEW."TitleAr", '')), 'A') ||
                        setweight(to_tsvector('arabic',  coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('arabic',  coalesce(NEW."ExtractedText", '')), 'C') ||
                        setweight(to_tsvector('english', coalesce(NEW."TitleEn", '')), 'A') ||
                        setweight(to_tsvector('english', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('english', coalesce(NEW."ExtractedText", '')), 'C');
                    RETURN NEW;
                END;
                $$;
                """);

            // 4) Backfill legacy rows: TitleEn = TitleAr. Admins refine later.
            //    The trigger now references TitleAr/TitleEn so this is safe.
            migrationBuilder.Sql(@"UPDATE reports SET ""TitleEn"" = ""TitleAr"" WHERE ""TitleEn"" IS NULL;");

            // 5) Lock TitleEn as NOT NULL once every row has a value.
            migrationBuilder.AlterColumn<string>(
                name: "TitleEn",
                table: "reports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            // 6) Backfill the search vector for every existing row by
            //    issuing a no-op update — the trigger fires and rebuilds
            //    SearchVector against the new column layout.
            migrationBuilder.Sql(@"UPDATE reports SET ""TitleAr"" = ""TitleAr"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the pre-split trigger first, then drop TitleEn,
            // then rename TitleAr back to Title.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reports_search_vector_update()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."SearchVector" :=
                        setweight(to_tsvector('arabic',  coalesce(NEW."TitleAr", '')), 'A') ||
                        setweight(to_tsvector('arabic',  coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('arabic',  coalesce(NEW."ExtractedText", '')), 'C') ||
                        setweight(to_tsvector('english', coalesce(NEW."TitleAr", '')), 'A') ||
                        setweight(to_tsvector('english', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('english', coalesce(NEW."ExtractedText", '')), 'C');
                    RETURN NEW;
                END;
                $$;
                """);

            migrationBuilder.DropColumn(
                name: "TitleEn",
                table: "reports");

            migrationBuilder.RenameColumn(
                name: "TitleAr",
                table: "reports",
                newName: "Title");
        }
    }
}
