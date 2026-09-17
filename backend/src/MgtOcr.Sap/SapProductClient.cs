using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

public record SapMaterial(string MaterialCode, string MaterialDescription, string? Language = null);

/// <summary>One alternative unit of measure SAP knows for a material, with its conversion ratio
/// to the material's base unit: 1 <see cref="Unit"/> = (<see cref="Numerator"/> / <see cref="Denominator"/>)
/// base units (standard MARM/A_ProductUnitsOfMeasure convention).</summary>
public record SapUnitOfMeasure(string Unit, decimal Numerator, decimal Denominator);

public record SapMaterialDetail(string MaterialCode, string BaseUnit, string? MaterialGroup, List<SapUnitOfMeasure> AltUnits);

// Step 2 of SAP integration for the Sales Order module (after the Business Partner lookup in
// SapBusinessPartnerClient): read-only OData V2 GET client for SAP Product (Material) plant
// data. Used to confirm a material has actually been extended to the target Plant in SAP before
// a Sales Order is posted there — per user: Plant is what identifies the company in practice
// (e.g. 2100 = GLC/Green Leaf, 1100 = MGT), so this check is done by Plant, not by Sales
// Organization. Mirrors SapBusinessPartnerClient's simulate-when-unconfigured shape so this can
// be exercised before Sap:Product:BaseUrl is filled in.
// NOTE: entity/field names (A_ProductPlant, Product/Plant) not yet verified against this
// tenant's actual $metadata — if this 404s or the filter is rejected, the field/entity name may
// differ on this system and needs adjusting, same caveat as SapBusinessPartnerClient.
public class SapProductClient(AppConfig config, HttpClient httpClient)
{
    /// <summary>Search SAP material descriptions through API_PRODUCT_SRV/A_ProductDescription.</summary>
    public async Task<List<SapMaterial>> SearchByDescriptionAsync(string keyword, string? plant = null, int top = 30)
    {
        var baseUrl = config.SapProductBaseUrl;
        var clean = keyword.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || clean.Length < 2) return [];

