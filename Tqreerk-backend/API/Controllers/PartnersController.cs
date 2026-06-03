using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Public (anonymous) endpoint that returns active partners grouped by category
/// for the About page.
[ApiController]
[Route("api/partners")]
[Produces("application/json")]
public class PartnersController : ControllerBase
{
    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;

    public PartnersController(TaqreerkDbContext db, IFileStorage files)
    {
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var categories = await _db.PartnerCategories
            .AsNoTracking()
            .Where(c => c.IsActive && c.Partners.Any(p => p.IsActive))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.NameAr)
            .Select(c => new
            {
                c.Id,
                c.NameAr,
                c.NameEn,
                Partners = c.Partners
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.SortOrder).ThenBy(p => p.NameAr)
                    .Select(p => new
                    {
                        p.Id,
                        p.NameAr,
                        p.NameEn,
                        LogoUrl = p.LogoUrl != null ? _files.GetPublicUrl(p.LogoUrl) : null,
                        p.WebsiteUrl,
                    })
                    .ToList(),
            })
            .ToListAsync(ct);

        return Ok(categories);
    }
}
