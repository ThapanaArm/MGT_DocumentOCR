using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

/// <summary>
/// Field-building pieces shared by the Zoho "Quotes" and "Sales Orders" writers (27 Oct 2026).
/// Both modules carry the same header block (Account_Code, Tax_ID, Customer_Ref, Delivery_Date,
/// Payment_Terms, Payment_Currency, Incoterms), the same "Select Address" / "Address Information"
/// block (Select_Address, Ship_to_Name, Address_Code, Street, Street_2..5, House_Number, District,
/// City, Difference_City, Post_Code, Country_Reg) and the same item subform columns
/// (Product_Name, Description, Material_Code, Material_Group, Ship_via, Stock, L_T_Days, Quantity,
/// Unit_Price, Unit/Unit1, Conversion_Ratio, Sub_Unit) -- Megachem's Zoho CRM manual
/// AO-CRM-UM-2026-003, sections 5.8.7 (Quotes, pages 249-256) and 5.9.6 (Sales Orders, pages
/// 295-303). The logic below is the same as ZohoSalesOrderClient.BuildRecordAsync (see that class's
/// header comment for the history of each field) so the Quote built from a document is filled
/// exactly like the Sales Order. The Sales Order writer itself was left as it was.
/// </summary>
public static class ZohoRecordFields
{
    public static void ApplyHeader(JsonObject record, string? accountCode, string? taxId, string? customerRef,
        string? deliveryDate, string? paymentTerms, string? paymentCurrency, string? incoterms)
    {
        Put(record, "Account_Code", accountCode);
        Put(record, "Tax_ID", taxId);
        Put(record, "Customer_Ref", customerRef);
        Put(record, "Delivery_Date", deliveryDate);
        Put(record, "Payment_Terms", paymentTerms);
        Put(record, "Payment_Currency", paymentCurrency);
        Put(record, "Incoterms", incoterms);
    }

    /// <summary>
    /// Select_Address + Ship_to_Name + the module's own unprefixed Address Information fields.
    /// <paramref name="shipToNameMeta"/> is the live data_type / lookup module of Ship_to_Name on
    /// the target module (settings/fields), exactly as ZohoSalesOrderClient used it before.
    /// </summary>
    public static void ApplyShipTo(JsonObject record, ZohoShipToInfo? shipTo, (string? DataType, string? LookupModule) shipToNameMeta)
    {
        if (shipTo is null) return;
        // "account" (the Account's own inline Ship-to fields) -> "Ship-to"; "shipto-module" (a record
        // of the separate Ship-to Module) -> "Other Ship-to"; anything else -> the common "Ship-to".
        Put(record, "Select_Address", shipTo.Source switch
        {
            "account" => "Ship-to",
            "shipto-module" => "Other Ship-to",
            _ => "Ship-to",
        });
        var (shipToNameType, shipToNameLookupModule) = shipToNameMeta;
        if (shipTo.Source == "shipto-module" && !string.IsNullOrWhiteSpace(shipTo.RecordId)
            && string.Equals(shipToNameType, "lookup", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(shipToNameLookupModule)
                || string.Equals(shipToNameLookupModule, "Ship-to", StringComparison.OrdinalIgnoreCase))
                record["Ship_to_Name"] = new JsonObject { ["id"] = shipTo.RecordId };
        }
        else if (!string.Equals(shipToNameType, "lookup", StringComparison.OrdinalIgnoreCase))
        {
            PutBigInt(record, "Ship_to_Name", shipTo.Code);
        }
        // Max lengths are the manual's own (Single Line with a fixed character count).
        PutTrunc(record, "Address_Code", shipTo.Code, 255);
        PutTrunc(record, "Street", shipTo.Street ?? shipTo.Address, 60);
        PutTrunc(record, "Street_2", shipTo.Street2, 40);
        PutTrunc(record, "Street_3", shipTo.Street3, 40);
        PutTrunc(record, "Street_4", shipTo.Street4, 40);
        PutTrunc(record, "Street_5", shipTo.Street5, 40);
        PutTrunc(record, "House_Number", shipTo.HouseNumber, 10);
        PutTrunc(record, "District", shipTo.District, 40);
        PutTrunc(record, "City", shipTo.City, 40);
        PutTrunc(record, "Difference_City", shipTo.DifferenceCity, 40);
        PutTrunc(record, "Post_Code", shipTo.PostCode, 10);
        PutTrunc(record, "Country_Reg", shipTo.CountryReg, 80);
    }

