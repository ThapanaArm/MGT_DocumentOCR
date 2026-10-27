using MgtOcr.Core.Config;

namespace MgtOcr.Core.Auth;

/// <summary>The ONE company this build's Sales Order flow serves (27 Oct 2026).
///
/// MGT and GLC are deployed separately: branch develop-mgt -> MGT's site + database, branch
/// develop-glc -> GLC's site + database. Scope is the Sales Order (SO) module only: every SO is
/// stamped, mapped, listed and posted as this company (MGT -> Zoho CRM, GLC -> SAP), whoever is
/// signed in. AP / II / PODP and sign-in are NOT affected by this file.
///
/// This file is the only intended difference between the two branches' shared code: Name is
/// "MGT" on develop-mgt and "GLC" on develop-glc. When cherry-picking between the branches, keep
/// this file as it is on the target branch.</summary>
public static class DeploymentCompany
{
    /// <summary>Company profile name in AppConfig.Companies — "MGT" or "GLC".</summary>
    public const string Name = "GLC";

    public static bool IsMgt => Name == "MGT";

    /// <summary>This company's SalesOrg from appsettings (MGT:SalesOrganization / GLC:SalesOrganization).</summary>
    public static string SalesOrg(AppConfig config) =>
        config.Companies.FirstOrDefault(c => string.Equals(c.Name, Name, StringComparison.OrdinalIgnoreCase))
            ?.SalesOrganization ?? (IsMgt ? "1000" : "2000");
}
