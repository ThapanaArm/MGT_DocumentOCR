using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

/// <summary>
/// One line to write onto the Sales Order's "Ordered Items" subform. Quantity/UnitPrice/Unit are
/// deliberately sourced from the DOCUMENT's own numbers by the caller (never the Deal's original
/// forecast numbers -- same rule ZohoDealClient documents), while MaterialId/MaterialGroup/
/// ShipVia/Stock/LeadTimeDays/ConversionRatio/SubUnit are reference/master-data-shaped values the
/// caller reuses from the matched Deal Item (there is no separate Zoho Material search feature,
/// so MaterialId -- required for the Product_Name lookup -- can only come from a matched Deal Item).
/// </summary>
public record ZohoSalesOrderLine(
    string MaterialId,
    string? Description,
    string? MaterialCode,
    string? MaterialGroup,
    string? ShipVia,
    string? Stock,
    string? LeadTimeDays,
    decimal Quantity,
    decimal? UnitPrice,
    string? Unit,
    decimal? ConversionRatio,
    string? SubUnit);

/// <summary>
/// Zoho CRM "Sales Orders" module — write-only client used by the OCR→Zoho Sales Order creation
/// flow (see DocumentsController's SAP "/post" for the SAP-side equivalent, which this
/// deliberately never touches or shares code with).
///
/// Header field names (Subject, Deal_Name, Account_Name, Account_Code, Tax_ID, Customer_Ref,
/// Delivery_Date, Payment_Terms, Payment_Currency, Incoterms) and the Ordered Items subform's
/// column names (Product_Name, Description, Material_Code, Material_Group, Ship_via, Stock,
/// L_T_Days, Quantity, Unit_Price, Unit1, Conversion_Ratio, Sub_Unit) are all confirmed against
/// Megachem's Zoho CRM user manual (AO-CRM-UM-2026-003, section 5.9.6, pages 295-303) — nothing
/// here is guessed. Deliberately excludes: Formula/computed fields (Q_ty_Kgs, Price_Sub_Unit,
/// Amount, Sub_Total, Vat_Amount, Grand_Total -- Zoho computes these, sending them is pointless
/// and Amount/Q_ty_Kgs/Price_Sub_Unit would be rejected as read-only); "ใช้สำหรับจัดเก็บข้อมูลตาม
/// Business Requirement ของ MGT" placeholder fields with no clear document-side source
/// (Distribution, Customer_No, D_O_NO, Billing_NO, P_O_No, Purchaser, Tax_Classification,
/// Potential, Status_Note, Next_Step, Remaining_Balance, Outstanding_Amount, Credit_Limited); and
/// Select_Address (a layout control rather than address data). Ship_to_Name and the confirmed
/// Ship_to_* address fields are populated from the address explicitly selected by the person.
///
/// The subform container's own api_name is never given in the manual (only its columns are), so
/// it's resolved once via ZohoClient.GetFieldsAsync and cached, mirroring ZohoDealClient's
/// module-resolution pattern rather than guessing a container key.
/// </summary>
public class ZohoSalesOrderClient(ZohoClient zoho)
{
    private string? _module;
    private string? _orderedItemsField;

    private async Task<string> ResolveModuleAsync()
    {
        if (_module is not null) return _module;
        var modules = await zoho.GetModulesAsync();
        foreach (var node in modules)
        {
            if (node is not JsonObject obj) continue;
            var plural = Str(obj, "plural_label") ?? "";
            var singular = Str(obj, "singular_label") ?? "";
            if (string.Equals(plural, "Sales Orders", StringComparison.OrdinalIgnoreCase)
                || string.Equals(singular, "Sales Order", StringComparison.OrdinalIgnoreCase))
            {
                var api = Str(obj, "api_name");
                if (!string.IsNullOrEmpty(api)) { _module = api; return _module; }
            }
        }
        _module = "Sales_Orders"; // Zoho's own standard module api_name, used only as a last resort
        return _module;
    }

