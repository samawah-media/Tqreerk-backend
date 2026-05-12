namespace Taqreerk.Application.DTOs.Plans;

/// Minimal read-only projection of a plan for the public pricing page.
/// Exposes only what the storefront needs — name, price, target type,
/// and a pre-computed human-readable feature list so the SPA doesn't
/// need to know anything about limit semantics.
public sealed record PublicPlanDto(
    Guid   Id,
    string NameAr,
    string NameEn,
    string TargetType,      // "Individual" | "Organization"
    decimal AnnualPrice,
    bool   IsHighlighted,   // true on the "most popular" tier per group
    IReadOnlyList<string> FeaturesAr);
