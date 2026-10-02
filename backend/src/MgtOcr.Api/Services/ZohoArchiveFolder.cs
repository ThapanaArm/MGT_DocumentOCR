using System.Text.Json.Nodes;

namespace MgtOcr.Api.Services;

// SharePoint folder for an MGT (Zoho) Sales Order: "{Sales name}/{CustomerCode}_{CustomerName}".
// Sales = the customer's Account Owner (labelled "Sales Employee" in Megachem's Zoho, manual
// AO-CRM-UM-2026-003 p.84), falling back to the Deal Owner. Not the Sales Order Owner: that is
// whoever created the order (CS, or the API account), see the manual p.270.
public static class ZohoArchiveFolder
{
    // Customer = the "{Code}_{Name}" folder on its own, made safe (GLC uses only this part).
    public sealed record Result(string? Sales, string Code, string Name, string SubPath, string Customer);

    // A lookup ({id, name}) -> its name; a plain string -> itself.
    public static string? Text(JsonNode? n) => n switch
    {
        JsonObject o => o["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => null,
    };

    public static string? LookupId(JsonNode? n) =>
        n is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static Result Build(string? sales, string? code, string? name)
    {
        var c = (code ?? "").Trim();
        var n = (name ?? "").Trim();
        var customer = c != "" && n != "" ? $"{c}_{n}" : c + n;
        return new(string.IsNullOrWhiteSpace(sales) ? null : sales.Trim(), c, n, GraphArchiveClient.JoinFolder(sales, customer),
            GraphArchiveClient.JoinFolder(customer));
    }
}
