using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

/// <summary>The last actual selling price SAP billed one customer for one material, and the base
/// unit that price is per (see <see cref="SapBillingClient.GetLastPriceAsync"/>).</summary>
public record SapLastPrice(decimal PricePerUnit, string Unit, string? BillingDocument, string? CreationDate);

// GLC Sales Order flow, material-confirm step: "what did we last actually charge this customer for
// this material?" — read-only OData V2 GET client against API_BILLING_DOCUMENT_SRV
// (A_BillingDocumentItem). Mirrors the calculation already proven correct in the nightly
// SapUomSyncService job that pushes Last_Prices into Zoho (NetAmount / BillingQuantityInBaseUnit,
// so the price is always "per base unit" regardless of what unit the invoice itself was billed
// in) but scoped live to ONE (customer, material) pair instead of bulk-fetching every billing line
// — this runs on demand when a person picks a material in the UI, not on a schedule.
//
// NOTE on why this can't just be one server-side $filter: A_BillingDocumentItem carries the
// Ship-to partner but not the Sold-to — the Sold-to (SoldToParty) lives on the item's own billing
// document header (to_BillingDocument), and OData V2 doesn't support filtering on an expanded nav
// property's field. So Material (a direct field, and optionally Plant) is filtered server-side,
// the results are paged newest-first, and each page's items are checked client-side for the first
// one whose to_BillingDocument.SoldToParty matches — same $expand=to_BillingDocument approach the
// Zoho job already uses for the same reason. Same simulate-safe contract as the other SAP clients
// in this project: returns null when SapBillingBaseUrl isn't configured, nothing matches within
// the page cap below, or the call fails — a missing/broken Last Price must never block the save.
public class SapBillingClient(AppConfig config, HttpClient httpClient)
{
    // How many billing lines (for this one Material, across all customers) to page through at
    // most looking for this customer's line. A material with a handful of customers rarely needs
    // more than a page or two of its newest lines; this just bounds the worst case. Kept fairly
    // generous (rather than the original 300/50) because a popular material can easily have more
    // than a few hundred newer billing lines from OTHER customers stacked ahead of the one this
    // customer's own (possibly old) order sits at — too small a cap here reads exactly like "no
    // Last Price found" even though SAP does have one, just further back in the newest-first order.
    private const int MAX_ITEMS_SCANNED = 1000;
    private const int PAGE_SIZE = 100;

