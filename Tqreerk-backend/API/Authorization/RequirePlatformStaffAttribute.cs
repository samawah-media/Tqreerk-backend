using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.API.Authorization;

/// Attribute that gates an action / controller on `users.is_platform_staff`.
/// Pairs with `[Authorize]` (which still validates the JWT). Anyone without
/// staff status — even with a valid user JWT — gets 403.
///
/// Usage:
///   [Authorize]
///   [RequirePlatformStaff]
///   public class AdminReviewsController : ControllerBase { ... }
public class RequirePlatformStaffAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var sub = context.HttpContext.User.FindFirstValue("sub")
            ?? context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(sub, out var userId))
        {
            // No JWT → let [Authorize] handle the 401. This branch is
            // defensive in case the filter ordering changes.
            context.Result = new UnauthorizedResult();
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<TaqreerkDbContext>();
        var isStaff = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.IsPlatformStaff)
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (!isStaff)
        {
            context.Result = new ObjectResult(new
            {
                title = "This account is not a platform staff member.",
                status = StatusCodes.Status403Forbidden,
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
