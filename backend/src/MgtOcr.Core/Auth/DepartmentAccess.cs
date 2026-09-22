using System;
using System.Collections.Generic;
using System.Linq;

namespace MgtOcr.Core.Auth;

// Which document modules a person may see, decided by their Ms_User.Department.
//
// Visibility is per-department, not per-owner: everyone in a department sees every document in
// that department's tab, and other departments do not see the tab at all. Enforced on the server
// (see DepartmentAccessFilter); hiding the nav on the client is only cosmetic.
//
//   Finance ("Account")  -> Invoice / Liability Recording = AP + II
//   Purchase             -> PO Down Payment               = PODP
//   CSR                  -> Sales Order                   = SO
//
// UserRole = Admin bypasses the map and sees every tab. A non-admin whose Department is blank or
// not one of the three sees no document tabs at all.
public static class DepartmentAccess
{
    public static readonly string[] AllModules = ["AP", "II", "PODP", "SO"];

    private static readonly Dictionary<string, string[]> ByDepartment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Finance"] = ["AP", "II"],
        ["Account"] = ["AP", "II"],   // Ms_User stores the finance department as "Account"
        ["Purchase"] = ["PODP"],
        ["CSR"] = ["SO"],
    };

    public static bool IsAdmin(CurrentUser u) =>
        string.Equals(u.Role, "Admin", StringComparison.OrdinalIgnoreCase);

    // The document modules this user may access. Admin => all; a mapped department => its set;
    // anyone else => none.
    public static IReadOnlyCollection<string> AllowedModules(CurrentUser u)
    {
        if (IsAdmin(u)) return AllModules;
        return ByDepartment.TryGetValue((u.Department ?? "").Trim(), out var mods)
            ? mods
            : Array.Empty<string>();
    }

    public static bool CanAccess(CurrentUser u, string module) =>
        AllowedModules(u).Contains((module ?? "").Trim().ToUpperInvariant());
}
