using MgtOcr.Core.Config;

namespace MgtOcr.Core.Auth;

/// <summary>Which company's data one signed-in person may see.
///
/// MGT and GLC share a single installation, and neither may see the other's documents or master
/// data. IT keys the two on View_UserPermission.CompanyID — 1 = MGT, 2 = GLC — which this turns
/// into the sales organization the data itself is stamped with (1000 / 2000).
///
/// Returning an empty string means "no company filter": that is Admin, who works across both, and
/// also a user record that names no company at all — those are left unfiltered rather than locked
/// out, because an incomplete user row should not silently empty someone's screen. Every caller
/// treats an empty scope as "show everything", so the two cases behave alike on purpose.</summary>
public static class CompanyScope
{
    public static bool SeesEveryCompany(CurrentUser user) =>
        string.Equals(user.Role?.Trim(), "Admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which company a NEW record belongs to — always the person's own company, Admin
    /// included.
    ///
    /// Seeing every company and filing FOR every company are different things: an Admin who imports
    /// an MGT invoice is still filing it for MGT, and leaving it company-less would put it on GLC's
    /// list too. So the stamp on a new document or master row comes from here, while what a person
    /// is allowed to SEE comes from For() below.</summary>
    public static string Own(CurrentUser user, AppConfig config) => Resolve(user, config);

    public static string For(CurrentUser user, AppConfig config)
    {
        if (SeesEveryCompany(user)) return "";
        return Resolve(user, config);
    }

    /// <summary>Which company an import being uploaded RIGHT NOW should be stamped with.
    ///
    /// Own() is the rule for everyone, with one exception: an Admin works both companies' post
    /// rooms, so the screen lets them pick, and most of the invoices an MGT-registered Admin keys
    /// in are in fact GLC's. The pick is honoured only for Admin — anyone else is stamped with
    /// their own company no matter what the form says, so the client cannot file into a company it
    /// is not allowed to see. An unrecognised value falls back to Own() as well.</summary>
    public static string ImportFor(CurrentUser user, AppConfig config, string? requested)
    {
        var want = (requested ?? "").Trim();
        if (SeesEveryCompany(user) && want is "1000" or "2000") return want;
        return Own(user, config);
    }

    private static string Resolve(CurrentUser user, AppConfig config)
    {
        // CompanyID decides first — it is the column the user master actually splits the group on.
        var byId = (user.PrimaryCompany?.CompanyId ?? "").Trim() switch
        {
            "1" => config.Companies.FirstOrDefault(c => string.Equals(c.Name, "MGT", StringComparison.OrdinalIgnoreCase)),
            "2" => config.Companies.FirstOrDefault(c => string.Equals(c.Name, "GLC", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };

        // Older rows may carry only a company code or a sales org; both still resolve.
        var profile = byId ?? config.CompanyForUser(user.PrimaryCompany?.CompanyCode, user.SalesOrganization);
        return (profile?.SalesOrganization ?? user.SalesOrganization ?? "").Trim();
    }
}
