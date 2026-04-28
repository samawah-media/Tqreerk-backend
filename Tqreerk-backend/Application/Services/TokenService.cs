using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Application.Services;

public class TokenService : ITokenService
{
    private readonly JwtSettings _jwt;

    public TokenService(IOptions<JwtSettings> jwt) => _jwt = jwt.Value;

    public string GenerateAccessToken(
        User user,
        IReadOnlyList<string> roleNames,
        IReadOnlyList<string> permissionKeys)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("user_type", user.UserType),
            new("lang", user.PreferredLanguage),
        };

        foreach (var role in roleNames)
            claims.Add(new Claim(ClaimTypes.Role, role));

        foreach (var perm in permissionKeys)
            claims.Add(new Claim("permissions", perm));

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwt.AccessTokenExpiryMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    private const string TwoFactorPurpose = "2fa-challenge.v1";

    public string GenerateTwoFactorChallengeToken(Guid userId, TimeSpan? lifetime = null)
    {
        // Body = "userId|expiresAtUnix|purpose"; signature = HMAC-SHA256
        // over the body using the same JWT secret. Encoded as
        // base64url(body) + "." + base64url(signature) so it's a single
        // URL-safe string the SPA can shove into a body field.
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5));
        var body = $"{userId:N}|{expiresAt.ToUnixTimeSeconds()}|{TwoFactorPurpose}";
        var signature = ComputeChallengeSignature(body);
        return $"{Base64UrlEncode(body)}.{Base64UrlEncode(signature)}";
    }

    public Guid? ValidateTwoFactorChallengeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;

        string body, sig;
        try
        {
            body = Base64UrlDecodeString(token[..dot]);
            sig = Base64UrlDecodeString(token[(dot + 1)..]);
        }
        catch
        {
            return null;
        }

        var expected = ComputeChallengeSignature(body);
        // Constant-time compare so an attacker can't infer prefix matches
        // from response timing.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(sig),
                Encoding.UTF8.GetBytes(expected)))
        {
            return null;
        }

        var parts = body.Split('|');
        if (parts.Length != 3 || parts[2] != TwoFactorPurpose) return null;
        if (!Guid.TryParse(parts[0], out var userId)) return null;
        if (!long.TryParse(parts[1], out var expiresUnix)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresUnix) return null;

        return userId;
    }

    private string ComputeChallengeSignature(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(hash);
    }

    private static string Base64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Base64UrlDecodeString(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    public ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = false, // expired tokens are valid here
            ValidateIssuerSigningKey = true,
            ValidIssuer = _jwt.Issuer,
            ValidAudience = _jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey)),
        };

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out var validated);
            if (validated is not JwtSecurityToken jwt || !jwt.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
                return null;
            return principal;
        }
        catch
        {
            return null;
        }
    }
}
