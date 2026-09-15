using MgtOcr.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Username/password sign-in — the convenience alternative to Microsoft SSO. AllowAnonymous because
// this is how a caller obtains a token in the first place; everything else stays behind the global
// authentication requirement.
[ApiController]
[AllowAnonymous]
[Route("api/auth")]
public class AuthController(PasswordService passwords, LocalTokenService tokens,
    ILogger<AuthController> log) : ControllerBase
{
    public sealed record LoginRequest(string Username, string Password);

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { detail = "Please enter your username and password" });

        var result = await passwords.VerifyAsync(req.Username.Trim(), req.Password, ct);
        if (!result.Ok || result.Row is null)
        {
            // Same message whether the user does not exist or the password is wrong — never say which.
            log.LogInformation("Password login failed for {User}", req.Username.Trim());
            return Unauthorized(new { detail = "Incorrect username or password" });
        }

        var row = result.Row;
        // No e-mail is fine for the password path — identity in the token is the UserID, and
        // /api/me resolves password tokens by UserID. (Only Microsoft SSO needs an e-mail.)
        var (token, exp) = tokens.Issue(row.UserId, row.Email, row.FullName, row.TokenVersion);
        // The SPA takes this token and calls /api/me for the full profile — same path as SSO.
        return Ok(new { token, expiresAt = exp });
    }
}
