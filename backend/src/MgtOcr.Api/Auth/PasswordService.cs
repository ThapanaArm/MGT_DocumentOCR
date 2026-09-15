using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using MgtOcr.Data;

namespace MgtOcr.Api.Auth;

// Verifies a username/password, hashing lazily and self-healing.
//
// The flow is deliberately tolerant of how Ms_User is maintained today (rows INSERTed by hand with a
// plaintext Password, no app in between):
//   - a stored PasswordHash present  -> verify against it (fast path)
//   - no hash yet, or the hash no longer matches (someone edited the plaintext by hand)
//                                     -> fall back to the plaintext source of truth and, on a match,
//                                        write the hash so next time takes the fast path
// So a hand-added user just works on first login and gets hashed transparently; a hand-edited
// password self-corrects. Nothing changes about how users are added.
//
// PasswordHasher<T> is part of the ASP.NET Core shared framework (PBKDF2, salted, self-describing),
// so no extra package is needed and the algorithm can be upgraded later without a schema change.
public sealed class PasswordService(UserRepository users, ILogger<PasswordService> log)
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();

    public sealed record Result(bool Ok, UserRepository.LoginRow? Row);

    public async Task<Result> VerifyAsync(string usernameOrEmail, string password, CancellationToken ct = default)
    {
        var row = await users.FindLoginRowAsync(usernameOrEmail, ct);
        if (row is null || !row.IsActive) return new(false, null);

        if (!string.IsNullOrEmpty(row.PasswordHash))
        {
            var v = Hasher.VerifyHashedPassword(Subject, row.PasswordHash!, password);
            if (v != PasswordVerificationResult.Failed) return new(true, row);
        }

        if (!string.IsNullOrEmpty(row.Password) && FixedTimeEquals(row.Password!, password))
        {
            var hash = Hasher.HashPassword(Subject, password);
            try { await users.SetPasswordHashAsync(row.UserId, hash, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Could not backfill PasswordHash for user {Id}", row.UserId); }
            return new(true, row);
        }
        return new(false, null);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
