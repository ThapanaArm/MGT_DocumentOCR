using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

/// <summary>The Sales Employee last used for a (customer, material) pair on a past Sales Order — read
/// from the item-level custom field YY1_SDSalesEmployeeI_SDI. Used to suggest, per OCR line, who
/// handled that customer+material before.</summary>
public record SapSalesEmployeeSuggestion(string PersonId, string? SalesOrder = null, string? CreationDate = null);

// GLC Sales Order flow: "who was the Sales Employee last time this customer bought this material?"
// Read-only OData V2 GET against API_SALES_ORDER_SRV (A_SalesOrder header, $expand=to_Item). Mirrors
// SapBillingClient's approach: SoldToParty IS filterable on the header, so filter by customer
// server-side, page newest-first, and walk each order's items to find the matching Material and read
// its item custom field YY1_SDSalesEmployeeI_SDI. Same simulate-safe contract: returns null when the
// service isn't configured, nothing matches within the scan cap, or the call fails — a missing
// suggestion must never block the flow.
public class SapSalesOrderClient(AppConfig config, HttpClient httpClient)
{
    // Item-level custom field carrying the Sales Employee Person ID (per Megachem's extensibility).
    private const string SalesEmployeeItemField = "YY1_SDSalesEmployeeI_SDI";

    private const int MAX_ORDERS_SCANNED = 400;
    private const int PAGE_SIZE = 50;

    public async Task<SapSalesEmployeeSuggestion?> GetLastSalesEmployeeAsync(string customerCode, string materialCode)
    {
        var baseUrl = config.SapSalesOrderBaseUrl;
        var customer = customerCode?.Trim() ?? "";
        var material = materialCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl) || customer.Length == 0 || material.Length == 0)
            return null; // not configured / nothing to look up — best-effort, never blocks

        try
        {
            // SoldToParty is a header field and CAN be filtered server-side; Material lives on the
            // item, so it's matched client-side after $expand=to_Item. Newest orders first so the
            // FIRST match is the most recent time this customer bought this material.
            var filter = $"SoldToParty eq '{EscapeODataLiteral(customer)}'";
            var url = $"{baseUrl.TrimEnd('/')}/A_SalesOrder" +
                      $"?$filter={Uri.EscapeDataString(filter)}" +
                      "&$expand=to_Item" +
                      "&$orderby=CreationDate desc,SalesOrder desc" +
                      $"&$top={PAGE_SIZE}&$format=json";

            var scanned = 0;
            while (!string.IsNullOrEmpty(url) && scanned < MAX_ORDERS_SCANNED)
            {
                var text = await GetJsonAsync(url);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("d", out var d) || !d.TryGetProperty("results", out var orders)
                    || orders.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var order in orders.EnumerateArray())
                {
                    scanned++;
                    if (!order.TryGetProperty("to_Item", out var itemNav)) continue;
                    foreach (var it in EnumerateNestedArray(itemNav))
                    {
                        var mat = GetString(it, "Material");
                        if (string.IsNullOrWhiteSpace(mat) || !string.Equals(mat.Trim(), material, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var person = GetString(it, SalesEmployeeItemField);
                        if (string.IsNullOrWhiteSpace(person)) continue; // that line had no sales employee stamped
                        return new SapSalesEmployeeSuggestion(
                            person.Trim(),
                            GetString(order, "SalesOrder"),
                            FormatSapDate(GetString(order, "CreationDate")));
                    }
                }

                url = d.TryGetProperty("__next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String
                    ? ResolveNext(baseUrl, nextEl.GetString())
                    : null;
            }

            return null; // no prior order for this customer+material within the scan cap
        }
        catch
        {
            return null; // best-effort — never blocks the flow
        }
    }

    /// <summary>Live-from-SAP list of every Sales Employee Person ID that actually appears on a
    /// recent Sales Order item (item custom field YY1_SDSalesEmployeeI_SDI). This complements the
    /// DB master list: the DB carries id→name for everyone IT maintains, while this surfaces IDs
    /// that are genuinely in use in SAP right now (including any newly used one not yet in the DB).
    /// Best-effort: returns an empty list when the service isn't configured or the call fails.</summary>
    public async Task<IReadOnlyList<SapSalesEmployeeSuggestion>> GetAllSalesEmployeesAsync()
    {
        var baseUrl = config.SapSalesOrderBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return [];

        // Person ID -> most recent (order, date) it was seen on. Newest-first scan means the first
        // time we see an ID is its most recent use, so we never overwrite with an older sighting.
        var seen = new Dictionary<string, SapSalesEmployeeSuggestion>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/A_SalesOrder" +
                      "?$expand=to_Item" +
                      "&$orderby=CreationDate desc,SalesOrder desc" +
                      $"&$top={PAGE_SIZE}&$format=json";

            var scanned = 0;
            while (!string.IsNullOrEmpty(url) && scanned < MAX_ORDERS_SCANNED)
            {
                var text = await GetJsonAsync(url);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("d", out var d) || !d.TryGetProperty("results", out var orders)
                    || orders.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var order in orders.EnumerateArray())
                {
                    scanned++;
                    if (!order.TryGetProperty("to_Item", out var itemNav)) continue;
                    foreach (var it in EnumerateNestedArray(itemNav))
                    {
                        var person = GetString(it, SalesEmployeeItemField);
                        if (string.IsNullOrWhiteSpace(person)) continue;
                        var id = person.Trim();
                        if (seen.ContainsKey(id)) continue; // already have its most recent sighting
                        seen[id] = new SapSalesEmployeeSuggestion(
                            id, GetString(order, "SalesOrder"), FormatSapDate(GetString(order, "CreationDate")));
                    }
                }

                url = d.TryGetProperty("__next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String
                    ? ResolveNext(baseUrl, nextEl.GetString())
                    : null;
            }
        }
        catch
        {
            // best-effort — a failed live lookup just means we fall back to the DB master alone
        }

        return seen.Values.OrderBy(s => s.PersonId, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<JsonElement> EnumerateNestedArray(JsonElement navProp)
    {
        if (navProp.ValueKind == JsonValueKind.Object && navProp.TryGetProperty("results", out var r)
            && r.ValueKind == JsonValueKind.Array)
            foreach (var x in r.EnumerateArray()) yield return x;
        else if (navProp.ValueKind == JsonValueKind.Array)
            foreach (var x in navProp.EnumerateArray()) yield return x;
    }

    private static string? ResolveNext(string baseUrl, string? next) =>
        string.IsNullOrEmpty(next) ? null
            : (next.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? next
               : new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), next).ToString());

    private async Task<string> GetJsonAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var resp = await httpClient.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"SAP Sales Order lookup failed: {(int)resp.StatusCode} {Trunc(text, 500)}");

        return text;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        // Reuse the Sales Order service's own communication-user credentials (same ones used for
        // posting), falling back to the write-side Sap:User/Sap:Password when blank — same pattern as
        // the other read-only SAP clients.
        if (!string.IsNullOrWhiteSpace(config.SapSalesOrderAuthHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", config.SapSalesOrderAuthHeader);
            return;
        }
        var authBytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? FormatSapDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"/Date\((-?\d+)(?:[+-]\d{4})?\)/");
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out var ms)) return raw;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            .ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string EscapeODataLiteral(string s) => s.Replace("'", "''");
    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s[..n]);
}
