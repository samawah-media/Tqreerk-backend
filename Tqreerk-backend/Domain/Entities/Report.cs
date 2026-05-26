using NpgsqlTypes;
using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class Report : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid UploadedByUserId { get; set; }
    public Guid? SectorId { get; set; }
    public Guid? CountryId { get; set; }
    /// Arabic display title. Required. Also drives slug generation.
    public string TitleAr { get; set; } = string.Empty;

    /// English display title. Required. Used by the SPA when locale=en.
    public string TitleEn { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ReportType { get; set; }
    public string OriginalLanguage { get; set; } = "ar";
    public int? PublicationYear { get; set; }
    public DateOnly? PublicationDate { get; set; }
    public int? PageCount { get; set; }
    public string? FileUrl { get; set; }
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// Base GCS object-key prefix for the cover-variant set, e.g.
    /// <c>public/covers/{reportId}</c>. When set, three sibling objects exist:
    /// <c>thumb.webp</c>, <c>medium.webp</c>, <c>full.webp</c>. All three are
    /// world-readable with a 1-year immutable Cache-Control so the browser
    /// can cache them across navigations — that's what makes the public-page
    /// hero image cheap on the LCP path.
    ///
    /// When this is non-null the variant URLs are the canonical cover source;
    /// <see cref="CoverImageUrl"/> is the legacy single-image fallback, kept
    /// for reports uploaded before the variant pipeline shipped.
    /// </summary>
    public string? CoverImageBaseKey { get; set; }
    public string? ExtractedText { get; set; }

    /// <summary>
    /// Free-form publisher / source label — set by staff during bulk
    /// imports of third-party reports (e.g. "World Bank", "Statista").
    /// Distinct from <see cref="Organization"/>, which is always the
    /// account-holding entity that owns the row.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Comma-separated list of report authors. Stored as a single string
    /// to keep the bulk-import Excel round-trip lossless — staff type
    /// "أحمد محمد، فاطمة علي" once and we don't have to round-trip
    /// through a join table for what is essentially a display field.
    /// </summary>
    public string? Authors { get; set; }

    /// <summary>Managed by PostgreSQL trigger — do not write from application</summary>
    public NpgsqlTsVector? SearchVector { get; set; }

    public int ViewsCount { get; set; }
    public int DownloadsCount { get; set; }
    public decimal AvgRating { get; set; }
    public int RatingsCount { get; set; }
    public bool IsFeatured { get; set; }
    public ReportSourceType SourceType { get; set; } = ReportSourceType.Organization;
    public ReportStatus Status { get; set; } = ReportStatus.PendingReview;

    // ── Review workflow (admin moderation) ────────────────────────────────
    /// When the org submitted the report for moderation. Null while Draft.
    public DateTime? SubmittedForReviewAt { get; set; }

    /// When the report transitioned to Published. Null until then. Distinct
    /// from CreatedAt because the workflow can take days.
    public DateTime? PublishedAt { get; set; }

    /// Reviewer currently holding the claim lock; null when the report is
    /// in PendingReview (back in the queue) or after a final decision.
    public Guid? ClaimedByReviewerId { get; set; }

    /// When the claim was taken. Used by the auto-release background job to
    /// drop stale claims (>60 min) so the report returns to the queue.
    public DateTime? ClaimedAt { get; set; }
    public User? ClaimedByReviewer { get; set; }

    public Organization Organization { get; set; } = null!;
    public User UploadedByUser { get; set; } = null!;
    public Sector? Sector { get; set; }
    public Country? Country { get; set; }
    public ICollection<ReportTranslation> Translations { get; set; } = [];
    public ICollection<ReportKeyword> Keywords { get; set; } = [];
    public ICollection<ReportAiContent> AiContents { get; set; } = [];
    public ICollection<AiJob> AiJobs { get; set; } = [];
    public ICollection<Infographic> Infographics { get; set; } = [];
    public ICollection<ReportRating> Ratings { get; set; } = [];
    public ICollection<ReportRecommendation> Recommendations { get; set; } = [];
    public ICollection<SavedReport> SavedByUsers { get; set; } = [];
    public ICollection<ReportView> Views { get; set; } = [];
    public ICollection<ReportChunk> Chunks { get; set; } = [];
    public ICollection<ChatSession> ChatSessions { get; set; } = [];
}