    private async Task<string> ResolveOrderedItemsFieldAsync()
    {
        if (_orderedItemsField is not null) return _orderedItemsField;
        var module = await ResolveModuleAsync();
        var fields = await zoho.GetFieldsAsync(module);

        // Prefer the subform field explicitly labelled "Ordered Items" (the manual's own name for
        // this subform); fall back to whichever subform field is on the module at all, since a
        // Sales Order layout only has the one subform.
        JsonObject? fallback = null;
        foreach (var node in fields)
        {
            if (node is not JsonObject f) continue;
            if (!string.Equals(Str(f, "data_type"), "subform", StringComparison.OrdinalIgnoreCase)) continue;
            fallback ??= f;
            var label = Str(f, "field_label") ?? "";
            if (label.Contains("Ordered Items", StringComparison.OrdinalIgnoreCase))
            {
                var api = Str(f, "api_name");
                if (!string.IsNullOrEmpty(api)) { _orderedItemsField = api; return _orderedItemsField; }
            }
        }
        var fallbackApi = fallback is null ? null : Str(fallback, "api_name");
        if (!string.IsNullOrEmpty(fallbackApi)) { _orderedItemsField = fallbackApi; return _orderedItemsField; }

        throw new Exception(
            "Could not resolve the Ordered Items subform field on the Sales Orders module " +
            "(settings/fields returned no subform field) — check the Zoho CRM connection's " +
            "permission on settings.fields, or that the Sales Orders layout still has this subform.");
    }

    /// <summary>
    /// Live picklist values for one field on the Sales Orders module (e.g. Payment_Terms,
    /// Payment_Currency) -- read straight from Zoho's own field metadata instead of transcribing
    /// the manual's option list by hand, so the dropdown the person edits before sending always
    /// matches whatever Megachem's Zoho admin has configured right now (the manual's own list can
    /// drift out of date; this can't). Returns each option's actual_value -- the exact string
    /// Zoho expects back on write -- falling back to display_value only if actual_value is absent.
    /// Empty list if the field isn't found or isn't a picklist.
    /// </summary>
    public async Task<List<string>> GetPicklistOptionsAsync(string fieldApiName)
    {
        var module = await ResolveModuleAsync();
        var fields = await zoho.GetFieldsAsync(module);
        foreach (var node in fields)
        {
            if (node is not JsonObject f) continue;
            if (!string.Equals(Str(f, "api_name"), fieldApiName, StringComparison.OrdinalIgnoreCase)) continue;
            if (f["pick_list_values"] is not JsonArray plv) return [];
            var result = new List<string>();
            foreach (var v in plv)
            {
                if (v is not JsonObject o) continue;
                var value = Str(o, "actual_value") ?? Str(o, "display_value");
                if (!string.IsNullOrEmpty(value)) result.Add(value);
            }
            return result;
        }
        return [];
    }

    /// <summary>
    /// Creates one Zoho "Sales Orders" record. dealId/accountId are required (the Sales Order's
    /// own Deal_Name/Account_Name Lookups); every other header value is written only when the
    /// caller actually has it (Zoho leaves an omitted field blank rather than erroring on a
    /// missing optional value). Throws only for module/subform resolution failures — a Zoho
    /// validation error (e.g. INVALID_DATA) comes back inside the returned ZohoUpsertResult.
    /// </summary>
    public async Task<ZohoUpsertResult> CreateAsync(
        string dealId,
        string accountId,
        string subject,
        string? accountCode,
        string? taxId,
        string? customerRef,
        string? deliveryDate,
        string? paymentTerms,
        string? paymentCurrency,
        string? incoterms,
        ZohoShipToInfo? shipTo,
        List<ZohoSalesOrderLine> items)
    {
        var module = await ResolveModuleAsync();
        var record = await BuildRecordAsync(
            dealId, accountId, subject, accountCode, taxId, customerRef, deliveryDate,
            paymentTerms, paymentCurrency, incoterms, shipTo, items);
        return await zoho.InsertAsync(module, record);
    }

