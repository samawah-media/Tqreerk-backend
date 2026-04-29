namespace Taqreerk.Domain.Enums;

/// Outcome of a single review action. One row per action — a report that's
/// returned for edit, re-uploaded, then approved produces two rows: first
/// `ReturnedForEdit`, then `Approved`.
public enum ReviewDecision
{
    Approved,
    Rejected,
    ReturnedForEdit,
}
