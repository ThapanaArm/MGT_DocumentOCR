using MgtOcr.Core.Auth;
using MgtOcr.Data;
using Microsoft.Extensions.Caching.Memory;

namespace MgtOcr.Api.Auth;

// One device per user; any number of tabs on that device.
//
// The browser keeps a random session id in localStorage (shared by all its tabs) and sends it on
// every API call as X-Session-Id. MGT_Datawarehouse.dbo.Ms_UserSession (AppName='OCR') holds the one
// id allowed per user. Signing in on
// a new device calls POST /api/session/claim, which overwrites it ("newest device wins"); the old
// device's next call gets 401 { code: "SESSION_REPLACED" } and the SPA signs it out.
//
// Works the same for password and Microsoft SSO tokens — the check is on our own header, not the
// token — and needs no change to Ms_User (shared with other systems; TokenVersion is NOT bumped or
// checked, so signing in to the Dashboard never kicks OCR and vice versa: 1 device per user PER SYSTEM).
public class SingleSessionService(UserSessionRepository repo, IMemoryCache cache)
{
    public const string Header = "X-Session-Id";
    private static string Key(string userId) => "mgtocr:session:" + userId;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    public async Task ClaimAsync(string userId, string sessionId, HttpContext ctx)
    {
        await repo.ClaimAsync(userId, sessionId, ctx.Request.Headers.UserAgent.ToString(), ctx.Connection.RemoteIpAddress?.ToString());
        cache.Set(Key(userId), sessionId, Ttl);
    }

    public async Task<bool> IsActiveAsync(string userId, string sessionId, HttpContext ctx)
    {
        if (!cache.TryGetValue(Key(userId), out string? active) || active is null)
        {
            active = await repo.GetOrRegisterAsync(userId, sessionId,
                ctx.Request.Headers.UserAgent.ToString(), ctx.Connection.RemoteIpAddress?.ToString());
            cache.Set(Key(userId), active, Ttl);
        }
        return string.Equals(active, sessionId, StringComparison.Ordinal);
    }

    public static string? ReadHeader(HttpContext ctx)
    {
        var v = ctx.Request.Headers[Header].ToString().Trim();
        return v.Length is > 0 and <= 64 ? v : null;
    }
}

// Runs after UseAuthorization, before the controller. Only authenticated /api calls are checked;
// sign-in endpoints and the claim endpoint itself are exempt.
public class SingleSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, ICurrentUserAccessor current, SingleSessionService sessions)
    {
        var path = ctx.Request.Path;
        var exempt = !path.StartsWithSegments("/api")
                  || path.StartsWithSegments("/api/auth")
                  || path.StartsWithSegments("/api/session/claim")
                  || HttpMethods.IsOptions(ctx.Request.Method)
                  || ctx.User.Identity?.IsAuthenticated != true;
        if (exempt) { await next(ctx); return; }

        var user = await current.GetAsync(ctx.RequestAborted);
        if (user is null) { await next(ctx); return; }   // unknown user — the controller answers 403

        var sid = SingleSessionService.ReadHeader(ctx);
        if (sid is null)
        {
            await Reject(ctx, "SESSION_MISSING", "Session expired — please sign in again");
            return;
        }
        if (!await sessions.IsActiveAsync(user.UserId, sid, ctx))
        {
            await Reject(ctx, "SESSION_REPLACED", "This account was signed in on another device. You have been signed out here.");
            return;
        }
        await next(ctx);
    }

    private static Task Reject(HttpContext ctx, string code, string detail)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return ctx.Response.WriteAsJsonAsync(new { detail, code });
    }
}
