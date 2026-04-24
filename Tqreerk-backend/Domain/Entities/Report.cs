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
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ReportType { get; set; }
    public string OriginalLanguage { get; set; } = "ar";
    public int? PublicationYear { get; set; }
    public DateOnly? PublicationDate { get; set; }
    public int? PageCount { get; set; }
    public string? FileUrl { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? ExtractedText { get; set; }

    /// <summary>Managed by PostgreSQL trigger — do not write from application</summary>
    public NpgsqlTsVector? SearchVector { get; set; }

    public int ViewsCount { get; set; }
    public int DownloadsCount { get; set; }
    public decimal AvgRating { get; set; }
    public int RatingsCount { get; set; }
    public bool IsFeatured { get; set; }
    public ReportSourceType SourceType { get; set; } = ReportSourceType.Organization;
    public ReportStatus Status { get; set; } = ReportStatus.Draft;

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
    public ICollection<ReportPage> Pages { get; set; } = [];
    public ICollection<ChatSession> ChatSessions { get; set; } = [];
}
