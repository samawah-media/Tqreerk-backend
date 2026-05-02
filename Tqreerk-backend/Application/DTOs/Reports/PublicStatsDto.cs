namespace Taqreerk.Application.DTOs.Reports;

/// Lightweight rollup for the public Landing hero. Three counts the
/// homepage strip wants to show off — published library size, active
/// publishers, and individual readers.
///
/// Cached server-side via ResponseCache; the SPA can call it on every
/// hero render without burning the DB.
public record PublicStatsOverviewDto(
    int PublishedReports,
    int ActiveOrganizations,
    int IndividualReaders
);
