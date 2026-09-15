using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

public record ZohoDealItem(
    string? MaterialName,
    string? MaterialId,
    string? MaterialCode,
    string? MaterialDescription,
    string? MaterialGroup,
    decimal? Quantity,
    string? Unit,
    decimal? ConversionRatio,
    string? SubUnit,
    decimal? UnitPrice,
    decimal? PriceSubUnit,
    decimal? LastPrice,
    decimal? TotalAmount,
    string? ShipVia,
    string? Stock,
    string? LeadTimeDays,
    decimal? SoCompleted,
    decimal? SoCancelled);

public record ZohoDeal(
    string Id,
    string DealName,
    string Stage,
    string? AccountId,
    string? AccountCode,
    string? CustomerRef,
    string? DeliveryDate,
    string? ClosingDate,
    List<ZohoDealItem> Items);

/// <summary>
/// Zoho CRM "Deals" module — read-only search used by the Deal-matching step of the OCR→Zoho
/// Sales Order flow (Megachem's decision: find the Deal a document belongs to, then build the
/// Sales Order from the DOCUMENT's own numbers, never the Deal's original forecast numbers).
///
/// Field names (Account_Name, Account_Code, Customer_Ref, Delivery_Date, Closing_Date, Stage,
/// and the Deal Items subform's Material_Name/Material_Code/Description_1/Material_Group/
/// Quantity/Unit/Conversion_Ratio/Sub_Unit/Unit_Price/Price_Sub_Unit/Last_Price/Total_Amount/
/// Ship_via/Stock/L_T_Days/SO_Completed/SO_Cancelled) are all confirmed against Megachem's Zoho
/// CRM user manual (AO-CRM-UM-2026-003, section 5.6.9, pages 166-167) — nothing here is guessed.
/// Deliberately excludes the subform's Select/Application/Potential/Status_Note/Next_Step columns
/// (internal MGT planning bookkeeping per the manual, not needed to build a Sales Order).
/// The module's own api_name is
/// NOT assumed to be "Deals" even though that's Zoho's usual standard module — resolved once via
/// ZohoClient.GetModulesAsync() and cached (same pattern as ZohoAccountClient's Ship-to
/// related-module discovery), falling back to the standard name only if that lookup comes up
/// empty (e.g. a permissions gap on settings.modules).
/// </summary>
public class ZohoDealClient(ZohoClient zoho)
{
    // Verbatim from the manual's Deal Stage picklist (5.6.4.6 - 5.6.4.11) — every stage that
    // means the Deal is closed (won or lost). Anything else (Draft/Qualification, Meeting/Memo,
    // Sample & Result, Sales Order Created, Sales Order Updated, ...) counts as still "open".
    private static readonly HashSet<string> ClosedStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Closed Won - Exact Match",
        "Closed Won - Over Forecast",
        "Closed Won - Under Forecast",
        "Closed Lost",
        "Closed Lost to Competition",
        "Close Lost - Expiry Date",
    };

    private string? _module;

    private async Task<string> ResolveModuleAsync()
    {
        if (_module is not null) return _module;
        var modules = await zoho.GetModulesAsync();
        foreach (var node in modules)
        {
            if (node is not JsonObject obj) continue;
            var plural = Str(obj, "plural_label") ?? "";
            var singular = Str(obj, "singular_label") ?? "";
            if (string.Equals(plural, "Deals", StringComparison.OrdinalIgnoreCase)
                || string.Equals(singular, "Deal", StringComparison.OrdinalIgnoreCase))
            {
                var api = Str(obj, "api_name");
                if (!string.IsNullOrEmpty(api)) { _module = api; return _module; }
            }
        }
        _module = "Deals"; // Zoho's own standard module api_name, used only as a last resort
        return _module;
    }

    /// <summary>
    /// Every OPEN (non-Closed-stage) Deal linked to this Account, each with its Deal Items
    /// subform. Deliberately not narrowed down to "the" best match — Megachem wants every
    /// candidate listed so the person (optionally helped by the OCR AI-compare tool) picks.
    ///
    /// Matches by the Deal's own "Account Code" field (API name Account_Code) rather than Zoho's
    /// internal Account record id. Confirmed against the manual (section 5.6, pages 123/163):
    /// Account_Code is a plain Single Line custom field that Zoho auto-fills from
    /// "Account Name > Account Code" when the Deal's Account is set — i.e. it carries the SAME
    /// customer code already stored locally as CustomerCode, so this needs no Zoho record id
    /// lookup/link at all (unlike the Ship-to/Sold-to address panels, which do need a real Zoho
    /// Account id and can be broken by a Master Customer row whose linked id is stale/wrong).
    /// </summary>
    public async Task<List<ZohoDeal>> FindOpenDealsByAccountCodeAsync(string accountCode)
    {
        var module = await ResolveModuleAsync();
        var rows = await zoho.QueryCoqlAsync(
            module,
            "id, Deal_Name, Stage, Account_Code, Customer_Ref, Delivery_Date, Closing_Date",
            $"Account_Code = '{Escape(accountCode)}'",
            "Deal_Name");

        var result = new List<ZohoDeal>();
        foreach (var row in rows)
        {
            var stage = Str(row, "Stage") ?? "";
            if (ClosedStages.Contains(stage)) continue;
            var id = Str(row, "id");
            if (string.IsNullOrEmpty(id)) continue;

            // COQL cannot return subform data at all (a hard Zoho limitation, not a query
            // mistake) — the Deal Items list only comes back on a per-record GET.
            var full = await zoho.GetByIdAsync(module, id);
            var items = full is null ? [] : ExtractDealItems(full);

            result.Add(new ZohoDeal(
                Id: id,
                DealName: Str(row, "Deal_Name") ?? "",
                Stage: stage,
                AccountId: full is null ? null : LookupId(full["Account_Name"]),
                AccountCode: Str(row, "Account_Code"),
                CustomerRef: Str(row, "Customer_Ref"),
                DeliveryDate: Str(row, "Delivery_Date"),
                ClosingDate: Str(row, "Closing_Date"),
                Items: items));
        }
        return result;
    }

    /// <summary>
    /// One Deal by its Zoho record id, with its Account link and Deal Items subform -- used by
    /// the Zoho Sales Order creation flow, which already knows exactly which Deal the person
    /// picked (from the Deal-comparison card) and doesn't need the open/by-account-code search
    /// above. Returns null if the id doesn't resolve to a record (deleted, wrong module, ...).
    /// </summary>
    public async Task<ZohoDeal?> GetByIdAsync(string dealId)
    {
        var module = await ResolveModuleAsync();
        var full = await zoho.GetByIdAsync(module, dealId);
        if (full is null) return null;
        return new ZohoDeal(
            Id: dealId,
            DealName: Str(full, "Deal_Name") ?? "",
            Stage: Str(full, "Stage") ?? "",
            AccountId: LookupId(full["Account_Name"]),
            AccountCode: Str(full, "Account_Code"),
            CustomerRef: Str(full, "Customer_Ref"),
            DeliveryDate: Str(full, "Delivery_Date"),
            ClosingDate: Str(full, "Closing_Date"),
            Items: ExtractDealItems(full));
    }

    // The Deal Items subform's own container field is never given an "API Name" in the manual's
    // field table (only its individual columns are, since it's a custom subform with no
    // standard Zoho equivalent to fall back on) — so rather than guess the container's key,
    // find it structurally: the one array-of-object field on the record whose entries carry
    // subform columns we DO have confirmed (Material_Name / Quantity).
    internal static List<ZohoDealItem> ExtractDealItems(JsonObject deal)
    {
        foreach (var kv in deal)
        {
            if (kv.Value is not JsonArray arr || arr.Count == 0) continue;
            if (arr[0] is not JsonObject first) continue;
            if (!first.ContainsKey("Material_Name") && !first.ContainsKey("Quantity")) continue;

            var items = new List<ZohoDealItem>();
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                items.Add(new ZohoDealItem(
                    MaterialName: LookupName(o["Material_Name"]),
                    MaterialId: LookupId(o["Material_Name"]),
                    MaterialCode: Str(o, "Material_Code"),
                    MaterialDescription: Str(o, "Description_1"),
                    MaterialGroup: Str(o, "Material_Group"),
                    Quantity: Dec(o, "Quantity"),
                    Unit: Str(o, "Unit"),
                    ConversionRatio: Dec(o, "Conversion_Ratio"),
                    SubUnit: Str(o, "Sub_Unit"),
                    UnitPrice: Dec(o, "Unit_Price"),
                    PriceSubUnit: Dec(o, "Price_Sub_Unit"),
                    LastPrice: Dec(o, "Last_Price"),
                    TotalAmount: Dec(o, "Total_Amount"),
                    ShipVia: Str(o, "Ship_via"),
                    Stock: Str(o, "Stock"),
                    LeadTimeDays: Str(o, "L_T_Days"),
                    SoCompleted: Dec(o, "SO_Completed"),
                    SoCancelled: Dec(o, "SO_Cancelled")));
            }
            return items;
        }
        return [];
    }

    internal static string? LookupName(JsonNode? n) =>
        n is JsonObject o && o["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string? LookupId(JsonNode? n) =>
        n is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string? Str(JsonObject row, string field) =>
        row[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static decimal? Dec(JsonObject row, string field) =>
        row[field] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    internal static string Escape(string s) => s.Replace("'", "''");
}
