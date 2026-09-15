using MgtOcr.Core.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Who am I? The SPA calls this once after sign-in: the Microsoft token says which account signed
// in, this endpoint says what that account is allowed to be inside this system.
[ApiController]
public class MeController(ICurrentUserAccessor current) : ControllerBase
{
    // Full path in the verb attribute, matching DocumentsController etc. — the routing style this
    // project already uses. (A class [Route] + a bare [HttpGet] did not register here.)
    [HttpGet("api/me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var u = await current.RequireAsync(ct);
        return Ok(new
        {
            userId = u.UserId,
            username = u.Username,
            email = u.Email,
            fullName = u.FullName,
            role = u.Role,
            department = u.Department,
            position = u.Position,
            salesOrganization = u.SalesOrganization,
            division = u.Division,
            companies = u.Companies.Select(c => new
            {
                companyId = c.CompanyId,
                companyCode = c.CompanyCode,
                companyName = c.CompanyName,
                isPrimary = c.IsPrimary,
            }),
            primaryCompany = u.PrimaryCompany is { } p
                ? new { companyId = p.CompanyId, companyCode = p.CompanyCode, companyName = p.CompanyName }
                : null,
        });
    }
}
