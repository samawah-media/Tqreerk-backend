using System.ComponentModel.DataAnnotations;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.FeatureRequests;

/// One row in either the org's "my feature requests" list or the
/// admin's review queue. Carries enough metadata for both surfaces:
/// the org needs the report title + decision context, the admin needs
/// the org name + reviewer attribution.
public sealed record FeatureRequestDto(
    Guid Id,
    Guid ReportId,
    string ReportTitleAr,
    string ReportTitleEn,
    string ReportSlug,
    string? ReportCoverImageUrl,
    Guid OrganizationId,
    string OrganizationNameAr,
    string? OrganizationLogoUrl,
    Guid RequestedByUserId,
    string RequestedByName,
    string? Note,
    FeatureRequestStatus Status,
    Guid? ReviewedByUserId,
    string? ReviewedByName,
    DateTime? ReviewedAt,
    string? DecisionNote,
    DateTime CreatedAt);

/// Body for `POST /api/reports/{id}/feature-request`. The report id
/// comes from the route; the optional note is the org's pitch to the
/// admin reviewing the queue.
public sealed record CreateFeatureRequest(
    [MaxLength(1000)] string? Note);

/// Body for `POST /api/admin/feature-requests/{id}/approve` and
/// `/reject`. The decision note is optional but recommended on
/// rejections so the org sees why.
public sealed record FeatureRequestDecisionRequest(
    [MaxLength(1000)] string? DecisionNote);
