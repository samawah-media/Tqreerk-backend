using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Compare;

/// Body of `POST /api/ai/compare`. Caller picks 2..4 published reports.
/// The frontend collects these via the global "compare selection" floating
/// bar and ships them in one call when the user clicks "قارن الآن".
public sealed record CreateComparisonRequest(
    [Required, MinLength(2)] IReadOnlyList<Guid> ReportIds);

/// Per-report metadata column the comparison table renders. The fields
/// mirror the org dashboard's report leaderboard so the comparison
/// page stays visually consistent.
public sealed record ComparedReportDto(
    Guid Id,
    string TitleAr,
    string TitleEn,
    string Slug,
    string? CoverImageUrl,
    string? OrganizationNameAr,
    int? PublicationYear,
    string? SectorNameAr,
    /// 3-7 bullet-point summary of the report. Empty list if the AI step
    /// hasn't run yet or the row predates the bullet-point migration.
    IReadOnlyList<string> Summary,
    IReadOnlyList<string> KeyFindings);

/// One row of the qualitative summary banner. The Python service emits
/// these as structured Gemini output (common topics, key differences,
/// shared indicators, strategic implications). We pass the bytes through
/// verbatim — the frontend renders whichever keys are present.
public sealed record ComparisonResultDto(
    Guid Id,
    DateTime CreatedAt,
    IReadOnlyList<ComparedReportDto> Reports,
    /// Pairwise cosine similarity matrix from layer 1, keyed by ordered
    /// pair (a, b) where a < b alphabetically. Score in [0,1].
    IReadOnlyList<SimilarityPairDto> Similarities,
    /// Raw Gemini structured-output JSON (object). Common keys:
    ///   - common_topics (string[])
    ///   - key_differences (string[])
    ///   - shared_indicators (string[])
    ///   - strategic_implications (string[])
    /// The frontend renders whichever sub-trees are populated.
    string QualitativeJson);

public sealed record SimilarityPairDto(Guid ReportIdA, Guid ReportIdB, double Score);

/// Compact row for the user's comparisons history list.
public sealed record ComparisonListItemDto(
    Guid Id,
    DateTime CreatedAt,
    int ReportCount,
    IReadOnlyList<ComparisonTitleDto> ReportTitles);

/// Bilingual title pair surfaced in comparison history rows.
public sealed record ComparisonTitleDto(string TitleAr, string TitleEn);
