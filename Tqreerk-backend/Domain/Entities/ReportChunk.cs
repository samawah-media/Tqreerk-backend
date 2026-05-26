using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// <summary>
/// A sub-page chunk produced by the Python ai-service during ingestion.
/// One PDF page is split into one or more chunks (~500 tokens each, with overlap)
/// so that retrieval can return a tighter, more relevant slice of context to the LLM.
///
/// Schema details (managed only in raw SQL by the migration / ai-service):
///   • embedding      vector(768)  — Gemini text-embedding-004
///   • search_vector  tsvector     — bilingual Arabic + English, populated by trigger
///   • metadata       jsonb        — { section_title, page_type, language }
/// EF never reads or writes those columns.
/// </summary>
public class ReportChunk : BaseEntity
{
    public Guid ReportId { get; set; }

    /// <summary>1-based page number this chunk was extracted from.</summary>
    public int PageNumber { get; set; }

    /// <summary>0-based ordinal of the chunk within its page.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Chunked text — Gemini Vision output for the page, sliced to ~500 tokens.</summary>
    public string Content { get; set; } = string.Empty;

    public Report Report { get; set; } = null!;
}
