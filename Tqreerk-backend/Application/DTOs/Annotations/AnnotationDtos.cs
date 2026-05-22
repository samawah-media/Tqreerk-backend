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

/// Free-text note on a saved report. A user can have many notes per
/// report; the editor lists them with add/edit/delete.
public sealed record PersonalNoteDto(
    Guid Id,
    string Body,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record CreatePersonalNoteRequest(
    string Body);

public sealed record UpdatePersonalNoteRequest(
    string Body);
