using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Contact;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Public contact / feedback form. Submissions are emailed to the
/// platform support inbox for triage — no WhatsApp redirect.
[ApiController]
[Route("api/contact")]
[Produces("application/json")]
[AllowAnonymous]
public class ContactController : ControllerBase
{
    private readonly IContactService _contact;

    public ContactController(IContactService contact)
    {
        _contact = contact;
    }

    /// <summary>Submit a contact form (suggestion, complaint, or inquiry).
    /// Delivers the message to the support team via email and sends an
    /// acknowledgement to the sender.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SubmitContactResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit(
        [FromBody] SubmitContactRequest req,
        CancellationToken ct)
        => Ok(await _contact.SubmitAsync(req, ct));
}
