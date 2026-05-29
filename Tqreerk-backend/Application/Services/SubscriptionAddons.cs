using System.Text.Json;

namespace Taqreerk.Application.Services;

/// <summary>Typed helpers for subscription.AddonsJson (no migration).</summary>
public static class SubscriptionAddons
{
    public sealed record State(
        bool AutoRenew = true,
        string? MoyasarToken = null,
        Guid? PendingPlanId = null);

    public static State Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new State();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var autoRenew = !root.TryGetProperty("autoRenew", out var ar) || ar.GetBoolean();
            var token = root.TryGetProperty("moyasarToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            Guid? pendingPlanId = null;
            if (root.TryGetProperty("pendingPlanId", out var pp)
                && pp.ValueKind == JsonValueKind.String
                && Guid.TryParse(pp.GetString(), out var parsed))
            {
                pendingPlanId = parsed;
            }

            return new State(autoRenew, token, pendingPlanId);
        }
        catch
        {
            return new State();
        }
    }

    public static string Serialize(State state)
        => JsonSerializer.Serialize(new
        {
            autoRenew = state.AutoRenew,
            moyasarToken = state.MoyasarToken,
            pendingPlanId = state.PendingPlanId,
        });
}
