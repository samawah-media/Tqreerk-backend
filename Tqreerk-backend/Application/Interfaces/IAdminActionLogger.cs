namespace Taqreerk.Application.Interfaces;

/// Convenience surface for writing rows into `admin_action_logs`. Lives at
/// the Application layer so any service can inject it; the infra-side
/// implementation owns the DbContext + IHttpContextAccessor for IP and UA.
///
/// All methods are best-effort — the logger swallows its own exceptions
/// and logs them to ILogger so a failed audit insert can never block the
/// real action that triggered it.
public interface IAdminActionLogger
{
    /// Record an action one of the platform staff just performed. The
    /// logger pulls IP / user-agent from the current HTTP context if there
    /// is one (background workers can pass adminUserId = null when the
    /// system itself acted, e.g. the auto-release sweep).
    Task LogAsync(
        Guid? adminUserId,
        string actionType,
        string targetEntityType,
        Guid? targetEntityId,
        string? reason = null,
        object? beforeState = null,
        object? afterState = null,
        CancellationToken ct = default);
}
