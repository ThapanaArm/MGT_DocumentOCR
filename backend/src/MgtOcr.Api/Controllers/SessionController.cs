using MgtOcr.Api.Auth;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Called by the SPA right after a fresh sign-in (password, Microsoft redirect, or silent SSO on a
// new device): makes THIS browser the user's only active device. See SingleSession.cs.
[ApiController]
public class SessionController(ICurrentUserAccessor current, SingleSessionService sessions) : ControllerBase
{
    [HttpPost("api/session/claim")]
    public async Task<IActionResult> Claim(CancellationToken ct)
    {
        var u = await current.RequireAsync(ct);
        var sid = SingleSessionService.ReadHeader(HttpContext)
                  ?? throw new HttpApiException(400, "Missing X-Session-Id header");
        await sessions.ClaimAsync(u.UserId, sid, HttpContext);
        return Ok(new { ok = true });
    }
}
