using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class TwoFactorService : ITwoFactorService
{
    private const string Issuer = "Taqreerk Admin";
    private const int BackupCodeCount = 10;
    /// Width of the TOTP-validation window. We allow ±1 step (≈30s
    /// either side) to forgive small clock drift between the server and
    /// the user's phone. Wider would weaken the signal.
    private static readonly VerificationWindow VerifyWindow =
        new(previous: 1, future: 1);

    private readonly TaqreerkDbContext _db;
    private readonly IEncryptionService _crypto;
    private readonly ILogger<TwoFactorService> _logger;

    public TwoFactorService(
        TaqreerkDbContext db,
        IEncryptionService crypto,
        ILogger<TwoFactorService> logger)
    {
        _db = db;
        _crypto = crypto;
        _logger = logger;
    }

    public async Task<TwoFactorSetupResponse> StartSetupAsync(
        Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var existing = await _db.Admin2faSecrets
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (existing is not null && existing.IsEnabled)
        {
            // Re-running setup on an active 2FA secret is a foot-gun. The
            // SuperAdmin's reset endpoint is the only legitimate path
            // back to setup once the user has activated.
            throw new InvalidOperationException(
                "2FA is already active for this account. Reset it first to reconfigure.");
        }

        // Fresh 160-bit (20-byte) secret, base32-encoded. RFC 4226
        // recommends ≥128 bits; we use 160 to match Google Authenticator
        // defaults.
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secretBytes);

        var backupCodes = GenerateBackupCodes(BackupCodeCount);
        var encryptedSecret = _crypto.Protect(base32Secret);
        var encryptedCodes = _crypto.Protect(JsonSerializer.Serialize(backupCodes));

        if (existing is null)
        {
            existing = new Admin2faSecret
            {
                UserId = userId,
                EncryptedSecret = encryptedSecret,
                EncryptedBackupCodes = encryptedCodes,
                IsEnabled = false,
            };
            _db.Admin2faSecrets.Add(existing);
        }
        else
        {
            // Pending setup not yet activated — overwrite the unused
            // secret so /activate's window matches what we just minted.
            existing.EncryptedSecret = encryptedSecret;
            existing.EncryptedBackupCodes = encryptedCodes;
            existing.IsEnabled = false;
            existing.LastUsedAt = null;
        }

        await _db.SaveChangesAsync(ct);

        var otpAuthUri = BuildOtpAuthUri(user.Email, base32Secret);

        _logger.LogInformation("2FA setup started for user {UserId}", userId);

        return new TwoFactorSetupResponse(otpAuthUri, base32Secret, backupCodes);
    }

    public async Task ActivateAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var record = await _db.Admin2faSecrets
            .FirstOrDefaultAsync(s => s.UserId == userId, ct)
            ?? throw new InvalidOperationException(
                "No pending 2FA setup. Run /setup before /activate.");

        if (record.IsEnabled)
            throw new InvalidOperationException("2FA is already activated for this account.");

        var secret = _crypto.Unprotect(record.EncryptedSecret);
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        if (!totp.VerifyTotp(code, out _, VerifyWindow))
        {
            // We don't reveal whether the code was wrong vs expired —
            // both look the same to brute-forcers.
            throw new ArgumentException("Invalid verification code.");
        }

        record.IsEnabled = true;
        record.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("2FA activated for user {UserId}", userId);
    }

    public async Task<bool> VerifyAsync(Guid userId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        var record = await _db.Admin2faSecrets
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (record is null || !record.IsEnabled) return false;

        var trimmed = code.Trim();

        // Try TOTP first. If the user typed a backup code (different
        // length / format) the TOTP check fails gracefully and we fall
        // through to the backup-code path.
        var secret = _crypto.Unprotect(record.EncryptedSecret);
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        if (totp.VerifyTotp(trimmed, out _, VerifyWindow))
        {
            record.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        // Backup-code path. Compare case-insensitively with stored codes;
        // remove the matched one so it can't be reused.
        var stored = JsonSerializer.Deserialize<List<string>>(
            _crypto.Unprotect(record.EncryptedBackupCodes)) ?? new();
        var matchIndex = stored.FindIndex(c =>
            string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
        if (matchIndex < 0) return false;

        stored.RemoveAt(matchIndex);
        record.EncryptedBackupCodes = _crypto.Protect(JsonSerializer.Serialize(stored));
        record.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Backup code consumed for user {UserId}, {Remaining} left",
            userId, stored.Count);

        return true;
    }

    public async Task<TwoFactorBackupCodesResponse> RegenerateBackupCodesAsync(
        Guid userId, CancellationToken ct = default)
    {
        var record = await _db.Admin2faSecrets
            .FirstOrDefaultAsync(s => s.UserId == userId, ct)
            ?? throw new InvalidOperationException("2FA is not configured.");
        if (!record.IsEnabled)
            throw new InvalidOperationException("Activate 2FA before regenerating backup codes.");

        var fresh = GenerateBackupCodes(BackupCodeCount);
        record.EncryptedBackupCodes = _crypto.Protect(JsonSerializer.Serialize(fresh));
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Backup codes regenerated for user {UserId}", userId);

        return new TwoFactorBackupCodesResponse(fresh);
    }

    public async Task ResetAsync(Guid userId, CancellationToken ct = default)
    {
        var record = await _db.Admin2faSecrets
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (record is null) return;

        _db.Admin2faSecrets.Remove(record);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("2FA reset for user {UserId}", userId);
    }

    public async Task<TwoFactorStatusDto> GetStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var record = await _db.Admin2faSecrets
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => new { s.IsEnabled, s.LastUsedAt })
            .FirstOrDefaultAsync(ct);

        if (record is null)
            return new TwoFactorStatusDto(IsConfigured: false, IsEnabled: false, LastUsedAt: null);

        return new TwoFactorStatusDto(
            IsConfigured: true,
            IsEnabled: record.IsEnabled,
            LastUsedAt: record.LastUsedAt);
    }

    public async Task<bool> RequiresVerificationAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.Admin2faSecrets
            .AsNoTracking()
            .AnyAsync(s => s.UserId == userId && s.IsEnabled, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string BuildOtpAuthUri(string accountName, string base32Secret)
    {
        // Standard otpauth scheme so any compatible authenticator app
        // (Google Authenticator, Authy, 1Password, etc.) can ingest the
        // QR code we render from this URI.
        var account = Uri.EscapeDataString($"{Issuer}:{accountName}");
        var secret = Uri.EscapeDataString(base32Secret);
        var issuer = Uri.EscapeDataString(Issuer);
        return $"otpauth://totp/{account}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    /// 8-character backup codes. RFC doesn't dictate a format; we use
    /// uppercase alphanumerics excluding ambiguous chars (0/O, 1/I/L) so
    /// the user has a fighting chance of reading them off paper.
    private static IReadOnlyList<string> GenerateBackupCodes(int count)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var codes = new List<string>(count);
        Span<byte> buffer = stackalloc byte[8];
        for (var i = 0; i < count; i++)
        {
            RandomNumberGenerator.Fill(buffer);
            var chars = new char[8];
            for (var j = 0; j < 8; j++)
            {
                chars[j] = alphabet[buffer[j] % alphabet.Length];
            }
            codes.Add(new string(chars));
        }
        return codes;
    }
}
