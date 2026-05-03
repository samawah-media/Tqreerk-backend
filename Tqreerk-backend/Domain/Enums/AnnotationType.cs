namespace Taqreerk.Domain.Enums;

/// What kind of annotation a row in `report_annotations` represents.
/// `Highlight` is a colored rectangle painted on the page (no required
/// text — the editor uses drag-to-paint, not text selection).
/// `Note` is a sticky-note pinned at a single point on the page; the
/// `Note` column on the entity carries the body, and `SelectionRect`
/// stores `{ point: { x, y } }`.
public enum AnnotationType
{
    Highlight,
    Note,
}
