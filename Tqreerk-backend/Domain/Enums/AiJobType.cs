namespace Taqreerk.Domain.Enums;

public enum AiJobType
{
    Summarization,
    Translation,
    Comparison,
    Infographic,
    KeywordExtraction,
    InsightExtraction,
    /// <summary>PDF ingestion via the ai-service /reports/ingest endpoint.
    /// Extracts text + writes report_pages + builds embeddings.</summary>
    Ingestion
}
