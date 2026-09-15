using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MgtOcr.Api.Auth;

// Settings for the token OCR issues itself after a username/password login. The signing key is a
// real secret (whoever has it can forge a token) — it must come from user-secrets / an env var, and
// AddMgtOcrAuth refuses to run in production without it.
public sealed record LocalAuthOptions(string SigningKey, string Issuer, string Audience, int LifetimeMinutes);

// Issues the OCR-signed JWT for the password path. SSO tokens come from Microsoft and never pass
// through here; both kinds converge later on the same Ms_User lookup (by the e-mail claim).
public sealed class LocalTokenService(LocalAuthOptions options)
{
    public (string Token, DateTime ExpiresUtc) Issue(string userId, string email, string fullName, string tokenVersion)
    {
        var now = DateTime.UtcNow;
        var exp = now.AddMinutes(options.LifetimeMinutes);
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = exp,
            SigningCredentials = creds,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId,
                ["email"] = email,       // the CurrentUser resolve keys off this, same as SSO tokens
                ["name"] = fullName,
                ["tv"] = tokenVersion,   // revocation: bump Ms_User.TokenVersion to invalidate old tokens
                ["amr"] = "pwd",         // marks this as a password login (vs Microsoft SSO)
            },
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), exp);
    }
}