        // Minimal query, matching the form verified working on this tenant: just the substringof
        // filter + $format=json — no $select and no "Language eq 'EN'" (which this tenant rejected).
        // English is preferred below at dedup time instead, so a Product that also has a Thai row
        // still surfaces its English name without a query filter that could break the call.
        // NOTE: SAP substringof is case-sensitive and ProductDescription is stored upper-case, so
        // "Fluor" matches nothing while "FLUOR" does. Upper-case the needle so any casing the user
        // types ("fluor"/"Fluor"/"FLU") matches.
        var needle = clean.ToUpperInvariant();
        var filter = $"substringof('{EscapeODataLiteral(needle)}',ProductDescription)";
        var url = $"{baseUrl.TrimEnd('/')}/A_ProductDescription" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  $"&$top={Math.Clamp(top, 1, 100)}&$format=json";
        var text = await GetJsonAsync(url);
        var results = EnumerateResults(text)
            .Select(el => new SapMaterial(
                GetString(el, "Product") ?? "",
                GetString(el, "ProductDescription") ?? "",
                GetString(el, "Language")))
            .Where(x => x.MaterialCode.Length > 0)
            // Per user: materials coded "SMRM..." (samples, not sellable stock) should never be
            // offered as a match when picking a material for a Sales Order line.
            .Where(x => !x.MaterialCode.StartsWith("SMRM", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.MaterialCode, StringComparer.OrdinalIgnoreCase)
            // Prefer the English row when a Product has several language descriptions.
            .Select(g => g.OrderByDescending(m =>
                string.Equals(m.Language, "EN", StringComparison.OrdinalIgnoreCase)).First())
            .ToList();
        if (string.IsNullOrWhiteSpace(plant) || results.Count == 0) return results;

        var productFilter = string.Join(" or ", results.Select(m =>
            $"Product eq '{EscapeODataLiteral(m.MaterialCode)}'"));
        var plantFilter = $"({productFilter}) and Plant eq '{EscapeODataLiteral(plant.Trim())}'";
        var plantUrl = $"{baseUrl.TrimEnd('/')}/A_ProductPlant" +
                       $"?$filter={Uri.EscapeDataString(plantFilter)}&$select=Product,Plant&$top={results.Count}";
        var plantText = await GetJsonAsync(plantUrl);
        var allowed = EnumerateResults(plantText)
            .Select(el => GetString(el, "Product"))
            .Where(code => !string.IsNullOrEmpty(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return results.Where(m => allowed.Contains(m.MaterialCode)).ToList();
    }

    /// <summary>
    /// True if <paramref name="material"/> has plant-level master data (MARC, exposed as
    /// A_ProductPlant) for <paramref name="plant"/> in SAP — i.e. it has been extended there and
    /// a Sales Order line for that plant should be postable.
    /// This is a best-effort pre-flight sanity check, NOT the source of truth (SAP itself still
    /// enforces this on the real POST) — returns true (never blocks) when Sap:Product:BaseUrl
    /// isn't configured yet, or when the SAP call itself fails, so an unconfigured/broken lookup
    /// never silently blocks document posting on its own.
    /// </summary>
    public async Task<bool> IsExtendedToPlantAsync(string material, string plant)
    {
        var baseUrl = config.SapProductBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(material) || string.IsNullOrWhiteSpace(plant))
            return true; // simulation mode / not configured / nothing to check

        var filter = $"Product eq '{EscapeODataLiteral(material.Trim())}' and Plant eq '{EscapeODataLiteral(plant.Trim())}'";
        var url = $"{baseUrl.TrimEnd('/')}/A_ProductPlant?$filter={Uri.EscapeDataString(filter)}&$select=Product,Plant&$top=1";

        try
        {
            var text = await GetJsonAsync(url);
            return EnumerateResults(text).Any();
        }
        catch
        {
            return true; // best-effort pre-check — a broken/unreachable lookup must not block real posting
        }
    }

    /// <summary>All plants <paramref name="material"/> is currently extended to in SAP — for
    /// diagnostics/UI only, not used by the check above.</summary>
    public async Task<List<string>> FindPlantsAsync(string material, int top = 50)
    {
        var baseUrl = config.SapProductBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(material)) return [];

        var filter = $"Product eq '{EscapeODataLiteral(material.Trim())}'";
        var url = $"{baseUrl.TrimEnd('/')}/A_ProductPlant?$filter={Uri.EscapeDataString(filter)}&$select=Product,Plant&$top={top}";

        var text = await GetJsonAsync(url);
        return EnumerateResults(text)
            .Select(el => GetString(el, "Plant"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
    }

    /// <summary>
    /// Best-effort fetch of a material's SAP base unit, material group, and alternative units of
    /// measure (each with its conversion ratio to the base unit) via API_PRODUCT_SRV — used to
    /// pre-fill the "confirm material details" step shown when a SAP search result is first saved,
    /// so Base Unit / pack size etc. don't have to be typed in from scratch. The caller still shows
    /// these as editable defaults, never applies them blind: returns null when SAP isn't configured,
    /// the material isn't found, or the call fails, same simulate-safe contract as the rest of this
    /// client — a missing/broken detail lookup must never block saving a material.
    /// Single $expand call on A_Product (not two independent A_Product / A_ProductUnitsOfMeasure
    /// calls) — this is the pattern confirmed working on this tenant by a separate, already-running
    /// production job (a Windows Service that syncs A_Product incl. to_ProductUnitsOfMeasure to
    /// Zoho CRM). The nav property comes back nested in the same response as
    /// to_ProductUnitsOfMeasure.results, per the standard OData V2 verbose JSON expand shape.
    /// </summary>
    public async Task<SapMaterialDetail?> GetMaterialDetailAsync(string materialCode)
    {
        var baseUrl = config.SapProductBaseUrl;
        var code = materialCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl) || code.Length == 0) return null;

        try
        {
            var filter = $"Product eq '{EscapeODataLiteral(code)}'";
            var url = $"{baseUrl.TrimEnd('/')}/A_Product?$filter={Uri.EscapeDataString(filter)}" +
                      "&$expand=to_ProductUnitsOfMeasure&$top=1&$format=json";
            var text = await GetJsonAsync(url);
            var pEl = EnumerateResults(text).FirstOrDefault();
            if (pEl.ValueKind != JsonValueKind.Object) return null;

            var baseUnit = GetString(pEl, "BaseUnit") ?? "";
            var group = GetString(pEl, "ProductGroup");

            var altUnits = new List<SapUnitOfMeasure>();
            if (pEl.TryGetProperty("to_ProductUnitsOfMeasure", out var uomNav))
            {
                altUnits = EnumerateNestedResults(uomNav)
                    .Select(el => new SapUnitOfMeasure(
                        GetString(el, "AlternativeUnit") ?? "",
                        GetDecimal(el, "QuantityNumerator") ?? 1m,
                        GetDecimal(el, "QuantityDenominator") ?? 1m))
                    .Where(u => u.Unit.Length > 0 && !u.Unit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return new SapMaterialDetail(code, baseUnit, group, altUnits);
        }
        catch
        {
            return null; // best-effort default only — never blocks the save flow
        }
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

    private async Task<string> GetJsonAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var resp = await httpClient.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"SAP Product Plant lookup failed: {(int)resp.StatusCode} {Trunc(text, 500)}");

        return text;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        // Sap:Product:AuthHeader lets this read-only service use its own communication-user
        // credentials without touching the write-side Sap:User/Sap:Password (or the dedicated
        // Sap:SalesOrder:AuthHeader) — falls back to Sap:User/Sap:Password when blank.
        if (!string.IsNullOrWhiteSpace(config.SapProductAuthHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", config.SapProductAuthHeader);
            return;
        }
        var authBytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    /// <summary>OData V2 verbose JSON: { "d": { "results": [...] } }. Tolerates a bare "value"
    /// array too, in case this ever points at an OData V4 service instead.</summary>
    private static IEnumerable<JsonElement> EnumerateResults(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement.Clone();

        JsonElement results = default;
        var found = false;
        if (root.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var r))
        {
            results = r; found = true;
        }
        else if (root.TryGetProperty("value", out var v))
        {
            results = v; found = true;
        }
        if (!found || results.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in results.EnumerateArray())
            yield return item;
    }

    /// <summary>Walks a $expand'd nav-property element's nested OData V2 verbose JSON shape —
    /// { "results": [...] } — and yields its items. Falls back to treating the element itself as
    /// the array, in case a future/alternate call ever returns it unwrapped.</summary>
    private static IEnumerable<JsonElement> EnumerateNestedResults(JsonElement navProp)
    {
        if (navProp.ValueKind == JsonValueKind.Object &&
            navProp.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in r.EnumerateArray()) yield return item;
        }
        else if (navProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in navProp.EnumerateArray()) yield return item;
        }
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string EscapeODataLiteral(string s) => s.Replace("'", "''");
    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s[..n]);
}
