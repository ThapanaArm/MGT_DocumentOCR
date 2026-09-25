using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MgtOcr.Zoho;

// A matched (or candidate) customer record from Zoho CRM's "Accounts" module — the Zoho-side
// counterpart to MgtOcr.Sap.SapBusinessPartnerClient's BusinessPartner record.
public record ZohoAccount(
    string AccountId,
    string AccountName,
    string? AccountCode = null,
    string? TaxId = null,
    string? BranchName = null,
    string? AccountType = null);

/// <summary>
/// Customer ("Account" in Zoho CRM) lookup for matching a customer read off an OCR'd document —
/// used for the MGT-side Sales Order flow (Green Leaf keeps using SapBusinessPartnerClient).
///
/// Field names below are confirmed against Megachem's Zoho CRM user manual
/// (AO-CRM-UM-2026-003, section 5.2.8 "Accounts Module"), not guessed:
///   module  = "Accounts"
///   Tax_ID       — Single Line, 13 characters, custom field (เลขประจำตัวผู้เสียภาษี)
///   Account_Name — Single Line, 100 characters (ชื่อบริษัท/ชื่อลูก้า)
///   Account_Code — Single Line (unique), 255 characters, custom (รหัสลูก้า/รหัส Account)
///   Branch_Name  — Single Line, 255 characters, custom (สาขาของบริษัท เช่น HEAD OFFICE)
///
/// Branch_Name is the head-office/branch disambiguator that SAP's Business Partner API turned
/// out NOT to provide (BPTaxType "TH3" was identical across branches on that tenant) — here it's
/// a real, documented field, so unlike the SAP side it's safe to show as authoritative rather
/// than an unverified guess.
/// </summary>
public class ZohoAccountClient(ZohoClient zoho)
{
    private const string Module = "Accounts";
    private const string SelectFields = "id,Account_Name,Account_Code,Tax_ID,Branch_Name,Account_Type";

    public async Task<ZohoAccount?> FindByAccountCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var rows = await zoho.QueryCoqlAsync(Module, SelectFields, $"Account_Code = '{Escape(code)}'", "Account_Name");
        var matches = rows.Select(ToAccount).Where(a => a.AccountCode == code).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // Partial match on the (unique) Account_Code custom field, so the customer live-search box can
    // find an account by its code, not just name/Tax ID. `like` mirrors FindByNameAsync; the exact
    // FindByAccountCodeAsync above stays for the one-shot code lookup that needs a single unique hit.
    public async Task<List<ZohoAccount>> FindByAccountCodeContainsAsync(string codeContains, int top = 20)
    {
        var clean = (codeContains ?? "").Trim();
        if (clean.Length == 0) return [];
        var rows = await zoho.QueryCoqlAsync(Module, SelectFields, $"Account_Code like '%{Escape(clean)}%'", "Account_Code");
        return rows.Take(top).Select(ToAccount).ToList();
    }

