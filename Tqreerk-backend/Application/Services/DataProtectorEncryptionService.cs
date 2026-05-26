using Microsoft.AspNetCore.DataProtection;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.Application.Services;

/// IDataProtector-backed encryption. The purpose string namespaces the
/// derived key — anything encrypted with one purpose can't be decrypted
/// with another, which matters when we add more uses later (e.g. a
/// future "API key" feature shouldn't share keys with 2FA secrets).
public class DataProtectorEncryptionService : IEncryptionService
{
    private const string Purpose = "Taqreerk.Admin.2fa.v1";

    private readonly IDataProtector _protector;

    public DataProtectorEncryptionService(IDataProtectionProvider provider)
    {
        // The provider auto-rotates keys and persists them under
        // %LOCALAPPDATA%/ASP.NET in dev. For prod we'll point it at a
        // shared key ring (file system on shared volume, or a managed
        // key store) before scaling out — same lift any IDataProtection
        // user has to do.
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plainText)
    {
        if (plainText is null) throw new ArgumentNullException(nameof(plainText));
        return _protector.Protect(plainText);
    }

    public string Unprotect(string protectedText)
    {
        if (protectedText is null) throw new ArgumentNullException(nameof(protectedText));
        return _protector.Unprotect(protectedText);
    }
}
