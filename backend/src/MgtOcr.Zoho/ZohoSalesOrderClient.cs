using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

/// <summary>
/// One line to write onto the Sales Order's "Ordered Items" subform. Quantity/UnitPrice/Unit are
/// deliberately sourced from the DOCUMENT's own numbers by the caller (never the Deal's original
/// forecast numbers -- same rule ZohoDealClient documents), while MaterialId/MaterialGroup/
/// ShipVia/Stock/LeadTimeDays/ConversionRatio/SubUnit are reference/master-data-shaped values the
/// caller reuses from the matched Deal Item (there is no separate Zoho Material search feature,
/// so MaterialId -- required for the Product_Name lookup -- can only come from a matched Deal Item).
/// StatusNote is the odd one out: it's the document's own per-line "Item Note 1"
/// (line.extra.itemNote1 -- OCR-prefilled from the PO, editable in DetailTable), the exact same
/// source SAP's Item Note 1 feature sends as item long text (to_Item -> to_Text) -- confirmed by
/// Megachem that Zoho's Status_Note column on this subform is the right place for it, reversing
/// this class's earlier assumption that Status_Note had no clear document-side source.
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
    string? SubUnit,
    string? StatusNote = null);

/// <summary>
/// Zoho CRM "Sales Orders" module — write-only client used by the OCR→Zoho Sales Order creation
/// flow (see DocumentsController's SAP "/post" for the SAP-side equivalent, which this
/// deliberately never touches or shares code with).
///
/// Header field names (Subject, Deal_Name, Account_Name, Account_Code, Tax_ID, Customer_Ref,
/// Delivery_Date, Payment_Terms, Payment_Currency, Incoterms) and the Ordered Items subform's
/// column names (Product_Name, Description, Material_Code, Material_Group, Ship_via, Stock,
/// L_T_Days, Quantity, Unit_Price, Unit1, Conversion_Ratio, Sub_Unit) are all confirmed against
/// Megachem's Zoho CRM user manual (AO-CRM-UM-2026-003, section 5.9.6 "ใบสั่งขาย", pages 295-303)
/// — nothing here is guessed. Deliberately excludes: Formula/computed fields (Q_ty_Kgs,
/// Price_Sub_Unit, Amount, Sub_Total, Vat_Amount, Grand_Total -- Zoho computes these, sending them
/// is pointless and Amount/Q_ty_Kgs/Price_Sub_Unit would be rejected as read-only); "ใช้สำหรับ
/// จัดเก็บข้อมูลตาม Business Requirement ของ MGT" placeholder fields with no clear document-side
/// source (Distribution, Customer_No, D_O_NO, Billing_NO, P_O_No, Purchaser, Tax_Classification,
/// Potential, Next_Step, Remaining_Balance, Outstanding_Amount, Credit_Limited). Status_Note (also
/// on the subform) is NOT excluded -- see ZohoSalesOrderLine.StatusNote.
///
/// Select_Address (picklist on the Sales Order itself: "-None-" / "Sold-to" / "Ship-to" /
/// "Other Ship-to", confirmed both from the manual's own field table, page 301, and Megachem's
/// Zoho screen) used to be treated as a pure layout control and never sent -- turned out that was
/// wrong: it's what tells Zoho WHICH address block to actually use, so leaving it unset risks
/// Zoho defaulting to the Sold-to address instead of the Ship-to actually matched. It's set from
/// the same ZohoShipToInfo.Source the caller already looked up (see BuildRecordAsync): "account"
/// (the Account's own inline "Ship to ..." fields) -> "Ship-to"; "shipto-module" (a record from
/// the separate Ship-to Module) -> "Other Ship-to".
///
/// Ship_to_Name (Lookup, per the manual's own field table -- shown/used only when Select_Address
/// = "Other Ship-to", "so the user can pick the Ship-to record linked to the Account") is sent
/// only for that case, using the matched Ship-to Module record's own id (ZohoShipToInfo.RecordId)
/// -- see BuildRecordAsync and ResolveShipToNameFieldAsync. This overturns two earlier, each
/// individually well-evidenced but wrong assumptions from the same day: first that this field is
/// a plain bigint (a live INVALID_DATA test result had said so), then (from Megachem's own Zoho
/// Setup screenshots) that it's simply a Lookup with no further qualification -- the manual
/// settles it precisely: Lookup, but conditional on Select_Address, and only populated when a
/// real Ship-to Module record exists to point at.
///
/// The rest of the address (Address_Code, Street, Street_2..Street_5, House_Number, District,
/// City, Difference_City, Post_Code, Country_Reg -- manual page 301-302, "Address Information")
/// is this module's OWN unprefixed field set, distinct from the Accounts module's "Ship-to
/// Information" sub-block (Ship_to_Code, Ship_to_Street*, Ship_to_House_Number, Ship_to_District,
/// Ship_to_City, Ship_to_Difference_City, Ship_to_Post_Code, Ship_to_Country_Reg -- see
/// ZohoAccountClient, manual section 5.2.8, page 91). Every earlier round of this class, including
/// the same-day reversal that restored explicit field-sending after Megachem found the address
/// blank on a real Sales Order, sent the ACCOUNTS module's Ship_to_*-prefixed names here by
/// mistake -- almost all of which don't exist on Sales Orders and were silently dropped by Zoho's
/// v8 API (it ignores unrecognized field names rather than erroring), which is the real reason
/// "Select Address" alone showed correctly while nothing under it filled in. Read directly from
/// the uploaded Zoho CRM user manual PDF and fixed 2026-09-22; see BuildRecordAsync's own comment
/// for the field-by-field correction.
///
/// The subform container's own api_name is never given in the manual (only its columns are), so
/// it's resolved once via ZohoClient.GetFieldsAsync and cached, mirroring ZohoDealClient's
/// module-resolution pattern rather than guessing a container key.
/// </summary>
public class ZohoSalesOrderClient(ZohoClient zoho)
{
    private string? _module;
    private string? _orderedItemsField;
    private (string? DataType, string? LookupModule)? _shipToNameMeta;

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
    /// Ship_to_Name's real column shape, read fresh from settings/fields (cached per process,
    /// same pattern as ResolveOrderedItemsFieldAsync) instead of assumed -- Megachem's own Zoho
    /// Setup screenshots on 2026-09-22 showed this field configured as a Lookup to the "Ship-to"
    /// module, which contradicts an earlier live test that had gotten back INVALID_DATA
    /// "expected_data_type":"bigint" for the very same field. Both are real observations, just
    /// taken at different times -- a tenant admin can change a custom field's type after the
    /// fact, so rather than trust either snapshot forever, BuildRecordAsync asks Zoho what the
    /// field actually is right now and branches on the answer (see its own comment there).
    /// </summary>
    private async Task<(string? DataType, string? LookupModule)> ResolveShipToNameFieldAsync()
    {
        if (_shipToNameMeta is { } cached) return cached;
        var module = await ResolveModuleAsync();
        var fields = await zoho.GetFieldsAsync(module);
        foreach (var node in fields)
        {
            if (node is not JsonObject f) continue;
            if (!string.Equals(Str(f, "api_name"), "Ship_to_Name", StringComparison.OrdinalIgnoreCase)) continue;
            string? lookupModule = f["lookup"] is JsonObject lk ? Str(lk, "module") : null;
            _shipToNameMeta = (Str(f, "data_type"), lookupModule);
            return _shipToNameMeta.Value;
        }
        _shipToNameMeta = (null, null);
        return _shipToNameMeta.Value;
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

        // CORRECTED 2026-09-22 against Megachem's own Zoho CRM user manual (AO-CRM-UM-2026-003,
        // section 5.9.6 "ใบสั่งขาย" (Sales Order), pages 300-303, "Select Address"/"Address
        // Information" field tables -- read directly from the uploaded PDF, not re-guessed).
        // Every previous round of this fix (including the same-day reversal above this comment)
        // had reused the ACCOUNTS module's own "Ship-to Information" sub-block field names
        // (Ship_to_Code, Ship_to_Street, Ship_to_Street_2, Ship_to_House_Number, Ship_to_District,
        // Ship_to_City, Ship_to_Difference_City, Ship_to_Post_Code, Ship_to_Country_Reg -- see
        // ZohoAccountClient's own doc comment, manual section 5.2.8, page 91) for the Sales
        // Orders record instead -- but the Sales Orders module's OWN "Address Information" block
        // uses a completely different, unprefixed set of API names (Address_Code, Street,
        // Street_2..Street_5, House_Number, District, City, Difference_City, Post_Code,
        // Country_Reg). Sending the Account-shaped names here meant most of them didn't match any
        // real field on this module -- Zoho's v8 API silently drops unrecognized field names
        // rather than erroring, which is exactly why the first live test showed "Select Address"
        // correctly set to Ship-to but nothing underneath filled in ("มันเลือก Ship-To ให้นะ แต่มัน
        // ไม่ default ให้แบบปกติ"). The one exception was Ship_to_Name, which DOES exist as a real
        // field on this module (per the manual: Lookup, shown when Select Address = "Other
        // Ship-to", "so the user can pick the Ship-to record linked to the selected Account") --
        // that one is real, so sending the wrong JSON shape for it (a bare bigint) failed loudly
        // instead of silently, with Zoho's own INVALID_DATA for field=Ship_to_Name -- the second,
        // more useful failure that led to this correction.
        if (shipTo is not null)
        {
            // Select_Address (picklist) -- see this class's own doc comment above. Without it,
            // Zoho's own default for this field could win over the address fields below and show
            // the Sold-to address instead of the Ship-to actually matched.
            Put(record, "Select_Address", shipTo.Source switch
            {
                "account" => "Ship-to",
                "shipto-module" => "Other Ship-to",
                // Unexpected/missing Source (shouldn't normally happen -- every ZohoShipToInfo
                // this app builds sets one of the two values above) -- "Ship-to" is the more
                // common case, so it's the safer default rather than leaving the picklist unset.
                _ => "Ship-to",
            });
            // Ship_to_Name (Lookup, per the manual -- see this method's own comment above) only
            // applies to the "Other Ship-to" case, where a real Ship-to Module record exists to
            // point at (Source == "shipto-module", carrying its own RecordId). For the plain
            // "Ship-to" case (Source == "account", the Account's own inline fields, no separate
            // module record), it is left unset rather than guessed -- Select_Address plus the
            // Address Information fields below still carry the actual address either way.
            // ResolveShipToNameFieldAsync re-checks the field's live data_type/lookup module
            // in case this tenant's configuration changes again (it already has, twice).
            var (shipToNameType, shipToNameLookupModule) = await ResolveShipToNameFieldAsync();
            if (shipTo.Source == "shipto-module" && !string.IsNullOrWhiteSpace(shipTo.RecordId)
                && string.Equals(shipToNameType, "lookup", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(shipToNameLookupModule)
                    || string.Equals(shipToNameLookupModule, "Ship-to", StringComparison.OrdinalIgnoreCase))
                    record["Ship_to_Name"] = new JsonObject { ["id"] = shipTo.RecordId };
                // else: Lookup points somewhere unexpected -- leave it unset rather than send an
                // id that resolves against the wrong module.
            }
            else if (!string.Equals(shipToNameType, "lookup", StringComparison.OrdinalIgnoreCase))
            {
                // Field metadata unresolved (e.g. a settings.fields permission issue) or genuinely
                // still numeric on this tenant -- fall back to the earlier confirmed bigint shape
                // rather than sending nothing.
                PutBigInt(record, "Ship_to_Name", shipTo.Code);
            }
            // Address Information block -- the manual's own field names (Sales Orders module),
            // NOT the Accounts module's Ship_to_*-prefixed equivalents (see this method's comment
            // above). Address_Code is the manual's field for "the address code to show on the
            // Sales Order" -- shipTo.Code (the Ship-to's own code) is what belongs there.
            //
            // CORRECTED 2026-09-22 (same day, third live test): a real POST with a long Street
            // value came back INVALID_DATA "field=Street, maximum_length=60" -- these fields are
            // real, but each is short (per the manual's own "Data Type" column: Single Line with a
            // fixed character count), far shorter than shipTo.Address's one-line combined string.
            // Truncated with PutTrunc rather than left as a hard failure: a shortened value is
            // still a usable address on the Sales Order, whereas rejecting the whole record over
            // one long field would silently lose everything else that DID fit (Select_Address,
            // Ship_to_Name, all the other address fields). Max lengths are the manual's own
            // (section 5.9.6, page 301-302), not guessed.
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

        var rows = new JsonArray();
        foreach (var it in items)
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

    // Truncates to Zoho's own documented max length for a field instead of sending a value that
    // gets the whole record rejected with INVALID_DATA "maximum_length" -- see the Address
    // Information block in BuildRecordAsync for why this is needed (shipTo.Address, used as a
    // Street fallback, is a full one-line combined address and easily exceeds Street's 60-char cap).
    private static void PutTrunc(JsonObject record, string field, string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        record[field] = value.Length > maxLen ? value[..maxLen] : value;
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
