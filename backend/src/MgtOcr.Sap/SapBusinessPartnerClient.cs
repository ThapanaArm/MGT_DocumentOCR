using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

// One row from A_BusinessPartner — only the fields this lookup currently needs.
// TaxId is filled in afterwards (a separate entity, A_BusinessPartnerTaxNumber) —
// always the tax number SAP itself has on file for this partner, regardless of whether the
// search was keyed by tax ID or by name, so the UI can show SAP's real value even when the
// document being matched had no Tax ID of its own (or a different one) to search with.
public record BusinessPartner(string BusinessPartnerId, string BusinessPartnerName, string? BusinessPartnerFullName = null, bool BusinessPartnerIsBlocked = false, string? AddressCity = null, string? AddressStreet = null, string? TaxId = null);

// One row from A_CustSalesPartnerFunc (a Sold-to's sales-area partner-function assignments).
// PartnerFunction "SH" = Ship-to, "SP" = Sold-to (per Megachem — matches the "SH"/PartnerFunction
// convention SapPayloadBuilder already writes on the to_Partner navigation when posting a Sales
// Order). Partner carries the full re-fetched BusinessPartner record once FindPartnerFunctionsAsync
// resolves the linked partner's name/address. C# property kept as "PartnerCustomer" for the rest of
// this codebase/the frontend even though the underlying OData field turned out to be named
// BPCustomerNumber (see FindPartnerFunctionsAsync) — only the JSON parsing needed to change.
public record PartnerFunctionLink(string Customer, string PartnerFunction, string PartnerCustomer, BusinessPartner? Partner = null);

// Step 1 of SAP integration for the Sales Order module: read-only OData V2 GET client for SAP
// Business Partner (Customer) master data. Used to match a customer read off an OCR'd document
// to its SAP Business Partner record — by Tax ID first (exact, one company = one Tax ID) and by
// name as a fallback (fuzzy substring match, only needed when Tax ID wasn't read/found). Mirrors
// SapClient's simulate-when-unconfigured shape so this can be exercised before
// Sap:BusinessPartner:AuthHeader is filled in.
public class SapBusinessPartnerClient(AppConfig config, HttpClient httpClient)
{
    /// <summary>
    /// Find the Business Partner(s) registered under this Tax ID, via the standard
    /// A_BusinessPartnerTaxNumber entity (BPTaxNumber) — a separate entity set from
    /// A_BusinessPartner itself, joined back by BusinessPartner code.
    /// NOTE: not yet verified against this tenant's actual $metadata — if this 404s or the
    /// filter is rejected, the field/entity name may differ on this system and needs adjusting.
    /// </summary>
    public async Task<List<BusinessPartner>> FindByTaxIdAsync(string taxId, int top = 20)
    {
        var baseUrl = config.SapBusinessPartnerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return []; // simulation mode: Sap:BusinessPartner:BaseUrl not configured yet

        var clean = taxId.Trim();
        if (clean.Length == 0) return [];

        var filter = $"BPTaxNumber eq '{EscapeODataLiteral(clean)}'";
        var url = $"{baseUrl.TrimEnd('/')}/A_BusinessPartnerTaxNumber" +
                  $"?$filter={Uri.EscapeDataString(filter)}&$select=BusinessPartner,BPTaxType,BPTaxNumber&$top={top}";

        var text = await GetJsonAsync(url);
        var partnerIds = ParseBusinessPartnerIds(text).Distinct().ToList();
        if (partnerIds.Count == 0) return [];

        var results = await FetchByIdsAsync(partnerIds, top);
        var addrByBp = await FetchAddressMapAsync(results);
        // The Tax ID is already known here — it's literally the value we just filtered
        // A_BusinessPartnerTaxNumber by — so there is no need for EnrichAsync's separate Tax ID
        // round trip on this path. Saves a whole extra SAP call on the common case (Tax ID search
        // is tried first specifically because it's the exact, cheap match).
        return results
            .Select(r =>
            {
                var withAddr = addrByBp.TryGetValue(r.BusinessPartnerId, out var a)
                    ? r with { AddressCity = a.city, AddressStreet = a.street }
                    : r;
                return withAddr with { TaxId = clean };
            })
            .ToList();
    }

