using MgtOcr.Api.Auth;
using MgtOcr.Api.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Json;
using MgtOcr.Core.Config;
using MgtOcr.Core.Mapping;
using MgtOcr.Data;
using MgtOcr.Zoho;
using Microsoft.AspNetCore.Mvc;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Api.Controllers;

// Zoho-side counterpart to DocumentsController's SAP "/post" action — deliberately its own
// controller/file rather than a branch inside PostDocument, since this project's standing rule is
// that DocumentsController.PostDocument / sap.PostAsync must never be touched. Creates a Zoho CRM
// "Sales Orders" record from a document, linked via lookup to the Deal the person picked on the
// Deal-comparison card (see ZohoDealCard / GET /api/zoho/deals/by-account-code). Field names are
// all confirmed against Megachem's Zoho CRM user manual — see ZohoSalesOrderClient's header
// comment for the full list and what's deliberately excluded.
//
// Every value that would otherwise be silently auto-derived (Subject, Customer PO/Customer_Ref,
// Delivery_Date, Payment_Terms/Payment_Currency/Incoterms/Tax_ID sourced from the matched
// Account, and each line's Quantity/Unit Price/Unit/Description) is exposed by GET .../preview
// as an editable default the person can review and change before POSTing — nothing here is
// forced through un-editable. Payment_Terms/Payment_Currency options come from Zoho's own live
// field metadata (ZohoSalesOrderClient.GetPicklistOptionsAsync), not a hand-typed list, so the
// dropdown always matches whatever picklist Megachem's Zoho admin has configured.
[ApiController]
[Route("api/zoho")]
[ServiceFilter(typeof(DepartmentAccessFilter))]
public class ZohoSalesOrderController(
    DocumentRepository repo,
    MasterRepository masterRepo,
    ZohoSalesOrderClient soClient,
    ZohoDealClient dealClient,
    ZohoAccountClient accountClient,
    ZohoQuoteClient quoteClient,
    ZohoMaterialClient materialClient,
    ZohoLastPriceClient lastPriceClient,
    ZohoClient zohoClient,
    UserRepository userRepo,
    AppConfig config,
    ICurrentUserAccessor currentUser,
    FileArchiveRepository archive,
    SalesOrderPostRepository soPosts,
    MgtOcr.Api.Services.ReadHintService readHints,
    ILogger<ZohoSalesOrderController> log) : ControllerBase
{
    // MaterialId, when present, is a person confirming (via the AI-assisted "suggest a match"
    // tool on a line the automatic code/description match skipped) which of this Deal's own Items
    // is the right one -- Create still only accepts it if it's actually one of deal.Items's own
    // MaterialId values (see Create below), so this can never smuggle in an arbitrary Zoho id.
    // Deal id the page sends when the document has no Deal (Megachem rule, 27 Oct 2026: no Deal =
    // not WCD -> the send opens a new Deal first, then a Quote, then the Sales Order).
    public const string NewDealId = "__new__";

    // One PO may hold items of several Deals, and some items may have no Deal yet (Megachem, 16 Nov 2026).
    // Each line carries its Deal key -- the page's lineDeals, else the line's saved extra.zohoDeal, else the
    // request's dealId: a Zoho Deal id, or "__new__", "__new__2" ... for a new Deal Send opens (CS groups
    // the items into Deals on the page, like the Split Sales Order board). A Zoho Sales Order links to one
    // Deal, so every Deal gets its own Deal -> Quote -> Sales Order(s) chain.
    private static bool IsNewDealKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.StartsWith(NewDealId, StringComparison.Ordinal);

    // itemNo -> Deal key of every line, in line order.
    private static Dictionary<string, string> LineDealKeys(List<Dictionary<string, object?>> lines, string? defaultDealId,
        IReadOnlyDictionary<string, string>? requested)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var itemNo = SoDeliveryGroups.ItemNoOf(lines[i], i);
            var key = requested is not null && requested.TryGetValue(itemNo, out var r) ? (r ?? "").Trim() : "";
            if (key.Length == 0) key = SoDeliveryGroups.LineDeal(lines[i]);
            if (key.Length == 0) key = (defaultDealId ?? "").Trim();
            map[itemNo] = key;
        }
        return map;
    }

    private static void RequireDeals(Dictionary<string, string> keys)
    {
        var missing = keys.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList();
        if (keys.Count == 0 || missing.Count == keys.Count) throw new HttpApiException(400, "A Deal must be selected first");
        if (missing.Count > 0)
            throw new HttpApiException(400, $"Item {string.Join(", ", missing)} has no Deal — put it in a Deal on the Deal (Zoho) card first");
    }

    // The Deals of the document in first-seen line order, each with its lines' itemNos.
    private static List<(string Key, HashSet<string> ItemNos)> DealParts(Dictionary<string, string> keys)
    {
        var parts = new List<(string Key, HashSet<string> ItemNos)>();
        foreach (var (itemNo, key) in keys)
        {
            var i = parts.FindIndex(p => p.Key == key);
            if (i < 0) parts.Add((key, new HashSet<string>(StringComparer.Ordinal) { itemNo }));
            else parts[i].ItemNos.Add(itemNo);
        }
        return parts;
    }

    private static HashSet<string> ItemNosOf(Dictionary<string, string> keys, string dealKey) =>
        keys.Where(kv => kv.Value == dealKey).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

    // Preview's ?lineDeals= (JSON object itemNo -> Deal key); null when absent or unreadable.
    private static Dictionary<string, string>? ParseLineDeals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (JsonException) { return null; }
    }

    public record LineOverride(string ItemNo, decimal? Quantity, decimal? UnitPrice, string? Unit, string? Description, string? MaterialId);

    public record CreateSalesOrderRequest(
        string DealId,
        string? Subject,
        string? CustomerRef,
        string? DeliveryDate,
        string? PaymentTerms,
        string? PaymentCurrency,
        string? Incoterms,
        string? TaxId,
        ZohoShipToInfo? ShipTo,
        List<LineOverride>? Lines,
        string? Remarks = null,
        // The person confirmed sending on a Deal whose Account isn't this document's customer
        // (a Deal picked through the manual name search). See the account check in BuildDefaultsAsync.
        bool AcceptOtherAccount = false,
        // Ms_User.UserID of the Sales person CS picked as Owner of a NEW Deal (12 Nov 2026), from the
        // Sales Organization 1000 list. The Zoho user is found by Email, then English name. Blank =
        // the customer's Account Owner, as before. Ignored when the document already has a Deal.
        // With several new Deals on one PO, the same Sales owns each of them.
        string? SalesUserId = null,
        // itemNo -> Deal key per line (16 Nov 2026): a Zoho Deal id, or "__new__", "__new__2" ... Lines not
        // listed use their saved extra.zohoDeal, else DealId.
        Dictionary<string, string>? LineDeals = null,
        // New Deal key ("__new__", "__new__2" ...) -> Forecast Status "Yes" / "No" that CS chose for it on
        // Send (16 Nov 2026). Required for every new Deal -- there is no default.
        Dictionary<string, string>? NewDealForecast = null,
        // Several Deals on one PO (19 Nov 2026): Deal key -> Description for that Deal's Sales Orders
        // ("" = none). A Deal not listed uses Remarks (else the document's Remark).
        Dictionary<string, string?>? DealRemarks = null);

    // CS's Forecast Status for a new Deal: "Yes" / "No", or null when not chosen (Send then refuses).
    private static string? NewDealForecastOf(CreateSalesOrderRequest body, string dealKey)
    {
        if (body.NewDealForecast is null || !body.NewDealForecast.TryGetValue(dealKey, out var v)) return null;
        v = (v ?? "").Trim();
        return v.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "Yes"
            : v.Equals("No", StringComparison.OrdinalIgnoreCase) ? "No" : null;
    }

    private record MatchedLine(
        string ItemNo, string? Desc, string? ExtCode, string MaterialId, string? MaterialName,
        string? MaterialCode, string? MaterialGroup, string? ShipVia, string? Stock, string? LeadTimeDays,
        decimal Quantity, decimal? UnitPrice, string? Unit, decimal? ConversionRatio, string? SubUnit,
        // Same source/column as SAP's Item Note 1 (line.extra.itemNote1) -- sent to Zoho's
        // Status_Note column on the Ordered Items subform. See ZohoSalesOrderLine.StatusNote.
        string? ItemNote1);

    private record SkippedLine(string ItemNo, string? Desc, string? ExtCode, string Reason);

    private record ZohoUomValue(decimal Quantity, decimal? UnitPrice, string? Unit);

    private static ZohoUomValue ConvertZohoUom(
        Dictionary<string, object?> line, ZohoDealItem item,
        IReadOnlyList<Dictionary<string, object?>> rules, string salesOrg, bool requireConversion)
    {
        var qty = (decimal)Num(line.Get("qty"));
        var priceObj = line.Get("price");
        decimal? price = priceObj is null ? null : (decimal)Num(priceObj);
        var docUnit = line.GetStr("uom").Trim();
        var targetUnit = (item.Unit ?? "").Trim();
        if (docUnit.Length == 0 || targetUnit.Length == 0
            || docUnit.Equals(targetUnit, StringComparison.OrdinalIgnoreCase))
            return new(qty, price, targetUnit.Length > 0 ? targetUnit : docUnit);

        var materialCode = (item.MaterialCode ?? "").Trim();
        var rule = rules
            .Where(r => string.IsNullOrEmpty(r.GetStr("SalesOrg")) || r.GetStr("SalesOrg") == salesOrg)
            .OrderByDescending(r => r.GetStr("SalesOrg") == salesOrg)
            .FirstOrDefault(r =>
                (r.GetStr("MaterialCode") == materialCode || r.GetStr("MaterialCodeSAP") == materialCode)
                && r.GetStr("ExtUom").Equals(docUnit, StringComparison.OrdinalIgnoreCase));

        decimal factor;
        // The selected Deal is authoritative for Zoho. Its Conversion_Ratio is the number of
        // document/sub-units in one Zoho selling unit (10 KG per BAG => factor 0.1 BAG per KG).
        // Prefer it over a stale locally saved rule; the local rule is only the fallback when the
        // Deal does not carry a ratio.
        if (item.ConversionRatio is > 0)
        {
            factor = 1m / item.ConversionRatio.Value;
        }
        else if (rule is not null)
        {
            var ruleUnit = rule.GetStr("SapUom").Trim();
            if (!ruleUnit.Equals(targetUnit, StringComparison.OrdinalIgnoreCase))
                throw new HttpApiException(400,
                    $"UoM rule for {materialCode} converts {docUnit} to {ruleUnit}, but the selected Zoho Deal uses {targetUnit}");
            factor = (decimal)Num(rule.Get("Factor"));
        }
        else
        {
            if (!requireConversion)
                return new(qty, price, docUnit);
            throw new HttpApiException(400,
                $"No unit conversion for material {materialCode}: {docUnit} → {targetUnit}. " +
                "Add it in Master → Unit Conversion (UoM), or set a Conversion Ratio on the Deal item or the Zoho Material, before viewing the payload or sending to Zoho");
        }

        if (factor <= 0)
            throw new HttpApiException(400, $"UoM conversion factor for material {materialCode} must be greater than zero");

        return new(
            Quantity: Math.Round(qty * factor, 3),
            UnitPrice: price is null ? null : Math.Round(price.Value / factor, 6),
            Unit: targetUnit);
    }

    private record Defaults(
        Dictionary<string, object?> Doc, ZohoDeal Deal,
        string Subject, string? CustomerRef, string? DeliveryDate,
        string? PaymentTerms, string? PaymentCurrency, string? Incoterms, string? TaxId,
        List<MatchedLine> Lines, List<SkippedLine> Skipped,
        List<Dictionary<string, object?>> UomRules, string SalesOrg, string? Remarks = null, bool NewDeal = false,
        List<string>? QuoteRefs = null, string AccountName = "", List<string>? DealCodes = null);

    private async Task<string> ActorAsync(CancellationToken ct = default) =>
        (await currentUser.RequireAsync(ct)).AuditName;

    // Exact (case-insensitive) label lookup against ZohoAccountClient.GetFullAccountFieldsAsync's
    // Flatten()'d field list (api_name "Payment_Terms" -> label "Payment Terms", etc.) — same
    // exact-reverse-of-Flatten approach ZohoAccountClient itself uses internally.
    private static string? Exact(List<(string Label, string? Value)> fields, string label) =>
        fields.FirstOrDefault(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase)).Value;

    // Everything a Sales Order needs, computed once and shared by both Preview and Create so the
    // two can never drift apart: the Deal/Account lookups, the header defaults (sourced from the
    // matched Account for Payment_Terms/Payment_Currency/Incoterms/Tax_ID, from the document for
    // Customer_Ref/Delivery_Date, per Megachem's picklist-safety decision), and the
    // document-line-to-Deal-Item matching (unaffected by anything the person edits in the
    // preview — a line's MaterialId/MaterialCode/etc always comes from a real matched Deal Item,
    // never from typed-in text, since Product_Name is a required Lookup with no Material search).
    // onlyItemNos: one PO, several Deals (16 Nov 2026) -- only the lines of this Deal are matched and sent.
    private async Task<Defaults> BuildDefaultsAsync(int docId, string dealId, bool requireUomConversion = false,
        bool enforceDealAccount = false, ISet<string>? onlyItemNos = null)
    {
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("module") != "SO")
            throw new HttpApiException(400, "Only Sales Order documents can be sent to Zoho as a Sales Order");

        // Zoho chain Deal -> Quote -> Sales Order (27 Oct 2026). No Deal at all = not WCD: a new Deal
        // is opened on the document's customer when the person sends (see EnsureDealAsync). Until
        // then it is a stand-in record with no id, no items and Forecast Status "No".
        var newDeal = IsNewDealKey(dealId);
        string? newDealAccountName = null;
        ZohoDeal? deal;
        if (newDeal)
        {
            var custCode = doc.GetStr("partnerCode").Trim();
            if (custCode.Length == 0)
                throw new HttpApiException(400, "Match the customer first — a new Deal is opened on this document's customer");
            var acc = await accountClient.FindByAccountCodeAsync(custCode)
                ?? throw new HttpApiException(400, $"Customer {custCode} was not found in Zoho CRM by Account Code — a new Deal cannot be opened");
            newDealAccountName = acc.AccountName;
            deal = new ZohoDeal(
                Id: "", DealName: "",
                Stage: ZohoDealClient.NewDealStage, AccountId: acc.AccountId, AccountCode: acc.AccountCode ?? custCode,
                CustomerRef: null, DeliveryDate: null, ClosingDate: null, Items: [], ForecastStatus: "No");
        }
        else
        {
            deal = await dealClient.GetByIdAsync(dealId);
        }
        if (deal is null)
            throw new HttpApiException(404, "The selected Deal was not found in Zoho — it may have been deleted, or its stage may have changed");
        if (string.IsNullOrEmpty(deal.AccountId))
            throw new HttpApiException(400, $"Deal \"{deal.DealName}\" has no Account linked in Zoho — cannot set Account Name on the Sales Order");

        var accountFields = await accountClient.GetFullAccountFieldsAsync(deal.AccountId);
        var paymentTerms = Exact(accountFields, "Payment Terms");
        var paymentCurrency = Exact(accountFields, "Payment Currency");
        var incoterms = Exact(accountFields, "Incoterms");
        var taxId = Exact(accountFields, "Tax ID");

        // The Deal must belong to this document's customer (12 Oct 2026). The page could still hold
        // the previous customer's Deal after the customer changed (lookup failed / still loading), and
        // the order was then created on the wrong Account. Same Account code passes; another code of
        // the same company (same Tax ID -- one customer with several Zoho codes) passes too.
        // Enforced on Create only; the manual Deal search can legitimately pick another Account's
        // Deal, so the page asks the person to confirm and then sends AcceptOtherAccount.
        var docCustomer = doc.GetStr("partnerCode").Trim();
        if (enforceDealAccount && !newDeal && docCustomer.Length > 0 && !string.IsNullOrWhiteSpace(deal.AccountCode)
            && !string.Equals(deal.AccountCode.Trim(), docCustomer, StringComparison.OrdinalIgnoreCase))
        {
            var docTax = Digits(((Dictionary<string, object?>)doc["header"]!).GetStr("customerTaxId"));
            if (docTax.Length == 0 || docTax != Digits(taxId))
                throw new HttpApiException(400,
                    $"Deal \"{deal.DealName}\" belongs to customer {deal.AccountCode}, but this document's customer is {docCustomer} — pick a Deal of this customer, or confirm sending on this Deal");
        }

        var header = (Dictionary<string, object?>)doc["header"]!;
        var salesOrg = await DocSalesOrgAsync(docId, doc);
        var companyCode = config.CompanyForSalesOrg(salesOrg)?.CompanyCode ?? salesOrg;
        var masterData = MgtOcr.Core.Mapping.MasterSchema.ForSalesOrg(
            await masterRepo.LoadForMappingAsync("SO", companyCode), companyCode);

        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var matched = new List<MatchedLine>();
        var skipped = new List<SkippedLine>();
        for (var li = 0; li < lines.Count; li++)
        {
            var l = lines[li];
            if (onlyItemNos is not null && !onlyItemNos.Contains(SoDeliveryGroups.ItemNoOf(l, li))) continue;
            var itemNo = l.Get("itemNo")?.ToString() ?? "";
            var desc = l.GetStr("desc");
            var extCode = l.GetStr("extCode");
            var mappedCode = l.GetStr("materialCode");
            // A line's Material Code (Deal search, Megachem 10 Nov 2026): the code on the PO (OCR/CS,
            // extCode) or the code mapped by Master Mapping / CS (materialCode) -- either may be the
            // Zoho Material Code. The PO code is tried first, then the mapped one, the same as the
            // page's Deal search (MappingCards lineCodes). Names/descriptions never substitute a
            // differently coded item. Several PO rows of one material (e.g. different delivery dates)
            // all use the same Deal item.
            var codes = new[] { extCode, mappedCode }
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var lineCode = codes.FirstOrDefault() ?? "";
            ZohoDealItem? item = null;
            foreach (var code in codes)
            {
                item = deal.Items.FirstOrDefault(it =>
                    !string.IsNullOrEmpty(it.MaterialCode) && string.Equals(it.MaterialCode, code, StringComparison.OrdinalIgnoreCase));
                if (item is not null) { lineCode = code; break; }
            }
            // Not WCD (Forecast Status No / blank, or a new Deal): a line that is not one of the Deal's
            // Items is looked up in Zoho Materials by its mapped material code, else the PO code
            // (Megachem, 27 Oct 2026). A WCD Deal (Forecast Status Yes) still only takes its own items.
            string? notFound = null;
            // A new Deal has no items yet: its lines always come from Zoho Materials, whatever Forecast
            // Status CS picks for it (16 Nov 2026).
            if ((item is null || string.IsNullOrEmpty(item.MaterialId)) && (!deal.IsWcd || newDeal))
            {
                if (codes.Count == 0)
                    notFound = "Not a Deal Item, and no material code is mapped on this line to look up in Zoho";
                else
                {
                    item = null;
                    var tried = new List<string>();
                    foreach (var code in codes.OrderByDescending(c => string.Equals(c, mappedCode.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        tried.Add(code);
                        try
                        {
                            var m = await materialClient.FindByCodeAsync(code);
                            if (m is not null) { item = MaterialAsItem(m); lineCode = code; notFound = null; break; }
                            notFound = $"Not a Deal Item, and material {string.Join(" / ", tried)} was not found in Zoho Materials";
                        }
                        catch (Exception e)
                        {
                            log.LogWarning(e, "Zoho Materials lookup failed for {Code} (doc {DocId})", code, docId);
                            notFound = $"Not a Deal Item, and the Zoho Materials lookup for {code} failed";
                        }
                    }
                }
            }
            if (item is null || string.IsNullOrEmpty(item.MaterialId))
            {
                skipped.Add(new SkippedLine(itemNo, desc, extCode, notFound
                    ?? (item is null
                        ? (deal.IsWcd ? "No matching Deal Item found for this line (Forecast (WCD) Deal: lines must be items of the Deal)" : "No matching Deal Item found for this line")
                        : "The matched Deal Item has no Zoho Material link")));
                continue;
            }

            var built = TryBuildMatchedLine(l, item, lineCode, masterData.Uoms, salesOrg, requireUomConversion);
            if (built is null)
            {
                skipped.Add(new SkippedLine(itemNo, desc, extCode, "Quantity is zero or blank on the document"));
                continue;
            }
            matched.Add(built);
        }

        if (newDeal && matched.Count > 0)
            deal = deal with { DealName = ZohoDealClient.BuildDealName(newDealAccountName!, matched.Select(l => l.MaterialCode)) };

        // Subjects (Megachem, 19 Nov 2026 -- ZohoSubjects): Quote = AccountName_MMYY; Sales Order =
        // AccountName_MMYY_All_Items when it holds the Deal's materials, else AccountName_MMYY_BillN. (Was
        // the Deal's name, 29 Oct 2026.) The Deal's materials: its Items, or -- a new Deal -- the lines it is
        // opened with. Here: the name these lines would get as ONE Sales Order, first Bill; the real
        // per-order name (and Bill number) is worked out per order on Send / Payload / Preview.
        var accountName = newDeal ? newDealAccountName ?? "" : Exact(accountFields, "Account Name") ?? "";
        if (string.IsNullOrWhiteSpace(accountName)) accountName = deal.AccountCode ?? "";
        var dealCodes = (newDeal ? matched.Select(l => l.MaterialCode) : deal.Items.Select(it => it.MaterialCode))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToList();
        var subject = ZohoSubjects.SalesOrder(accountName,
            ZohoSubjects.SameMaterials(matched.Select(l => l.MaterialCode), dealCodes), 1);

        // Delivery_Date: same rule as SapPayloadBuilder's RequestedDeliveryDate -- when every line
        // ships on one date (always true for a child of "split by delivery date"), that line date
        // wins over the header's, because a split child keeps the PARENT's header deliveryDate.
        var lineDates = LineDeliveryDates(onlyItemNos is null ? lines
            : lines.Where((x, i) => onlyItemNos.Contains(SoDeliveryGroups.ItemNoOf(x, i))).ToList());
        var deliveryDate = lineDates.Count == 1 ? lineDates[0]
            : header.GetStr("deliveryDate") is { Length: > 0 } dd ? dd : deal.DeliveryDate;

        return new Defaults(
            Doc: doc,
            Deal: deal,
            Subject: subject,
            // No PO number on the document -> "-" (Megachem rule 8, same as SAP). The Deal's own
            // Customer_Ref is another order's PO and must not be reused.
            CustomerRef: header.GetStr("poNo").Trim() is { Length: > 0 } poNo ? poNo : "-",
            DeliveryDate: deliveryDate,
            PaymentTerms: paymentTerms,
            PaymentCurrency: paymentCurrency,
            Incoterms: incoterms,
            TaxId: taxId,
            Lines: matched,
            Skipped: skipped,
            UomRules: masterData.Uoms,
            SalesOrg: salesOrg,
            // Document Remark is reviewed as Description and sent to Zoho's header Description.
            Remarks: header.GetStr("remark") is { Length: > 0 } remark ? remark : null,
            NewDeal: newDeal,
            QuoteRefs: DocQuoteRefs(header, lines),
            AccountName: accountName,
            DealCodes: dealCodes);
    }

    // Megachem Quote numbers the customer's PO refers to (27 Oct 2026): the OCR'd quoteRef plus any
    // "1QT0000266"-shaped text in the header or the lines.
    private static List<string> DocQuoteRefs(Dictionary<string, object?> header, List<Dictionary<string, object?>> lines)
    {
        var texts = header.Where(kv => kv.Key != "quoteRef").Select(kv => kv.Value as string)
            .Concat(lines.SelectMany(l => new[]
            {
                l.GetStr("desc"), l.GetStr("extCode"), (l.Get("extra") as Dictionary<string, object?>).GetStr("itemNote1"),
            }));
        return ZohoQuoteClient.QuoteRefsIn(texts, header.GetStr("quoteRef"));
    }

    // A Zoho Material used as a Sales Order line source when it isn't one of the Deal's Items
    // (non-WCD only). Carries the Material's own Unit / Conversion Ratio / Sub Unit, so the line is
    // converted to and sent in the Unit (Megachem, 29 Oct 2026), the same as a Deal item.
    private static ZohoDealItem MaterialAsItem(ZohoMaterial m) => new(
        MaterialName: m.Name, MaterialId: m.Id, MaterialCode: m.Code, MaterialDescription: m.Description,
        MaterialGroup: m.MaterialGroup, Quantity: null, Unit: m.Unit, ConversionRatio: m.ConversionRatio, SubUnit: m.SubUnit,
        UnitPrice: null, PriceSubUnit: null, LastPrice: null, TotalAmount: null, ShipVia: null, Stock: null,
        LeadTimeDays: null, SoCompleted: null, SoCancelled: null);

    // ── Quote step (27 Oct 2026): Deal -> Quote -> Sales Order ─────────────────────────────────
    private record QuotePlan(ZohoQuote? Quote, string Reason);

    // Which of the Deal's Quotes this document's Sales Order is made from (Megachem, 27 Oct 2026):
    // 1) a Quote whose Customer Ref is the document's PO no. (items matching wins among several);
    // 2) no PO match (e.g. a Quote made with "Convert to Quote" before the PO came): the newest Quote
    //    whose items match the document by the Deal rule (QuoteItemsMatch); 3) none -> a new Quote.
    private async Task<QuotePlan> PlanQuoteAsync(ZohoDeal deal, string? customerRef, IReadOnlyList<ZohoSalesOrderLine> lines,
        IReadOnlyList<string>? quoteRefs = null)
    {
        var refs = quoteRefs ?? [];
        var refNote = refs.Count > 0 ? $" (the PO mentions Quote {string.Join(", ", refs)}, which is not on this Deal)" : "";
        if (string.IsNullOrEmpty(deal.Id)) return new(null, "New Deal — a new Quote will be created" + refNote);
        // A Quote at Stage "Completed" is finished and never reused (Megachem, 11 Nov 2026); Open and
        // In Process Quotes are candidates.
        var quotes = (await quoteClient.FindByDealAsync(deal.Id))
            .Where(q => !string.Equals((q.Stage ?? "").Trim(), "Completed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // 0) The PO quotes Megachem's own Quote No. -- that Quote wins over every other rule.
        var byNo = quotes.FirstOrDefault(q => refs.Contains((q.QuoteNo ?? "").Trim().ToUpperInvariant()));
        if (byNo is not null) return new(byNo, $"The PO refers to Quote {byNo.QuoteNo}");
        if (quotes.Count == 0) return new(null, "The Deal has no open Quote (none, or all Completed) — a new Quote will be created" + refNote);
        var po = (customerRef ?? "").Trim();
        var byRef = po.Length > 0 && po != "-"
            ? quotes.Where(q => string.Equals((q.CustomerRef ?? "").Trim(), po, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<ZohoQuote>();
        var hit = byRef.FirstOrDefault(q => QuoteItemsMatch(q, lines, deal.IsWcd));
        if (hit is not null) return new(hit, $"Customer Ref {po} and items match");
        if (byRef.Count > 0) return new(byRef[0], $"Customer Ref {po} matches");
        hit = quotes.FirstOrDefault(q => QuoteItemsMatch(q, lines, deal.IsWcd));
        if (hit is not null)
            return new(hit, deal.IsWcd ? "Items match (Forecast (WCD): materials)" : "Items match (materials and quantities)");
        return new(null, $"None of the Deal's {quotes.Count} Quote(s) matches this document's Customer Ref or items — a new Quote will be created" + refNote);
    }

    // Does an existing Quote fit this document? Same rule as picking the Deal (Megachem, 11 Nov 2026):
    // every document line's material must be on the Quote (the PO may carry only some of the Quote's
    // items). Forecast (WCD) Deal: materials only, quantities may differ. Not WCD: the quantity per
    // material must match too (document rows of one material are added up, likewise the Quote's
    // items -- one item may be split over several delivery dates). Unit prices are not compared.
    private static bool QuoteItemsMatch(ZohoQuote q, IReadOnlyList<ZohoSalesOrderLine> lines, bool isWcd)
    {
        if (lines.Count == 0 || q.Items.Count == 0) return false;
        var groups = new List<(ZohoSalesOrderLine First, decimal Qty)>();
        foreach (var l in lines)
        {
            if (!q.Items.Any(it => SameMaterial(it, l))) return false;
            var i = groups.FindIndex(g => SameLineMaterial(g.First, l));
            if (i < 0) groups.Add((l, l.Quantity));
            else groups[i] = (groups[i].First, groups[i].Qty + l.Quantity);
        }
        if (isWcd) return true;
        foreach (var g in groups)
        {
            var quoted = q.Items.Where(it => SameMaterial(it, g.First)).Sum(it => it.Quantity ?? 0m);
            if (Math.Round(quoted, 3) != Math.Round(g.Qty, 3)) return false;
        }
        return true;
    }

    private static bool SameLineMaterial(ZohoSalesOrderLine a, ZohoSalesOrderLine b) =>
        (!string.IsNullOrEmpty(a.MaterialId) && a.MaterialId == b.MaterialId)
        || (!string.IsNullOrWhiteSpace(a.MaterialCode) && string.Equals(a.MaterialCode.Trim(), (b.MaterialCode ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        || (string.IsNullOrWhiteSpace(a.MaterialId) && string.IsNullOrWhiteSpace(a.MaterialCode)
            && string.Equals((a.Description ?? "").Trim(), (b.Description ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool SameMaterial(ZohoQuoteItem it, ZohoSalesOrderLine l) =>
        (!string.IsNullOrEmpty(it.MaterialId) && it.MaterialId == l.MaterialId)
        || (!string.IsNullOrWhiteSpace(it.MaterialCode) && string.Equals(it.MaterialCode.Trim(), (l.MaterialCode ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(it.MaterialName) && string.Equals(it.MaterialName.Trim(), (l.Description ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    // Last price per Material Code for this customer from Zoho's Last Prices module; empty when the
    // lookup fails (a reference figure only -- never blocks a preview or a send).
    private async Task<Dictionary<string, ZohoLastPrice>> LastPricesAsync(string? accountCode, IEnumerable<string?> codes, int docId)
        => (await LastPricesWithErrorAsync(accountCode, codes, docId)).Prices;

    // Same, plus the reason when Zoho could not be read (shown on the preview so a wrong module /
    // field name is visible instead of an empty column).
    private async Task<(Dictionary<string, ZohoLastPrice> Prices, string? Error)> LastPricesWithErrorAsync(
        string? accountCode, IEnumerable<string?> codes, int docId)
    {
        try { return (await lastPriceClient.FindForAccountAsync(accountCode ?? "", codes.Select(c => c ?? "")), null); }
        catch (Exception e)
        {
            log.LogWarning(e, "Zoho Last Prices lookup failed for doc {DocId}", docId);
            return (new Dictionary<string, ZohoLastPrice>(StringComparer.OrdinalIgnoreCase), e.Message);
        }
    }

    private static ZohoSalesOrderLine ToSoLine(MatchedLine l) => new(
        MaterialId: l.MaterialId, Description: l.Desc, MaterialCode: l.MaterialCode, MaterialGroup: l.MaterialGroup,
        ShipVia: l.ShipVia, Stock: l.Stock, LeadTimeDays: l.LeadTimeDays, Quantity: l.Quantity, UnitPrice: l.UnitPrice,
        Unit: l.Unit, ConversionRatio: l.ConversionRatio, SubUnit: l.SubUnit, StatusNote: l.ItemNote1);

    // Delivery date for the Deal / Quote headers (mandatory there): the Sales Order's own date, else
    // the earliest group date, else today.
    private static string HeaderDate(string? deliveryDate, IEnumerable<SoGroup> groups) =>
        deliveryDate is { Length: > 0 } d ? d
        : groups.Select(g => g.Date).Where(x => x.Length > 0).OrderBy(x => x).FirstOrDefault()
          ?? DateTime.Today.ToString("yyyy-MM-dd");

    private Task<JsonObject> BuildNewDealRecordAsync(Assembled a, IEnumerable<SoGroup> groups) =>
        dealClient.BuildCreateRecordAsync(a.Deal.AccountId!, a.Deal.AccountCode,
            HeaderDate(a.DeliveryDate, groups), a.CustomerRef is { Length: > 0 } cr && cr != "-" ? cr : null,
            a.PaymentTerms, a.PaymentCurrency, a.Incoterms,
            a.SoLines.Select(l => new ZohoNewDealLine(l.MaterialId, l.MaterialCode, l.Description, l.MaterialGroup,
                l.Quantity, l.UnitPrice, l.Unit, l.LastPrice)).ToList(), a.OwnerId,
            forecastStatus: a.Deal.IsWcd ? "Yes" : "No");

    // Megachem (12 Nov 2026): Zoho rejects a new Deal whose Owner is not an active Zoho user (e.g. the
    // customer's Account Owner has left), so check the owner before opening the Deal and tell CS to
    // pick the Sales person on the page. If Zoho's user list can't be read (e.g. the refresh token
    // lacks ZohoCRM.users.READ) the check is skipped and Zoho decides, as before.
    // Megachem (12 Nov 2026): the Quote and Sales Orders belong to the same Sales as their Deal. The Deal
    // Owner's Zoho user id: read off the Deal itself once it exists; for a Deal that Send will open,
    // the Sales CS picked, else the customer's Account Owner (what BuildCreateRecordAsync uses).
    // Null when it can't be read -- the record is then sent without an Owner, as before.
    private async Task<string?> DealOwnerIdAsync(string? dealId, Assembled a)
    {
        static string? OwnerIdOf(JsonObject? rec) =>
            rec?["Owner"] is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
        try
        {
            if (!string.IsNullOrWhiteSpace(dealId)) return OwnerIdOf(await dealClient.GetRecordAsync(dealId));
            return a.OwnerId ?? OwnerIdOf(await accountClient.GetRecordAsync(a.Deal.AccountId!));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the Deal Owner for Deal {DealId}", dealId);
            return null;
        }
    }

    // Zoho refused the Owner (e.g. that user was deactivated after the Deal was made).
    private static bool OwnerRejected(ZohoUpsertResult r) =>
        r.Status != "success" && (r.Message ?? "").Contains("field=Owner", StringComparison.OrdinalIgnoreCase);

    // Ms_User Sales person (Sales Organization 1000) -> the Zoho user id for the new Deal's Owner.
    private async Task<string> ResolveSalesOwnerAsync(string salesUserId)
    {
        var sales = await userRepo.FindSalesUserAsync(salesUserId, ZohoAccountController.MgtSalesOrg)
            ?? throw new HttpApiException(400, "The selected Sales person is not an active user of Sales Organization 1000. Pick another Sales — nothing was sent.");
        ZohoUser? zohoUser;
        try { zohoUser = await zohoClient.FindActiveUserAsync(sales.Email, sales.Name); }
        catch (Exception e)
        {
            throw new HttpApiException(502, $"Could not look up Sales \"{sales.Name}\" in Zoho CRM: {e.Message} — nothing was sent.");
        }
        return zohoUser?.Id ?? throw new HttpApiException(400,
            $"Sales \"{sales.Name}\"{(sales.Email is null ? "" : $" ({sales.Email})")} was not found as an active user in Zoho CRM (checked by email, then English name). Check the email / name in Zoho or pick another Sales — nothing was sent.");
    }

    private async Task CheckNewDealOwnerAsync(Assembled a)
    {
        string? ownerId = a.OwnerId, ownerName = null;
        if (ownerId is null)
        {
            var account = await accountClient.GetRecordAsync(a.Deal.AccountId!);
            if (account?["Owner"] is JsonObject owner)
            {
                ownerId = owner["id"] is JsonValue idv && idv.TryGetValue<string>(out var id) ? id : null;
                ownerName = owner["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) ? name : null;
            }
        }
        if (string.IsNullOrWhiteSpace(ownerId)) return; // BuildCreateRecordAsync reports a missing owner
        List<ZohoUser> users;
        try { users = await zohoClient.GetActiveUsersAsync(); }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the Zoho users to check the owner of the new Deal");
            return;
        }
        if (users.Count == 0 || users.Any(u => u.Id == ownerId)) return;
        throw new HttpApiException(400, a.OwnerId is null
            ? $"The customer's Account Owner{(string.IsNullOrWhiteSpace(ownerName) ? "" : $" \"{ownerName}\"")} is not an active user in Zoho CRM, so Zoho cannot assign the new Deal to them. Pick the Sales person (Deal Owner) above, then send again — nothing was sent."
            : "The selected Sales person is not an active user in Zoho CRM any more. Reload the Sales list, pick another, then send again — nothing was sent.");
    }

    private Task<JsonObject> BuildNewQuoteRecordAsync(Assembled a, string? dealId, ZohoShipToInfo? shipTo, IEnumerable<SoGroup> groups) =>
        quoteClient.BuildRecordAsync(dealId, a.Deal.AccountId!, a.QuoteSubject, a.Deal.AccountCode, a.TaxId, a.CustomerRef,
            HeaderDate(a.DeliveryDate, groups), a.PaymentTerms, a.PaymentCurrency, a.Incoterms, shipTo, a.SoLines);

    // Step 1 of a send without a Deal: open the (non-WCD) Deal. A retry after a failed later step
    // reuses the Deal an earlier attempt opened (same customer, same Customer Ref, not WCD, stage
    // NewDealStage) instead of opening a second one.
    // usedDealIds: Deals already used by this send (16 Nov 2026: several new Deals of one PO share the
    // Customer Ref, so an earlier attempt's Deal is reused only when it holds the same materials).
    private async Task<ZohoDeal> EnsureDealAsync(int docId, Assembled a, List<SoGroup> groups, string actor,
        ISet<string>? usedDealIds = null)
    {
        var po = (a.CustomerRef ?? "").Trim();
        if (po.Length > 0 && po != "-" && !string.IsNullOrWhiteSpace(a.Deal.AccountCode))
        {
            try
            {
                var open = await dealClient.FindOpenDealsByAccountCodeAsync(a.Deal.AccountCode!);
                var candidates = open.Where(d => d.IsWcd == a.Deal.IsWcd && ZohoDealStage.IsOpenOrDraft(d.Stage)
                    && string.Equals((d.CustomerRef ?? "").Trim(), po, StringComparison.OrdinalIgnoreCase)
                    && !(usedDealIds?.Contains(d.Id) ?? false)).ToList();
                static string Code(string? c) => (c ?? "").Trim().ToUpperInvariant();
                var wanted = a.SoLines.Select(l => Code(l.MaterialCode)).Where(c => c.Length > 0).ToHashSet();
                // New Deals now open at Draft/Qualification like any other Deal (16 Nov 2026), so an earlier
                // attempt's Deal is recognised by its materials too, never by Customer Ref alone.
                var earlier = candidates.FirstOrDefault(d =>
                        d.Items.Select(it => Code(it.MaterialCode)).Where(c => c.Length > 0).ToHashSet().SetEquals(wanted));
                if (earlier is not null && !string.IsNullOrEmpty(earlier.AccountId))
                {
                    await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
                        detail: $"Using Zoho Deal \"{earlier.DealName}\" (Customer Ref {po}, Forecast Status {(a.Deal.IsWcd ? "Yes" : "No")}) opened earlier instead of opening another");
                    return earlier;
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not check Zoho for an earlier Deal of doc {DocId}", docId);
            }
        }

        await CheckNewDealOwnerAsync(a);
        JsonObject record;
        try { record = await BuildNewDealRecordAsync(a, groups); }
        catch (TimeoutException)
        {
            throw new HttpApiException(504, "Zoho took too long to return the Deal field definitions. No new Deal, Quote or Sales Order was created. Please try sending again.");
        }
        ZohoUpsertResult res;
        try { res = await dealClient.CreateAsync(record); }
        catch (Exception e) { res = new ZohoUpsertResult { SapKey = "", ZohoId = "", Status = "error", Message = e.Message }; }
        if (res.Status != "success" || string.IsNullOrEmpty(res.ZohoId))
        {
            await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
                detail: $"Could not open a new Zoho Deal for customer {a.Deal.AccountCode}: {res.Message}");
            throw new HttpApiException(400, $"Could not open a new Deal in Zoho CRM: {res.Message} — nothing was sent");
        }
        var created = await dealClient.GetByIdAsync(res.ZohoId);
        if (string.IsNullOrWhiteSpace(created?.DealName))
            throw new HttpApiException(400, $"Deal {res.ZohoId} was created, but its name could not be read back from Zoho. Please retry; no Quote or Sales Order was created.");
        var deal = created is null || string.IsNullOrEmpty(created.AccountId)
            ? a.Deal with { Id = res.ZohoId }
            : created;
        await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
            detail: $"Opened Zoho Deal \"{deal.DealName}\" (record {res.ZohoId}, Forecast Status {(a.Deal.IsWcd ? "Yes" : "No")}, Stage {ZohoDealClient.NewDealStage}) — chosen by CS on Send");
        return deal;
    }

    // Step 2: the Deal's Quote for this document (PlanQuoteAsync), or a new one (Stage Open) with
    // every line of the document. One Quote per document; each Sales Order of the document links to it.
    private async Task<(string Id, string? No)> EnsureQuoteAsync(int docId, ZohoDeal deal, Assembled a,
        ZohoShipToInfo? shipTo, List<SoGroup> groups, string actor, string? ownerId = null)
    {
        QuotePlan plan;
        try { plan = await PlanQuoteAsync(deal, a.CustomerRef, a.SoLines, a.QuoteRefs); }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the Quotes of Deal {DealId} (doc {DocId})", deal.Id, docId);
            throw new HttpApiException(400, $"Could not read the Quotes of Deal \"{deal.DealName}\" from Zoho CRM: {e.Message} — nothing was sent");
        }
        if (plan.Quote is { } q)
        {
            await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
                detail: $"Using Zoho Quote {q.QuoteNo ?? q.Id} of Deal \"{deal.DealName}\" ({plan.Reason})");
            return (q.Id, q.QuoteNo);
        }

        var record = await BuildNewQuoteRecordAsync(a, deal.Id, shipTo, groups);
        if (!string.IsNullOrWhiteSpace(ownerId)) record["Owner"] = new JsonObject { ["id"] = ownerId };
        ZohoUpsertResult res;
        try { res = await quoteClient.CreateAsync(record); }
        catch (Exception e) { res = new ZohoUpsertResult { SapKey = "", ZohoId = "", Status = "error", Message = e.Message }; }
        if (OwnerRejected(res))
        {
            // Deal Owner no longer usable in Zoho: create the Quote without it rather than fail.
            await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
                detail: $"Zoho refused the Deal Owner as Quote owner ({res.Message}) — Quote created without it");
            record.Remove("Owner");
            try { res = await quoteClient.CreateAsync(record); }
            catch (Exception e) { res = new ZohoUpsertResult { SapKey = "", ZohoId = "", Status = "error", Message = e.Message }; }
        }
        if (res.Status != "success" || string.IsNullOrEmpty(res.ZohoId))
        {
            await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
                detail: $"Could not create a Zoho Quote on Deal \"{deal.DealName}\": {res.Message}");
            throw new HttpApiException(400, $"Could not create the Quote in Zoho CRM: {res.Message} — no Sales Order was sent");
        }
        string? no = null;
        try { no = await quoteClient.QuoteNoAsync(res.ZohoId); }
        catch (Exception e) { log.LogWarning(e, "Could not read back the Quote No. of Zoho Quote {Id}", res.ZohoId); }
        await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
            detail: $"Created Zoho Quote {no ?? res.ZohoId} (Stage {ZohoQuoteClient.NewQuoteStage}) on Deal \"{deal.DealName}\" — {plan.Reason}");
        return (res.ZohoId, no);
    }

    // Distinct per-line delivery dates (extra.deliveryDate, OCR-prefilled / CS-edited), blanks ignored.
    private static List<string> LineDeliveryDates(List<Dictionary<string, object?>> lines) =>
        lines.Select(l => (l.Get("extra") as Dictionary<string, object?>).GetStr("deliveryDate").Trim())
            .Where(s => s.Length > 0).Distinct().ToList();

    // Builds the actual Sales Order line from a doc line + the Deal Item it's matched to (by the
    // automatic code/description rule in BuildDefaultsAsync's loop, or -- Create only -- by a
    // person confirming an AI-suggested Deal Item for a line the automatic rule skipped). Returns
    // null when the document line's own Quantity is zero/blank, the one thing that always blocks
    // sending a line regardless of how its Material was matched.
    private static MatchedLine? TryBuildMatchedLine(
        Dictionary<string, object?> l, ZohoDealItem item, string? lineCode,
        IReadOnlyList<Dictionary<string, object?>> uomRules, string salesOrg, bool requireUomConversion)
    {
        var qty = Num(l.Get("qty"));
        if (qty <= 0) return null;
        var converted = ConvertZohoUom(l, item, uomRules, salesOrg, requireUomConversion);
        // Same per-line "extra" bag SAP's Item Note 1 reads (SapPayloadBuilder) -- persisted
        // separately from the top-level line fields so it survives the DB round-trip; edited by
        // the CS in DetailTable regardless of GLC/SAP vs MGT/Zoho.
        var lineExtra = l.Get("extra") as Dictionary<string, object?>;
        return new MatchedLine(
            ItemNo: l.Get("itemNo")?.ToString() ?? "",
            Desc: l.GetStr("desc"),
            ExtCode: l.GetStr("extCode"),
            MaterialId: item.MaterialId!,
            MaterialName: item.MaterialName ?? item.MaterialCode,
            MaterialCode: lineCode is { Length: > 0 } ? lineCode : item.MaterialCode,
            MaterialGroup: item.MaterialGroup,
            ShipVia: item.ShipVia,
            Stock: item.Stock,
            LeadTimeDays: item.LeadTimeDays,
            Quantity: converted.Quantity,
            UnitPrice: converted.UnitPrice,
            Unit: converted.Unit,
            ConversionRatio: item.ConversionRatio,
            SubUnit: item.SubUnit,
            ItemNote1: lineExtra?.GetStr("itemNote1"));
    }

    // What the send does for one Deal of the document (Deal -> Quote -> Sales Order, 27 Oct 2026).
    private record FlowView(bool Wcd, string? ForecastStatus, bool NewDeal, string? DealStage,
        string QuoteAction, string? QuoteNo, string? QuoteStage, string QuoteReason, List<string> QuoteRefs);

    // One Deal's part of the preview: its defaults (lines of this Deal only), the Quote plan and the owner.
    private record PartPreview(string Key, Defaults D, FlowView Flow, string Label,
        string? OwnerName, string? OwnerError, string? OwnerId, bool? OwnerActive);

    private async Task<PartPreview> PreviewPartAsync(int docId, string dealKey, ISet<string> itemNos)
    {
        var d = await BuildDefaultsAsync(docId, dealKey, onlyItemNos: itemNos);
        string quoteAction, quoteReason; string? quoteNo = null, quoteStage = null;
        try
        {
            var plan = await PlanQuoteAsync(d.Deal, d.CustomerRef, d.Lines.Select(ToSoLine).ToList(), d.QuoteRefs);
            quoteAction = plan.Quote is null ? "create" : "use";
            quoteReason = plan.Reason; quoteNo = plan.Quote?.QuoteNo; quoteStage = plan.Quote?.Stage;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Preview: could not read the Quotes of Deal {DealId}", d.Deal.Id);
            quoteAction = "unknown"; quoteReason = "Could not read the Deal's Quotes from Zoho CRM: " + e.Message;
        }

        string? dealOwnerName = null, dealOwnerError = null, dealOwnerId = null;
        bool? dealOwnerActive = null;
        try
        {
            var ownerRecord = d.NewDeal
                ? await accountClient.GetRecordAsync(d.Deal.AccountId!)
                : await dealClient.GetRecordAsync(d.Deal.Id);
            if (ownerRecord?["Owner"] is JsonObject owner)
            {
                dealOwnerName = owner["name"]?.GetValue<string>();
                dealOwnerId = owner["id"] is JsonValue idv && idv.TryGetValue<string>(out var oid) ? oid : null;
            }
            if (string.IsNullOrWhiteSpace(dealOwnerName))
                dealOwnerError = "Responsible salesperson could not be determined.";
            // A new Deal goes to the Account Owner: flag it when that user is not active in Zoho any
            // more (Zoho would reject the Owner id), so CS picks the Sales person first (12 Nov 2026).
            if (d.NewDeal && !string.IsNullOrWhiteSpace(dealOwnerId))
            {
                try
                {
                    var users = await zohoClient.GetActiveUsersAsync();
                    if (users.Count > 0) dealOwnerActive = users.Any(u => u.Id == dealOwnerId);
                }
                catch (Exception e) { log.LogWarning(e, "Preview: could not read the Zoho users"); }
                if (dealOwnerActive == false)
                    dealOwnerError = $"Account Owner \"{dealOwnerName}\" is not an active user in Zoho CRM. Pick the Sales person for the new Deal.";
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Preview: could not read the Deal owner for account {AccountCode}", d.Deal.AccountCode);
            dealOwnerError = "Responsible salesperson is temporarily unavailable. Please reload the preview.";
        }

        var flow = new FlowView(d.Deal.IsWcd, d.Deal.ForecastStatus, d.NewDeal, d.Deal.Stage,
            quoteAction, quoteNo, quoteStage, quoteReason, d.QuoteRefs ?? []);
        var label = d.Deal.DealName is { Length: > 0 } n ? n : "New Deal";
        return new PartPreview(dealKey, d, flow, label, dealOwnerName, dealOwnerError, dealOwnerId, dealOwnerActive);
    }

    // GET /api/zoho/sales-order/preview/{docId}?dealId=...&lineDeals={"10":"<deal id>","20":"__new__"} —
    // every value Create would send, laid out for the person to review/edit first (see class comment).
    // Never writes anything. One PO, several Deals (16 Nov 2026): deals[] has one entry per Deal; the
    // top-level Deal / flow / owner fields are the first Deal's (the owner: the first NEW Deal's, since CS
    // picks one Sales for every new Deal), and lines / skipped cover every Deal, each with its dealKey.
    [HttpGet("sales-order/preview/{docId:int}")]
    public async Task<IActionResult> Preview(int docId, [FromQuery] string? dealId, [FromQuery] string? lineDeals = null)
    {
        var doc = await repo.GetDocumentAsync(docId);
        var keys = LineDealKeys((List<Dictionary<string, object?>>)doc["lines"]!, dealId, ParseLineDeals(lineDeals));
        RequireDeals(keys);

        var views = new List<PartPreview>();
        foreach (var p in DealParts(keys)) views.Add(await PreviewPartAsync(docId, p.Key, p.ItemNos));

        // Each Sales Order's Subject (Megachem, 19 Nov 2026 -- ZohoSubjects): one per delivery-date group of
        // the Deal, _All_Items or the Deal's next _BillN (numbered on from its orders in Zoho, best effort).
        var docLines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var previewGroups = SoDeliveryGroups.Build((Dictionary<string, object?>)doc["header"]!, docLines,
            dealOf: (l, i) => keys[SoDeliveryGroups.ItemNoOf(l, i)]);
        var soSubjects = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var v in views)
        {
            var lastBill = v.D.NewDeal ? 0 : await LastBillNoAsync(v.D.Deal.Id, docId, bestEffort: true);
            var names = new List<string>();
            foreach (var g in previewGroups.Where(g => g.Deal == v.Key))
            {
                var codes = v.D.Lines.Where(l => g.ItemNos.Contains(l.ItemNo)).Select(l => l.MaterialCode).ToList();
                if (codes.Count == 0) continue;
                var all = ZohoSubjects.SameMaterials(codes, v.D.DealCodes ?? []);
                names.Add(ZohoSubjects.SalesOrder(v.D.AccountName, all, lastBill + 1));
                if (!all) lastBill++;
            }
            soSubjects[v.Key] = names.Count > 0 ? names : [v.D.Subject];
        }
        string SubjectOf(PartPreview v) => string.Join(" · ", soSubjects[v.Key]);

        var first = views[0];
        var d = first.D;
        var allLines = views.SelectMany(v => v.D.Lines.Select(l => (v.Key, Line: l))).ToList();

        var (previewLastPrices, lastPriceError) = await LastPricesWithErrorAsync(d.Deal.AccountCode, allLines.Select(x => x.Line.MaterialCode), docId);
        // The dropdown options only decorate the editor (blank = "use Deal default"), so a slow or
        // failing Zoho settings/fields call must not fail the whole preview: give it 20 s, then
        // carry on with empty lists. The second lookup reads the cached field list.
        List<string> paymentTermsOptions = [], paymentCurrencyOptions = [];
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            paymentTermsOptions = await soClient.GetPicklistOptionsAsync("Payment_Terms", cts.Token);
            paymentCurrencyOptions = await soClient.GetPicklistOptionsAsync("Payment_Currency", cts.Token);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Preview: could not read the Payment Terms / Currency picklists from Zoho CRM");
        }

        var ownerView = views.FirstOrDefault(v => v.D.NewDeal) ?? first;
        return Ok(new
        {
            dealName = views.Count == 1 ? d.Deal.DealName : string.Join(" + ", views.Select(v => v.Label)),
            dealOwnerName = ownerView.OwnerName,
            dealOwnerError = ownerView.OwnerError,
            dealOwnerId = ownerView.OwnerId,
            dealOwnerActive = ownerView.OwnerActive,
            accountCode = d.Deal.AccountCode,
            // newDeal = any Deal of the document is opened on Send (drives the Sales / Deal Owner picker).
            flow = first.Flow with { NewDeal = views.Any(v => v.D.NewDeal) },
            deals = views.Select(v => new
            {
                key = v.Key,
                dealName = v.Label,
                dealId = v.D.NewDeal ? null : v.D.Deal.Id,
                flow = v.Flow,
                subject = SubjectOf(v),
                salesOrderSubjects = soSubjects[v.Key],
                quoteSubject = ZohoSubjects.Quote(v.D.AccountName),
                itemNos = v.D.Lines.Select(l => l.ItemNo).Concat(v.D.Skipped.Select(s => s.ItemNo)).ToList(),
                ownerName = v.OwnerName,
                ownerError = v.OwnerError,
            }),
            subject = SubjectOf(first),
            salesOrderSubjects = soSubjects[first.Key],
            quoteSubject = ZohoSubjects.Quote(d.AccountName),
            customerRef = d.CustomerRef,
            deliveryDate = d.DeliveryDate,
            paymentTerms = d.PaymentTerms,
            paymentCurrency = d.PaymentCurrency,
            incoterms = d.Incoterms,
            taxId = d.TaxId,
            remarks = d.Remarks,
            paymentTermsOptions,
            paymentCurrencyOptions,
            lastPriceError,
            lines = allLines.Select(x => new
            {
                dealKey = x.Key,
                itemNo = x.Line.ItemNo,
                desc = x.Line.Desc,
                extCode = x.Line.ExtCode,
                materialName = x.Line.MaterialName,
                materialCode = x.Line.MaterialCode,
                quantity = x.Line.Quantity,
                unitPrice = x.Line.UnitPrice,
                unit = x.Line.Unit,
                conversionRatio = x.Line.ConversionRatio,
                subUnit = x.Line.SubUnit,
                lastPrice = previewLastPrices.TryGetValue(x.Line.MaterialCode ?? "", out var lp) ? lp.Price : (decimal?)null,
                lastPriceDate = previewLastPrices.TryGetValue(x.Line.MaterialCode ?? "", out var lpd) ? lpd.UpdateDate : null,
                lastPriceSo = previewLastPrices.TryGetValue(x.Line.MaterialCode ?? "", out var lps) ? lps.SalesOrderName : null,
            }),
            skipped = views.SelectMany(v => v.D.Skipped.Select(s => new { itemNo = s.ItemNo, desc = s.Desc, extCode = s.ExtCode, reason = s.Reason, dealKey = v.Key })),
        });
    }

    // Shared by Create (which sends) and Payload (which only previews): recompute the defaults,
    // fold in the caller's edits + any AI-confirmed material matches, and assemble the exact line
    // list + resolved header values that both endpoints hand to the ZohoSalesOrderClient. Material
    // identity per line always comes from the server-side Deal Item match, never the request.
    // dealKey / itemNos: one Deal of the document and its lines (16 Nov 2026). single = the document has
    // only this Deal -- only then does a typed Subject apply (otherwise each Deal's own name is used).
    private async Task<Assembled> AssembleAsync(int docId, CreateSalesOrderRequest body, string dealKey, ISet<string>? itemNos,
        bool single, bool forCreate = false)
    {
        var d = await BuildDefaultsAsync(docId, dealKey, requireUomConversion: true,
            enforceDealAccount: forCreate && !body.AcceptOtherAccount, onlyItemNos: itemNos);
        // Only Draft/Qualification Deals are used (Megachem, 16 Nov 2026) -- also one picked through the
        // manual Deal search, or the Deal of a Quote No. on the PO.
        if (!d.NewDeal && !ZohoDealStage.IsOpenOrDraft(d.Deal.Stage))
            throw new HttpApiException(400,
                $"Deal \"{d.Deal.DealName}\" is at Stage {d.Deal.Stage} — only {ZohoDealStage.Draft} Deals can be used. Move its items to another Deal — nothing was sent");
        if (d.NewDeal)
        {
            // Forecast Status of the new Deal: CS chooses Yes or No on Send (16 Nov 2026), no default.
            var forecast = NewDealForecastOf(body, dealKey)
                ?? throw new HttpApiException(400, "Choose Forecast Yes or No for the new Deal"
                    + (IsNewDealKey(dealKey) && dealKey.Length > NewDealId.Length ? $" {dealKey[NewDealId.Length..]}" : "")
                    + " before sending — nothing was sent");
            d = d with { Deal = d.Deal with { ForecastStatus = forecast } };
        }

        var overrides = (body.Lines ?? []).Where(x => !string.IsNullOrEmpty(x.ItemNo))
            .ToDictionary(x => x.ItemNo, x => x);

        // A line the automatic code/description rule skipped can still go out if the person
        // confirmed a specific Deal Item for it (via the AI-suggested-match tool) -- but MaterialId
        // is only ever honored when it's actually the id of one of THIS Deal's own Items; a stale or
        // tampered id just leaves the line skipped. The underlying doc line is looked up fresh from
        // d.Doc so Quantity/Price/Unit still come from the real document, never from the client.
        var docLinesByItemNo = ((List<Dictionary<string, object?>>)d.Doc["lines"]!)
            .ToDictionary(l => l.Get("itemNo")?.ToString() ?? "", l => l);
        var resolvedLines = new List<MatchedLine>(d.Lines);
        var stillSkipped = new List<SkippedLine>();
        foreach (var sk in d.Skipped)
        {
            MatchedLine? built = null;
            if (overrides.TryGetValue(sk.ItemNo, out var ov) && !string.IsNullOrEmpty(ov.MaterialId)
                && docLinesByItemNo.TryGetValue(sk.ItemNo, out var docLine))
            {
                var resolved = d.Deal.Items.FirstOrDefault(it => string.Equals(it.MaterialId, ov.MaterialId, StringComparison.Ordinal));
                if (resolved != null)
                    built = TryBuildMatchedLine(docLine, resolved, docLine.GetStr("materialCode"), d.UomRules, d.SalesOrg, requireUomConversion: true);
            }
            if (built != null) resolvedLines.Add(built);
            else stillSkipped.Add(sk);
        }

        if (resolvedLines.Count == 0)
            throw new HttpApiException(400, (d.NewDeal
                ? "No item of the new Deal could be matched to a Zoho Material"
                : "No document line could be matched to a Deal Item in \"" + d.Deal.DealName + "\"") + " — nothing to send to Zoho");

        // Zoho's Last Prices module (customer + material) -- copied into each item's Last_Price.
        var lastPrices = await LastPricesAsync(d.Deal.AccountCode, resolvedLines.Select(l => l.MaterialCode), docId);
        var soLines = resolvedLines.Select(l =>
        {
            overrides.TryGetValue(l.ItemNo, out var ov);
            return new ZohoSalesOrderLine(
                MaterialId: l.MaterialId,
                Description: ov?.Description is { Length: > 0 } ? ov.Description : l.Desc,
                MaterialCode: l.MaterialCode,
                MaterialGroup: l.MaterialGroup,
                ShipVia: l.ShipVia,
                Stock: l.Stock,
                LeadTimeDays: l.LeadTimeDays,
                Quantity: ov?.Quantity is decimal oq && oq > 0 ? oq : l.Quantity,
                UnitPrice: ov?.UnitPrice ?? l.UnitPrice,
                Unit: ov?.Unit is { Length: > 0 } ? ov.Unit : l.Unit,
                ConversionRatio: l.ConversionRatio,
                SubUnit: l.SubUnit,
                StatusNote: l.ItemNote1,
                LastPrice: lastPrices.TryGetValue(l.MaterialCode ?? "", out var lp) ? lp.Price : null);
        }).ToList();

        // A Subject the person typed (one Deal only) names every Sales Order of the send; otherwise each order
        // gets its own All_Items / BillN name (SoSubjectOf). The Quote always follows AccountName_MMYY.
        string? typedSubject = single && !string.IsNullOrWhiteSpace(body.Subject) ? body.Subject.Trim() : null;
        if (typedSubject is { Length: > 50 }) typedSubject = typedSubject[..50];
        var subject = typedSubject ?? d.Subject;

        return new Assembled(
            Deal: d.Deal,
            Subject: subject,
            TaxId: body.TaxId is { Length: > 0 } ? body.TaxId : d.TaxId,
            CustomerRef: body.CustomerRef is { Length: > 0 } ? body.CustomerRef : d.CustomerRef,
            DeliveryDate: body.DeliveryDate is { Length: > 0 } ? body.DeliveryDate : d.DeliveryDate,
            PaymentTerms: body.PaymentTerms is { Length: > 0 } ? body.PaymentTerms : d.PaymentTerms,
            PaymentCurrency: body.PaymentCurrency is { Length: > 0 } ? body.PaymentCurrency : d.PaymentCurrency,
            Incoterms: body.Incoterms is { Length: > 0 } ? body.Incoterms : d.Incoterms,
            ShipTo: await FillShipToAddressAsync(d.Deal, body.ShipTo, docId),
            // Null means no override; an explicit empty string clears the document's Remark. Several
            // Deals: the Deal's own Description when CS gave one (19 Nov 2026).
            Remarks: !single && body.DealRemarks is { } dr && dr.TryGetValue(dealKey, out var dealRemark) && dealRemark is not null
                ? dealRemark
                : body.Remarks ?? d.Remarks,
            SoLines: soLines,
            StillSkipped: stillSkipped,
            Doc: d.Doc,
            SoItemNos: resolvedLines.Select(l => l.ItemNo).ToList(),
            NewDeal: d.NewDeal,
            QuoteRefs: d.QuoteRefs ?? [],
            OwnerId: d.NewDeal && !string.IsNullOrWhiteSpace(body.SalesUserId) ? await ResolveSalesOwnerAsync(body.SalesUserId.Trim()) : null,
            QuoteSubject: ZohoSubjects.Quote(d.AccountName),
            AccountName: d.AccountName,
            DealCodes: d.DealCodes ?? [],
            TypedSubject: typedSubject);
    }

    private record Assembled(
        ZohoDeal Deal, string Subject,
        string? TaxId, string? CustomerRef, string? DeliveryDate,
        string? PaymentTerms, string? PaymentCurrency, string? Incoterms,
        ZohoShipToInfo? ShipTo, string? Remarks,
        List<ZohoSalesOrderLine> SoLines, List<SkippedLine> StillSkipped,
        Dictionary<string, object?> Doc,
        List<string> SoItemNos, // itemNo of each SoLines entry (same order) -- used to cut per-delivery-date groups
        bool NewDeal = false,
        List<string>? QuoteRefs = null,
        string? OwnerId = null,
        string QuoteSubject = "",
        string AccountName = "",
        List<string>? DealCodes = null,
        string? TypedSubject = null);

    // Subject of one Sales Order (Megachem, 19 Nov 2026 -- ZohoSubjects): AccountName_MMYY_All_Items when it
    // holds exactly the Deal's materials (quantities not compared), else AccountName_MMYY_Bill{lastBill + 1}.
    // UsesBill = this order takes the next Bill number. A typed Subject wins (and takes no number).
    private static (string Subject, bool UsesBill) SoSubjectOf(Assembled a, IEnumerable<ZohoSalesOrderLine> items, int lastBill)
    {
        if (!string.IsNullOrWhiteSpace(a.TypedSubject)) return (a.TypedSubject!, false);
        var all = ZohoSubjects.SameMaterials(items.Select(i => i.MaterialCode), a.DealCodes ?? []);
        return (ZohoSubjects.SalesOrder(a.AccountName, all, lastBill + 1), !all);
    }

    // Highest _BillN already used by a Sales Order of the Deal in Zoho (0: none, or a Deal not opened yet).
    // Bill numbers run on across every order of the Deal, from any document (Megachem, 19 Nov 2026).
    // bestEffort (Preview / Payload): a failed read just starts at Bill1; on Send it stops the Deal instead.
    private async Task<int> LastBillNoAsync(string? dealId, int docId, bool bestEffort = false)
    {
        if (string.IsNullOrWhiteSpace(dealId)) return 0;
        try { return ZohoSubjects.MaxBillNo(await soClient.ListSubjectsByDealAsync(dealId)); }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the Sales Orders of Deal {DealId} to number the Bill (doc {DocId})", dealId, docId);
            if (bestEffort) return 0;
            throw new HttpApiException(400,
                $"Could not read this Deal's Sales Orders from Zoho CRM to number the Bill: {e.Message} — nothing was sent for this Deal");
        }
    }

    // POST /api/zoho/sales-order/create/{docId} { dealId, ...edited fields } — {docId} as a route
    // parameter (not buried inside the body) so DepartmentAccessFilter's docId-based module gate
    // applies here exactly like every DocumentsController action.
    [HttpPost("sales-order/create/{docId:int}")]
    public async Task<IActionResult> Create(int docId, [FromBody] CreateSalesOrderRequest body)
    {
        if (body is null)
            throw new HttpApiException(400, "A Deal must be selected first");

        // F09: block a re-send. Once a document is POSTED (SAP or Zoho) it must not be sent again,
        // exactly like DocumentsController.PostDocument guards the SAP path.
        var existing = await repo.GetDocumentAsync(docId);
        if (existing.GetStr("status") == "POSTED")
            throw new HttpApiException(400, $"This document has already been sent to Zoho ({existing.GetStr("sapDocNo")})");

        if (existing.GetStr("status") == "SPLIT")
            throw new HttpApiException(400, "This document was split into separate documents — send those instead");
        if (existing.GetStr("module") != "SO")
            throw new HttpApiException(400, "Only Sales Order documents can be sent to Zoho CRM");
        // Only MGT Sales Orders go to Zoho; GLC's go to SAP (DocumentsController post-so). The
        // company stamped on the document wins over its editable header text.
        if (string.Equals(config.CompanyForSalesOrg(await DocSalesOrgAsync(docId, existing))?.Name, "GLC", StringComparison.OrdinalIgnoreCase))
            throw new HttpApiException(400, "This Sales Order belongs to GLC — it is sent to SAP, not Zoho CRM");

        // One Zoho Sales Order PER SO GROUP + DELIVERY DATE (SoDeliveryGroups; same rule as GLC/SAP),
        // all from this one document -- replaces "split into child documents". Every group gets the
        // same Customer PO, its own Delivery_Date and its SO Group's ship-to; groups already created
        // are skipped (no duplicates on a retry); each result is stamped on the group's lines
        // (PostedSoNo / PostError) + ocr.PostLog.
        var existingHeader = (Dictionary<string, object?>)existing["header"]!;
        var existingLines = (List<Dictionary<string, object?>>)existing["lines"]!;
        // One PO, several Deals (16 Nov 2026): the Deal is part of the group key, so every Sales Order
        // belongs to exactly one Deal.
        var dealKeys = LineDealKeys(existingLines, body.DealId, body.LineDeals);
        RequireDeals(dealKeys);
        var groups = SoDeliveryGroups.Build(existingHeader, existingLines,
            dealOf: (l, i) => dealKeys[SoDeliveryGroups.ItemNoOf(l, i)]);
        var multi = groups.Count > 1;
        var states = (await soPosts.GroupStatesAsync(docId, groups)).ToDictionary(x => x.Key);

        // Megachem rule (6 Oct 2026): an MGT Sales Order must have a Ship-to before it is sent --
        // now checked per Sales Order: each SO Group uses its own chosen ship-to, else the document's.
        var missingShipTo = groups
            .Where(g => !(states.TryGetValue(g.Key, out var st) && st.Posted))
            .Where(g => !HasShipTo(GroupShipTo(existingHeader, g) ?? body.ShipTo))
            .Select(g => g.SoGroupNo).Distinct().ToList();
        if (missingShipTo.Count > 0)
            throw new HttpApiException(400, multi
                ? $"Select a Ship-to for SO Group {string.Join(", ", missingShipTo)} before sending the Sales Orders to Zoho CRM"
                : "Select a Ship-to before sending the Sales Order to Zoho CRM");

        // Every Deal of the document (and each new Deal) is assembled -- and checked -- before anything
        // is sent: one Deal -> Quote -> Sales Order(s) chain per Deal (16 Nov 2026).
        var dealOrder = groups.Select(g => g.Deal).Distinct(StringComparer.Ordinal).ToList();
        var single = dealOrder.Count == 1;
        var parts = new List<(Assembled A, List<SoGroup> Groups)>();
        foreach (var k in dealOrder)
            parts.Add((await AssembleAsync(docId, body, k, ItemNosOf(dealKeys, k), single, forCreate: true),
                groups.Where(g => g.Deal == k).ToList()));

        // One send at a time per document (double click, second tab, SAP and Zoho at once): see
        // SalesOrderPostRepository.TryClaimAsync. States are read again under the claim.
        var previousStatus = await soPosts.TryClaimAsync(docId)
            ?? throw new HttpApiException(409, "This Sales Order is being sent right now — wait a moment, then refresh the page");
        try
        {
            states = (await soPosts.GroupStatesAsync(docId, groups)).ToDictionary(x => x.Key);
            var alreadyRecorded = states.Values.Where(s => s.Posted && !string.IsNullOrEmpty(s.DocNo)).Select(s => s.DocNo!).ToHashSet();
            var actor = await ActorAsync();
            var results = new List<object>();
            // Zoho record id (and its Deal) of a Sales Order created in THIS call -- used to read it back
            // for the SharePoint folder. (The stored DocNo is Zoho's Sales Order No., not the record id; and
            // a document only reaches POSTED here when this call created at least one order.)
            string? firstZohoId = null, firstZohoDealId = null, firstZohoAccountCode = null;
            string? firstError = null;
            var linesSent = 0;
            var dealNames = new List<string>();
            var quoteNos = new List<string>();
            var stillSkipped = new List<SkippedLine>();
            var usedDealIds = new HashSet<string>(StringComparer.Ordinal);
            var gi = 0;

            foreach (var part in parts)
            {
                var a = part.A;
                var partGroups = part.Groups;
                stillSkipped.AddRange(a.StillSkipped);

                // Zoho chain Deal -> Quote -> Sales Order (Megachem, 27 Oct 2026): before the first Sales
                // Order of this Deal, make sure the Deal and its Quote exist -- open a (non-WCD) Deal for a
                // new-Deal group, and find the Deal's Quote for these lines or create one. Every Sales Order
                // of this Deal links to that Quote. Skipped when all of this Deal's orders are in Zoho.
                var deal = a.Deal;
                string? quoteId = null, quoteNo = null, dealOwnerId = null;
                var lastBill = 0;
                if (partGroups.Any(g => !(states.TryGetValue(g.Key, out var st) && st.Posted)))
                {
                    try
                    {
                        if (a.NewDeal)
                        {
                            deal = await EnsureDealAsync(docId, a, partGroups, actor, usedDealIds);
                        }
                        usedDealIds.Add(deal.Id);
                        // Bill numbers continue from the Deal's orders already in Zoho (also a Deal an earlier
                        // attempt of this document opened). Read before the Quote, so a failed read sends nothing.
                        lastBill = await LastBillNoAsync(deal.Id, docId);
                        var quoteShipTo = a.ShipTo ?? partGroups.Select(g => GroupShipTo(existingHeader, g)).FirstOrDefault(x => x is not null);
                        dealOwnerId = await DealOwnerIdAsync(deal.Id, a);
                        (quoteId, quoteNo) = await EnsureQuoteAsync(docId, deal, a, quoteShipTo, partGroups, actor, dealOwnerId);
                    }
                    catch (HttpApiException e) when (!single)
                    {
                        // This Deal's Deal / Quote step failed: none of its Sales Orders is sent; the other
                        // Deals of the document still go (a later Send retries only what is missing).
                        firstError ??= e.Message;
                        foreach (var g in partGroups)
                        {
                            gi++;
                            if (states.TryGetValue(g.Key, out var st) && st.Posted) continue;
                            await soPosts.RecordAsync(docId, "SO", g.ItemNos, false, null, e.Message, "Zoho:Sales_Orders", "{}", actor, salesOrg: null);
                            results.Add(new { key = g.Key, itemNos = g.ItemNos, success = false, skipped = false, zohoId = (string?)null, message = e.Message });
                        }
                        continue;
                    }
                }
                if (!string.IsNullOrWhiteSpace(deal.DealName)) dealNames.Add(deal.DealName);
                if (!string.IsNullOrWhiteSpace(quoteNo)) quoteNos.Add(quoteNo);

                foreach (var g in partGroups)
                {
                    gi++;
                    var hasState = states.TryGetValue(g.Key, out var done);
                    if (hasState && done!.Posted)
                    {
                        results.Add(new { key = g.Key, itemNos = g.ItemNos, success = true, skipped = true, zohoId = done.DocNo, message = "Already created" });
                        continue;
                    }
                    var idx = Enumerable.Range(0, a.SoLines.Count).Where(i => g.ItemNos.Contains(a.SoItemNos[i])).ToList();
                    // Every line of the group must go out together: the group's lines are all stamped with
                    // the created Sales Order below, so sending only the matched ones used to mark an
                    // unmatched line as sent (and lock the document) although Zoho never received it.
                    var unmatched = g.ItemNos.Where(n => !idx.Any(i => a.SoItemNos[i] == n)).ToList();
                    if (idx.Count == 0 || unmatched.Count > 0)
                    {
                        var msg = idx.Count == 0
                            ? "No line of this Sales Order is matched to a Deal Item — match it first"
                            : $"Item {string.Join(", ", unmatched)} is not matched to a Deal Item — match it first (nothing in this Sales Order was sent)";
                        firstError ??= msg;
                        await soPosts.RecordAsync(docId, "SO", g.ItemNos, false, null, msg, "Zoho:Sales_Orders", "{}", actor, salesOrg: null);
                        results.Add(new { key = g.Key, itemNos = g.ItemNos, success = false, skipped = false, zohoId = (string?)null, message = msg });
                        continue;
                    }
                    var items = idx.Select(i => a.SoLines[i]).ToList();
                    var deliveryDate = multi && g.Date.Length > 0 ? g.Date : a.DeliveryDate;
                    var shipTo = GroupShipTo(existingHeader, g) ?? a.ShipTo;
                    var (subject, usesBill) = SoSubjectOf(a, items, lastBill);

                    // The previous attempt of this group failed. A timeout can fail AFTER Zoho created the
                    // order, so look for it before sending again (same Deal, Customer Ref, Subject and
                    // delivery date, created in the last 7 days, not recorded on this document yet).
                    (string Id, string? SoNo)? earlierOrder = null;
                    if (hasState && !string.IsNullOrEmpty(done!.Error) && !string.IsNullOrWhiteSpace(a.CustomerRef))
                        earlierOrder = await soClient.FindExistingAsync(deal.Id, a.CustomerRef!, subject, deliveryDate,
                            DateTime.UtcNow.AddDays(-7), alreadyRecorded,
                            subjectPrefix: a.TypedSubject is null ? ZohoSubjects.SalesOrderPrefix(a.AccountName) : null);
                    // This order takes the next Bill number only when it is really sent now (an order found
                    // above is already counted in lastBill). A failed attempt keeps its number used, so a
                    // later order never repeats a number Zoho may have received.
                    if (earlierOrder is null && usesBill) lastBill++;

                    ZohoUpsertResult result;
                    if (earlierOrder is { } found)
                    {
                        result = new ZohoUpsertResult
                        {
                            SapKey = "", ZohoId = found.Id, Status = "success",
                            Message = $"Already in Zoho as {found.SoNo ?? found.Id} (created by an earlier attempt that reported an error) — not sent again",
                        };
                    }
                    else try
                    {
                        result = await soClient.CreateAsync(
                            dealId: deal.Id, accountId: deal.AccountId!, subject: subject, accountCode: deal.AccountCode,
                            taxId: a.TaxId, customerRef: a.CustomerRef, deliveryDate: deliveryDate,
                            paymentTerms: a.PaymentTerms, paymentCurrency: a.PaymentCurrency, incoterms: a.Incoterms,
                            shipTo: shipTo, items: items, remarks: a.Remarks, quoteId: quoteId, quoteNo: quoteNo,
                            ownerId: dealOwnerId);
                        if (OwnerRejected(result))
                        {
                            // Deal Owner no longer usable in Zoho: send the Sales Order without it rather than fail.
                            await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
                                detail: $"Zoho refused the Deal Owner as Sales Order owner ({result.Message}) — sent without it");
                            dealOwnerId = null;
                            result = await soClient.CreateAsync(
                                dealId: deal.Id, accountId: deal.AccountId!, subject: subject, accountCode: deal.AccountCode,
                                taxId: a.TaxId, customerRef: a.CustomerRef, deliveryDate: deliveryDate,
                                paymentTerms: a.PaymentTerms, paymentCurrency: a.PaymentCurrency, incoterms: a.Incoterms,
                                shipTo: shipTo, items: items, remarks: a.Remarks, quoteId: quoteId, quoteNo: quoteNo);
                        }
                    }
                    catch (Exception e)
                    {
                        log.LogError(e,
                            "Zoho Sales Order creation failed for document {DocId}, delivery-date group {DeliveryDate}, items {ItemNos}",
                            docId, g.Key, string.Join(",", g.ItemNos));
                        result = new ZohoUpsertResult { SapKey = "", ZohoId = "", Status = "error", Message = e.Message };
                    }
                    var ok = result.Status == "success";
                    if (ok)
                    {
                        linesSent += items.Count;
                        if (firstZohoId is null) { firstZohoId = result.ZohoId; firstZohoDealId = deal.Id; firstZohoAccountCode = deal.AccountCode; }
                    }
                    else firstError ??= result.Message;
                    // What people see and search for in Zoho is the "Sales Order No." (auto-number, API
                    // name Quote_No, e.g. 1OR0000310) -- stored as the posted number instead of the long
                    // record id. The record id is kept in the PostLog payload (zohoRecordId).
                    var soNo = ok ? await SalesOrderNoAsync(result.ZohoId) : null;
                    if (soNo is not null) alreadyRecorded.Add(soNo);

                    var label = multi ? $" (Sales Order {gi}/{groups.Count}, SO Group {g.SoGroupNo}, delivery {(g.Date.Length > 0 ? g.Date : "-")})" : "";
                    await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"), detail: ok
                        ? $"Created Zoho Sales Order from Deal \"{deal.DealName}\"{label} -> {soNo} (record {result.ZohoId})"
                        : $"Zoho Sales Order creation failed from Deal \"{deal.DealName}\"{label}: {result.Message}");

                    var payloadJson = JsonSerializer.Serialize(new
                    {
                        dealId = deal.Id, dealName = deal.DealName, accountId = deal.AccountId, quoteId, quoteNo,
                        subject, customerRef = a.CustomerRef, deliveryDate,
                        paymentTerms = a.PaymentTerms, paymentCurrency = a.PaymentCurrency, incoterms = a.Incoterms,
                        taxId = a.TaxId, remarks = a.Remarks, shipTo, lines = items, group = g.Key,
                        zohoRecordId = ok ? result.ZohoId : null, salesOrderNo = soNo,
                    }, PyJson.Options);
                    await soPosts.RecordAsync(docId, "SO", g.ItemNos, ok, soNo,
                        result.Message, "Zoho:Sales_Orders", payloadJson, actor, salesOrg: null);
                    results.Add(new { key = g.Key, itemNos = g.ItemNos, success = ok, skipped = false, zohoId = ok ? result.ZohoId : null, soNo, message = result.Message });
                }
            }

            // companyCode "MGT" literal: this controller only ever creates Zoho Sales Orders (MGT).
            var newStatus = await soPosts.FinalizeAsync(docId, groups, "MGT", actor);
            // Release before reading the document back so the response never shows POSTING.
            await soPosts.ReleaseAsync(docId, previousStatus);

            // Queue the source file for SharePoint once EVERY group is in Zoho. The folder comes from
            // reading one created Sales Order back (its Account's sales person + customer). Best effort.
            if (newStatus == "POSTED" && !string.IsNullOrEmpty(firstZohoId))
            {
                var folder = await ArchiveFolderAsync(docId, firstZohoId, firstZohoDealId ?? "", firstZohoAccountCode);
                // File name PONUMBER_YYMMDD (SalesOrderFileName): the header delivery date, else the
                // earliest line delivery date when the PO prints a date per line (12 Nov 2026).
                var archiveDate = parts[0].A.DeliveryDate is { Length: > 0 } hd ? hd
                    : groups.Select(g => g.Date).Where(x => x.Length > 0).OrderBy(x => x).FirstOrDefault();
                try { await archive.EnqueueAsync(docId, DocumentTables.ForId(docId).Doc, "MGT", "SO", folder, deliveryDate: archiveDate); }
                catch (Exception e) { log.LogWarning(e, "Could not queue doc {DocId} for SharePoint (run sql/27 + sql/29?)", docId); }
                // Reading rules (sql/39): the positions of this checked, posted PO become the customer's AUTO rules. Best effort.
                await readHints.LearnFromPostedAsync(docId, repo);
            }

            var after = await repo.GetDocumentAsync(docId);
            return Ok(new
            {
                success = newStatus == "POSTED",
                status = newStatus,
                zohoId = after.GetStr("sapDocNo"),
                message = firstError,
                dealName = string.Join(" + ", dealNames.Distinct()),
                quoteNo = quoteNos.Count == 0 ? null : string.Join(", ", quoteNos.Distinct()),
                linesSent,
                skipped = stillSkipped,
                groups = results,
                document = after,
            });
        }
        finally
        {
            await soPosts.ReleaseAsync(docId, previousStatus);
        }
    }

    // The company (SalesOrg) of a Sales Order: the SalesOrg column stamped at import wins; older
    // documents without it fall back to the header text.
    private async Task<string> DocSalesOrgAsync(int docId, Dictionary<string, object?> doc)
    {
        // Separate MGT / GLC deployments (27 Oct 2026): always this site's company (DeploymentCompany).
        if (MgtOcr.Core.Auth.DeploymentCompany.SalesOrg(config) is { Length: > 0 } site) return site;
        var stamped = (await repo.GetCompanyAsync(docId) ?? "").Trim();
        if (stamped is "1000" or "2000") return stamped;
        return ((Dictionary<string, object?>)doc["header"]!).GetStr("salesOrg");
    }

    // The Zoho ship-to the person chose for this SO Group (header.soGroupShipTos, stored as the
    // ZohoShipToInfo fields in camelCase), or null when the group uses the document's own ship-to.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static ZohoShipToInfo? GroupShipTo(Dictionary<string, object?> header, SoGroup g)
    {
        var stored = SoDeliveryGroups.ShipToFor(header, g.SoGroupNo);
        if (stored is null) return null;
        try { return JsonSerializer.Deserialize<ZohoShipToInfo>(JsonSerializer.Serialize(stored), WebJson); }
        catch (JsonException) { return null; }
    }

    // The Ship-to from the mapping may carry only its code (e.g. 1000939) when the Ship-to master row
    // has no address saved -- the Zoho Sales Order then got Address Code only (Megachem, 11 Nov 2026).
    // Fill the empty address fields from the Account's Ship-tos in Zoho: the one with the same code;
    // else, when the code is the customer's own code, the Account's own Ship-to; else the only one.
    // Best effort: a Zoho error keeps the Ship-to as it was.
    private async Task<ZohoShipToInfo?> FillShipToAddressAsync(ZohoDeal deal, ZohoShipToInfo? s, int docId)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.Code) || string.IsNullOrWhiteSpace(deal.AccountId)) return s;
        static bool Blank(string? v) => string.IsNullOrWhiteSpace(v);
        if (!(Blank(s.Street) && Blank(s.Street2) && Blank(s.Street3) && Blank(s.Street4) && Blank(s.Street5)
              && Blank(s.HouseNumber) && Blank(s.District) && Blank(s.City) && Blank(s.PostCode))) return s;
        try
        {
            var all = (await accountClient.GetAllShipTosAsync(deal.AccountId)).ShipTos;
            var code = s.Code.Trim();
            var hit = all.FirstOrDefault(x => string.Equals((x.Code ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase))
                ?? (string.Equals(code, (deal.AccountCode ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                    ? all.FirstOrDefault(x => x.Source == "account") : null)
                ?? (all.Count == 1 ? all[0] : null);
            if (hit is null)
            {
                log.LogInformation("Ship-to {Code} of doc {DocId}: no matching Ship-to with an address on Zoho Account {AccountId}", code, docId, deal.AccountId);
                return s;
            }
            static string? F(string? mine, string? zoho) => Blank(mine) ? zoho : mine;
            return s with
            {
                Address = F(s.Address, hit.Address),
                Street = hit.Street, Street2 = hit.Street2, Street3 = hit.Street3, Street4 = hit.Street4, Street5 = hit.Street5,
                HouseNumber = hit.HouseNumber, District = hit.District, City = hit.City, DifferenceCity = F(s.DifferenceCity, hit.DifferenceCity),
                PostCode = hit.PostCode, CountryReg = F(s.CountryReg, hit.CountryReg),
                Source = F(s.Source, hit.Source), RecordId = F(s.RecordId, hit.RecordId),
            };
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Ship-to {Code} of doc {DocId}: could not read the Account's Ship-tos from Zoho", s.Code, docId);
            return s;
        }
    }

    private static bool HasShipTo(ZohoShipToInfo? s) =>
        s is not null && (!string.IsNullOrWhiteSpace(s.Code) || !string.IsNullOrWhiteSpace(s.Address));


    // Zoho's own "Sales Order No." (auto-number field, API name Quote_No) of a just-created record.
    // The create response only returns the record id, so the record is read back once. Falls back
    // to the record id when it can't be read, so a send never fails over a display number.
    private async Task<string> SalesOrderNoAsync(string zohoId)
    {
        try
        {
            var so = await soClient.GetByIdAsync(zohoId);
            var no = so?["Quote_No"]?.ToString().Trim();
            if (!string.IsNullOrEmpty(no)) return no;
            log.LogWarning("Zoho Sales Order {Id} has no Quote_No (Sales Order No.) - storing the record id", zohoId);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the Sales Order No. (Quote_No) of Zoho record {Id} - storing the record id", zohoId);
        }
        return zohoId;
    }

    // SharePoint folder "{Sales}/{CustomerCode}_{CustomerName}": read the created Sales Order back
    // from Zoho to find its Account, then take the Account's Owner ("Sales Employee" - the sales
    // person responsible for this customer). Fallback: the Deal Owner, then "_Unknown".
    private async Task<string?> ArchiveFolderAsync(int docId, string zohoId, string dealId, string? dealAccountCode)
    {
        try
        {
            var so = await soClient.GetByIdAsync(zohoId);
            var accountId = so is null ? null : ZohoArchiveFolder.LookupId(so["Account_Name"]);
            var acc = accountId is null ? null : await accountClient.GetRecordAsync(accountId);

            var sales = ZohoArchiveFolder.Text(acc?["Owner"]);
            var salesFrom = "Account Owner (Sales Employee)";
            if (string.IsNullOrWhiteSpace(sales))
            {
                var deal = await dealClient.GetRecordAsync(dealId);
                sales = ZohoArchiveFolder.Text(deal?["Owner"]);
                salesFrom = "Deal Owner";
            }
            var code = ZohoArchiveFolder.Text(acc?["Account_Code"]) ?? ZohoArchiveFolder.Text(so?["Account_Code"]) ?? dealAccountCode;
            var name = ZohoArchiveFolder.Text(acc?["Account_Name"]) ?? ZohoArchiveFolder.Text(so?["Account_Name"]);
            var sub = ZohoArchiveFolder.Build(sales, code, name).SubPath;
            log.LogInformation("Archive folder for doc {DocId}: {Folder} (sales from {From})", docId, sub, salesFrom);
            return sub;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Archive folder: could not read Sales Order {Id} / its Account from Zoho for doc {DocId} - default folder used", zohoId, docId);
            return null;
        }
    }

    // POST /api/zoho/sales-order/payload/{docId} { dealId, ...edited fields } — the SAP "View
    // Payload" counterpart: assembles the EXACT record JSON Create would send to Zoho (from the
    // same edits) and returns it without creating anything, so a person can review it first.
    [HttpPost("sales-order/payload/{docId:int}")]
    public async Task<IActionResult> Payload(int docId, [FromBody] CreateSalesOrderRequest body)
    {
        if (body is null)
            throw new HttpApiException(400, "A Deal must be selected first");

        var doc = await repo.GetDocumentAsync(docId);
        var docHeader = (Dictionary<string, object?>)doc["header"]!;
        var docLines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var keys = LineDealKeys(docLines, body.DealId, body.LineDeals);
        RequireDeals(keys);
        var groups = SoDeliveryGroups.Build(docHeader, docLines, dealOf: (l, i) => keys[SoDeliveryGroups.ItemNoOf(l, i)]);
        var multi = groups.Count > 1;
        var dealOrder = groups.Select(g => g.Deal).Distinct(StringComparer.Ordinal).ToList();
        var single = dealOrder.Count == 1;

        // Deal / Quote steps of the send (27 Oct 2026), shown the same way: the record that would be
        // created, or which existing one is used -- one Deal -> Quote -> Sales Orders part per Deal.
        const string createdOnSend = "(created on Send)";
        var parts = new System.Text.Json.Nodes.JsonArray();
        var salesOrderCount = 0;
        foreach (var k in dealOrder)
        {
            var partGroups = groups.Where(g => g.Deal == k).ToList();
            var a = await AssembleAsync(docId, body, k, ItemNosOf(keys, k), single);

            // Quote / Sales Orders are owned by the Deal Owner (12 Nov 2026) -- shown in the payload too.
            var payloadOwnerId = await DealOwnerIdAsync(a.NewDeal ? null : a.Deal.Id, a);
            var dealPart = new JsonObject();
            if (a.NewDeal)
            {
                dealPart["action"] = "create";
                dealPart["record"] = await BuildNewDealRecordAsync(a, partGroups);
            }
            else
            {
                dealPart["action"] = "use";
                dealPart["id"] = a.Deal.Id;
                dealPart["dealName"] = a.Deal.DealName;
                dealPart["forecastStatus"] = a.Deal.ForecastStatus;
                dealPart["wcd"] = a.Deal.IsWcd;
            }
            var quotePart = new JsonObject();
            string quoteIdForSo = createdOnSend; string? quoteNoForSo = createdOnSend;
            var plan = await PlanQuoteAsync(a.Deal, a.CustomerRef, a.SoLines, a.QuoteRefs);
            quotePart["reason"] = plan.Reason;
            if (plan.Quote is { } pq)
            {
                quotePart["action"] = "use";
                quotePart["id"] = pq.Id;
                quotePart["quoteNo"] = pq.QuoteNo;
                quoteIdForSo = pq.Id; quoteNoForSo = pq.QuoteNo;
            }
            else
            {
                quotePart["action"] = "create";
                var quoteShipTo = a.ShipTo ?? partGroups.Select(g => GroupShipTo(docHeader, g)).FirstOrDefault(x => x is not null);
                var quoteRecord = await BuildNewQuoteRecordAsync(a, a.NewDeal ? createdOnSend : a.Deal.Id, quoteShipTo, partGroups);
                if (!string.IsNullOrWhiteSpace(payloadOwnerId)) quoteRecord["Owner"] = new JsonObject { ["id"] = payloadOwnerId };
                quotePart["record"] = quoteRecord;
            }

            var records = new System.Text.Json.Nodes.JsonArray();
            var lastBill = a.NewDeal ? 0 : await LastBillNoAsync(a.Deal.Id, docId, bestEffort: true);
            foreach (var g in partGroups)
            {
                var items = Enumerable.Range(0, a.SoLines.Count).Where(i => g.ItemNos.Contains(a.SoItemNos[i])).Select(i => a.SoLines[i]).ToList();
                if (items.Count == 0) continue;
                var (soSubject, usesBill) = SoSubjectOf(a, items, lastBill);
                if (usesBill) lastBill++;
                var record = await soClient.BuildRecordAsync(
                    dealId: a.NewDeal ? createdOnSend : a.Deal.Id, accountId: a.Deal.AccountId!,
                    subject: soSubject,
                    accountCode: a.Deal.AccountCode, taxId: a.TaxId, customerRef: a.CustomerRef,
                    deliveryDate: multi && g.Date.Length > 0 ? g.Date : a.DeliveryDate,
                    paymentTerms: a.PaymentTerms, paymentCurrency: a.PaymentCurrency, incoterms: a.Incoterms,
                    shipTo: GroupShipTo(docHeader, g) ?? a.ShipTo, items: items, remarks: a.Remarks,
                    quoteId: quoteIdForSo, quoteNo: quoteNoForSo, ownerId: payloadOwnerId);
                records.Add(record);
            }
            salesOrderCount += records.Count;
            parts.Add(new JsonObject
            {
                ["_dealKey"] = k,
                ["_deal"] = dealPart,
                ["_quote"] = quotePart,
                ["_salesOrders"] = records.Count,
                ["data"] = records,
            });
        }

        // Sent next to "data" on each Quote / Sales Order insert (the Deal insert sends none, so Zoho
        // runs its usual automation for a new Deal).
        var trigger = new System.Text.Json.Nodes.JsonArray(
            ZohoSalesOrderClient.InsertTriggers.Select(t => (System.Text.Json.Nodes.JsonNode?)t).ToArray());
        if (single)
        {
            // One Deal: the same envelope as before -- one Zoho insert per Sales Order, each shown the way
            // it goes on the wire ("data" array of one record per call).
            var only = (JsonObject)parts[0]!;
            return Ok(new JsonObject
            {
                ["_target"] = "Zoho CRM — Deal -> Quote -> Sales Orders (POST /crm/v8/{module})",
                ["_deal"] = only["_deal"]!.DeepClone(),
                ["_quote"] = only["_quote"]!.DeepClone(),
                ["_salesOrders"] = salesOrderCount,
                ["data"] = only["data"]!.DeepClone(),
                ["trigger"] = trigger,
            });
        }
        return Ok(new JsonObject
        {
            ["_target"] = $"Zoho CRM — {dealOrder.Count} Deals, each Deal -> Quote -> Sales Orders (POST /crm/v8/{{module}})",
            ["_salesOrders"] = salesOrderCount,
            ["_deals"] = parts,
            ["trigger"] = trigger,
        });
    }
}