    public async Task<List<ZohoAccount>> FindByTaxIdAsync(string taxId, int top = 20)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return [];
        var rows = await zoho.QueryCoqlAsync(Module, SelectFields, $"Tax_ID = '{Escape(taxId)}'", "Account_Name");
        return rows.Take(top).Select(ToAccount).ToList();
    }

    // A single-word `like` search only, deliberately -- combining multiple words into one COQL
    // query with "and"/"or" turned out to be unreliable against Zoho's actual parser (a plain
    // multi-condition chain threw SYNTAX_ERROR; even parenthesizing each condition per Zoho's
    // own documented grammar didn't reliably fix it). Rather than keep chasing COQL's exact
    // combining syntax, search on just the ONE word most likely to be unique to this company and
    // let the results -- close, not necessarily exact -- go to the chat-driven AI compare
    // already built into the Customer card, which is better at the final disambiguation anyway.
    public async Task<List<ZohoAccount>> FindByNameAsync(string nameContains, int top = 20)
    {
        var word = MostDistinctiveWord(nameContains);
        if (word is null) return [];
        var rows = await zoho.QueryCoqlAsync(Module, SelectFields, $"Account_Name like '%{Escape(word)}%'", "Account_Name");
        return rows.Take(top).Select(ToAccount).ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "co", "ltd", "limited", "company", "corp", "corporation", "inc", "pcl", "plc", "the", "and", "of",
        "บริษัท", "จำกัด", "มหาชน",
    };

    // The longest word that isn't a generic legal-suffix word (Co/Ltd/Company/PCL/บริษัท/จำกัด/
    // มหาชน etc.) -- those would otherwise match almost every account in Zoho and defeat the
    // point of searching on one word. Falls back to the longest word overall if the whole name
    // turned out to be nothing but suffix words.
    private static string? MostDistinctiveWord(string name)
    {
        var words = Regex.Matches(name, @"[\p{L}\p{N}]+").Select(m => m.Value).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return null;
        var distinctive = words.Where(w => !StopWords.Contains(w) && w.Length >= 3).ToList();
        return (distinctive.Count > 0 ? distinctive : words).OrderByDescending(w => w.Length).First();
    }

    private static ZohoAccount ToAccount(JsonObject row) => new(
        AccountId: Str(row, "id") ?? "",
        AccountName: Str(row, "Account_Name") ?? "",
        AccountCode: Str(row, "Account_Code"),
        TaxId: Str(row, "Tax_ID"),
        BranchName: Str(row, "Branch_Name"),
        AccountType: Str(row, "Account_Type"));

    private static string? Str(JsonObject row, string field) =>
        row[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // COQL string literals use single quotes; escape an embedded one by doubling it (same
    // convention as SQL) — Zoho's docs don't spell this out but every COQL example follows it.
    private static string Escape(string s) => s.Replace("'", "''");

    /// <summary>
    /// Full field set for one Account, for the "Compare with AI" popup. Deliberately NOT a
    /// hand-picked SELECT list (see SelectFields above, used only for search results) — this
    /// calls Zoho's record-by-id GET with no "fields" restriction, so it returns whatever
    /// standard + custom fields are populated on Megachem's Accounts layout without us having
    /// to confirm each field's api_name up front (the mistake made earlier with SAP's TH3
    /// field). New fields added to the Zoho layout later show up here automatically.
    /// </summary>
    public async Task<List<(string Label, string? Value)>> GetFullAccountFieldsAsync(string id)
    {
        var row = await zoho.GetByIdAsync(Module, id);
        return row is null ? [] : Flatten(row);
    }

    // Zoho's own bookkeeping/state fields — never useful for a human or AI comparing two
    // customer records, so keep them out of the popup rather than filtering by an allow-list
    // (which would put us back to guessing which fields matter).
    private static readonly HashSet<string> SkipFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "Layout", "Created_By", "Modified_By", "Created_Time", "Modified_Time",
        "Last_Activity_Time", "Tag", "Record_Image",
    };

    private static List<(string, string?)> Flatten(JsonObject row)
    {
        var result = new List<(string, string?)>();
        foreach (var kv in row)
        {
            // "$"-prefixed and "__s"-suffixed keys are both Zoho's own internal/state field
            // naming conventions (audit trail, soft-delete flags, etc.) -- e.g. Record_Status__s,
            // Change_Log_Time__s -- never meaningful for a human or AI comparing two customers.
            if (kv.Key.StartsWith('$') || kv.Key.EndsWith("__s") || SkipFields.Contains(kv.Key)) continue;
            string? value = FlattenValue(kv.Value);
            if (string.IsNullOrWhiteSpace(value)) continue;
            result.Add((kv.Key.Replace('_', ' '), value));
        }
        return result;
    }

    // A JsonValue prints as-is; a lookup (Owner, Account_Manager, ...) is {id, name, email?} ->
    // show its "name"; a list (e.g. Layout's fields, or a multi-select) joins non-empty items
    // with ", "; anything else unrecognized is skipped rather than dumped as raw JSON.
    private static string? FlattenValue(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToString(),
        JsonObject o when o["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) => name,
        JsonObject => null,
        JsonArray arr => string.Join(", ", arr.Select(FlattenValue).Where(x => !string.IsNullOrWhiteSpace(x))),
        _ => null,
    };

    // Exact (case-insensitive) label lookup against a Flatten()'d field list -- Flatten turns an
    // api_name like "Sold_to_Street_2" into the label "Sold to Street 2" (underscores -> spaces),
    // so this is the exact reverse of that transform, not a loose Contains() match. Using exact
    // labels matters here specifically because a loose match on "Code" also matches "... Post
    // Code", and a loose match on "Country" would also match nothing else here but is still
    // fragile the moment Zoho adds a new "... Country ..." field later.
    private static string? Exact(List<(string Label, string? Value)> fields, string label) =>
        fields.FirstOrDefault(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// All confirmed Sold-to/Ship-to sub-fields for one address block (labelPrefix "Sold to" or
    /// "Ship to"), read from an already-fetched Flatten()'d field list. Field names confirmed
    /// against the Zoho CRM user manual, section 5.2.8 "Accounts Module" (Sold-to Information /
    /// Ship-to Information sub-blocks): Code, Street, Street 2-5, House Number, District, City,
    /// Difference City, Post Code, Country / Reg -- no longer guessed via loose Contains matching.
    /// Returns null only when NOTHING in this address block is populated.
    /// </summary>
    private static ZohoShipToInfo? BuildAddressInfo(List<(string Label, string? Value)> fields, string labelPrefix, string source)
    {
        var code = Exact(fields, $"{labelPrefix} Code");
        var street = Exact(fields, $"{labelPrefix} Street");
        var street2 = Exact(fields, $"{labelPrefix} Street 2");
        var street3 = Exact(fields, $"{labelPrefix} Street 3");
        var street4 = Exact(fields, $"{labelPrefix} Street 4");
        var street5 = Exact(fields, $"{labelPrefix} Street 5");
        var houseNumber = Exact(fields, $"{labelPrefix} House Number");
        var district = Exact(fields, $"{labelPrefix} District");
        var city = Exact(fields, $"{labelPrefix} City");
        var differenceCity = Exact(fields, $"{labelPrefix} Difference City");
        var postCode = Exact(fields, $"{labelPrefix} Post Code");
        var countryReg = Exact(fields, $"{labelPrefix} Country Reg");

        var parts = new[] { houseNumber, street, street2, street3, street4, street5, district, differenceCity, city, postCode, countryReg }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var address = string.Join(", ", parts);
        if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(code)) return null;

        return new ZohoShipToInfo(
            Address: string.IsNullOrWhiteSpace(address) ? null : address,
            Code: code,
            Street: street,
            Street2: street2,
            Street3: street3,
            Street4: street4,
            Street5: street5,
            HouseNumber: houseNumber,
            District: district,
            City: city,
            DifferenceCity: differenceCity,
            PostCode: postCode,
            CountryReg: countryReg,
            Source: source);
    }

    /// <summary>
    /// Ship-to address(es) for this Account. Returns the Account's own inline "Ship to ..." sub-
    /// fields (see BuildAddressInfo) plus, when the "Ship to มากกว่า 1" checkbox (Ship_to_1) is
    /// ticked, every record from Megachem's separate Ship-to Module linked to this Account (see
    /// GetShipToFromRelatedModuleAsync) -- per Megachem's own field description: that checkbox
    /// means "this company has more than one delivery address", stored there rather than inline.
    /// If the checkbox is unset and the inline fields are empty, the separate module is still
    /// checked once as a fallback (the address may have only ever been entered there).
    /// </summary>
    public async Task<ZohoShipToListResult> GetAllShipTosAsync(string accountId)
    {
        var fields = await GetFullAccountFieldsAsync(accountId);
        var hasMultiple = string.Equals(Exact(fields, "Ship to 1"), "Yes", StringComparison.OrdinalIgnoreCase);

        var result = new List<ZohoShipToInfo>();
        var inline = BuildAddressInfo(fields, "Ship to", "account");
        if (inline != null) result.Add(inline);

        if (hasMultiple || result.Count == 0)
            result.AddRange(await GetShipToFromRelatedModuleAsync(accountId));

        return new ZohoShipToListResult(hasMultiple, result);
    }

    /// <summary>Single best Ship-to for this Account -- the inline one if present, else the first
    /// record from the separate Ship-to Module. Used by the "Use &amp; Save" single-pick flow;
    /// see GetAllShipTosAsync for the full list (used to just display everything on file).</summary>
    public async Task<ZohoShipToInfo?> GetShipToInfoAsync(string accountId) =>
        (await GetAllShipTosAsync(accountId)).ShipTos.FirstOrDefault();

    // Cached after the first lookup (this class is registered as a singleton) so every
    // subsequent document doesn't re-hit Zoho's settings API just to find the same related list
    // again. null means "checked, no Ship-to-looking related list exists".
    private string? _shipToRelatedListApiName;
    private bool _shipToRelatedListChecked;

    /// <summary>
    /// Every record from Megachem's separate "Ship-to Module" linked to this Account, discovered
    /// (not hardcoded) via Accounts' configured related lists (GetRelatedListsAsync) picking the
    /// first whose display label contains "ship". Field names on that module are confirmed
    /// against the manual, section 5.4.3 "รายละเอียดและความหมายของฟิลด์บน Ship-to Module": the
    /// module's own primary/Name field is reused for the required "Ship-to Street" line; every
    /// other sub-field mirrors the Ship_to_* fields on Accounts one-for-one (Ship_to_Code,
    /// Ship_to_Street_2..5, Ship_to_House_Number, Ship_to_District, Ship_to_City,
    /// Ship_to_Difference_City, Ship_to_Post_Code, Ship_to_Country_Reg).
    /// </summary>
    private async Task<List<ZohoShipToInfo>> GetShipToFromRelatedModuleAsync(string accountId)
    {
        if (!_shipToRelatedListChecked)
        {
            _shipToRelatedListChecked = true;
            var relatedLists = await zoho.GetRelatedListsAsync(Module);
            foreach (var node in relatedLists)
            {
                if (node is not JsonObject obj) continue;
                var label = Str(obj, "display_label") ?? Str(obj, "name") ?? "";
                if (label.Contains("ship", StringComparison.OrdinalIgnoreCase))
                {
                    var apiName = Str(obj, "api_name");
                    if (!string.IsNullOrEmpty(apiName)) { _shipToRelatedListApiName = apiName; break; }
                }
            }
        }
        if (string.IsNullOrEmpty(_shipToRelatedListApiName)) return [];

        var records = await zoho.GetRelatedRecordsAsync(Module, accountId, _shipToRelatedListApiName);
        var result = new List<ZohoShipToInfo>();
        foreach (var node in records)
        {
            if (node is not JsonObject obj) continue;

            var street = Str(obj, "Name"); // module's primary field -- confirmed to hold "Ship-to Street"
            var street2 = Str(obj, "Ship_to_Street_2");
            var street3 = Str(obj, "Ship_to_Street_3");
            var street4 = Str(obj, "Ship_to_Street_4");
            var street5 = Str(obj, "Ship_to_Street_5");
            var houseNumber = Str(obj, "Ship_to_House_Number");
            var district = Str(obj, "Ship_to_District");
            var city = Str(obj, "Ship_to_City");
            var differenceCity = Str(obj, "Ship_to_Difference_City");
            var postCode = Str(obj, "Ship_to_Post_Code");
            var countryReg = Str(obj, "Ship_to_Country_Reg");
            var code = Str(obj, "Ship_to_Code") ?? Str(obj, "CRM_Ship_to_Code");

            var parts = new[] { houseNumber, street, street2, street3, street4, street5, district, differenceCity, city, postCode, countryReg }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            var address = string.Join(", ", parts);

            result.Add(new ZohoShipToInfo(
                Address: string.IsNullOrWhiteSpace(address) ? null : address,
                Code: code,
                Street: street,
                Street2: street2,
                Street3: street3,
                Street4: street4,
                Street5: street5,
                HouseNumber: houseNumber,
                District: district,
                City: city,
                DifferenceCity: differenceCity,
                PostCode: postCode,
                CountryReg: countryReg,
                Source: "shipto-module",
                RecordId: Str(obj, "id")));
        }
        return result;
    }

    /// <summary>
    /// Sold-to (the customer's own billing) address off this same Account -- shown alongside a
    /// search result so the person can eyeball which candidate is right without opening a full
    /// compare. Field names confirmed against the manual, section 5.2.8 "Accounts Module",
    /// Sold-to Information sub-block (see BuildAddressInfo).
    /// </summary>
    public async Task<ZohoShipToInfo?> GetSoldToInfoAsync(string accountId)
    {
        var fields = await GetFullAccountFieldsAsync(accountId);
        return BuildAddressInfo(fields, "Sold to", "account");
    }
}

/// <summary>
/// One Sold-to/Ship-to address, every confirmed sub-field exposed individually (not just a
/// collapsed joined string) so the UI can show exactly what's on file in Zoho -- Address is kept
/// alongside as a convenience for places that just want one line of text. Source distinguishes
/// an address read inline off the Account record ("account") from one read off Megachem's
/// separate Ship-to Module ("shipto-module") -- relevant once an Account has more than one.
/// </summary>
public record ZohoShipToInfo(
    string? Address,
    string? Code,
    string? Street = null,
    string? Street2 = null,
    string? Street3 = null,
    string? Street4 = null,
    string? Street5 = null,
    string? HouseNumber = null,
    string? District = null,
    string? City = null,
    string? DifferenceCity = null,
    string? PostCode = null,
    string? CountryReg = null,
    string? Source = null,
    string? RecordId = null);

/// <summary>Every Ship-to on file for one Account, plus whether the "Ship to มากกว่า 1" (Ship_to_1)
/// checkbox is ticked -- see ZohoAccountClient.GetAllShipTosAsync.</summary>
public record ZohoShipToListResult(bool HasMultipleShipTos, List<ZohoShipToInfo> ShipTos);