    /// <summary>
    /// Read the Ship-to ("SH") and Sold-to ("SP") partner-function links SAP already has on file
    /// for a Sold-to's sales area, via A_CustSalesPartnerFunc — the customer-master partner-function
    /// entity (not to be confused with the sales-order-level A_SalesOrderPartner). Used to help
    /// match a document's Ship-to against what SAP already knows for this customer, instead of
    /// relying on the local shiptos master alone. Re-fetches the full BusinessPartner (name,
    /// address) for each linked partner code, same pattern as FindByTaxIdAsync.
    /// Field name fix (2026-09-10): the first cut of this method guessed "PartnerCustomer" for the
    /// assigned partner's own customer code and a live call 404'd with "Resource not found for the
    /// segment 'PartnerCustomer'" — the real OData field on A_CustSalesPartnerFunc is
    /// <b>BPCustomerNumber</b> (confirmed against SAP's own A_CUSTSALESPARTNERFUNC field list: key
    /// fields Customer/SalesOrganization/DistributionChannel/Division/PartnerCounter/PartnerFunction,
    /// plus AuthorizationGroup/BPCustomerNumber/CustomerPartnerDescription/DefaultPartner). Still not
    /// verified end-to-end against this tenant's live data beyond the $select no longer 404ing —
    /// confirm a real response looks right before trusting this in the UI, same caveat as
    /// FindByTaxIdAsync above.
    /// </summary>
    public async Task<List<PartnerFunctionLink>> FindPartnerFunctionsAsync(string soldToCustomerId, string? function = null, int top = 50)
    {
        var baseUrl = config.SapBusinessPartnerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return []; // simulation mode: Sap:BusinessPartner:BaseUrl not configured yet

        var clean = soldToCustomerId.Trim();
        if (clean.Length == 0) return [];

        var filter = $"Customer eq '{EscapeODataLiteral(clean)}'";
        var url = $"{baseUrl.TrimEnd('/')}/A_CustSalesPartnerFunc" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  "&$select=Customer,PartnerFunction,BPCustomerNumber,SalesOrganization,DistributionChannel,Division" +
                  $"&$top={top}";

        var text = await GetJsonAsync(url);
        var links = ParsePartnerFunctionLinks(text);
        if (!string.IsNullOrWhiteSpace(function))
            links = links.Where(l => string.Equals(l.PartnerFunction, function.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (links.Count == 0) return links;

        var partnerIds = links.Select(l => l.PartnerCustomer).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (partnerIds.Count == 0) return links;

        var partners = await EnrichAsync(await FetchByIdsAsync(partnerIds, partnerIds.Count));
        var byId = partners.ToDictionary(p => p.BusinessPartnerId);
        return links
            .Select(l => byId.TryGetValue(l.PartnerCustomer, out var bp) ? l with { Partner = bp } : l)
            .ToList();
    }

    private static List<PartnerFunctionLink> ParsePartnerFunctionLinks(string json)
    {
        var list = new List<PartnerFunctionLink>();
        foreach (var item in EnumerateResults(json))
        {
            var customer = GetString(item, "Customer") ?? "";
            var fn = GetString(item, "PartnerFunction") ?? "";
            var partner = GetString(item, "BPCustomerNumber");
            if (string.IsNullOrEmpty(partner)) continue;
            list.Add(new PartnerFunctionLink(customer, fn, partner));
        }
        return list;
    }

    /// <summary>
    /// Find Business Partners whose BusinessPartnerName contains <paramref name="nameContains"/>.
    /// A single substringof(wholeName) filter used to require SAP's stored name to contain the
    /// OCR'd name byte-for-byte, which rarely happens — OCR'd names carry the full legal wrapper
    /// ("บริษัท ... จำกัด (มหาชน)") and formatting that SAP's own BusinessPartnerName usually
    /// drops or spaces differently, so a real match was missed. Instead this breaks the name into
    /// its significant words (<see cref="SignificantWords"/>).
    /// Bug fixed (2026-09-10, take 1): the first cut of this OR'd every significant word together
    /// (substringof(w1,...) or substringof(w2,...) or ...). That's broad recall, but real-world
    /// company names often carry one very common word alongside the distinctive one — e.g.
    /// "Henkel (Thailand) Ltd." splits to ["Henkel","Thailand"], and "Thailand" alone matches a
    /// huge fraction of every customer in this tenant. OR'd together with no relevance ranking
    /// from SAP, the actual Henkel row can sit past the $top cutoff behind dozens of unrelated
    /// "...(Thailand)..." companies, so it silently never comes back. Fix: try an AND filter
    /// (every significant word must appear) first — far more selective, so the real match surfaces
    /// well within $top — and only fall back to the broader OR filter if AND finds nothing at all.
    /// Bug fixed (2026-09-10, take 2 — the actual root cause): "Henkel" AND "Thailand" *still*
    /// only returned unrelated "...Thailand..." companies, never Henkel itself — i.e. the AND
    /// filter was finding zero matches for "Henkel" alone and silently falling through to the OR
    /// fallback. This tenant's SAP UI shows the real record as "HENKEL (THAILAND) LTD." — all caps
    /// — while the document/search text is normal-case "Henkel". SAP Gateway's substringof() is
    /// case-SENSITIVE, so substringof('Henkel',BusinessPartnerName) never matches a value stored as
    /// "HENKEL...".
    /// Bug fixed (2026-09-10, take 3 — take 2's own fix didn't work either): tried wrapping both
    /// sides in OData's tolower() to normalize case generically, but this SAP Gateway service
    /// rejects it outright — live 400: "/IWBEP/CM_MGW_EXPR/000 Function tolower is not supported".
    /// Not every OData V2 service implements every string function. Per Megachem confirming SAP
    /// data is always stored upper-case on this tenant, the fix is simpler and doesn't need a
    /// filter-side function at all: <see cref="SubstringFilter"/> just upper-cases the search word
    /// in C# before building the filter — substringof('HENKEL',BusinessPartnerName) — relying on
    /// BusinessPartnerName itself always being upper-case rather than trying to normalize it
    /// server-side. Narrower assumption than tolower() would have been (breaks if some record turns
    /// out mixed/lower-case after all), but it's what this tenant's data actually looks like and
    /// it's what the service can actually execute.
    /// </summary>
    public async Task<List<BusinessPartner>> FindByNameAsync(string nameContains, int top = 20)
    {
        var baseUrl = config.SapBusinessPartnerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return []; // simulation mode: Sap:BusinessPartner:BaseUrl not configured yet

        var words = SignificantWords(nameContains);
        if (words.Count == 0)
        {
            // nothing survived the stop-word filter — fall back to the raw string rather than searching for nothing
            return await EnrichAsync(await FetchByFilterAsync(baseUrl, SubstringFilter(nameContains), top));
        }

        if (words.Count > 1)
        {
            var andFilter = string.Join(" and ", words.Select(SubstringFilter));
            var andResults = await FetchByFilterAsync(baseUrl, andFilter, top);
            if (andResults.Count > 0) return await EnrichAsync(andResults);
        }

        // Single significant word, or the AND of all of them found nothing — OR is the broadest net.
        var orFilter = string.Join(" or ", words.Select(SubstringFilter));
        var orResults = await FetchByFilterAsync(baseUrl, orFilter, top);
        return await EnrichAsync(orResults);
    }

    /// <summary>substringof() check against BusinessPartnerName, upper-casing the search word
    /// first since this tenant's SAP data is always stored upper-case and this OData service
    /// doesn't support tolower()/toupper() as filter functions (see FindByNameAsync's 2026-09-10
    /// take-3 note) — so the case-matching has to happen on the C# side, not in the filter.</summary>
    private static string SubstringFilter(string word) =>
        $"substringof('{EscapeODataLiteral(word.ToUpperInvariant())}',BusinessPartnerName)";

    /// <summary>
    /// Diagnostic only — not called from any search path or the UI. Fetches one Business Partner
    /// by ID with no $select, returning SAP's complete raw response text untouched by our own
    /// parsing/model. Added 2026-09-10 after three rounds of guessing why "Henkel" substring
    /// matching kept failing (wrong field name, unsupported tolower(), then a wrong assumption
    /// about case) — rather than guess a fourth time, this lets us see exactly what
    /// BusinessPartnerName (and every other field) actually contains for a specific record, e.g.
    /// GET /api/sap/business-partner/1000053/raw. Safe to remove once the real cause is confirmed.
    /// </summary>
    public async Task<string> GetRawByIdAsync(string businessPartnerId)
    {
        var baseUrl = config.SapBusinessPartnerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return "{}";
        var clean = businessPartnerId.Trim();
        var url = $"{baseUrl.TrimEnd('/')}/A_BusinessPartner('{Uri.EscapeDataString(clean)}')?$format=json";
        return await GetJsonAsync(url);
    }

    private async Task<List<BusinessPartner>> FetchByFilterAsync(string baseUrl, string filter, int top)
    {
        var url = $"{baseUrl.TrimEnd('/')}/A_BusinessPartner?$filter={Uri.EscapeDataString(filter)}&$top={top}";
        var text = await GetJsonAsync(url);
        return ParseResults(text);
    }

    // Legal-entity wrapper words that show up in almost every Thai/English company name and add
    // nothing distinctive to a search (every customer has "จำกัด"/"Ltd" in its name) — stripping
    // them leaves the words that actually identify *this* company.
    private static readonly HashSet<string> NameStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "บริษัท", "จำกัด", "มหาชน", "หจก", "ห้างหุ้นส่วน", "ห้างหุ้นส่วนจำกัด", "สำนักงานใหญ่", "สาขา",
        "co", "co.", "ltd", "ltd.", "limited", "company", "plc", "plc.", "inc", "inc.", "corp", "corp.",
        "corporation", "public",
    };

    /// <summary>
    /// Splits a name into its significant search words: breaks on whitespace and common
    /// punctuation, drops <see cref="NameStopWords"/> and anything shorter than 2 characters
    /// (single letters/initials are too common to narrow a search), removes duplicates, and caps
    /// the count so the resulting OData $filter stays a reasonable length/query cost. Word order
    /// is preserved.
    /// </summary>
    private static List<string> SignificantWords(string name, int maxWords = 6)
    {
        var raw = name.Split(new[] { ' ', '\t', '\n', '\r', '(', ')', ',', '.', '-', '/', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var w in raw)
        {
            if (w.Length < 2 || NameStopWords.Contains(w) || !seen.Add(w)) continue;
            words.Add(w);
            if (words.Count >= maxWords) break;
        }
        return words;
    }

    // Runs every search result (by name, or the Ship-to/Sold-to links — FindByTaxIdAsync fills in
    // its own Tax ID directly and skips this) through the same enrichment so the UI always has
    // address + Tax ID to show, no matter which path found the partner. Address and Tax ID live on
    // separate OData entities with no way to join them into one request, but neither depends on
    // the other, so the two round trips run CONCURRENTLY (Task.WhenAll) rather than one after
    // another — this costs the same wall-clock time as the address-only lookup it replaced, not
    // double, which matters since every extra SAP round trip is felt directly as UI search latency.
    private async Task<List<BusinessPartner>> EnrichAsync(List<BusinessPartner> results)
    {
        if (results.Count == 0) return results;
        var addressTask = FetchAddressMapAsync(results);
        var taxIdTask = FetchTaxIdMapAsync(results);
        await Task.WhenAll(addressTask, taxIdTask);
        var addrByBp = addressTask.Result;
        var taxByBp = taxIdTask.Result;

        return results
            .Select(r =>
            {
                var withAddr = addrByBp.TryGetValue(r.BusinessPartnerId, out var a)
                    ? r with { AddressCity = a.city, AddressStreet = a.street }
                    : r;
                return taxByBp.TryGetValue(r.BusinessPartnerId, out var t) && !string.IsNullOrWhiteSpace(t)
                    ? withAddr with { TaxId = t }
                    : withAddr;
            })
            .ToList();
    }

    /// <summary>
    /// TH3 (branch-code tax number) turned out to be identical across this tenant's duplicate
    /// Business Partners, so it can't tell them apart — dropped. Address is the fallback: head
    /// office and its branches are, by definition, different physical locations, and address is a
    /// standard entity (not a custom/localization field), so it should hold for any tenant.
    /// Field names confirmed (2026-09-10) against a real A_BusinessPartnerAddress OData response
    /// from this tenant — CityName/StreetName below are exactly right, no guess.
    /// </summary>
    private async Task<Dictionary<string, (string? city, string? street)>> FetchAddressMapAsync(List<BusinessPartner> results)
    {
        var baseUrl = config.SapBusinessPartnerBaseUrl;
        var ids = results.Select(r => r.BusinessPartnerId).Distinct().ToList();
        var idFilter = string.Join(" or ", ids.Select(id => $"BusinessPartner eq '{EscapeODataLiteral(id)}'"));
        var url = $"{baseUrl.TrimEnd('/')}/A_BusinessPartnerAddress?$filter={Uri.EscapeDataString(idFilter)}" +
                  $"&$select=BusinessPartner,CityName,StreetName&$top={ids.Count}";
        try
        {
            var text = await GetJsonAsync(url);
            return ParseAddressMap(text);
        }
        catch
        {
            return new(); // address is a nice-to-have — never fail the main lookup over it
        }
    }

    private static Dictionary<string, (string? city, string? street)> ParseAddressMap(string json)
    {
        var map = new Dictionary<string, (string?, string?)>();
        foreach (var item in EnumerateResults(json))
        {
            var bp = GetString(item, "BusinessPartner");
            if (string.IsNullOrEmpty(bp)) continue;
            map[bp] = (GetString(item, "CityName"), GetString(item, "StreetName"));
        }
        return map;
    }

    /// <summary>
    /// Looks up each result's real SAP Tax ID from A_BusinessPartnerTaxNumber (BPTaxNumber) — the
    /// same entity FindByTaxIdAsync searches, but here read back the other direction: given a
    /// partner code, what Tax ID does SAP have on file for it. A partner can carry more than one
    /// tax-number row (different BPTaxType); the first one returned is kept, same "good enough"
    /// tradeoff as the address lookup above. One batched call regardless of result count (same
    /// pattern as FetchAddressMapAsync), not one call per row.
    /// </summary>
    private async Task<Dictionary<string, string?>> FetchTaxIdMapAsync(List<BusinessPartner> results)
    {
        var baseUrl = config.SapBusinessPartnerBaseUrl;
        var ids = results.Select(r => r.BusinessPartnerId).Distinct().ToList();
        var idFilter = string.Join(" or ", ids.Select(id => $"BusinessPartner eq '{EscapeODataLiteral(id)}'"));
        var url = $"{baseUrl.TrimEnd('/')}/A_BusinessPartnerTaxNumber?$filter={Uri.EscapeDataString(idFilter)}" +
                  $"&$select=BusinessPartner,BPTaxNumber&$top={Math.Max(ids.Count * 4, ids.Count)}";
        try
        {
            var text = await GetJsonAsync(url);
            return ParseTaxIdMap(text);
        }
        catch
        {
            return new(); // Tax ID is a nice-to-have here — never fail the main lookup over it
        }
    }

    private static Dictionary<string, string?> ParseTaxIdMap(string json)
    {
        var map = new Dictionary<string, string?>();
        foreach (var item in EnumerateResults(json))
        {
            var bp = GetString(item, "BusinessPartner");
            if (string.IsNullOrEmpty(bp) || map.ContainsKey(bp)) continue; // keep the first tax-number row per partner
            var tax = GetString(item, "BPTaxNumber");
            if (!string.IsNullOrWhiteSpace(tax)) map[bp] = tax;
        }
        return map;
    }

    /// <summary>Re-fetch full Business Partner rows (name etc.) for a small, known set of codes.</summary>
    private async Task<List<BusinessPartner>> FetchByIdsAsync(List<string> businessPartnerIds, int top)
    {
        var baseUrl = config.SapBusinessPartnerBaseUrl;
        // OR'd into one $filter so this is a single extra round trip regardless of how many
        // tax-number rows matched (normally at most one distinct BP per Tax ID anyway).
        var filter = string.Join(" or ", businessPartnerIds.Select(id => $"BusinessPartner eq '{EscapeODataLiteral(id)}'"));
        var url = $"{baseUrl.TrimEnd('/')}/A_BusinessPartner?$filter={Uri.EscapeDataString(filter)}&$top={Math.Max(top, businessPartnerIds.Count)}";

        var text = await GetJsonAsync(url);
        return ParseResults(text);
    }

    private async Task<string> GetJsonAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var resp = await httpClient.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"SAP Business Partner lookup failed: {(int)resp.StatusCode} {Trunc(text, 500)}");

        return text;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        // Sap:BusinessPartner:AuthHeader lets this one read-only service use its own
        // communication-user credentials ("Basic <base64>") without touching the write-side
        // Sap:User/Sap:Password used for posting Sales Orders/Invoices. Falls back to those
        // when left blank.
        if (!string.IsNullOrWhiteSpace(config.SapBusinessPartnerAuthHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", config.SapBusinessPartnerAuthHeader);
            return;
        }
        var authBytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    private static List<string> ParseBusinessPartnerIds(string json)
    {
        var ids = new List<string>();
        foreach (var item in EnumerateResults(json))
        {
            var id = GetString(item, "BusinessPartner");
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return ids;
    }

    private static List<BusinessPartner> ParseResults(string json)
    {
        var list = new List<BusinessPartner>();
        foreach (var item in EnumerateResults(json))
        {
            list.Add(new BusinessPartner(
                BusinessPartnerId: GetString(item, "BusinessPartner") ?? "",
                BusinessPartnerName: GetString(item, "BusinessPartnerName") ?? "",
                BusinessPartnerFullName: GetString(item, "BusinessPartnerFullName"),
                BusinessPartnerIsBlocked: GetBool(item, "BusinessPartnerIsBlocked")
                // AddressCity/AddressStreet/TaxId filled in afterwards by EnrichAsync/FetchAddressMapAsync (separate entities)
            ));
        }
        return list;
    }

    /// <summary>OData V2 verbose JSON: { "d": { "results": [...] } }. Tolerates a bare "value"
    /// array too, in case this ever points at an OData V4 service instead.</summary>
    private static IEnumerable<JsonElement> EnumerateResults(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement.Clone();

        JsonElement results = default;
        var found = false;
        if (root.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var r))
        {
            results = r; found = true;
        }
        else if (root.TryGetProperty("value", out var v))
        {
            results = v; found = true;
        }
        if (!found || results.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in results.EnumerateArray())
            yield return item;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // OData V2 booleans come back as the JSON literal true/false OR (older services) the
    // string "true"/"false" — tolerate both.
    private static bool GetBool(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase) || v.GetString() == "X",
            _ => false,
        };
    }

    private static string EscapeODataLiteral(string s) => s.Replace("'", "''");
    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s[..n]);
}
