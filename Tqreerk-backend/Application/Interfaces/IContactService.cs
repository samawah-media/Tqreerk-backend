using Taqreerk.Application.DTOs.Contact;

namespace Taqreerk.Application.Interfaces;

public interface IContactService
{
    /// Accepts a public contact/feedback form submission, emails the
    /// support team, and sends an acknowledgement to the sender.
    Task<SubmitContactResponse> SubmitAsync(
        SubmitContactRequest req, CancellationToken ct = default);
}
