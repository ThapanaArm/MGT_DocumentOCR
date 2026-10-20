using System.Net.Http.Headers;
using MgtOcr.Core;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

public record SapPostResult(bool Success, bool Simulated, string SapDocNo, string Endpoint, string Message, object? Raw = null);

// Ported from app/sap.py's post() (lines 116-141). Does a raw HTTP POST that first fetches a CSRF
// token from the OData service (SAP Gateway rejects modifying requests without one). There is no
// simulation mode any more: an unset write URL is reported as a failure. `Simulated` stays in the
// result for API compatibility and is always false.
public class SapClient(AppConfig config, HttpClient httpClient)
{
    // Strip every "_"-prefixed key at ALL levels before the live POST. These keys (_target, _source,
    // _wht at the top level and _internalMaterial/_isoUnit/_docQuantity/_uomFactor nested inside the
    // to_Item / to_SuplrInvcItemPurOrdRef line arrays) are internal bookkeeping for logging and
    // simulation only. SAP's OData validator rejects any property it doesn't know, so a nested one
    // that leaks into a line item fails the whole POST with
    // /IWCOR/CX_DS_EP_PROPERTY_ERROR "Property '_internalMaterial' is invalid".
    private static Dictionary<string, object?> StripUnderscoreKeys(Dictionary<string, object?> payload) =>
        (Dictionary<string, object?>)StripUnderscore(payload)!;

