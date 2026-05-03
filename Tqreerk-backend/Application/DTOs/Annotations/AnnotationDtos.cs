using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Annotations;

/// One row in the annotations list for the editor.
/// `SelectionRect` is forwarded verbatim — the frontend owns the shape:
///   highlight → { rects: [{ x, y, w, h }] }
///   note      → { point: { x, y } }
public sealed record AnnotationDto(
    Guid Id,
    AnnotationType Type,
    int Page,
    string SelectionText,
    string SelectionRect,
    string Color,
    string? Note,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record CreateAnnotationRequest(
    AnnotationType Type,
    int Page,
    string? SelectionText,
    string SelectionRect,
    string Color,
    string? Note);

public sealed record UpdateAnnotationRequest(
    string? Color,
    string? Note);

/// Personal notepad for a (user, report) pair — exactly one row exists
/// once the user has interacted with the editor's notes drawer. Returns
/// `Body = ""` and `UpdatedAt = null` when no row exists yet, so the
/// editor can render an empty textarea on first load.
public sealed record PersonalNoteDto(
    string Body,
    DateTime? UpdatedAt);

public sealed record UpdatePersonalNoteRequest(
    string Body);
