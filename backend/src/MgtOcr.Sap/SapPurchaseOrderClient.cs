using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

/// <summary>One item of a purchase order as SAP holds it. The ITEM NUMBER is the point: SAP
/// numbers PO items 10, 40, 50, 60 — with gaps, because items get deleted and inserted — and a
/// supplier invoice posted against the PO has to name those exact numbers. Nothing on the paper
/// document carries them, and guessing 10, 20, 30 produces an invoice SAP will not take.</summary>
public record SapPurchaseOrderItem(
    string PurchaseOrder,
    string Item,
    string Material,
    string Text,
    decimal Quantity,
    string Unit,
    decimal NetPrice,
    string Currency,
    string Plant,
    string TaxCode);

/// <summary>The purchase order's own header — who it is for and which company placed it.
///
/// Checked before a supplier invoice is posted against it: PO 2230000020 is Kimex 2000014 under
/// company 2000 (Green Leaf), so an invoice that names a different supplier or a different
/// company code is posted against the wrong purchase order, and SAP rejects it only after the
/// person has filled the whole screen in.</summary>
public record SapPurchaseOrderHead(
    string PurchaseOrder,
    string Supplier,
    string SupplierName,
    string CompanyCode,
    string PurchasingOrganization,
    string Currency);

/// <summary>Read-only lookup of a purchase order's items — API_PURCHASEORDER_PROCESS_SRV.
///
/// Used before a supplier invoice is posted against a PO (MIRO): the read gives us what the
/// supplier billed, this gives us what the PO actually holds, and the two are matched so the
/// invoice names the right PO item. Blank BaseUrl = not configured, which returns an empty list
/// rather than throwing, so the rest of the screen keeps working.</summary>
public class SapPurchaseOrderClient(AppConfig config, HttpClient httpClient)
{
    public bool Configured => !string.IsNullOrWhiteSpace(config.SapPurchaseOrderBaseUrl);

    public async Task<List<SapPurchaseOrderItem>> ItemsAsync(string purchaseOrder)
    {
        var po = (purchaseOrder ?? "").Trim();
        if (!Configured || po.Length == 0) return [];

        var filter = $"PurchaseOrder eq '{EscapeODataLiteral(po)}'";
        var url = $"{config.SapPurchaseOrderBaseUrl.TrimEnd('/')}/A_PurchaseOrderItem" +
                  $"?$filter={Uri.EscapeDataString(filter)}&$top=200&$format=json";

        var items = EnumerateResults(await GetJsonAsync(url))
            .Select(el => new SapPurchaseOrderItem(
                GetString(el, "PurchaseOrder") ?? po,
                GetString(el, "PurchaseOrderItem") ?? "",
                GetString(el, "Material") ?? "",
                GetString(el, "PurchaseOrderItemText") ?? "",
                GetDecimal(el, "OrderQuantity"),
                GetString(el, "PurchaseOrderQuantityUnit") ?? "",
                GetDecimal(el, "NetPriceAmount"),
                GetString(el, "DocumentCurrency") ?? "",
                GetString(el, "Plant") ?? "",
                GetString(el, "TaxCode") ?? ""))
            .Where(x => x.Item.Length > 0)
            .ToList();

        // SAP returns them in whatever order the service pleases; the screen shows them the way
        // the PO does, by item number, and "10" must sort before "40" as a number, not as text.
        items.Sort((a, b) => ItemNo(a.Item).CompareTo(ItemNo(b.Item)));
        return items;
    }

    /// <summary>The PO header (supplier, company code, currency). Never throws for a PO that is
    /// not there — the items call is the one that decides whether the PO could be read.</summary>
    public async Task<SapPurchaseOrderHead?> HeadAsync(string purchaseOrder)
    {
        var po = (purchaseOrder ?? "").Trim();
        if (!Configured || po.Length == 0) return null;

        var url = $"{config.SapPurchaseOrderBaseUrl.TrimEnd('/')}/A_PurchaseOrder" +
                  $"?$filter={Uri.EscapeDataString($"PurchaseOrder eq '{EscapeODataLiteral(po)}'")}" +
                  "&$top=1&$format=json";
        try
        {
            var el = EnumerateResults(await GetJsonAsync(url)).FirstOrDefault();
            if (el.ValueKind != JsonValueKind.Object) return null;
            return new SapPurchaseOrderHead(
                GetString(el, "PurchaseOrder") ?? po,
                GetString(el, "Supplier") ?? "",
                GetString(el, "SupplierName") ?? "",
                GetString(el, "CompanyCode") ?? "",
                GetString(el, "PurchasingOrganization") ?? "",
                GetString(el, "DocumentCurrency") ?? "");
        }
        catch
        {
            // The header is a cross-check, not the data the invoice needs. If the service does
            // not expose it, the items still came through and the screen keeps working.
            return null;
        }
    }

    private static int ItemNo(string item) => int.TryParse(item, out var n) ? n : int.MaxValue;

    private async Task<string> GetJsonAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var resp = await httpClient.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"SAP purchase-order lookup failed: {(int)resp.StatusCode} {Trunc(text, 500)}");

        return text;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(config.SapPurchaseOrderAuthHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", config.SapPurchaseOrderAuthHeader);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
    }

    // A single quote inside an OData literal is doubled; a PO number never has one, but the
    // escape stays so a stray character cannot change the filter.
    private static string EscapeODataLiteral(string value) => value.Replace("'", "''");

    private static IEnumerable<JsonElement> EnumerateResults(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement.Clone();

        JsonElement results = default;
        var found = false;
        if (root.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var r)) { results = r; found = true; }
        else if (root.TryGetProperty("value", out var v)) { results = v; found = true; }
        if (!found || results.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in results.EnumerateArray()) yield return item;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // OData V2 sends decimals as strings ("25.000"); V4 sends numbers. Both appear here.
    private static decimal GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
            _ => 0,
        };
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