    private static object? StripUnderscore(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> dict:
                return dict
                    .Where(kv => !kv.Key.StartsWith('_'))
                    .ToDictionary(kv => kv.Key, kv => StripUnderscore(kv.Value));
            // A string is IEnumerable<char>, so match it before the sequence case and leave it alone.
            case string:
                return value;
            // Line-item arrays (to_Item, to_PricingElement, ...) are IEnumerable of nested
            // dictionaries — recurse into each element so their nested "_" keys are stripped too.
            case System.Collections.IEnumerable seq:
                return seq.Cast<object?>().Select(StripUnderscore).ToList();
            default:
                return value;
        }
    }

    public async Task<SapPostResult> PostAsync(string module, Dictionary<string, object?> payload)
    {
        var endpoint = payload.GetValueOrDefault("_target") as string ?? "";
        var body = StripUnderscoreKeys(payload);

        // "SO" (Sales Order) prefers its own dedicated service root (Sap:SalesOrder:BaseUrl_Dev/
        // Prod, e.g. ".../API_SALES_ORDER_SRV") once configured, same as SapBusinessPartnerClient
        // — that root already ends in the service name, so only the entity name (the part of
        // `endpoint` after the last "/") gets appended to it, not the full "SRV/A_Entity" path.
        // Falls back to the old flat Sap:BaseUrl (+ "/sap/opu/odata/sap/{endpoint}") when blank,
        // so this keeps working exactly as before until Sap:SalesOrder is filled in.
        // This is the ONLY place the app creates anything in SAP, so it is the only place that
        // uses the WRITE base URLs (Sap:WriteEnvironment). Everything else — Business Partner,
        // Product, Billing, Sales Order lookups — keeps reading from Sap:ActiveEnvironment. With
        // WriteEnvironment = dev and ActiveEnvironment = prod, master data is matched against live
        // production data while no document is ever posted into production.
        var useDedicatedSoUrl = module == "SO" && !string.IsNullOrEmpty(config.SapSalesOrderWriteBaseUrl);
        // A supplier invoice posts to its own service root as well (Sap:SupplierInvoice:BaseUrl_*,
        // on its own environment key that defaults to dev). Leaving BaseUrl_Prod empty is what
        // keeps a mis-set environment from ever creating a document in the live tenant.
        var useDedicatedApUrl = module is "AP" or "II" && !string.IsNullOrEmpty(config.SapSupplierInvoiceBaseUrl);
        var useDedicatedUrl = useDedicatedSoUrl || useDedicatedApUrl;
        var baseUrl = useDedicatedSoUrl ? config.SapSalesOrderWriteBaseUrl
            : useDedicatedApUrl ? config.SapSupplierInvoiceBaseUrl
            : config.SapWriteBaseUrl;

        // Simulation mode removed (12 Oct 2026, per Megachem): a missing write URL used to return a
        // made-up document number as a SUCCESS, which marked the document POSTED and locked it
        // although nothing existed in SAP. A missing URL is now a plain failure the person can see
        // and retry once the setting is fixed.
        if (string.IsNullOrEmpty(baseUrl))
        {
            var missing = module == "SO"
                ? "Sap:SalesOrder:BaseUrl_<Dev|Prod> (or Sap:BaseUrl_<Dev|Prod>)"
                : "Sap:BaseUrl_<Dev|Prod>";
            return new SapPostResult(false, false, "", endpoint,
                $"SAP is not configured: {missing} is empty in appsettings.json, so nothing was sent to SAP");
        }

        var url = useDedicatedUrl
            ? $"{baseUrl.TrimEnd('/')}/{endpoint[(endpoint.LastIndexOf('/') + 1)..]}?sap-client={config.SapClient}"
            : $"{baseUrl.TrimEnd('/')}/sap/opu/odata/sap/{endpoint}?sap-client={config.SapClient}";

        // Service-root URL used to fetch the CSRF token. For the dedicated SO service, baseUrl IS
        // the service root (".../API_SALES_ORDER_SRV"); for the flat layout the service root is
        // everything up to and including the service name (the part of `endpoint` before its last
        // "/", e.g. "API_SALES_ORDER_SRV" out of "API_SALES_ORDER_SRV/A_SalesOrder").
        var serviceName = endpoint.Contains('/') ? endpoint[..endpoint.LastIndexOf('/')] : endpoint;
        var tokenUrl = useDedicatedUrl
            ? $"{baseUrl.TrimEnd('/')}/?sap-client={config.SapClient}"
            : $"{baseUrl.TrimEnd('/')}/sap/opu/odata/sap/{serviceName}/?sap-client={config.SapClient}";

        // Applies whichever auth the SO/other path uses to a request. Shared by the token fetch and
        // the POST so both requests authenticate identically (the CSRF token is bound to the
        // authenticated session, so a mismatch would re-trigger a 403).
        void ApplyAuth(HttpRequestMessage r)
        {
            if (module == "SO" && !string.IsNullOrWhiteSpace(config.SapSalesOrderAuthHeader))
            {
                r.Headers.TryAddWithoutValidation("Authorization", config.SapSalesOrderAuthHeader);
            }
            else if (useDedicatedApUrl && !string.IsNullOrWhiteSpace(config.SapSupplierInvoiceAuthHeader))
            {
                r.Headers.TryAddWithoutValidation("Authorization", config.SapSupplierInvoiceAuthHeader);
            }
            else
            {
                var authBytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
                r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            // Step 1: fetch a CSRF token. SAP returns it in the "x-csrf-token" response header and
            // sets the session cookie(s) it is bound to; those cookies flow to the POST below via
            // the HttpClient's CookieContainer (enabled by default on AddHttpClient<SapClient>()),
            // so this fetch and the POST must run on the same HttpClient instance.
            using var fetchReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
            ApplyAuth(fetchReq);
            fetchReq.Headers.TryAddWithoutValidation("X-CSRF-Token", "Fetch");
            fetchReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var fetchResp = await httpClient.SendAsync(fetchReq, cts.Token);

            string? csrfToken = null;
            if (fetchResp.Headers.TryGetValues("x-csrf-token", out var tokenValues))
            {
                csrfToken = tokenValues.FirstOrDefault();
            }
            if (string.IsNullOrWhiteSpace(csrfToken))
            {
                var fetchText = await fetchResp.Content.ReadAsStringAsync(cts.Token);
                return new SapPostResult(false, false, "", endpoint,
                    $"Failed to fetch CSRF token from SAP: {(int)fetchResp.StatusCode} " +
                    $"{fetchText[..Math.Min(300, fetchText.Length)]}");
            }

            // Step 2: POST with the fetched token (cookies are attached automatically).
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            ApplyAuth(req);
            req.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var resp = await httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return new SapPostResult(false, false, "", endpoint,
                    $"Failed to post to SAP: {(int)resp.StatusCode} {text[..Math.Min(500, text.Length)]}");
            }
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;
            var d = root.TryGetProperty("d", out var dEl) ? dEl : root;
            var sapDocNo = TryGetString(d, "SalesOrder") ?? TryGetString(d, "SupplierInvoice") ?? "";
            var message = "Document created in SAP successfully";

            // GLC Sales Orders: the deep insert adds our Manual Price Gross (ZPR0) row, but the
            // pricing procedure ALSO determines its own ZPR0 row with no rate (shown with a red
            // status in VA03), so every item ends up with two ZPR0 lines. Remove the empty one(s)
            // after create. Best-effort only: the order already exists with the right price, so a
            // failure here never fails the post — it is just noted in the message.
            if (module == "SO" && !string.IsNullOrEmpty(sapDocNo)
                && !string.IsNullOrWhiteSpace(config.SapSalesOrderPriceConditionType))
            {
                var serviceRoot = useDedicatedSoUrl
                    ? baseUrl.TrimEnd('/')
                    : $"{baseUrl.TrimEnd('/')}/sap/opu/odata/sap/{serviceName}";
                var cleanup = await RemoveEmptyDuplicatePriceConditionsAsync(
                    serviceRoot, sapDocNo, config.SapSalesOrderPriceConditionType, csrfToken, ApplyAuth, cts.Token);
                if (!string.IsNullOrEmpty(cleanup)) message += " — " + cleanup;
            }
            return new SapPostResult(true, false, sapDocNo, endpoint, message, JsonSerializer.Deserialize<object>(d.GetRawText()));
        }
        catch (Exception e)
        {
            return new SapPostResult(false, false, "", endpoint, $"Failed to post to SAP: {e.Message}");
        }
    }

    /// <summary>Deletes the empty (rate 0) duplicate of <paramref name="conditionType"/> on every
    /// item of a just-created Sales Order, but only on items that also carry a non-zero row of the
    /// same condition type — so an item whose only ZPR0 is empty is never touched. Returns a short
    /// note for the post message, or "" when there was nothing to clean up.</summary>
    private async Task<string> RemoveEmptyDuplicatePriceConditionsAsync(
        string serviceRoot, string salesOrder, string conditionType, string csrfToken,
        Action<HttpRequestMessage> applyAuth, CancellationToken ct)
    {
        try
        {
            var filter = $"SalesOrder eq '{salesOrder.Replace("'", "''")}' and ConditionType eq '{conditionType.Replace("'", "''")}'";
            var listUrl = $"{serviceRoot}/A_SalesOrderItemPrElement?$filter={Uri.EscapeDataString(filter)}" +
                          "&$select=SalesOrderItem,PricingProcedureStep,PricingProcedureCounter,ConditionRateValue,ConditionAmount" +
                          $"&$format=json&sap-client={config.SapClient}";
            using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
            applyAuth(listReq);
            listReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var listResp = await httpClient.SendAsync(listReq, ct);
            var listText = await listResp.Content.ReadAsStringAsync(ct);
            if (!listResp.IsSuccessStatusCode)
                return $"could not read {conditionType} conditions to remove the empty duplicate ({(int)listResp.StatusCode})";

            using var doc = JsonDocument.Parse(listText);
            var rows = new List<(string Item, string Step, string Counter, decimal Rate, decimal Amount)>();
            if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in results.EnumerateArray())
                {
                    rows.Add((TryGetString(r, "SalesOrderItem") ?? "", TryGetString(r, "PricingProcedureStep") ?? "",
                        TryGetString(r, "PricingProcedureCounter") ?? "", ParseDec(r, "ConditionRateValue"),
                        ParseDec(r, "ConditionAmount")));
                }
            }

            var toDelete = rows
                .GroupBy(r => r.Item)
                .Where(g => g.Count() > 1 && g.Any(r => r.Rate != 0))
                .SelectMany(g => g.Where(r => r.Rate == 0 && r.Amount == 0))
                .ToList();
            if (toDelete.Count == 0) return "";

            var failed = new List<string>();
            foreach (var r in toDelete)
            {
                var key = $"SalesOrder='{salesOrder}',SalesOrderItem='{r.Item}'," +
                          $"PricingProcedureStep='{r.Step}',PricingProcedureCounter='{r.Counter}'";
                using var delReq = new HttpRequestMessage(HttpMethod.Delete,
                    $"{serviceRoot}/A_SalesOrderItemPrElement({key})?sap-client={config.SapClient}");
                applyAuth(delReq);
                delReq.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
                delReq.Headers.TryAddWithoutValidation("If-Match", "*");
                delReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var delResp = await httpClient.SendAsync(delReq, ct);
                if (!delResp.IsSuccessStatusCode)
                {
                    var t = await delResp.Content.ReadAsStringAsync(ct);
                    failed.Add($"item {r.Item}: {(int)delResp.StatusCode} {t[..Math.Min(200, t.Length)]}");
                }
            }
            return failed.Count == 0
                ? $"removed {toDelete.Count} empty duplicate {conditionType} row(s)"
                : $"could not remove empty duplicate {conditionType} row(s): {string.Join("; ", failed)}";
        }
        catch (Exception e)
        {
            return $"could not remove empty duplicate {conditionType} row(s): {e.Message}";
        }
    }

    // OData V2 sends Edm.Decimal as a string ("450.000000000"); accept a JSON number too.
    private static decimal ParseDec(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        return v.ValueKind == JsonValueKind.String
               && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                   System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
