using Taqreerk.Application.DTOs.Annotations;

namespace Taqreerk.Application.Interfaces;

/// Per-user reads/writes for annotations + the personal notepad on a
/// SAVED report. All methods enforce the saved-only gate: a user must
/// have a saved_reports row for the target report or every call here
/// returns/throws KeyNotFound. Saving a report (1.1, gated by the
/// freemium cap) is the de-facto unlock for the editor.
public interface IAnnotationsService
{
    Task<IReadOnlyList<AnnotationDto>> ListAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);

    Task<AnnotationDto> CreateAsync(
        Guid userId, Guid reportId, CreateAnnotationRequest req, CancellationToken ct = default);

    Task<AnnotationDto> UpdateAsync(
        Guid userId, Guid reportId, Guid annotationId,
        UpdateAnnotationRequest req, CancellationToken ct = default);

    Task DeleteAsync(
        Guid userId, Guid reportId, Guid annotationId, CancellationToken ct = default);

    Task<PersonalNoteDto> GetNoteAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);

    /// Upsert the per-(user, report) notepad. Empty body is fine —
    /// stores it as an empty string rather than deleting the row, so
    /// `UpdatedAt` still reflects the last edit.
    Task<PersonalNoteDto> UpsertNoteAsync(
        Guid userId, Guid reportId, UpdatePersonalNoteRequest req, CancellationToken ct = default);

    /// One-shot bootstrap for the editor page: metadata + AI + signed
    /// URLs + caller's annotations + note + plan tier. 404 if the
    /// caller hasn't saved the report.
    Task<EditorBootstrapDto> GetEditorBootstrapAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);
}
