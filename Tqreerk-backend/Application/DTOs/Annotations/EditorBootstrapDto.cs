using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.DTOs.Annotations;

/// One-shot payload for the editor page. Everything the workspace
/// needs to render: report metadata + signed URLs (fileUrl, cover,
/// translations), AI content, the caller's existing highlights and
/// notepad, and a tier label so the UI can hide premium-only tabs.
///
/// Returns 404 for a report the caller hasn't saved (saved-only gate
/// in AnnotationsService.EnsureSavedAsync).
public sealed record EditorBootstrapDto(
    /// Full public-report detail — title, signed fileUrl, cover, AI
    /// summary, key findings, topics/indicators, translations. Same
    /// shape the read-only public page consumes.
    PublicReportDetailDto Report,
    EditorPlanInfo Plan,
    IReadOnlyList<AnnotationDto> Annotations,
    IReadOnlyList<PersonalNoteDto> Notes);

/// Tier identification for the UI's premium-gated tabs. The plan id
/// is the source of truth (matched against Domain.Common.PlanIds);
/// `tier` is a derived label so the SPA doesn't need to know specific
/// Guids.
public sealed record EditorPlanInfo(
    Guid PlanId,
    /// "free" | "basic" | "premium" | "unknown". Frontend keys layout
    /// branches on this string.
    string Tier,
    string PlanNameAr,
    string PlanNameEn);
