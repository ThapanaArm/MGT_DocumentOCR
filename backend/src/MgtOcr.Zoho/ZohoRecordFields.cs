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
        // No line Description (Megachem, 29 Oct 2026) -- Product_Name + Material_Code identify the item.
        if (!string.IsNullOrWhiteSpace(it.StatusNote)) row["Status_Note"] = it.StatusNote;
        if (!string.IsNullOrWhiteSpace(it.MaterialCode)) row["Material_Code"] = it.MaterialCode;
        if (!string.IsNullOrWhiteSpace(it.MaterialGroup)) row["Material_Group"] = it.MaterialGroup;
        if (!string.IsNullOrWhiteSpace(it.ShipVia)) row["Ship_via"] = it.ShipVia;
        if (!string.IsNullOrWhiteSpace(it.Stock)) row["Stock"] = it.Stock;
        if (!string.IsNullOrWhiteSpace(it.LeadTimeDays)) row["L_T_Days"] = it.LeadTimeDays;
        if (it.UnitPrice is decimal up) row["Unit_Price"] = up;
        if (it.LastPrice is decimal lp) row["Last_Price"] = lp;
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

    // ---------------------------------------------------------------------------------------------
    // Account defaults (16 Nov 2026). On Zoho's screen, choosing the Account on a Quote / Sales Order
    // fills several fields from that Account (e.g. the tax fields the VAT is calculated from). That
    // fill runs in the browser only -- a record created through the API gets none of it, whatever
    // "trigger" is sent. So OCR copies those values itself: every field of the target module that
    // has a field of the same api_name (else the same label) on the Account, is writable and of a
    // plain type, and was not already set by OCR (OCR's own values always win).
    // ---------------------------------------------------------------------------------------------

    // Set elsewhere by OCR (header, address block, lookups) or Zoho's own bookkeeping -- never copied.
    private static readonly HashSet<string> AccountCopySkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "Owner", "Layout", "Created_By", "Modified_By", "Created_Time", "Modified_Time",
        "Last_Activity_Time", "Tag", "Record_Image", "Description", "Subject", "Status", "Quote_Stage",
        "Deal_Name", "Account_Name", "Contact_Name", "Quote_Name", "Quote_No", "Quote_No1", "SO_Number",
        "Currency", "Exchange_Rate", "Valid_Till", "Due_Date", "Delivery_Date", "Customer_Ref",
        "Select_Address", "Ship_to_Name", "Address_Code", "Street", "Street_2", "Street_3", "Street_4",
        "Street_5", "House_Number", "District", "City", "Difference_City", "Post_Code", "Country_Reg",
        "Billing_Street", "Billing_City", "Billing_State", "Billing_Code", "Billing_Country",
        "Shipping_Street", "Shipping_City", "Shipping_State", "Shipping_Code", "Shipping_Country",
        "Sub_Total", "Grand_Total", "Discount", "Adjustment", "Tax", "Vat_Amount", "Amount",
    };

    private static readonly HashSet<string> AccountCopyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "textarea", "email", "phone", "website", "picklist", "multiselectpicklist", "integer",
        "bigint", "double", "currency", "percent", "decimal", "boolean", "date", "datetime",
    };

    /// <summary>
    /// Copies the Account's values into <paramref name="record"/> for the fields Zoho's screen would
    /// fill from the Account (see above). Returns the api_names copied. Never throws: a failure to read
    /// the Account or the field lists leaves the record as OCR built it.
    /// </summary>
    public static async Task<List<string>> ApplyAccountDefaultsAsync(ZohoClient zoho, string module, JsonObject record, string accountId)
    {
        var copied = new List<string>();
        if (string.IsNullOrWhiteSpace(accountId)) return copied;
        try
        {
            var account = await zoho.GetByIdAsync("Accounts", accountId);
            if (account is null) return copied;
            // Account field label -> api_name, for fields named differently on the two modules.
            var accByLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in await zoho.GetFieldsAsync("Accounts"))
                if (node is JsonObject af && Str(af, "field_label") is { Length: > 0 } lbl && Str(af, "api_name") is { Length: > 0 } aapi)
                    accByLabel.TryAdd(lbl.Trim(), aapi);

            foreach (var node in await zoho.GetFieldsAsync(module))
            {
                if (node is not JsonObject f) continue;
                var api = Str(f, "api_name");
                if (string.IsNullOrEmpty(api) || AccountCopySkip.Contains(api) || record.ContainsKey(api)) continue;
                var type = Str(f, "data_type") ?? "";
                if (!AccountCopyTypes.Contains(type)) continue;
                if (IsTrue(f, "read_only") || IsTrue(f, "field_read_only") || f["formula"] is JsonObject { Count: > 0 }) continue;

                JsonNode? value = account[api];
                if (IsEmpty(value) && Str(f, "field_label") is { Length: > 0 } label
                    && accByLabel.TryGetValue(label.Trim(), out var accApi))
                    value = account[accApi];
                if (IsEmpty(value)) continue;

                var fitted = Fit(f, type, value!);
                if (fitted is null) continue;
                record[api] = fitted;
                copied.Add(api);
            }
        }
        catch
        {
            // Best effort: the Quote / Sales Order is still created with OCR's own values.
        }
        return copied;
    }

    private static bool IsTrue(JsonObject o, string field) =>
        o[field] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool IsEmpty(JsonNode? v) => v switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject => true, // lookups / owners are not copied
        JsonValue jv => jv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s),
        _ => true,
    };

    // The Account's value in the shape the target field accepts, or null when it would not be accepted
    // (a picklist option the target does not have, a non-number for a number field, ...). Zoho rejects
    // the WHOLE record on one bad field, so anything doubtful is left out.
    private static JsonNode? Fit(JsonObject f, string type, JsonNode value)
    {
        string? text = value is JsonValue jv ? jv.ToString() : null;
        switch (type.ToLowerInvariant())
        {
            case "picklist":
                return text is not null && PickOption(f, text) is { } opt ? JsonValue.Create(opt) : null;
            case "multiselectpicklist":
            {
                var src = value is JsonArray arr ? arr.Select(n => n?.ToString()) : [text];
                var outArr = new JsonArray();
                foreach (var s in src)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (PickOption(f, s) is not { } o) return null;
                    outArr.Add(o);
                }
                return outArr.Count > 0 ? outArr : null;
            }
            case "integer":
            case "bigint":
                return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n) : null;
            case "double":
            case "currency":
            case "percent":
            case "decimal":
                return decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null;
            case "boolean":
                return value is JsonValue bv && bv.TryGetValue<bool>(out var b) ? JsonValue.Create(b)
                    : bool.TryParse(text, out var b2) ? JsonValue.Create(b2) : null;
            default: // text, textarea, email, phone, website, date, datetime
                if (text is null) return null;
                int max = f["length"] is JsonValue lv && lv.TryGetValue<int>(out var len) && len > 0 ? len : int.MaxValue;
                return JsonValue.Create(text.Length > max ? text[..max] : text);
        }
    }

    // The target picklist's own option (actual_value) matching the Account's value, by actual or display value.
    private static string? PickOption(JsonObject f, string value)
    {
        if (f["pick_list_values"] is not JsonArray opts) return null;
        foreach (var o in opts)
        {
            if (o is not JsonObject op) continue;
            var actual = Str(op, "actual_value");
            var display = Str(op, "display_value");
            if (string.Equals(actual?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(display?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                return actual ?? display;
        }
        return null;
    }
}
