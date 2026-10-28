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
    string TaxCode,
    IReadOnlyList<SapPoCondition> Conditions,
    // The rate this item was priced at — PriceDetnExchangeRate, 33.50860 on PO 2230000031. SAP
    // uses it for an invoice that names this PO, because it is the rate the goods receipt
    // accrued at, so it is the figure the screen shows before anything is posted.
    decimal ExchangeRate);

/// <summary>One pricing element of a purchase order item, from to_PurchaseOrderPricingElement.
///
/// This is where the planned delivery costs live, and an invoice that settles them has to name
/// the SAME condition the purchase order accrued under: the goods receipt credited the accrual
/// per condition, and naming another one leaves it uncleared. The amounts also say what each
/// charge is worth, which is how a cost line on the shipping form finds its condition without
/// anyone opening the purchase order.
///
/// RateValue is what was typed into the PO, in RateCurrency (13,751.73 THB for ZLH1); Amount is
/// the same figure in the purchase order's own currency (410.39 USD).</summary>
public record SapPoCondition(
    string Item,
    string ConditionType,
    decimal RateValue,
    string RateCurrency,
    decimal Amount,
    string AmountCurrency,
    bool IsGroupCondition,
    bool IsForAccrual);

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
        // The conditions come along with the items in one call: a second round trip per item
        // would be eleven more requests for a purchase order this size.
        var url = $"{config.SapPurchaseOrderBaseUrl.TrimEnd('/')}/A_PurchaseOrderItem" +
                  $"?$filter={Uri.EscapeDataString(filter)}&$expand=to_PurchaseOrderPricingElement&$top=200&$format=json";

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
                GetString(el, "TaxCode") ?? "",
                Conditions(el),
                ExchangeRateOf(el)))
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

    /// <summary>The conditions expanded onto one item. The base price (PMP0) and the cash
    /// discount placeholder (DCD1) are dropped: neither is a charge an invoice settles, and
    /// leaving them in would offer "PMP0" in the condition picker beside the real ones.</summary>
    private static List<SapPoCondition> Conditions(JsonElement item)
    {
        var result = new List<SapPoCondition>();
        if (!item.TryGetProperty("to_PurchaseOrderPricingElement", out var node)) return result;
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("results", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var el in arr.EnumerateArray())
        {
            var type = (GetString(el, "ConditionType") ?? "").Trim().ToUpperInvariant();
            if (type.Length == 0 || type is "PMP0" or "DCD1") continue;
            result.Add(new SapPoCondition(
                GetString(el, "PurchaseOrderItem") ?? "",
                type,
                GetDecimal(el, "ConditionRateValue"),
                GetString(el, "ConditionCurrency") ?? "",
                GetDecimal(el, "ConditionAmount"),
                GetString(el, "TransactionCurrency") ?? "",
                IsFlag(el, "IsGroupCondition"),
                IsFlag(el, "IsRelevantForAccrual")));
        }
        return result;
    }

    /// <summary>The rate SAP priced this item at, read straight off PriceDetnExchangeRate.
    ///
    /// Read over ALL the pricing rows, because the row that carries it is the base price PMP0 —
    /// which Conditions() drops as not being a charge an invoice settles. The delivery-cost rows
    /// that survive carry 1.00000, since they were entered in THB and need no translation;
    /// taking the rate from those would report 1.00000 and quietly say the invoice needs no
    /// conversion at all. So 0 and 1 both count as "nothing said".</summary>
    private static decimal ExchangeRateOf(JsonElement item)
    {
        if (!item.TryGetProperty("to_PurchaseOrderPricingElement", out var node)) return 0m;
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("results", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return 0m;
        foreach (var el in arr.EnumerateArray())
        {
            var r = GetDecimal(el, "PriceDetnExchangeRate");
            if (r > 1m) return r;
        }
        return 0m;
    }

    /// <summary>The purchase order's rate, from the first item that states one.</summary>
    public static decimal RateOf(IEnumerable<SapPurchaseOrderItem> items)
    {
        foreach (var i in items) if (i.ExchangeRate > 0) return i.ExchangeRate;
        return 0m;
    }

    /// <summary>SAP writes its booleans both ways here: "X" on the group-condition flag, a JSON
    /// true on the accrual one.</summary>
    private static bool IsFlag(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String
                && (v.GetString() ?? "").Trim() is "X" or "true" or "True"));

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
