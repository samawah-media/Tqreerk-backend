namespace Taqreerk.Application.Settings;

/// Configuration for admin-side background workers. Today that's just the
/// claim auto-release sweep; will grow to include audit log retention,
/// quick-stats refresh, and similar admin-only chores.
public class AdminWorkerSettings
{
    public const string Section = "AdminWorkers";

    /// How often the auto-release sweep runs. Short enough that a stuck
    /// claim only blocks the queue for a minute or so; long enough that
    /// the query cost is trivial.
    public int ClaimSweepPollSeconds { get; set; } = 60;

    /// Claim TTL — a report is auto-released once its `ClaimedAt` is older
    /// than this. The plan calls for 60 minutes.
    public int ClaimMaxAgeMinutes { get; set; } = 60;
}
