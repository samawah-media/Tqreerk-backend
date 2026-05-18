using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Annotations;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Common;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// Allowed colors for v1 highlights. The picker exposes 4; an unknown
/// value falls back to yellow rather than 400ing — kept here so the
/// service is the single source of truth for the palette.
internal static class HighlightColors
{
    public const string Yellow = "yellow";
    public const string Green = "green";
    public const string Pink = "pink";
    public const string Blue = "blue";

    public static string Normalize(string? c) =>
        c?.ToLowerInvariant() switch
        {
            Yellow or Green or Pink or Blue => c!.ToLowerInvariant(),
            _ => Yellow,
        };
}

public class AnnotationsService : IAnnotationsService
{
    private readonly TaqreerkDbContext _db;
    private readonly IPublicReportService _publicReports;

    public AnnotationsService(TaqreerkDbContext db, IPublicReportService publicReports)
    {
        _db = db;
        _publicReports = publicReports;
    }

    public async Task<IReadOnlyList<AnnotationDto>> ListAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        await EnsureSavedAsync(userId, reportId, ct);

        return await _db.ReportAnnotations
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.ReportId == reportId)
            .OrderBy(a => a.Page)
            .ThenBy(a => a.CreatedAt)
            .Select(a => new AnnotationDto(
                a.Id, a.Type, a.Page, a.SelectionText, a.SelectionRect,
                a.Color, a.Note, a.CreatedAt, a.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<AnnotationDto> CreateAsync(
        Guid userId, Guid reportId, CreateAnnotationRequest req, CancellationToken ct = default)
    {
        await EnsureSavedAsync(userId, reportId, ct);

        if (string.IsNullOrWhiteSpace(req.SelectionRect))
            throw new ArgumentException("Selection rect is required.");
        if (req.Page < 1)
            throw new ArgumentException("Page must be 1 or greater.");
        // Notes must carry a body — there's no point pinning an empty
        // sticky. Highlights can be empty (drag-paint with no text).
        if (req.Type == AnnotationType.Note && string.IsNullOrWhiteSpace(req.Note))
            throw new ArgumentException("Note body is required for notes.");

        var row = new ReportAnnotation
        {
            UserId = userId,
            ReportId = reportId,
            Type = req.Type,
            Page = req.Page,
            // SelectionText is optional now — drag-paint highlights
            // don't capture text and notes don't either.
            SelectionText = string.IsNullOrWhiteSpace(req.SelectionText)
                ? string.Empty
                : req.SelectionText.Trim(),
            SelectionRect = req.SelectionRect,
            Color = HighlightColors.Normalize(req.Color),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
        };

        _db.ReportAnnotations.Add(row);
        await _db.SaveChangesAsync(ct);

        return ToDto(row);
    }

    public async Task<AnnotationDto> UpdateAsync(
        Guid userId, Guid reportId, Guid annotationId,
        UpdateAnnotationRequest req, CancellationToken ct = default)
    {
        await EnsureSavedAsync(userId, reportId, ct);

        var row = await _db.ReportAnnotations
            .FirstOrDefaultAsync(
                a => a.Id == annotationId && a.UserId == userId && a.ReportId == reportId, ct)
            ?? throw new KeyNotFoundException("Annotation not found.");

        // Partial update — only fields the caller sent are touched.
        // Empty-string Note is treated as a real "clear it" intent;
        // null means "don't change". The editor sends Note: "" when
        // the user blanks the textarea.
        if (req.Color is not null)
            row.Color = HighlightColors.Normalize(req.Color);
        if (req.Note is not null)
            row.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();

        await _db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task DeleteAsync(
        Guid userId, Guid reportId, Guid annotationId, CancellationToken ct = default)
    {
        await EnsureSavedAsync(userId, reportId, ct);

        var row = await _db.ReportAnnotations
            .FirstOrDefaultAsync(
                a => a.Id == annotationId && a.UserId == userId && a.ReportId == reportId, ct)
            ?? throw new KeyNotFoundException("Annotation not found.");

        _db.ReportAnnotations.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PersonalNoteDto> GetNoteAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        await EnsureSavedAsync(userId, reportId, ct);

        var row = await _db.ReportPersonalNotes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.UserId == userId && n.ReportId == reportId, ct);

        // Empty-stub when no row exists. Lets the editor render an
        // empty textarea without a separate "exists?" probe.
        return row is null
            ? new PersonalNoteDto(Body: "", UpdatedAt: null)
            : new PersonalNoteDto(row.Body, row.UpdatedAt);
    }

    public async Task<PersonalNoteDto> UpsertNoteAsync(
        Guid userId, Guid reportId, UpdatePersonalNoteRequest req, CancellationToken ct = default)
    {
        await EnsureSavedAsync(userId, reportId, ct);

        var body = req.Body ?? "";
        if (body.Length > 50_000)
            throw new ArgumentException("Note is too long (max 50000 chars).");

        var existing = await _db.ReportPersonalNotes
            .FirstOrDefaultAsync(n => n.UserId == userId && n.ReportId == reportId, ct);

        if (existing is null)
        {
            existing = new ReportPersonalNote
            {
                UserId = userId,
                ReportId = reportId,
                Body = body,
            };
            _db.ReportPersonalNotes.Add(existing);
        }
        else
        {
            existing.Body = body;
        }

        await _db.SaveChangesAsync(ct);
        return new PersonalNoteDto(existing.Body, existing.UpdatedAt);
    }

    public async Task<EditorBootstrapDto> GetEditorBootstrapAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        await EnsureSavedAsync(userId, reportId, ct);

        // Resolve the slug so we can reuse PublicReportService for the
        // metadata + signed URLs. Cheap one-row lookup; the alternative
        // is duplicating ~60 lines of URL-signing logic. The query
        // filter on Report enforces non-deleted; status check matches
        // PublicReportService's PublishedQuery so unpublished reports
        // give the same 404 path.
        var slug = await _db.Reports
            .AsNoTracking()
            .Where(r => r.Id == reportId && r.Status == ReportStatus.Published)
            .Select(r => r.Slug)
            .FirstOrDefaultAsync(ct);
        if (slug is null)
            throw new KeyNotFoundException("Report not found.");

        var detail = await _publicReports.GetBySlugAsync(slug, ct);

        // Plan tier: match the user's active subscription against the
        // seeded individual plan ids. Anything else (no row, an org
        // plan, a future plan we don't recognize) falls through to
        // "unknown" and the UI treats it as "no premium features".
        var subscription = await _db.Subscriptions
            .AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var (tier, planNameAr, planNameEn, planId) = subscription?.PlanId switch
        {
            { } id when id == PlanIds.IndividualFree =>
                ("free", subscription!.Plan.NameAr, subscription.Plan.NameEn, id),
            { } id when id == PlanIds.IndividualBasic =>
                ("basic", subscription!.Plan.NameAr, subscription.Plan.NameEn, id),
            { } id when id == PlanIds.IndividualPremium =>
                ("premium", subscription!.Plan.NameAr, subscription.Plan.NameEn, id),
            _ => ("unknown", "—", "—", subscription?.PlanId ?? Guid.Empty),
        };

        var annotations = await ListAsync(userId, reportId, ct);
        var note = await GetNoteAsync(userId, reportId, ct);

        return new EditorBootstrapDto(
            Report: detail,
            Plan: new EditorPlanInfo(planId, tier, planNameAr, planNameEn),
            Annotations: annotations,
            Note: note);
    }

    /// 404 path for every endpoint here. Editor + annotations are now
    /// open to any authenticated user on any published report — no
    /// "must be saved first" gate. We still 404 on missing/unpublished
    /// reports so callers can't probe for existence.
    private async Task EnsureSavedAsync(Guid userId, Guid reportId, CancellationToken ct)
    {
        var exists = await _db.Reports
            .AsNoTracking()
            .AnyAsync(r => r.Id == reportId && r.Status == ReportStatus.Published, ct);
        if (!exists)
            throw new KeyNotFoundException("Report not found.");
    }

    private static AnnotationDto ToDto(ReportAnnotation a) =>
        new(a.Id, a.Type, a.Page, a.SelectionText, a.SelectionRect,
            a.Color, a.Note, a.CreatedAt, a.UpdatedAt);
}
