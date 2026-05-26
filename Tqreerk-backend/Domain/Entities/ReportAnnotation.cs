using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// One annotation a user adds to a saved report — either a colored
/// highlight rectangle (drag-paint, no text-selection requirement) or
/// a sticky note pinned at a point on the page. Annotations are
/// per-user — different users see different annotations on the same
/// report. Only owners of a saved_reports row can create them.
///
/// `SelectionRect` is jsonb that the frontend interprets:
///   highlight → { rects: [{ x, y, w, h }] }   (normalized 0..1)
///   note      → { point: { x, y } }           (normalized 0..1)
public class ReportAnnotation : AuditableEntity
{
    public Guid UserId { get; set; }
    public Guid ReportId { get; set; }

    /// Distinguishes drag-painted highlight rectangles from
    /// point-pinned sticky notes. See AnnotationType.
    public AnnotationType Type { get; set; } = AnnotationType.Highlight;

    /// 1-indexed page number the annotation lives on. Same numbering
    /// as the PDF viewer's pager.
    public int Page { get; set; }

    /// Highlights leave this empty — the user no longer selects text;
    /// they paint a rectangle. Notes also leave it empty (the body is
    /// in Note). Kept on the entity so legacy rows survive but the
    /// frontend no longer reads it for new highlights.
    public string SelectionText { get; set; } = string.Empty;

    /// JSON describing the annotation's geometry. Shape depends on
    /// Type — see the doc comment on this class.
    public string SelectionRect { get; set; } = "{}";

    /// Highlight color — the v1 picker exposes 4 (yellow / green /
    /// pink / blue). For notes, this colors the pin shown on the PDF.
    public string Color { get; set; } = "yellow";

    /// Body of the sticky note (Type = Note). Unused for highlights.
    public string? Note { get; set; }

    public User User { get; set; } = null!;
    public Report Report { get; set; } = null!;
}
