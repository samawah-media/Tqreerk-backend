using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Annotations;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Caller-scoped CRUD for highlights and the personal notepad on a
/// SAVED report. Routes live under /api/me/reports/{id} so they
/// compose naturally with the existing /api/me surface (saved-reports,
/// activity, points). Every method 404s when the caller hasn't saved
/// the target report.
[ApiController]
[Route("api/me/reports/{reportId:guid}")]
[Produces("application/json")]
[Authorize]
public class AnnotationsController : ControllerBase
{
    private readonly IAnnotationsService _annotations;

    public AnnotationsController(IAnnotationsService annotations)
    {
        _annotations = annotations;
    }

    /// <summary>One-shot editor bootstrap: report metadata + signed
    /// URLs + AI content + my annotations + my notepad + my plan tier.
    /// Saves the editor page from making 4 separate calls on mount.</summary>
    [HttpGet("editor")]
    [ProducesResponseType(typeof(EditorBootstrapDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Editor(Guid reportId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _annotations.GetEditorBootstrapAsync(userId, reportId, ct));
    }

    /// <summary>List all my highlights on this saved report.</summary>
    [HttpGet("annotations")]
    [ProducesResponseType(typeof(IReadOnlyList<AnnotationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid reportId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _annotations.ListAsync(userId, reportId, ct));
    }

    /// <summary>Create a highlight (text selection + color + optional note).</summary>
    [HttpPost("annotations")]
    [ProducesResponseType(typeof(AnnotationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(
        Guid reportId, [FromBody] CreateAnnotationRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var created = await _annotations.CreateAsync(userId, reportId, req, ct);
        return Created($"/api/me/reports/{reportId}/annotations/{created.Id}", created);
    }

    /// <summary>Patch a highlight — color and/or note. Other fields are immutable.</summary>
    [HttpPatch("annotations/{annotationId:guid}")]
    [ProducesResponseType(typeof(AnnotationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid reportId, Guid annotationId,
        [FromBody] UpdateAnnotationRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _annotations.UpdateAsync(userId, reportId, annotationId, req, ct));
    }

    /// <summary>Remove a highlight.</summary>
    [HttpDelete("annotations/{annotationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid reportId, Guid annotationId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await _annotations.DeleteAsync(userId, reportId, annotationId, ct);
        return NoContent();
    }

    /// <summary>Get my notepad for this report. Returns an empty-body
    /// stub when no notepad exists yet so the editor can render an
    /// empty textarea without an extra round-trip.</summary>
    [HttpGet("note")]
    [ProducesResponseType(typeof(PersonalNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetNote(Guid reportId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _annotations.GetNoteAsync(userId, reportId, ct));
    }

    /// <summary>Upsert my notepad. Empty body is allowed and stored
    /// as-is so UpdatedAt still tracks the most recent edit.</summary>
    [HttpPut("note")]
    [ProducesResponseType(typeof(PersonalNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpsertNote(
        Guid reportId, [FromBody] UpdatePersonalNoteRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _annotations.UpsertNoteAsync(userId, reportId, req, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
