using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Anonymous-readable view of the published reports library. No JWT required.
/// Only Status=Published reports are returned and uploader PII is excluded by
/// the service layer.
[ApiController]
[Route("api/public/reports")]
[Produces("application/json")]
[AllowAnonymous]
public class PublicReportsController : ControllerBase
{
    private readonly IPublicReportService _reports;

    public PublicReportsController(IPublicReportService reports)
    {
        _reports = reports;
    }

    /// <summary>Paginated list of published reports with search + filters.</summary>
    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PagedResult<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery(Name = "q")] string? q = null,
        [FromQuery(Name = "sectors")] Guid[]? sectors = null,
        [FromQuery(Name = "countries")] Guid[]? countries = null,
        [FromQuery(Name = "year_from")] int? yearFrom = null,
        [FromQuery(Name = "year_to")] int? yearTo = null,
        [FromQuery(Name = "language")] string? language = null,
        [FromQuery(Name = "sort")] string? sort = null,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "pageSize")] int pageSize = 12,
        CancellationToken ct = default)
    {
        var req = new PublicReportListRequest(
            Q: q,
            Sectors: sectors,
            Countries: countries,
            YearFrom: yearFrom,
            YearTo: yearTo,
            Language: language,
            Sort: sort,
            Page: page,
            PageSize: pageSize
        );
        return Ok(await _reports.ListAsync(req, ct));
    }

    /// <summary>Curated featured reports for the homepage.</summary>
    [HttpGet("featured")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Featured([FromQuery] int take = 5, CancellationToken ct = default)
        => Ok(await _reports.GetFeaturedAsync(take, ct));

    /// <summary>Most-viewed reports in the last 7 days.</summary>
    [HttpGet("trending")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Trending([FromQuery] int take = 5, CancellationToken ct = default)
        => Ok(await _reports.GetTrendingAsync(take, ct));

    /// <summary>Most recent published reports.</summary>
    [HttpGet("recent")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Recent([FromQuery] int take = 8, CancellationToken ct = default)
        => Ok(await _reports.GetRecentAsync(take, ct));

    /// <summary>Single report by slug. Slugs are unique and stable per report.</summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(PublicReportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
        => Ok(await _reports.GetBySlugAsync(slug, ct));
}
