namespace Taqreerk.Application.Interfaces;

/// Symmetric, purpose-scoped string encryption. Backed by ASP.NET's
/// IDataProtectionProvider so we don't ship our own crypto. Used today
/// for the admin 2FA secret + backup codes; can be re-used wherever a
/// small, server-side secret needs to live in the DB.
public interface IEncryptionService
{
    /// Encrypt a UTF-8 string. Output is a base64 envelope safe to store
    /// in a varchar column.
    string Protect(string plainText);

    /// Decrypt a value previously produced by Protect. Throws if the
    /// payload was tampered with or encrypted with a different purpose.
    string Unprotect(string protectedText);
}
