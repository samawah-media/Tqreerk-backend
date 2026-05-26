namespace Taqreerk.Application.Interfaces;

public interface ISubscriptionProvisioningService
{
    /// Creates an active free individual subscription when none exists.
    Task EnsureIndividualFreeAsync(Guid userId, CancellationToken ct = default);

    /// Creates an organization subscription for the chosen plan.
    /// When <paramref name="awaitingPayment"/> is true, status stays inactive until checkout completes.
    Task EnsureOrganizationPlanAsync(
        Guid organizationId,
        Guid planId,
        bool awaitingPayment = true,
        CancellationToken ct = default);
}