    // `plant` is accepted but NOT applied as a filter (see below) — kept in the signature only so
    // callers don't need to change if this needs revisiting.
    public async Task<SapLastPrice?> GetLastPriceAsync(string customerCode, string materialCode, string? plant = null)
    {
        var baseUrl = config.SapBillingBaseUrl;
        var customer = customerCode?.Trim() ?? "";
        var material = materialCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl) || customer.Length == 0 || material.Length == 0)
            return null; // not configured / nothing to look up — best-effort, never blocks the caller

        try
        {
            // Filter ONLY by Material server-side, then check NetAmount/BillingQuantity CLIENT-SIDE
            // below -- exactly what the proven ZohoSalesOrderLastPriceJob does (it filters SAP by
            // CreationDate/Plant only and does `if (net <= 0 || qty <= 0) continue;` in C# after
            // parsing). Do NOT put "NetAmount gt 0"/"BillingQuantity gt 0" in the OData $filter:
            // on this tenant's OData V2 service, comparing an Edm.Decimal field to a bare integer
            // literal 0 is rejected (400 "incompatible types"), which the try/catch below turns
            // into a silent null -- that was the actual bug (the unfiltered raw endpoint worked
            // because it never had these clauses).
            // Also deliberately NOT filtering by Plant, same as that job: a real billing document's
            // Plant can differ from the current DefaultPlant, and filtering on it would drop
            // otherwise-correct matches. Customer + Material together are specific enough.
            var filter = $"Material eq '{EscapeODataLiteral(material)}'";

            var url = $"{baseUrl.TrimEnd('/')}/A_BillingDocumentItem" +
                      $"?$filter={Uri.EscapeDataString(filter)}" +
                      "&$expand=to_BillingDocument" +
                      "&$orderby=CreationDate desc,BillingDocument desc" +
                      $"&$top={PAGE_SIZE}&$format=json";

            var scanned = 0;
            while (!string.IsNullOrEmpty(url) && scanned < MAX_ITEMS_SCANNED)
            {
                var text = await GetJsonAsync(url);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("d", out var d) || !d.TryGetProperty("results", out var results)
                    || results.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var it in results.EnumerateArray())
                {
                    scanned++;
                    var soldTo = GetString(it, "to_BillingDocument", "SoldToParty");
                    if (string.IsNullOrWhiteSpace(soldTo) || !string.Equals(soldTo.Trim(), customer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var net = GetDecimal(it, "NetAmount") ?? 0m;
                    var qty = GetDecimal(it, "BillingQuantity") ?? 0m;
                    var qtyBase = GetDecimal(it, "BillingQuantityInBaseUnit") ?? 0m;
                    if (qtyBase <= 0) qtyBase = qty;
                    if (net <= 0 || qtyBase <= 0) continue; // guard div-by-zero / nonsense rows

                    var unit = GetString(it, "BaseUnit") ?? GetString(it, "BillingQuantityUnit") ?? "";
                    var pricePerUnit = Math.Round(net / qtyBase, 2, MidpointRounding.AwayFromZero);
                    return new SapLastPrice(
                        pricePerUnit,
                        unit,
                        GetString(it, "BillingDocument"),
                        FormatSapDate(GetString(it, "CreationDate")));
                }

                url = d.TryGetProperty("__next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String
                    ? ResolveNext(baseUrl, nextEl.GetString())
                    : null;
            }

            return null; // no line for this customer found within the scan cap
        }
        catch
        {
            return null; // best-effort — never blocks the save flow
        }
    }

    // Diagnostic only (mirrors SapBusinessPartnerClient.GetRawByIdAsync — same reason: when a
    // lookup that should find something comes back null, the fastest way to find out why is to
    // look at SAP's own unfiltered response directly, e.g. via Postman, rather than guess at field
    // names/formats blind). NO NetAmount/BillingQuantity/Plant filters here on purpose — this shows
    // every billing line SAP has for the material, in the same newest-first order GetLastPriceAsync
    // scans in, so a person can check things like: does the expected customer's line even appear;
    // if so, how many pages back (is MAX_ITEMS_SCANNED too small for this material); what format is
    // SoldToParty actually in (leading zeros?); is Plant on the line what CompanyProfile.DefaultPlant
    // expects. Returns SAP's raw JSON text as-is.
    public async Task<string> GetRawByMaterialAsync(string materialCode, int top = 50, string? customer = null)
    {
        var baseUrl = config.SapBillingBaseUrl;
        var material = materialCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl) || material.Length == 0)
            return "{\"error\":\"SapBillingBaseUrl not configured, or no material given\"}";

        var filter = $"Material eq '{EscapeODataLiteral(material)}'";
        var url = $"{baseUrl.TrimEnd('/')}/A_BillingDocumentItem" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  "&$expand=to_BillingDocument" +
                  "&$orderby=CreationDate desc,BillingDocument desc" +
                  $"&$top={top}&$format=json";
        var raw = await GetJsonAsync(url);

        // No customer given -> return SAP's response as-is (full raw dump).
        var cust = customer?.Trim() ?? "";
        if (cust.Length == 0) return raw;

        // Customer given -> parse and keep only the lines whose Sold-to matches (contains, so a
        // partial code works too), and return a COMPACT summary of exactly the fields
        // GetLastPriceAsync reads, plus the pricePerUnit it would compute. This is the fastest way
        // to confirm what a real (customer, material) lookup sees: which SoldToParty format SAP
        // returns, and whether NetAmount/BillingQuantityInBaseUnit produce the expected price.
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            if (!doc.RootElement.TryGetProperty("d", out var d) || !d.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return "{\"matched\":0,\"note\":\"unexpected SAP response shape\",\"raw\":" + raw + "}";

            var matched = new List<object>();
            foreach (var it in results.EnumerateArray())
            {
                var soldTo = GetString(it, "to_BillingDocument", "SoldToParty") ?? "";
                if (!soldTo.Contains(cust, StringComparison.OrdinalIgnoreCase)) continue;

                var net = GetDecimal(it, "NetAmount") ?? 0m;
                var qtyBase = GetDecimal(it, "BillingQuantityInBaseUnit") ?? 0m;
                if (qtyBase <= 0) qtyBase = GetDecimal(it, "BillingQuantity") ?? 0m;
                var price = qtyBase > 0 ? Math.Round(net / qtyBase, 2, MidpointRounding.AwayFromZero) : 0m;
                matched.Add(new
                {
                    soldToParty = soldTo,
                    material = GetString(it, "Material"),
                    netAmount = net,
                    billingQuantity = GetDecimal(it, "BillingQuantity"),
                    billingQuantityInBaseUnit = GetDecimal(it, "BillingQuantityInBaseUnit"),
                    baseUnit = GetString(it, "BaseUnit"),
                    pricePerUnit = price,
                    billingDocument = GetString(it, "BillingDocument"),
                    creationDate = FormatSapDate(GetString(it, "CreationDate")),
                });
            }
            return JsonSerializer.Serialize(new { customer = cust, material, matched = matched.Count, results = matched },
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return "{\"error\":\"failed to filter by customer: " + EscapeODataLiteral(ex.Message) + "\"}";
        }
    }

    // Diagnostic, customer-only search: unlike GetRawByMaterialAsync (which filters items by
    // Material), Sold-to isn't a field on A_BillingDocumentItem, so this queries the HEADER
    // (A_BillingDocument, where SoldToParty CAN be filtered server-side) and expands to_Item to walk
    // that customer's billing lines. Returns the same compact per-line summary as the material
    // search. `customer` is an EXACT SoldToParty match here (OData header $filter), not a "contains".
    public async Task<string> GetRawByCustomerAsync(string customerCode, int top = 50)
    {
        var baseUrl = config.SapBillingBaseUrl;
        var customer = customerCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl) || customer.Length == 0)
            return "{\"error\":\"SapBillingBaseUrl not configured, or no customer given\"}";

        try
        {
            var filter = $"SoldToParty eq '{EscapeODataLiteral(customer)}'";
            var url = $"{baseUrl.TrimEnd('/')}/A_BillingDocument" +
                      $"?$filter={Uri.EscapeDataString(filter)}" +
                      "&$expand=to_Item" +
                      $"&$top={top}&$format=json";
            var raw = await GetJsonAsync(url);

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            if (!doc.RootElement.TryGetProperty("d", out var d) || !d.TryGetProperty("results", out var docs)
                || docs.ValueKind != JsonValueKind.Array)
                return "{\"matched\":0,\"note\":\"unexpected SAP response shape\",\"raw\":" + raw + "}";

            var lines = new List<object>();
            foreach (var bd in docs.EnumerateArray())
            {
                var soldTo = GetString(bd, "SoldToParty") ?? "";
                var billingDoc = GetString(bd, "BillingDocument");
                var date = FormatSapDate(GetString(bd, "BillingDocumentDate") ?? GetString(bd, "CreationDate"));
                if (!bd.TryGetProperty("to_Item", out var itemNav)) continue;
                foreach (var it in EnumerateNestedArray(itemNav))
                {
                    var net = GetDecimal(it, "NetAmount") ?? 0m;
                    var qtyBase = GetDecimal(it, "BillingQuantityInBaseUnit") ?? 0m;
                    if (qtyBase <= 0) qtyBase = GetDecimal(it, "BillingQuantity") ?? 0m;
                    var price = qtyBase > 0 ? Math.Round(net / qtyBase, 2, MidpointRounding.AwayFromZero) : 0m;
                    lines.Add(new
                    {
                        soldToParty = soldTo,
                        material = GetString(it, "Material"),
                        netAmount = net,
                        billingQuantity = GetDecimal(it, "BillingQuantity"),
                        billingQuantityInBaseUnit = GetDecimal(it, "BillingQuantityInBaseUnit"),
                        baseUnit = GetString(it, "BaseUnit"),
                        pricePerUnit = price,
                        billingDocument = billingDoc,
                        creationDate = date,
                    });
                }
            }
            return JsonSerializer.Serialize(new { customer, matched = lines.Count, results = lines },
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return "{\"error\":\"failed customer lookup: " + EscapeODataLiteral(ex.Message) + "\"}";
        }
    }

    /// <summary>Walks an OData V2 $expand'd nav-property array (e.g. "to_Item" -> { "results": [...] })
    /// and yields its items; falls back to treating the element itself as the array.</summary>
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
            throw new Exception($"SAP Billing Document lookup failed: {(int)resp.StatusCode} {Trunc(text, 500)}");

        return text;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        // Sap:Billing:AuthHeader lets this read-only service use its own communication-user
        // credentials without touching the write-side Sap:User/Sap:Password — falls back to
        // those when blank, same pattern as SapProductClient/SapBusinessPartnerClient.
        if (!string.IsNullOrWhiteSpace(config.SapBillingAuthHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", config.SapBillingAuthHeader);
            return;
        }
        var authBytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    /// <summary>Reads a top-level string field, or one nested one level down inside an OData V2
    /// $expand'd nav property object (e.g. "to_BillingDocument" -> "SoldToParty").</summary>
    private static string? GetString(JsonElement el, string prop, string? nestedProp = null)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (nestedProp is null)
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return v.ValueKind == JsonValueKind.Object && v.TryGetProperty(nestedProp, out var nv) && nv.ValueKind == JsonValueKind.String
            ? nv.GetString() : null;
    }

    private static decimal? GetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ds))
            return ds;
        return null;
    }

    /// <summary>Turns SAP OData V2's verbose-JSON date ("/Date(1784851200000)/", epoch ms, with an
    /// optional "+0000" offset) into a plain dd/MM/yyyy string for display. Any value that isn't in
    /// that shape (already a plain date, or empty) is returned unchanged.</summary>
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
