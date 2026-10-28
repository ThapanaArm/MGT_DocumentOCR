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
    AppConfig config,
    ICurrentUserAccessor currentUser,
    FileArchiveRepository archive,
    SalesOrderPostRepository soPosts,
    ILogger<ZohoSalesOrderController> log) : ControllerBase
{
    // MaterialId, when present, is a person confirming (via the AI-assisted "suggest a match"
    // tool on a line the automatic code/description match skipped) which of this Deal's own Items
    // is the right one -- Create still only accepts it if it's actually one of deal.Items's own
    // MaterialId values (see Create below), so this can never smuggle in an arbitrary Zoho id.
    // Deal id the page sends when the document has no Deal (Megachem rule, 27 Oct 2026: no Deal =
    // not WCD -> the send opens a new Deal first, then a Quote, then the Sales Order).
    public const string NewDealId = "__new__";

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
        bool AcceptOtherAccount = false);

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
                "Add it in Master → Unit Conversion (UoM), or set a Conversion Ratio on the Deal item, before viewing the payload or sending to Zoho");
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
        List<string>? QuoteRefs = null);

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
    private async Task<Defaults> BuildDefaultsAsync(int docId, string dealId, bool requireUomConversion = false,
        bool enforceDealAccount = false)
    {
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("module") != "SO")
            throw new HttpApiException(400, "Only Sales Order documents can be sent to Zoho as a Sales Order");

        // Zoho chain Deal -> Quote -> Sales Order (27 Oct 2026). No Deal at all = not WCD: a new Deal
        // is opened on the document's customer when the person sends (see EnsureDealAsync). Until
        // then it is a stand-in record with no id, no items and Forecast Status "No".
        var newDeal = dealId == NewDealId;
        ZohoDeal? deal;
        if (newDeal)
        {
            var custCode = doc.GetStr("partnerCode").Trim();
            if (custCode.Length == 0)
                throw new HttpApiException(400, "Match the customer first — a new Deal is opened on this document's customer");
            var acc = await accountClient.FindByAccountCodeAsync(custCode)
                ?? throw new HttpApiException(400, $"Customer {custCode} was not found in Zoho CRM by Account Code — a new Deal cannot be opened");
            var po = ((Dictionary<string, object?>)doc["header"]!).GetStr("poNo").Trim();
            deal = new ZohoDeal(
                Id: "", DealName: $"{acc.AccountName} / {(po.Length > 0 ? "PO " + po : "Doc#" + docId)}",
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
        foreach (var l in lines)
        {
            var itemNo = l.Get("itemNo")?.ToString() ?? "";
            var lineCode = l.GetStr("materialCode");
            var desc = l.GetStr("desc");
            var extCode = l.GetStr("extCode");

            // Same match order as the frontend's ZohoDealCard: matched local Material code first
            // (exact, case-insensitive), else a fuzzy substring match on the description. This is
            // deliberately a cheap/strict rule, not a fuzzy-similarity score — real OCR'd
            // descriptions (CAS numbers, remarks, requestor codes tacked on) routinely don't
            // literally contain the Deal Item's own material name or vice versa, so a line this
            // misses is expected to fall to "skipped" and get resolved by the AI-assisted
            // suggestion tool (see MappingCards' Ask AI button) rather than a looser string rule
            // silently guessing which real-world material is meant.
            ZohoDealItem? item = null;
            if (!string.IsNullOrEmpty(lineCode))
                item = deal.Items.FirstOrDefault(it =>
                    !string.IsNullOrEmpty(it.MaterialCode) && string.Equals(it.MaterialCode, lineCode, StringComparison.OrdinalIgnoreCase));
            if (item is null && !string.IsNullOrEmpty(desc))
            {
                var d = desc.ToLowerInvariant();
                item = deal.Items.FirstOrDefault(it =>
                    (!string.IsNullOrEmpty(it.MaterialName) && (it.MaterialName.ToLowerInvariant().Contains(d) || d.Contains(it.MaterialName.ToLowerInvariant())))
                    || (!string.IsNullOrEmpty(it.MaterialDescription) && (it.MaterialDescription.ToLowerInvariant().Contains(d) || d.Contains(it.MaterialDescription.ToLowerInvariant()))));
            }
            // Not WCD (Forecast Status No / blank, or a new Deal): a line that is not one of the Deal's
            // Items is looked up in Zoho Materials by its mapped material code (Megachem, 27 Oct 2026).
            // A WCD Deal (Forecast Status Yes) still only takes its own forecast items.
            string? notFound = null;
            if ((item is null || string.IsNullOrEmpty(item.MaterialId)) && !deal.IsWcd)
            {
                if (string.IsNullOrWhiteSpace(lineCode))
                    notFound = "Not a Deal Item, and no material code is mapped on this line to look up in Zoho";
                else
                {
                    try
                    {
                        var m = await materialClient.FindByCodeAsync(lineCode);
                        if (m is null) notFound = $"Not a Deal Item, and material {lineCode} was not found in Zoho Materials";
                        else item = MaterialAsItem(m);
                    }
                    catch (Exception e)
                    {
                        log.LogWarning(e, "Zoho Materials lookup failed for {Code} (doc {DocId})", lineCode, docId);
                        notFound = $"Not a Deal Item, and the Zoho Materials lookup for {lineCode} failed";
                    }
                }
            }
            if (item is null || string.IsNullOrEmpty(item.MaterialId))
            {
                skipped.Add(new SkippedLine(itemNo, desc, extCode, notFound
                    ?? (item is null
                        ? (deal.IsWcd ? "No matching Deal Item found for this line (WCD Deal: lines must be items of the Deal)" : "No matching Deal Item found for this line")
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

        var subject = $"{deal.DealName} / Doc#{docId}";
        if (subject.Length > 50) subject = subject[..50];

        // Delivery_Date: same rule as SapPayloadBuilder's RequestedDeliveryDate -- when every line
        // ships on one date (always true for a child of "split by delivery date"), that line date
        // wins over the header's, because a split child keeps the PARENT's header deliveryDate.
        var lineDates = LineDeliveryDates(lines);
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
            // The document's Remark -> Zoho Sales Order "Remarks" (Multi Line, Megachem Zoho manual
            // AO-CRM-UM-2026-003 p.302-303). MGT/Zoho only: GLC/SAP carries notes in Item Note 1.
            Remarks: header.GetStr("remark") is { Length: > 0 } rm ? rm : null,
            NewDeal: newDeal,
            QuoteRefs: DocQuoteRefs(header, lines));
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
    // (non-WCD only). No unit / conversion: the line keeps the document's own unit.
    private static ZohoDealItem MaterialAsItem(ZohoMaterial m) => new(
        MaterialName: m.Name, MaterialId: m.Id, MaterialCode: m.Code, MaterialDescription: null,
        MaterialGroup: m.MaterialGroup, Quantity: null, Unit: null, ConversionRatio: null, SubUnit: null,
        UnitPrice: null, PriceSubUnit: null, LastPrice: null, TotalAmount: null, ShipVia: null, Stock: null,
        LeadTimeDays: null, SoCompleted: null, SoCancelled: null);

    // ── Quote step (27 Oct 2026): Deal -> Quote -> Sales Order ─────────────────────────────────
    private record QuotePlan(ZohoQuote? Quote, string Reason);

    // Which of the Deal's Quotes this document's Sales Order is made from (Megachem, 27 Oct 2026):
    // 1) a Quote whose Customer Ref is the document's PO no. (items matching wins among several);
    // 2) no PO match (e.g. the customer didn't quote a number): a Quote whose items match the
    //    document -- same materials, quantities and unit prices; 3) none -> a new Quote is created.
    private async Task<QuotePlan> PlanQuoteAsync(ZohoDeal deal, string? customerRef, IReadOnlyList<ZohoSalesOrderLine> lines,
        IReadOnlyList<string>? quoteRefs = null)
    {
        var refs = quoteRefs ?? [];
        var refNote = refs.Count > 0 ? $" (the PO mentions Quote {string.Join(", ", refs)}, which is not on this Deal)" : "";
        if (string.IsNullOrEmpty(deal.Id)) return new(null, "New Deal — a new Quote will be created" + refNote);
        var quotes = await quoteClient.FindByDealAsync(deal.Id);
        // 0) The PO quotes Megachem's own Quote No. -- that Quote wins over every other rule.
        var byNo = quotes.FirstOrDefault(q => refs.Contains((q.QuoteNo ?? "").Trim().ToUpperInvariant()));
        if (byNo is not null) return new(byNo, $"The PO refers to Quote {byNo.QuoteNo}");
        if (quotes.Count == 0) return new(null, "The Deal has no Quote yet — a new Quote will be created" + refNote);
        var po = (customerRef ?? "").Trim();
        var byRef = po.Length > 0 && po != "-"
            ? quotes.Where(q => string.Equals((q.CustomerRef ?? "").Trim(), po, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<ZohoQuote>();
        var hit = byRef.FirstOrDefault(q => QuoteItemsMatch(q, lines));
        if (hit is not null) return new(hit, $"Customer Ref {po} and items match");
        if (byRef.Count > 0) return new(byRef[0], $"Customer Ref {po} matches");
        hit = quotes.FirstOrDefault(q => QuoteItemsMatch(q, lines));
        if (hit is not null) return new(hit, "Items match (material, quantity, unit price)");
        return new(null, $"None of the Deal's {quotes.Count} Quote(s) matches this document's Customer Ref or items — a new Quote will be created" + refNote);
    }

    private static bool QuoteItemsMatch(ZohoQuote q, IReadOnlyList<ZohoSalesOrderLine> lines)
    {
        if (lines.Count == 0 || q.Items.Count != lines.Count) return false;
        var pool = q.Items.ToList();
        foreach (var l in lines)
        {
            var i = pool.FindIndex(it =>
                SameMaterial(it, l)
                && it.Quantity is decimal qq && Math.Round(qq, 3) == Math.Round(l.Quantity, 3)
                && (l.UnitPrice is null || it.UnitPrice is null || Math.Round(it.UnitPrice.Value, 2) == Math.Round(l.UnitPrice.Value, 2)));
            if (i < 0) return false;
            pool.RemoveAt(i);
        }
        return true;
    }

    private static bool SameMaterial(ZohoQuoteItem it, ZohoSalesOrderLine l) =>
        (!string.IsNullOrEmpty(it.MaterialId) && it.MaterialId == l.MaterialId)
        || (!string.IsNullOrWhiteSpace(it.MaterialCode) && string.Equals(it.MaterialCode.Trim(), (l.MaterialCode ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(it.MaterialName) && string.Equals(it.MaterialName.Trim(), (l.Description ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

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
        dealClient.BuildCreateRecordAsync(a.Deal.AccountId!, a.Deal.AccountCode, a.Deal.DealName,
            HeaderDate(a.DeliveryDate, groups), a.CustomerRef is { Length: > 0 } cr && cr != "-" ? cr : null,
            a.PaymentTerms, a.PaymentCurrency, a.Incoterms,
            a.SoLines.Select(l => new ZohoNewDealLine(l.MaterialId, l.MaterialCode, l.Description, l.MaterialGroup,
                l.Quantity, l.UnitPrice, l.Unit)).ToList());

    private Task<JsonObject> BuildNewQuoteRecordAsync(Assembled a, string? dealId, ZohoShipToInfo? shipTo, IEnumerable<SoGroup> groups) =>
        quoteClient.BuildRecordAsync(dealId, a.Deal.AccountId!, a.Subject, a.Deal.AccountCode, a.TaxId, a.CustomerRef,
            HeaderDate(a.DeliveryDate, groups), a.PaymentTerms, a.PaymentCurrency, a.Incoterms, shipTo, a.SoLines);

    // Step 1 of a send without a Deal: open the (non-WCD) Deal. A retry after a failed later step
    // reuses the Deal an earlier attempt opened (same customer, same Customer Ref, not WCD, stage
    // NewDealStage) instead of opening a second one.
    private async Task<ZohoDeal> EnsureDealAsync(int docId, Assembled a, List<SoGroup> groups, string actor)
    {
        var po = (a.CustomerRef ?? "").Trim();
        if (po.Length > 0 && po != "-" && !string.IsNullOrWhiteSpace(a.Deal.AccountCode))
        {
            try
            {
                var open = await dealClient.FindOpenDealsByAccountCodeAsync(a.Deal.AccountCode!);
                var earlier = open.FirstOrDefault(d => !d.IsWcd && d.Stage == ZohoDealClient.NewDealStage
                    && string.Equals((d.CustomerRef ?? "").Trim(), po, StringComparison.OrdinalIgnoreCase));
                if (earlier is not null && !string.IsNullOrEmpty(earlier.AccountId))
                {
                    await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
                        detail: $"Using Zoho Deal \"{earlier.DealName}\" (Customer Ref {po}, not WCD) opened earlier instead of opening another");
                    return earlier;
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not check Zoho for an earlier Deal of doc {DocId}", docId);
            }
        }

        var record = await BuildNewDealRecordAsync(a, groups);
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
        var deal = created is null || string.IsNullOrEmpty(created.AccountId)
            ? a.Deal with { Id = res.ZohoId }
            : created;
        await repo.LogAuditAsync(docId, "SO", "CREATE", actor, fileName: a.Doc.GetStr("fileName"),
            detail: $"Opened Zoho Deal \"{deal.DealName}\" (record {res.ZohoId}, Forecast Status No, Stage {ZohoDealClient.NewDealStage}) — the document had no Deal");
        return deal;
    }

    // Step 2: the Deal's Quote for this document (PlanQuoteAsync), or a new one (Stage Open) with
    // every line of the document. One Quote per document; each Sales Order of the document links to it.
    private async Task<(string Id, string? No)> EnsureQuoteAsync(int docId, ZohoDeal deal, Assembled a,
        ZohoShipToInfo? shipTo, List<SoGroup> groups, string actor)
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
        ZohoUpsertResult res;
        try { res = await quoteClient.CreateAsync(record); }
        catch (Exception e) { res = new ZohoUpsertResult { SapKey = "", ZohoId = "", Status = "error", Message = e.Message }; }
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

    // GET /api/zoho/sales-order/preview/{docId}?dealId=... — every value Create would send,
    // laid out for the person to review/edit first (see class comment). Never writes anything.
    [HttpGet("sales-order/preview/{docId:int}")]
    public async Task<IActionResult> Preview(int docId, [FromQuery] string dealId)
    {
        if (string.IsNullOrWhiteSpace(dealId))
            throw new HttpApiException(400, "A Deal must be selected first");

        var d = await BuildDefaultsAsync(docId, dealId);
        // Deal -> Quote -> Sales Order plan shown above the editor (27 Oct 2026).
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
        var paymentTermsOptions = await soClient.GetPicklistOptionsAsync("Payment_Terms");
        var paymentCurrencyOptions = await soClient.GetPicklistOptionsAsync("Payment_Currency");

        return Ok(new
        {
            dealName = d.Deal.DealName,
            accountCode = d.Deal.AccountCode,
            flow = new
            {
                wcd = d.Deal.IsWcd,
                forecastStatus = d.Deal.ForecastStatus,
                newDeal = d.NewDeal,
                dealStage = d.Deal.Stage,
                quoteAction,
                quoteNo,
                quoteStage,
                quoteReason,
                quoteRefs = d.QuoteRefs ?? [],
            },
            subject = d.Subject,
            customerRef = d.CustomerRef,
            deliveryDate = d.DeliveryDate,
            paymentTerms = d.PaymentTerms,
            paymentCurrency = d.PaymentCurrency,
            incoterms = d.Incoterms,
            taxId = d.TaxId,
            remarks = d.Remarks,
            paymentTermsOptions,
            paymentCurrencyOptions,
            lines = d.Lines.Select(l => new
            {
                itemNo = l.ItemNo,
                desc = l.Desc,
                extCode = l.ExtCode,
                materialName = l.MaterialName,
                materialCode = l.MaterialCode,
                quantity = l.Quantity,
                unitPrice = l.UnitPrice,
                unit = l.Unit,
                conversionRatio = l.ConversionRatio,
                subUnit = l.SubUnit,
            }),
            skipped = d.Skipped,
        });
    }

    // Shared by Create (which sends) and Payload (which only previews): recompute the defaults,
    // fold in the caller's edits + any AI-confirmed material matches, and assemble the exact line
    // list + resolved header values that both endpoints hand to the ZohoSalesOrderClient. Material
    // identity per line always comes from the server-side Deal Item match, never the request.
    private async Task<Assembled> AssembleAsync(int docId, CreateSalesOrderRequest body, bool forCreate = false)
    {
        var d = await BuildDefaultsAsync(docId, body.DealId, requireUomConversion: true,
            enforceDealAccount: forCreate && !body.AcceptOtherAccount);

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
            throw new HttpApiException(400, "No document line could be matched to a Deal Item in \"" + d.Deal.DealName + "\" — nothing to send to Zoho");

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
                StatusNote: l.ItemNote1);
        }).ToList();

        var subject = body.Subject is { Length: > 0 } ? body.Subject : d.Subject;
        if (subject.Length > 50) subject = subject[..50];

        return new Assembled(
            Deal: d.Deal,
            Subject: subject,
            TaxId: body.TaxId is { Length: > 0 } ? body.TaxId : d.TaxId,
            CustomerRef: body.CustomerRef is { Length: > 0 } ? body.CustomerRef : d.CustomerRef,
            DeliveryDate: body.DeliveryDate is { Length: > 0 } ? body.DeliveryDate : d.DeliveryDate,
            PaymentTerms: body.PaymentTerms is { Length: > 0 } ? body.PaymentTerms : d.PaymentTerms,
            PaymentCurrency: body.PaymentCurrency is { Length: > 0 } ? body.PaymentCurrency : d.PaymentCurrency,
            Incoterms: body.Incoterms is { Length: > 0 } ? body.Incoterms : d.Incoterms,
            ShipTo: body.ShipTo,
            Remarks: body.Remarks is { Length: > 0 } ? body.Remarks : d.Remarks,
            SoLines: soLines,
            StillSkipped: stillSkipped,
            Doc: d.Doc,
            SoItemNos: resolvedLines.Select(l => l.ItemNo).ToList(),
            NewDeal: d.NewDeal,
            QuoteRefs: d.QuoteRefs ?? []);
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
        List<string>? QuoteRefs = null);

    // POST /api/zoho/sales-order/create/{docId} { dealId, ...edited fields } — {docId} as a route
    // parameter (not buried inside the body) so DepartmentAccessFilter's docId-based module gate
    // applies here exactly like every DocumentsController action.
    [HttpPost("sales-order/create/{docId:int}")]
    public async Task<IActionResult> Create(int docId, [FromBody] CreateSalesOrderRequest body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.DealId))
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
        var groups = SoDeliveryGroups.Build(existingHeader, (List<Dictionary<string, object?>>)existing["lines"]!);
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

        var a = await AssembleAsync(docId, body, forCreate: true);

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
            // Zoho record id of a Sales Order created in THIS call -- used to read it back for the
            // SharePoint folder. (The stored DocNo is now Zoho's Sales Order No., not the record id; and
            // a document only reaches POSTED here when this call created at least one order.)
            string? firstZohoId = null;
            string? firstError = null;
            var linesSent = 0;

            // Zoho chain Deal -> Quote -> Sales Order (Megachem, 27 Oct 2026): before the first Sales
            // Order of this send, make sure the Deal and its Quote exist -- open a (non-WCD) Deal when
            // the document has none, and find the Deal's Quote for this document or create one. Every
            // Sales Order below links to that Quote. Skipped when every group is already in Zoho.
            var deal = a.Deal;
            string? quoteId = null, quoteNo = null;
            if (groups.Any(g => !(states.TryGetValue(g.Key, out var st) && st.Posted)))
            {
                if (a.NewDeal) deal = await EnsureDealAsync(docId, a, groups, actor);
                var quoteShipTo = a.ShipTo ?? groups.Select(g => GroupShipTo(existingHeader, g)).FirstOrDefault(x => x is not null);
                (quoteId, quoteNo) = await EnsureQuoteAsync(docId, deal, a, quoteShipTo, groups, actor);
            }

            for (var gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
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
                var subject = multi ? GroupSubject(a.Subject, gi + 1, groups.Count) : a.Subject;

                // The previous attempt of this group failed. A timeout can fail AFTER Zoho created the
                // order, so look for it before sending again (same Deal, Customer Ref, Subject and
                // delivery date, created in the last 7 days, not recorded on this document yet).
                (string Id, string? SoNo)? earlierOrder = null;
                if (hasState && !string.IsNullOrEmpty(done!.Error) && !string.IsNullOrWhiteSpace(a.CustomerRef))
                    earlierOrder = await soClient.FindExistingAsync(deal.Id, a.CustomerRef!, subject, deliveryDate,
                        DateTime.UtcNow.AddDays(-7), alreadyRecorded);

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
                        shipTo: shipTo, items: items, remarks: a.Remarks, quoteId: quoteId, quoteNo: quoteNo);
                }
                catch (Exception e)
                {
                    log.LogError(e,
                        "Zoho Sales Order creation failed for document {DocId}, delivery-date group {DeliveryDate}, items {ItemNos}",
                        docId, g.Key, string.Join(",", g.ItemNos));
                    result = new ZohoUpsertResult { SapKey = "", ZohoId = "", Status = "error", Message = e.Message };
                }
                var ok = result.Status == "success";
                if (ok) { linesSent += items.Count; firstZohoId ??= result.ZohoId; } else firstError ??= result.Message;
                // What people see and search for in Zoho is the "Sales Order No." (auto-number, API
                // name Quote_No, e.g. 1OR0000310) -- stored as the posted number instead of the long
                // record id. The record id is kept in the PostLog payload (zohoRecordId).
                var soNo = ok ? await SalesOrderNoAsync(result.ZohoId) : null;
                if (soNo is not null) alreadyRecorded.Add(soNo);

                var label = multi ? $" (Sales Order {gi + 1}/{groups.Count}, SO Group {g.SoGroupNo}, delivery {(g.Date.Length > 0 ? g.Date : "-")})" : "";
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

            // companyCode "MGT" literal: this controller only ever creates Zoho Sales Orders (MGT).
            var newStatus = await soPosts.FinalizeAsync(docId, groups, "MGT", actor);
            // Release before reading the document back so the response never shows POSTING.
            await soPosts.ReleaseAsync(docId, previousStatus);

            // Queue the source file for SharePoint once EVERY group is in Zoho. The folder comes from
            // reading one created Sales Order back (its Account's sales person + customer). Best effort.
            if (newStatus == "POSTED" && !string.IsNullOrEmpty(firstZohoId))
            {
                var folder = await ArchiveFolderAsync(docId, firstZohoId, deal.Id, deal.AccountCode);
                try { await archive.EnqueueAsync(docId, DocumentTables.ForId(docId).Doc, "MGT", "SO", folder); }
                catch (Exception e) { log.LogWarning(e, "Could not queue doc {DocId} for SharePoint (run sql/27 + sql/29?)", docId); }
            }

            var after = await repo.GetDocumentAsync(docId);
            return Ok(new
            {
                success = newStatus == "POSTED",
                status = newStatus,
                zohoId = after.GetStr("sapDocNo"),
                message = firstError,
                dealName = deal.DealName,
                quoteNo,
                linesSent,
                skipped = a.StillSkipped,
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

    private static bool HasShipTo(ZohoShipToInfo? s) =>
        s is not null && (!string.IsNullOrWhiteSpace(s.Code) || !string.IsNullOrWhiteSpace(s.Address));

    // "Deal name / Doc#123 (1/2)" -- Zoho Subject is capped at 50 characters, so the base is cut to
    // make room for the group suffix instead of losing the suffix.
    private static string GroupSubject(string baseSubject, int n, int total)
    {
        var suffix = $" ({n}/{total})";
        var room = Math.Max(0, 50 - suffix.Length);
        return (baseSubject.Length > room ? baseSubject[..room] : baseSubject) + suffix;
    }

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
        if (body is null || string.IsNullOrWhiteSpace(body.DealId))
            throw new HttpApiException(400, "A Deal must be selected first");

        var doc = await repo.GetDocumentAsync(docId);
        var docHeader = (Dictionary<string, object?>)doc["header"]!;
        var groups = SoDeliveryGroups.Build(docHeader, (List<Dictionary<string, object?>>)doc["lines"]!);
        var multi = groups.Count > 1;
        var a = await AssembleAsync(docId, body);

        // Deal / Quote steps of the send (27 Oct 2026), shown the same way: the record that would be
        // created, or which existing one is used.
        const string createdOnSend = "(created on Send)";
        var dealPart = new JsonObject();
        if (a.NewDeal)
        {
            dealPart["action"] = "create";
            dealPart["record"] = await BuildNewDealRecordAsync(a, groups);
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
            var quoteShipTo = a.ShipTo ?? groups.Select(g => GroupShipTo(docHeader, g)).FirstOrDefault(x => x is not null);
            quotePart["record"] = await BuildNewQuoteRecordAsync(a, a.NewDeal ? createdOnSend : a.Deal.Id, quoteShipTo, groups);
        }

        var records = new System.Text.Json.Nodes.JsonArray();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var items = Enumerable.Range(0, a.SoLines.Count).Where(i => g.ItemNos.Contains(a.SoItemNos[i])).Select(i => a.SoLines[i]).ToList();
            if (items.Count == 0) continue;
            var record = await soClient.BuildRecordAsync(
                dealId: a.NewDeal ? createdOnSend : a.Deal.Id, accountId: a.Deal.AccountId!,
                subject: multi ? GroupSubject(a.Subject, gi + 1, groups.Count) : a.Subject,
                accountCode: a.Deal.AccountCode, taxId: a.TaxId, customerRef: a.CustomerRef,
                deliveryDate: multi && g.Date.Length > 0 ? g.Date : a.DeliveryDate,
                paymentTerms: a.PaymentTerms, paymentCurrency: a.PaymentCurrency, incoterms: a.Incoterms,
                shipTo: GroupShipTo(docHeader, g) ?? a.ShipTo, items: items, remarks: a.Remarks,
                quoteId: quoteIdForSo, quoteNo: quoteNoForSo);
            records.Add(record);
        }

        // One Zoho insert per delivery-date group; each is shown the way it goes on the wire
        // ("data" array of one record per call).
        var envelope = new System.Text.Json.Nodes.JsonObject
        {
            ["_target"] = "Zoho CRM — Deal -> Quote -> Sales Orders (POST /crm/v8/{module})",
            ["_deal"] = dealPart,
            ["_quote"] = quotePart,
            ["_salesOrders"] = records.Count,
            ["data"] = records,
        };
        return Ok(envelope);
    }
}
