using System.Net.Http.Headers;
using MgtOcr.Core;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

public record SapPostResult(bool Success, bool Simulated, string SapDocNo, string Endpoint, string Message, object? Raw = null);

// Ported from app/sap.py's post() (lines 116-141). Simulate mode when SAP_BASE_URL is unset
// (the normal case today); live mode does a raw HTTP POST that first fetches a CSRF token from
// the OData service (SAP Gateway rejects modifying requests without one).
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

    /// <summary>Reads a supplier invoice back from API_SUPPLIERINVOICE_PROCESS_SRV with its PO lines,
    /// so what SAP holds can be set against what was sent. Never throws: (false, message) on any
    /// failure, because a reconciliation that cannot read one invoice should still report the rest.</summary>
    public async Task<(bool Ok, string Body)> GetSupplierInvoiceAsync(string supplierInvoice, string fiscalYear)
    {
        var baseUrl = config.SapSupplierInvoiceBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Sap:SupplierInvoice base URL is not configured");
        static string Esc(string v) => (v ?? "").Trim().Replace("'", "''");
        var url = $"{baseUrl.TrimEnd('/')}/A_SupplierInvoice(SupplierInvoice='{Esc(supplierInvoice)}',FiscalYear='{Esc(fiscalYear)}')"
                  + $"?$expand=to_SuplrInvcItemPurOrdRef&$format=json&sap-client={config.SapClient}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(config.SapSupplierInvoiceAuthHeader))
                req.Headers.TryAddWithoutValidation("Authorization", config.SapSupplierInvoiceAuthHeader);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return (resp.IsSuccessStatusCode, text);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>Every supplier-invoice line booked against a purchase order (goods and planned
    /// delivery costs alike), raw JSON of A_SuplrInvcItemPurOrdRef. (false, message) on failure.</summary>
    public async Task<(bool Ok, string Body)> GetInvoiceLinesForPoAsync(string purchaseOrder)
    {
        var baseUrl = config.SapSupplierInvoiceBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Sap:SupplierInvoice base URL is not configured");
        var filter = Uri.EscapeDataString($"PurchaseOrder eq '{(purchaseOrder ?? "").Trim().Replace("'", "''")}'");
        var url = $"{baseUrl.TrimEnd('/')}/A_SuplrInvcItemPurOrdRef?$filter={filter}&$top=1000&$format=json&sap-client={config.SapClient}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(config.SapSupplierInvoiceAuthHeader))
                req.Headers.TryAddWithoutValidation("Authorization", config.SapSupplierInvoiceAuthHeader);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return (resp.IsSuccessStatusCode, text);
        }
        catch (Exception e)
        {
            return (false, e.Message);
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

        if (string.IsNullOrEmpty(baseUrl))
        {
            var docNo = (module == "SO" ? "00" : "51") + Random.Shared.Next(100000, 1000000);
            return new SapPostResult(true, true, docNo, endpoint,
                "Simulation mode: SAP base URL is not set in appsettings (payload and log have been saved)");
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
            // What SAP says it actually did with a supplier invoice. Sent "A" (parked) and got a
            // posted document back once (5105600162), and the message only ever said "created" —
            // so the status from SAP's own response is now written into the message, which lands
            // in ocr.PostLog. Sent vs returned is then visible per attempt without a debugger.
            var sentStatus = body.GetValueOrDefault("SupplierInvoiceStatus") as string;
            var gotStatus = TryGetString(d, "SupplierInvoiceStatus");
            var message = sentStatus is null && gotStatus is null
                ? "Document created in SAP successfully"
                : $"Document created in SAP successfully — SupplierInvoiceStatus sent '{sentStatus ?? "(none)"}', "
                  + $"returned by SAP '{gotStatus ?? "(not in response)"}'";
            return new SapPostResult(true, false, sapDocNo, endpoint, message, JsonSerializer.Deserialize<object>(d.GetRawText()));
        }
        catch (Exception e)
        {
            return new SapPostResult(false, false, "", endpoint, $"Failed to post to SAP: {e.Message}");
        }
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
