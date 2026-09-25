using System.Security.Claims;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
using MgtOcr.Data;
using Microsoft.Extensions.Caching.Memory;

namespace MgtOcr.Api.Auth;

// Turns a validated token into "this is the person, this is their company". Registered scoped, so
// the Ms_User lookup happens at most once per request.
//
// Two token kinds, two identities:
//   password token (issued by this app) — identity is the Ms_User UserID in `sub`; resolved by id,
//                                          so accounts with no e-mail on file still work.
//   Microsoft SSO token                 — identity is the e-mail claim; resolved by e-mail.
public class CurrentUserAccessor(
    IHttpContextAccessor http,
    UserRepository users,
    IMemoryCache cache,
    AppConfig config,
    IWebHostEnvironment env,
    ILogger<CurrentUserAccessor> log) : ICurrentUserAccessor
{
    private CurrentUser? _resolved;
    private bool _done;

    // Where a Microsoft token can carry the sign-in address, most reliable first.
    private static readonly string[] EmailClaims =
        ["preferred_username", "upn", "email", ClaimTypes.Upn, ClaimTypes.Email, "unique_name"];

    public async Task<CurrentUser?> GetAsync(CancellationToken ct = default)
    {
        if (_done) return _resolved;
        _done = true;

        var principal = http.HttpContext?.User;
        var authed = principal?.Identity?.IsAuthenticated == true;

        // Which issuer minted this token? Ours ("mgtocr") vs Microsoft. amr=pwd is NOT a reliable
        // discriminator — a Microsoft token can also carry amr=pwd — so key off the issuer.
        var iss = authed ? (principal!.FindFirst("iss")?.Value ?? principal.Claims.FirstOrDefault()?.Issuer ?? "") : "";
        var fromMicrosoft = iss.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
                         || iss.Contains("sts.windows.net", StringComparison.OrdinalIgnoreCase);
        var sub = principal?.FindFirst("sub")?.Value ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        string cacheKey;
        Func<CancellationToken, Task<CurrentUser?>> load;

        if (authed && !fromMicrosoft && !string.IsNullOrWhiteSpace(sub))
        {
            // Our own password token — identity is the Ms_User UserID.
            cacheKey = "mgtocr:user:id:" + sub;
            load = c => users.FindByUserIdAsync(sub!, c);
        }
        else
        {
            // Microsoft SSO (or the dev fallback): identity is the e-mail claim.
            var email = authed ? EmailFromClaims(principal!) : null;
            if (email is null && !config.AuthConfigured && env.IsDevelopment()
                && !string.IsNullOrWhiteSpace(config.DevFallbackEmail))
            {
                email = config.DevFallbackEmail.Trim();
                log.LogWarning("DEV FALLBACK: treating this request as {Email} — no Entra tenant configured.", email);
            }
            if (string.IsNullOrWhiteSpace(email))
            {
                if (authed)
                    log.LogWarning("Token carries no usable identity (no UserID sub, no e-mail). Claims: {Claims}",
                        string.Join(", ", principal!.Claims.Select(c => c.Type).Distinct()));
                return _resolved = null;
            }
            var emailKey = email;
            cacheKey = "mgtocr:user:email:" + email.ToLowerInvariant();
            load = c => users.FindByEmailAsync(emailKey, c);
        }

        if (cache.TryGetValue(cacheKey, out CurrentUser? cached)) return _resolved = cached;

        var found = await load(ct);
        if (found is null)
            log.LogWarning("Sign-in refused: no active Ms_User row for {Key}.", cacheKey);

        // NOTE: the password token still carries "tv" (Ms_User.TokenVersion at sign-in) but it is
        // deliberately NOT checked any more. The Dashboard system bumps TokenVersion on every login, so
        // checking it here kicked OCR users whenever they signed in to the Dashboard. OCR's own
        // one-device rule lives in Ms_UserSession (AppName='OCR') — see Auth/SingleSession.cs. To force a
        // user out of OCR: UPDATE their Ms_UserSession row (AppName='OCR') SET SessionId='REVOKED' — every
        // device is signed out within ~20 s until they sign in again — or set Ms_User.IsActive = 0.
        // (Deleting the row does NOT kick anyone: the next request simply re-registers.)

        cache.Set(cacheKey, found, TimeSpan.FromSeconds(found is null ? 15 : 60));
        return _resolved = found;
    }

    public async Task<CurrentUser> RequireAsync(CancellationToken ct = default) =>
        await GetAsync(ct) ?? throw new HttpApiException(403,
            "This account is not authorized to use the system — no matching user in Ms_User, or IsActive = 0");

    private static string? EmailFromClaims(ClaimsPrincipal p) =>
        EmailClaims.Select(c => p.FindFirst(c)?.Value)
                   .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
