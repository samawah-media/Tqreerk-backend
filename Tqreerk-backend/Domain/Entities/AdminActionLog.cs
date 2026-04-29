using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// Cross-cutting audit trail of every action a platform-staff member
/// takes. Distinct from `audit_logs` (which is a generic event stream that
/// any user can produce) — this table is exclusively staff actions and
/// powers the "who did what" view in the admin panel.
///
/// Schema is intentionally generic — `entityType` + `entityId` cover any
/// target the staff member acts on (report, organization, user, plan…),
/// while `beforeState` / `afterState` jsonb columns let us record diffs
/// without per-action tables.
public class AdminActionLog : BaseEntity
{
    /// Staff member who performed the action. Null for system-triggered
    /// rows (e.g. the auto-release sweep) — the ActionType string still
    /// makes the source clear (e.g. "report.claim_auto_released").
    public Guid? AdminUserId { get; set; }

    /// Verb describing what happened. Free-form for now; common values:
    ///   - "report.approved" / "report.rejected" / "report.returned_for_edit"
    ///   - "report.claim_auto_released" (no admin user; system action — see
    ///     the AdminUserId nullability note below)
    ///   - "organization.suspended" / "organization.reactivated" (Feature 4)
    ///   - "user.banned" / "user.unbanned" (Feature 5)
    ///   - "plan.modified" / "settings.changed" (later)
    public string ActionType { get; set; } = string.Empty;

    /// What the action was performed on. Free-form again — we don't enum
    /// because new entity types are added often. Common values:
    ///   - "report", "organization", "user", "plan", "settings"
    public string TargetEntityType { get; set; } = string.Empty;

    /// PK of the target row. Null for actions that don't target a single
    /// entity (e.g. system-wide settings changes).
    public Guid? TargetEntityId { get; set; }

    /// jsonb — pre-action snapshot. Optional; only some actions bother
    /// with a before image.
    public string? BeforeState { get; set; }

    /// jsonb — post-action snapshot. Same caveat — optional.
    public string? AfterState { get; set; }

    /// Free-form reason / notes the staff member provided. Used heavily on
    /// destructive actions (rejection notes, ban reasons) for downstream
    /// review.
    public string? Reason { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public User? AdminUser { get; set; }
}
