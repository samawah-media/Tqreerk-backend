using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Taqreerk.Infrastructure.Data;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds a Topics jsonb column to report_ai_contents.
    ///
    /// Why: the previous schema had no place for the summarizer's `topics`
    /// output, so the C# finalizer mis-stored it in the Indicators column.
    /// That collided with the new combined summarize+insights pipeline which
    /// returns real structured indicators (KPIs) that belong in Indicators.
    ///
    /// After this migration the columns map cleanly:
    ///   Summary         ← summary (text)
    ///   KeyFindings     ← key_findings (string[])
    ///   Topics          ← topics (string[])           NEW
    ///   Indicators      ← indicators (object[])      now stores the real thing
    ///   Trends          ← trends (object[])
    /// </summary>
    [DbContext(typeof(TaqreerkDbContext))]
    [Migration("20260502010000_AddReportAiContentTopicsColumn")]
    public partial class AddReportAiContentTopicsColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Topics",
                table: "report_ai_contents",
                type: "jsonb",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Topics",
                table: "report_ai_contents");
        }
    }
}