    /// <summary>One row of the item subform. The unit column is "Unit1" on Sales Orders and
    /// "Unit" on Quotes (manual 5.9.6 / 5.8.7), hence <paramref name="unitField"/>.</summary>
    public static JsonObject ItemRow(ZohoSalesOrderLine it, string unitField)
    {
        var row = new JsonObject
        {
            ["Product_Name"] = new JsonObject { ["id"] = it.MaterialId },
            ["Quantity"] = it.Quantity,
        };
        if (!string.IsNullOrWhiteSpace(it.Description)) row["Description"] = it.Description;
        if (!string.IsNullOrWhiteSpace(it.StatusNote)) row["Status_Note"] = it.StatusNote;
        if (!string.IsNullOrWhiteSpace(it.MaterialCode)) row["Material_Code"] = it.MaterialCode;
        if (!string.IsNullOrWhiteSpace(it.MaterialGroup)) row["Material_Group"] = it.MaterialGroup;
        if (!string.IsNullOrWhiteSpace(it.ShipVia)) row["Ship_via"] = it.ShipVia;
        if (!string.IsNullOrWhiteSpace(it.Stock)) row["Stock"] = it.Stock;
        if (!string.IsNullOrWhiteSpace(it.LeadTimeDays)) row["L_T_Days"] = it.LeadTimeDays;
        if (it.UnitPrice is decimal up) row["Unit_Price"] = up;
        if (!string.IsNullOrWhiteSpace(it.Unit)) row[unitField] = it.Unit;
        if (it.ConversionRatio is decimal cr) row["Conversion_Ratio"] = cr;
        if (!string.IsNullOrWhiteSpace(it.SubUnit)) row["Sub_Unit"] = it.SubUnit;
        return row;
    }

    /// <summary>The api_name of a module's subform field: the one whose label contains
    /// <paramref name="labelContains"/>, else the module's only/first subform. Null if none.</summary>
    public static async Task<string?> ResolveSubformFieldAsync(ZohoClient zoho, string module, string labelContains)
    {
        var fields = await zoho.GetFieldsAsync(module);
        string? fallback = null;
        foreach (var node in fields)
        {
            if (node is not JsonObject f) continue;
            if (!string.Equals(Str(f, "data_type"), "subform", StringComparison.OrdinalIgnoreCase)) continue;
            var api = Str(f, "api_name");
            if (string.IsNullOrEmpty(api)) continue;
            fallback ??= api;
            if ((Str(f, "field_label") ?? "").Contains(labelContains, StringComparison.OrdinalIgnoreCase)) return api;
        }
        return fallback;
    }

    /// <summary>data_type and lookup module of one field on a module (settings/fields).</summary>
    public static async Task<(string? DataType, string? LookupModule)> ResolveFieldMetaAsync(ZohoClient zoho, string module, string apiName)
    {
        var fields = await zoho.GetFieldsAsync(module);
        foreach (var node in fields)
        {
            if (node is not JsonObject f) continue;
            if (!string.Equals(Str(f, "api_name"), apiName, StringComparison.OrdinalIgnoreCase)) continue;
            string? lookupModule = f["lookup"] is JsonObject lk ? Str(lk, "module") : null;
            return (Str(f, "data_type"), lookupModule);
        }
        return (null, null);
    }

    /// <summary>The module's api_name from settings/modules by its plural/singular label, else the fallback.</summary>
    public static async Task<string> ResolveModuleAsync(ZohoClient zoho, string plural, string singular, string fallback)
    {
        var modules = await zoho.GetModulesAsync();
        foreach (var node in modules)
        {
            if (node is not JsonObject obj) continue;
            if (string.Equals(Str(obj, "plural_label"), plural, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Str(obj, "singular_label"), singular, StringComparison.OrdinalIgnoreCase))
            {
                var api = Str(obj, "api_name");
                if (!string.IsNullOrEmpty(api)) return api;
            }
        }
        return fallback;
    }

    public static string? Str(JsonObject o, string field) =>
        o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static void Put(JsonObject record, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) record[field] = value;
    }

    public static void PutTrunc(JsonObject record, string field, string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        record[field] = value.Length > maxLen ? value[..maxLen] : value;
    }

    public static void PutBigInt(JsonObject record, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && long.TryParse(value, out var n)) record[field] = n;
    }
}
