using System.Text.RegularExpressions;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;

namespace MgtOcr.Data;

// Reads the shared user master that other internal systems also use:
//   MGT_Datawarehouse.dbo.Ms_User        — the person, their role and whether they are still active
//   MGT_Datawarehouse.dbo.Ms_UserCompany — which company (or companies) they belong to
//   MGT_Datawarehouse.dbo.Ms_Company     — CompanyID -> SAP CompanyCode
//
// Same SQL Server instance as MGT_Document_OCR, so it is reached with a three-part name over the
// existing connection rather than a second connection string. OCR only reads this table.
//
// Two ways to resolve the current user:
//   FindByEmailAsync  — for Microsoft SSO, whose token only carries an e-mail.
//   FindByUserIdAsync — for password login, where identity is the row that was just password-verified
//                       (UserID), so it works even for accounts that have no e-mail on file.
public partial class UserRepository(Db db, AppConfig config)
{
    // A database name is an identifier, not a value, so it cannot be passed as a SQL parameter.
    // It comes from configuration, but is validated against a plain-identifier pattern before being
    // interpolated, so a bad config value fails loudly here instead of becoming an injection point.
    private string UserDb => SafeIdentifier().IsMatch(config.UserDatabase)
        ? config.UserDatabase
        : throw new InvalidOperationException(
            $"Auth:UserDatabase is not a valid SQL identifier: '{config.UserDatabase}'");

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex SafeIdentifier();

    private const string UserColumns =
        "UserID, Username, FullName, UserRole, Email, TokenEmail, " +
        "Department, Position, SalesOrganization, Division, Tier, TokenVersion";

    // Matches on Email OR TokenEmail because the address people sign in to Microsoft 365 with is not
    // guaranteed to be the one kept in either single column. Trimmed, case-insensitive.
    public async Task<CurrentUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var key = (email ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) return null;

        var row = await db.QueryOneAsync($@"
            SELECT TOP 1 {UserColumns}
            FROM {UserDb}.dbo.Ms_User
            WHERE IsActive = 1
              AND (LOWER(LTRIM(RTRIM(CAST(ISNULL(Email, '') AS NVARCHAR(256))))) = @key
                OR LOWER(LTRIM(RTRIM(CAST(ISNULL(TokenEmail, '') AS NVARCHAR(256))))) = @key)", new { key }, ct);

        return await BuildAsync(row, ct);
    }

    // Identity for the password path — no e-mail required.
    public async Task<CurrentUser?> FindByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var key = (userId ?? "").Trim();
        if (key.Length == 0) return null;

        var row = await db.QueryOneAsync($@"
            SELECT TOP 1 {UserColumns}
            FROM {UserDb}.dbo.Ms_User
            WHERE IsActive = 1 AND CAST(UserID AS NVARCHAR(50)) = @key", new { key }, ct);

        return await BuildAsync(row, ct);
    }

    // Sales Employee list for the GLC Sales Order per-line picker: every active user who has a SAP
    // Person ID on file (Ms_User.PersonID). Code = PersonID (the SAP custom value stamped on the SO
    // item field YY1_SDSalesEmployeeI_SDI), Text = the person's name. IT maintains PersonID on the
    // same shared user master used for login, so this list stays in step with staff automatically.
    // Returns Code + Text rows, same shape the SysDataMapping-based lookups used before.
    public async Task<IEnumerable<dynamic>> GetSalesEmployeesAsync(CancellationToken ct = default) =>
        await db.QueryAsync($@"
            SELECT LTRIM(RTRIM(CAST(PersonID AS NVARCHAR(50)))) AS Code,
                   COALESCE(NULLIF(LTRIM(RTRIM(FullName)), ''), Username) AS [Text]
            FROM {UserDb}.dbo.Ms_User
            WHERE IsActive = 1
              AND PersonID IS NOT NULL
              AND LTRIM(RTRIM(CAST(PersonID AS NVARCHAR(50)))) <> ''
            ORDER BY [Text]", new { }, ct);

    private async Task<CurrentUser?> BuildAsync(dynamic? row, CancellationToken ct)
    {
        if (row is null) return null;
        var u = (IDictionary<string, object?>)row;
        var userId = Str(u, "UserID");

        var companyRows = await db.QueryAsync($@"
            SELECT c.CompanyID, c.CompanyCode, c.CompanyName, uc.IsPrimary
            FROM {UserDb}.dbo.Ms_UserCompany uc
            JOIN {UserDb}.dbo.Ms_Company c ON c.CompanyID = uc.CompanyID
            WHERE uc.UserID = @userId
            ORDER BY uc.IsPrimary DESC, c.CompanyID", new { userId }, ct);

        var companies = new List<UserCompany>();
        foreach (var cr in companyRows)
        {
            var c = (IDictionary<string, object?>)cr;
            companies.Add(new UserCompany(
                Str(c, "CompanyID"), Str(c, "CompanyCode"), Str(c, "CompanyName"), Bool(c, "IsPrimary")));
        }

        // Real address from the row (Email, then TokenEmail) — may be empty for password-only accounts.
        var email = Str(u, "Email");
        if (email.Length == 0) email = Str(u, "TokenEmail");

        return new CurrentUser
        {
            UserId = userId,
            Username = Str(u, "Username"),
            Email = email,
            FullName = Str(u, "FullName"),
            Role = Str(u, "UserRole"),
            Department = Str(u, "Department"),
            Position = Str(u, "Position"),
            SalesOrganization = Str(u, "SalesOrganization"),
            Division = Str(u, "Division"),
            Tier = Str(u, "Tier"),
            TokenVersion = Str(u, "TokenVersion"),
            Companies = companies,
        };
    }

    private static string Str(IDictionary<string, object?> r, string col) =>
        r.TryGetValue(col, out var v) && v is not null ? v.ToString()!.Trim() : "";

    private static int Int(IDictionary<string, object?> r, string col) =>
        r.TryGetValue(col, out var v) && v is not null ? Convert.ToInt32(v) : 0;

    private static bool Bool(IDictionary<string, object?> r, string col) =>
        r.TryGetValue(col, out var v) && v is not null && Convert.ToBoolean(v);
}
