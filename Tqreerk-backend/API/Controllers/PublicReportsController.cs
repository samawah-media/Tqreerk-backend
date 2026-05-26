using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
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
    [OutputCache(PolicyName = "PublicList")]
    [ProducesResponseType(typeof(PagedResult<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery(Name = "q")] string? q = null,
        [FromQuery(Name = "sectors")] Guid[]? sectors = null,
        [FromQuery(Name = "countries")] Guid[]? countries = null,
        [FromQuery(Name = "organizations")] Guid[]? organizations = null,
        [FromQuery(Name = "year_from")] int? yearFrom = null,
        [FromQuery(Name = "year_to")] int? yearTo = null,
        [FromQuery(Name = "page_count_min")] int? pageCountMin = null,
        [FromQuery(Name = "page_count_max")] int? pageCountMax = null,
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
            Organizations: organizations,
            YearFrom: yearFrom,
            YearTo: yearTo,
            PageCountMin: pageCountMin,
            PageCountMax: pageCountMax,
            Language: language,
            Sort: sort,
            Page: page,
            PageSize: pageSize
        );
        return Ok(await _reports.ListAsync(req, ct));
    }

    /// <summary>Per-facet counts for the library sidebar. Same query
    /// surface as <see cref="List"/> — counts on each dimension respect
    /// every filter EXCEPT the dimension itself.</summary>
    [HttpGet("facets")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [OutputCache(PolicyName = "PublicFacets")]
    [ProducesResponseType(typeof(PublicReportFacetsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Facets(
        [FromQuery(Name = "q")] string? q = null,
        [FromQuery(Name = "sectors")] Guid[]? sectors = null,
        [FromQuery(Name = "countries")] Guid[]? countries = null,
        [FromQuery(Name = "organizations")] Guid[]? organizations = null,
        [FromQuery(Name = "year_from")] int? yearFrom = null,
        [FromQuery(Name = "year_to")] int? yearTo = null,
        [FromQuery(Name = "page_count_min")] int? pageCountMin = null,
        [FromQuery(Name = "page_count_max")] int? pageCountMax = null,
        [FromQuery(Name = "language")] string? language = null,
        CancellationToken ct = default)
    {
        var req = new PublicReportListRequest(
            Q: q,
            Sectors: sectors,
            Countries: countries,
            Organizations: organizations,
            YearFrom: yearFrom,
            YearTo: yearTo,
            PageCountMin: pageCountMin,
            PageCountMax: pageCountMax,
            Language: language
        );
        return Ok(await _reports.GetFacetsAsync(req, ct));
    }

    /// <summary>Curated featured reports for the homepage. Pass `section`
    /// to scope to one column (HomepageHero / HomepageCarousel /
    /// SectorTop / CountryTop). Omit it to get hero + carousel
    /// fall-through.</summary>
    [HttpGet("featured")]
    // Two layers of cache: ResponseCache writes Cache-Control headers so any
    // CDN / browser caches the response for 5 min, and OutputCache stores
    // the rendered bytes in-process so the first hit of each (take, section)
    // pair after instance start is the only one that touches the DB.
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [OutputCache(PolicyName = "PublicFeatured")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Featured(
        [FromQuery] int take = 5,
        [FromQuery] string? section = null,
        CancellationToken ct = default)
        => Ok(await _reports.GetFeaturedAsync(take, section, ct));

    /// <summary>Most-viewed reports in the last 7 days.</summary>
    [HttpGet("trending")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [OutputCache(PolicyName = "PublicTrending")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Trending([FromQuery] int take = 5, CancellationToken ct = default)
        => Ok(await _reports.GetTrendingAsync(take, ct));

    /// <summary>Most recent published reports.</summary>
    [HttpGet("recent")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [OutputCache(PolicyName = "PublicRecent")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Recent([FromQuery] int take = 8, CancellationToken ct = default)
        => Ok(await _reports.GetRecentAsync(take, ct));

    /// <summary>Single report by slug. Slugs are unique and stable per report.</summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(PublicReportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
        => Ok(await _reports.GetBySlugAsync(slug, ct));

    /// <summary>"More like this" — sector match first, then most-viewed
    /// overall as a backfill. Never includes the source report itself.</summary>
    [HttpGet("{slug}/related")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [OutputCache(PolicyName = "PublicRelated")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Related(
        string slug, [FromQuery] int take = 3, CancellationToken ct = default)
        => Ok(await _reports.GetRelatedAsync(slug, take, ct));
}
