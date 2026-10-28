using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

public record ZohoQuoteItem(string? MaterialId, string? MaterialName, string? MaterialCode, decimal? Quantity, decimal? UnitPrice);

public record ZohoQuote(string Id, string? QuoteNo, string? Subject, string? Stage, string? CustomerRef, List<ZohoQuoteItem> Items);

/// <summary>
/// Zoho CRM "Quotes" module (27 Oct 2026) -- the middle step of Megachem's Zoho chain
/// Deal -> Quote -> Sales Order: the OCR flow finds the Deal's Quote for a document, or creates
/// one when there is none, and links the Sales Order to it (Quote_Name / Quote_No1).
///
/// Field names from the manual (AO-CRM-UM-2026-003, section 5.8.7, pages 249-256): Quote_No
/// (auto-number "Quote No.", e.g. 1QT0000001), Subject* (50), Deal_Name (Lookup), Quote_Stage
/// (Open / In Process / Completed), Account_Name* (Lookup), Account_Code, Tax_ID, Customer_Ref,
/// Payment_Terms, Payment_Currency, Incoterms, Valid_Till ("Create Date"), Delivery_Date*, the
/// same Select Address / Address Information block as Sales Orders, and the "Quoted Items"
/// subform (Product_Name*, Description, Material_Code, Material_Group, Ship_via, Stock, L_T_Days,
/// Quantity, Unit_Price, Unit, Conversion_Ratio, Sub_Unit). A Quote the OCR flow creates starts at
/// Quote_Stage "Open" (Megachem's choice, 27 Oct 2026 -- same as Zoho's own Convert to Quote).
/// </summary>
public class ZohoQuoteClient(ZohoClient zoho)
{
    public const string NewQuoteStage = "Open";

    private string? _module;
    private string? _itemsField;
    private (string? DataType, string? LookupModule)? _shipToNameMeta;

    private async Task<string> ResolveModuleAsync() =>
        _module ??= await ZohoRecordFields.ResolveModuleAsync(zoho, "Quotes", "Quote", "Quotes");

