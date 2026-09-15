namespace MgtOcr.Core.Auth;

// One company the user belongs to — MGT_Datawarehouse.dbo.Ms_UserCompany joined to Ms_Company.
// CompanyCode is the SAP company code: in phase 2 it replaces AppConfig.SapCompanyCode, which is
// currently a single hard-coded value and therefore assumes a single-company installation.
public sealed record UserCompany(string CompanyId, string CompanyCode, string CompanyName, bool IsPrimary);

// The signed-in user for one request.
//
// Authentication (who is this, really?) comes from the Entra ID access token.
// Authorization (which company, which role, still employed?) comes from Ms_User in the
// MGT_Datawarehouse database, which other internal systems share. Nothing here is taken from the
// request body: before this existed, every write endpoint accepted a "user" field from the client
// and defaulted it to "system", so any caller could stamp any name on an audit row.
public sealed class CurrentUser
{
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }      // the address that actually matched Ms_User
    public required string FullName { get; init; }
    public required string Role { get; init; }       // Admin | Leader | Manager | user
    public string Department { get; init; } = "";
    public string Position { get; init; } = "";
    public string SalesOrganization { get; init; } = "";
    public string Division { get; init; } = "";
    public string Tier { get; init; } = "";
    public string TokenVersion { get; init; } = "";
    public required IReadOnlyList<UserCompany> Companies { get; init; }

    // Ms_UserCompany.IsPrimary marks the home company for people who belong to more than one.
    public UserCompany? PrimaryCompany =>
        Companies.FirstOrDefault(c => c.IsPrimary) ?? Companies.FirstOrDefault();

    // What gets written to CreatedBy / PerformedBy / PostedBy. Username is preferred over the
    // e-mail address because those columns are nvarchar(100) and a login name survives a mailbox
    // rename, which is exactly the kind of change that would otherwise orphan old audit rows.
    public string AuditName => string.IsNullOrWhiteSpace(Username) ? Email : Username;

    public bool BelongsTo(string companyId) => Companies.Any(c => c.CompanyId == companyId);
}

// Resolved once per request and cached for the lifetime of that request.
public interface ICurrentUserAccessor
{
    // Null when the caller is unauthenticated, or authenticated but not present/active in Ms_User.
    Task<CurrentUser?> GetAsync(CancellationToken ct = default);

    // Same, but throws 403 instead of returning null — for the write paths that must stamp a name.
    Task<CurrentUser> RequireAsync(CancellationToken ct = default);
}
