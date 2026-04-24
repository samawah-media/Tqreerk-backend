using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ReportPage : BaseEntity
{
    public Guid ReportId { get; set; }
    public int PageNumber { get; set; }

    /// <summary>Rich text description extracted by Gemini (text + graph descriptions)</summary>
    public string Content { get; set; } = string.Empty;

    // Embedding (vector(768)) is managed exclusively by the Python ai-service via psycopg2.
    // It is added via raw SQL in the migration and never read or written from C#.

    public Report Report { get; set; } = null!;
}
