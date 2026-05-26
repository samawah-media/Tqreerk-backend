using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Users;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/users")]
[Produces("application/json")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserService _users;

    public UsersController(IUserService users) => _users = users;

    /// <summary>Current user's profile.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _users.GetProfileAsync(userId, ct));
    }

    /// <summary>Update the current user's profile (name, job title, interest field, country, language, phone).</summary>
    [HttpPut("me")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _users.UpdateProfileAsync(userId, req, ct));
    }

    /// <summary>Current user's interests (sector / organization / country IDs).</summary>
    [HttpGet("me/interests")]
    [ProducesResponseType(typeof(UserInterestsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInterests(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _users.GetInterestsAsync(userId, ct));
    }

    /// <summary>Replace the current user's interests with the provided lists.</summary>
    [HttpPost("me/interests")]
    [ProducesResponseType(typeof(UserInterestsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetInterests([FromBody] SetInterestsRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _users.SetInterestsAsync(userId, req, ct));
    }

    /// <summary>Change the current user's password. Requires the current
    /// password as a possession check; refresh tokens stay valid because
    /// the user is already authenticated on this device.</summary>
    [HttpPost("me/change-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await _users.ChangePasswordAsync(userId, req, ct);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
