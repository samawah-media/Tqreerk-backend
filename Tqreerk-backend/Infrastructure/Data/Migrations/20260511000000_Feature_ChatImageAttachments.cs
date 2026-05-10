using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the `ImagesAttached` jsonb column to `chat_messages`. The
    /// Python ai-service writes a short list of `{r: report_id, p: page}`
    /// entries here every time the chat agent's `get_page_image` tool
    /// renders a PDF page for multimodal Q&amp;A. On the next chat turn,
    /// `api/chat.py` re-renders those pages and injects them as multimodal
    /// HumanMessages so the agent doesn't have to re-call the tool —
    /// cuts ~10–15 s off follow-up visual questions on the same page.
    ///
    /// The column is intentionally not mapped on the .NET `ChatMessage`
    /// entity (Python owns the read/write path). EF does not complain
    /// about columns it doesn't know about, so this stays a small,
    /// self-contained schema change.
    ///
    /// Rollback path: drop the column. Application code first checks
    /// `PAGE_IMAGE_PERSIST_ENABLED`, so disabling that env var stops the
    /// writes; the existing column then just stays empty.
    /// </summary>
    public partial class Feature_ChatImageAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // jsonb so a single column can hold an array of small dicts
            // ({"r": "...", "p": 7}) — no per-row table needed. Nullable
            // because most existing rows + cache-hit assistant rows don't
            // attach images.
            migrationBuilder.Sql(@"
                ALTER TABLE chat_messages
                  ADD COLUMN IF NOT EXISTS ""ImagesAttached"" jsonb NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE chat_messages
                  DROP COLUMN IF EXISTS ""ImagesAttached"";
            ");
        }
    }
}
