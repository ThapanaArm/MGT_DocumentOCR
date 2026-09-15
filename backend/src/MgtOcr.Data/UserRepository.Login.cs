using System.Security.Cryptography;
using System.Text;

namespace MgtOcr.Data;

// Password-login reads over the shared user master. Kept separate from the SSO lookup so the
// distinction stays obvious: SSO never needs Password/PasswordHash, and password login never needs
// the company join (that happens later via the normal CurrentUser resolve by e-mail).
public partial class UserRepository
{
    // Ms_User.Password is the hand-maintained plaintext (new users are still INSERTed by hand), so
    // it is the source of truth; PasswordHash is the cache OCR fills and refreshes on login.
    public sealed record LoginRow(
        string UserId, string Username, string Email, string FullName, string Role,
        bool IsActive, string TokenVersion, string? Password, string? PasswordHash);

    public async Task<LoginRow?> FindLoginRowAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        var key = (usernameOrEmail ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) return null;

        var row = await db.QueryOneAsync($@"
            SELECT TOP 1 UserID, Username, FullName, UserRole, Email, TokenEmail,
                         IsActive, TokenVersion, Password, PasswordHash
            FROM {UserDb}.dbo.Ms_User
            WHERE LOWER(LTRIM(RTRIM(CAST(ISNULL(Username,'') AS NVARCHAR(256))))) = @key
               OR LOWER(LTRIM(RTRIM(CAST(ISNULL(Email,'') AS NVARCHAR(256))))) = @key
               OR LOWER(LTRIM(RTRIM(CAST(ISNULL(TokenEmail,'') AS NVARCHAR(256))))) = @key", new { key }, ct);
        if (row is null) return null;

        var u = (IDictionary<string, object?>)row;
        string? Raw(string c) => u.TryGetValue(c, out var v) && v is not null ? v.ToString() : null;
        var email = Str(u, "Email");
        if (email.Length == 0) email = Str(u, "TokenEmail");

        return new LoginRow(
            Str(u, "UserID"), Str(u, "Username"), email, Str(u, "FullName"), Str(u, "UserRole"),
            Bool(u, "IsActive"), Str(u, "TokenVersion"), Raw("Password"), Raw("PasswordHash"));
    }

    // Only ever writes PasswordHash — never touches the plaintext Password other systems still read.
    public async Task SetPasswordHashAsync(string userId, string hash, CancellationToken ct = default) =>
        await db.ExecuteAsync(
            $"UPDATE {UserDb}.dbo.Ms_User SET PasswordHash = @hash WHERE UserID = @userId",
            new { hash, userId }, ct);
}