    /// <summary>
    /// Builds the exact Zoho "Sales Orders" record JSON that <see cref="CreateAsync"/> would POST,
    /// without sending it -- used by the "View Payload" preview so a person can see precisely what
    /// will be created in Zoho CRM before pressing Send, exactly as the SAP side's View Payload does.
    /// </summary>
    public async Task<JsonObject> BuildRecordAsync(
        string dealId,
        string accountId,
        string subject,
        string? accountCode,
        string? taxId,
        string? customerRef,
        string? deliveryDate,
        string? paymentTerms,
        string? paymentCurrency,
        string? incoterms,
        ZohoShipToInfo? shipTo,
        List<ZohoSalesOrderLine> items)
    {
        var itemsField = await ResolveOrderedItemsFieldAsync();

        var record = new JsonObject
        {
            ["Subject"] = subject,
            ["Deal_Name"] = new JsonObject { ["id"] = dealId },
            ["Account_Name"] = new JsonObject { ["id"] = accountId },
        };
        if (!string.IsNullOrWhiteSpace(accountCode)) record["Account_Code"] = accountCode;
        if (!string.IsNullOrWhiteSpace(taxId)) record["Tax_ID"] = taxId;
        if (!string.IsNullOrWhiteSpace(customerRef)) record["Customer_Ref"] = customerRef;
        if (!string.IsNullOrWhiteSpace(deliveryDate)) record["Delivery_Date"] = deliveryDate;
        if (!string.IsNullOrWhiteSpace(paymentTerms)) record["Payment_Terms"] = paymentTerms;
        if (!string.IsNullOrWhiteSpace(paymentCurrency)) record["Payment_Currency"] = paymentCurrency;
        if (!string.IsNullOrWhiteSpace(incoterms)) record["Incoterms"] = incoterms;

        if (shipTo is not null)
        {
            Put(record, "Ship_to_Code", shipTo.Code);
            // Megachem's Ship_to_Name field expects the selected numeric ShipToCode, not the
            // delivery-address text or the Ship-to module record id. Zoho's field metadata says
            // this field's data_type is "bigint" -- it must be a JSON number, not a quoted string,
            // or the whole record is rejected with INVALID_DATA "expected_data_type":"bigint".
            PutBigInt(record, "Ship_to_Name", shipTo.Code);
            Put(record, "Ship_to_Street", shipTo.Street ?? shipTo.Address);
            Put(record, "Ship_to_Street_2", shipTo.Street2);
            Put(record, "Ship_to_Street_3", shipTo.Street3);
            Put(record, "Ship_to_Street_4", shipTo.Street4);
            Put(record, "Ship_to_Street_5", shipTo.Street5);
            Put(record, "Ship_to_House_Number", shipTo.HouseNumber);
            Put(record, "Ship_to_District", shipTo.District);
            Put(record, "Ship_to_City", shipTo.City);
            Put(record, "Ship_to_Difference_City", shipTo.DifferenceCity);
            Put(record, "Ship_to_Post_Code", shipTo.PostCode);
            Put(record, "Ship_to_Country_Reg", shipTo.CountryReg);
        }

        var rows = new JsonArray();
        foreach (var it in items)
        {
            var row = new JsonObject
            {
                ["Product_Name"] = new JsonObject { ["id"] = it.MaterialId },
                ["Quantity"] = it.Quantity,
            };
            if (!string.IsNullOrWhiteSpace(it.Description)) row["Description"] = it.Description;
            if (!string.IsNullOrWhiteSpace(it.MaterialCode)) row["Material_Code"] = it.MaterialCode;
            if (!string.IsNullOrWhiteSpace(it.MaterialGroup)) row["Material_Group"] = it.MaterialGroup;
            if (!string.IsNullOrWhiteSpace(it.ShipVia)) row["Ship_via"] = it.ShipVia;
            if (!string.IsNullOrWhiteSpace(it.Stock)) row["Stock"] = it.Stock;
            if (!string.IsNullOrWhiteSpace(it.LeadTimeDays)) row["L_T_Days"] = it.LeadTimeDays;
            if (it.UnitPrice is decimal up) row["Unit_Price"] = up;
            if (!string.IsNullOrWhiteSpace(it.Unit)) row["Unit1"] = it.Unit;
            if (it.ConversionRatio is decimal cr) row["Conversion_Ratio"] = cr;
            if (!string.IsNullOrWhiteSpace(it.SubUnit)) row["Sub_Unit"] = it.SubUnit;
            rows.Add(row);
        }
        record[itemsField] = rows;
        return record;
    }

    private static string? Str(JsonObject o, string field) =>
        o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static void Put(JsonObject record, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) record[field] = value;
    }

    // For fields Zoho's own metadata reports as numeric (e.g. Ship_to_Name's data_type "bigint").
    // Writing a quoted string to one of these fails with INVALID_DATA "expected_data_type":
    // "bigint" -- it must be sent as a bare JSON number. Silently omits the field if the value
    // isn't a whole number (same "leave it out rather than send something invalid" behavior as
    // Put), since guessing at a numeric coercion would just trade one Zoho error for another.
    private static void PutBigInt(JsonObject record, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && long.TryParse(value, out var n)) record[field] = n;
    }
}
