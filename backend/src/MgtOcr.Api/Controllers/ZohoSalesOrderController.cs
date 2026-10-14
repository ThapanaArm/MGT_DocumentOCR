using MgtOcr.Api.Auth;
using MgtOcr.Api.Services;
using System.Text.Json;
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
        List<Dictionary<string, object?>> UomRules, string SalesOrg, string? Remarks = null);

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

        var deal = await dealClient.GetByIdAsync(dealId);
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
        if (enforceDealAccount && docCustomer.Length > 0 && !string.IsNullOrWhiteSpace(deal.AccountCode)
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
            if (item is null || string.IsNullOrEmpty(item.MaterialId))
            {
                skipped.Add(new SkippedLine(itemNo, desc, extCode,
                    item is null ? "No matching Deal Item found for this line" : "The matched Deal Item has no Zoho Material link"));
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
            Remarks: header.GetStr("remark") is { Length: > 0 } rm ? rm : null);
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
        var paymentTermsOptions = await soClient.GetPicklistOptionsAsync("Payment_Terms");
        var paymentCurrencyOptions = await soClient.GetPicklistOptionsAsync("Payment_Currency");

        return Ok(new
        {
            dealName = d.Deal.DealName,
            accountCode = d.Deal.AccountCode,
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
            SoItemNos: resolvedLines.Select(l => l.ItemNo).ToList());
    }

    private record Assembled(
        ZohoDeal Deal, string Subject,
        string? TaxId, string? CustomerRef, string? DeliveryDate,
        string? PaymentTerms, string? PaymentCurrency, string? Incoterms,
        ZohoShipToInfo? ShipTo, string? Remarks,
        List<ZohoSalesOrderLine> SoLines, List<SkippedLine> StillSkipped,
        Dictionary<string, object?> Doc,
        List<string> SoItemNos); // itemNo of each SoLines entry (same order) -- used to cut per-delivery-date groups

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
                    earlierOrder = await soClient.FindExistingAsync(a.Deal.Id, a.CustomerRef!, subject, deliveryDate,
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
                        dealId: a.Deal.Id, accountId: a.Deal.AccountId!, subject: subject, accountCode: a.Deal.AccountCode,
                        taxId: a.TaxId, customerRef: a.CustomerRef, deliveryDate: deliveryDate,
                        paymentTerms: a.PaymentTerms, paymentCurrency: a.PaymentCurrency, incoterms: a.Incoterms,
                        shipTo: shipTo, items: items, remarks: a.Remarks);
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
                    ? $"Created Zoho Sales Order from Deal \"{a.Deal.DealName}\"{label} -> {soNo} (record {result.ZohoId})"
                    : $"Zoho Sales Order creation failed from Deal \"{a.Deal.DealName}\"{label}: {result.Message}");

                var payloadJson = JsonSerializer.Serialize(new
                {
                    dealId = a.Deal.Id, dealName = a.Deal.DealName, accountId = a.Deal.AccountId,
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
                var folder = await ArchiveFolderAsync(docId, firstZohoId, a.Deal.Id, a.Deal.AccountCode);
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
                dealName = a.Deal.DealName,
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
        var records = new System.Text.Json.Nodes.JsonArray();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var items = Enumerable.Range(0, a.SoLines.Count).Where(i => g.ItemNos.Contains(a.SoItemNos[i])).Select(i => a.SoLines[i]).ToList();
            if (items.Count == 0) continue;
            var record = await soClient.BuildRecordAsync(
                dealId: a.Deal.Id, accountId: a.Deal.AccountId!,
                subject: multi ? GroupSubject(a.Subject, gi + 1, groups.Count) : a.Subject,
                accountCode: a.Deal.AccountCode, taxId: a.TaxId, customerRef: a.CustomerRef,
                deliveryDate: multi && g.Date.Length > 0 ? g.Date : a.DeliveryDate,
                paymentTerms: a.PaymentTerms, paymentCurrency: a.PaymentCurrency, incoterms: a.Incoterms,
                shipTo: GroupShipTo(docHeader, g) ?? a.ShipTo, items: items, remarks: a.Remarks);
            records.Add(record);
        }

        // One Zoho insert per delivery-date group; each is shown the way it goes on the wire
        // ("data" array of one record per call).
        var envelope = new System.Text.Json.Nodes.JsonObject
        {
            ["_target"] = "Zoho CRM — Sales Orders (POST /crm/v8/{module})",
            ["_salesOrders"] = records.Count,
            ["data"] = records,
        };
        return Ok(envelope);
    }
}
