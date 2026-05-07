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
    Ingestion,
    /// <summary>Online RAG evaluation (Ragas metrics) over a finished chat.
    /// Enqueued by the Python ai-service after each SSE stream completes;
    /// the .NET worker's finalizer ignores these rows (no report-status
    /// transition is owned here). Listed in the enum only so EF's string
    /// converter doesn't throw when one shows up in ai_jobs.</summary>
    Evaluation
}