    /// <summary>Every Quote linked to this Deal, newest first, each with its Quoted Items (COQL
    /// cannot return subforms, so each Quote is read once more by id).</summary>
    public async Task<List<ZohoQuote>> FindByDealAsync(string dealId)
    {
        if (string.IsNullOrWhiteSpace(dealId)) return [];
        var module = await ResolveModuleAsync();
        var rows = await zoho.QueryCoqlAsync(module, "id, Quote_No, Subject, Quote_Stage, Customer_Ref",
            $"Deal_Name = '{dealId.Replace("'", "''")}'", "Created_Time desc");
        var list = new List<ZohoQuote>();
        foreach (var row in rows.Take(30))
        {
            var id = ZohoRecordFields.Str(row, "id");
            if (string.IsNullOrEmpty(id)) continue;
            var full = await zoho.GetByIdAsync(module, id);
            list.Add(new ZohoQuote(id, ZohoRecordFields.Str(row, "Quote_No"), ZohoRecordFields.Str(row, "Subject"),
                ZohoRecordFields.Str(row, "Quote_Stage"), ZohoRecordFields.Str(row, "Customer_Ref"),
                full is null ? [] : ExtractItems(full)));
        }
        return list;
    }

    /// <summary>One Quote by its Quote No. (e.g. 1QT0000266), with the id of its Deal -- a customer PO
    /// can quote Megachem's own Quote No. (27 Oct 2026). Null when no single Quote has that number.</summary>
    public async Task<(string Id, string? QuoteNo, string? Stage, string? DealId)?> FindByNoAsync(string quoteNo)
    {
        var no = (quoteNo ?? "").Trim();
        if (no.Length == 0) return null;
        var module = await ResolveModuleAsync();
        var rows = await zoho.QueryCoqlAsync(module, "id, Quote_No, Quote_Stage, Deal_Name",
            $"Quote_No = '{no.Replace("'", "''")}'", "Quote_No");
        var hits = rows.Where(r => string.Equals((ZohoRecordFields.Str(r, "Quote_No") ?? "").Trim(), no, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count != 1) return null;
        var id = ZohoRecordFields.Str(hits[0], "id");
        if (string.IsNullOrEmpty(id)) return null;
        return (id, ZohoRecordFields.Str(hits[0], "Quote_No"), ZohoRecordFields.Str(hits[0], "Quote_Stage"),
            ZohoDealClient.LookupId(hits[0]["Deal_Name"]));
    }

    /// <summary>Megachem Quote numbers mentioned on a document: its quoteRef field plus anything in
    /// its header / line text shaped like a Zoho Quote No. ("1QT0000266"). Upper-cased, distinct.</summary>
    public static List<string> QuoteRefsIn(IEnumerable<string?> texts, string? quoteRef)
    {
        var found = new List<string>();
        foreach (var t in texts.Prepend(quoteRef))
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            foreach (System.Text.RegularExpressions.Match m in QuoteNoPattern.Matches(t))
                found.Add(m.Value.ToUpperInvariant());
        }
        var r = (quoteRef ?? "").Trim();
        if (found.Count == 0 && r.Length > 0) found.Add(r.ToUpperInvariant());
        return found.Distinct().ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex QuoteNoPattern =
        new(@"\b\d?QT\d{6,8}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The Quote No. (auto-number) of a Quote, read back after creating it.</summary>
    public async Task<string?> QuoteNoAsync(string id)
    {
        var rec = await zoho.GetByIdAsync(await ResolveModuleAsync(), id);
        return rec?["Quote_No"]?.ToString().Trim() is { Length: > 0 } no ? no : null;
    }

    public async Task<JsonObject> BuildRecordAsync(
        string? dealId, string accountId, string subject, string? accountCode, string? taxId, string? customerRef,
        string? deliveryDate, string? paymentTerms, string? paymentCurrency, string? incoterms,
        ZohoShipToInfo? shipTo, List<ZohoSalesOrderLine> items)
    {
        var module = await ResolveModuleAsync();
        _itemsField ??= await ZohoRecordFields.ResolveSubformFieldAsync(zoho, module, "Quoted Items")
            ?? throw new Exception("Could not resolve the Quoted Items subform field on the Quotes module (settings/fields returned no subform field)");
        _shipToNameMeta ??= await ZohoRecordFields.ResolveFieldMetaAsync(zoho, module, "Ship_to_Name");

        var record = new JsonObject
        {
            ["Subject"] = subject.Length > 50 ? subject[..50] : subject,
            ["Account_Name"] = new JsonObject { ["id"] = accountId },
            ["Quote_Stage"] = NewQuoteStage,
            // "Create Date" on the Quote (API name Valid_Till, per the manual).
            ["Valid_Till"] = DateTime.Today.ToString("yyyy-MM-dd"),
        };
        if (!string.IsNullOrWhiteSpace(dealId)) record["Deal_Name"] = new JsonObject { ["id"] = dealId };
        ZohoRecordFields.ApplyHeader(record, accountCode, taxId, customerRef, deliveryDate, paymentTerms, paymentCurrency, incoterms);
        ZohoRecordFields.ApplyShipTo(record, shipTo, _shipToNameMeta.Value);
        var rows = new JsonArray();
        foreach (var it in items) rows.Add(ZohoRecordFields.ItemRow(it, "Unit"));
        record[_itemsField] = rows;
        return record;
    }

    public async Task<ZohoUpsertResult> CreateAsync(JsonObject record) =>
        await zoho.InsertAsync(await ResolveModuleAsync(), record);

    // The Quoted Items subform, found structurally (the array-of-object field whose rows carry
    // Product_Name), like ZohoDealClient.ExtractDealItems.
    internal static List<ZohoQuoteItem> ExtractItems(JsonObject quote)
    {
        foreach (var kv in quote)
        {
            if (kv.Value is not JsonArray arr || arr.Count == 0 || arr[0] is not JsonObject first) continue;
            if (!first.ContainsKey("Product_Name")) continue;
            var items = new List<ZohoQuoteItem>();
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                items.Add(new ZohoQuoteItem(
                    ZohoDealClient.LookupId(o["Product_Name"]), ZohoDealClient.LookupName(o["Product_Name"]),
                    ZohoDealClient.Str(o, "Material_Code"), ZohoDealClient.Dec(o, "Quantity"), ZohoDealClient.Dec(o, "Unit_Price")));
            }
            return items;
        }
        return [];
    }
}
