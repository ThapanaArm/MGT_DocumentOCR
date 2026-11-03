using MgtOcr.Core;
using MgtOcr.Sap;

namespace MgtOcr.Api.Export;

/// <summary>
/// The MIRO document as the OData service takes it — API_SUPPLIERINVOICE_PROCESS_SRV.
///
/// Why this exists next to the Excel exporter rather than inside SapPayloadBuilder: the two routes
/// must post the SAME document, so both build their lines from GlRowsFor / DeferredVatReversal.
/// The old AP payload predates every rule GLC gave us — it sent every line as a PO item with
/// invented item numbers (10, 20, 30…), one hardcoded tax code "V7", a single tax row off the
/// header, and no G/L items at all.
///
/// The split, which is the whole point of posting through the API:
///   • goods bought against a purchase order  -> to_SuplrInvcItemPurOrdRef, naming the PO's own
///     item numbers (10, 40, 50, 60 on PO 2230000020 — SAP numbers them with gaps, so they are
///     read from SAP, never guessed). The Import Supplier Invoices spreadsheet cannot express
///     this at all, which is why trade invoices have to go this way.
///   • delivery costs, duty and fees        -> to_SupplierInvoiceItemGLAcct, one row per COSTS heading
///     exactly as the file produces them.
///   • VAT                                  -> to_SupplierInvoiceTax, one row per tax invoice with its
///     own code (V1 claimable, D1 deferred), not 7% of everything.
/// </summary>
public static class SupplierInvoicePayload
{
    public const string Endpoint = "API_SUPPLIERINVOICE_PROCESS_SRV/A_SupplierInvoice";
    private const string HeadOffice = "0000";

    public static Dictionary<string, object?> Build(
        Dictionary<string, object?> doc,
        string companyCode,
        string invoicingParty,
        IReadOnlyList<SapPurchaseOrderItem> poItems,
        Dictionary<string, object?>? source = null,
        string paymentTerms = "",
        string vendorFilter = "",
        IReadOnlyList<System.Text.Json.JsonElement>? goodsReceiptItems = null)
    {
        var header = doc.Get("header") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var poRef = PoNumber(header);
        var currency = Or(header.GetStr("currency").Trim(), "THB");
        var businessPlace = Or(header.GetStr("businessPlace").Trim(), HeadOffice);
        var branch = Or(header.GetStr("branch").Trim(), HeadOffice);

        // The same rows the file is built from, narrowed to ONE supplier when the caller is
        // splitting the document. A shipping bundle carries several suppliers' invoices on one
        // form (the agent, the port authority, the customs broker), and a SAP supplier invoice
        // has exactly one invoicing party — so one bundle becomes one document per supplier.
        var vendor = vendorFilter.Trim();
        var mainVendor = Or(doc.GetStr("partnerCode").Trim(), header.GetStr("vendorCode").Trim());
        bool Mine(Dictionary<string, object?> r)
        {
            if (vendor.Length == 0) return true;
            // A row the read could not tag belongs to the document's own supplier.
            var v = Or(r.GetStr("vendorCode").Trim(), mainVendor);
            return string.Equals(v, vendor, StringComparison.OrdinalIgnoreCase);
        }
        var glRows = SapImportSupplierInvoiceExcel.GlRowsFor(doc).Where(Mine).ToList();
        var taxRows = DeferredVatReversal.TaxRows(doc).Where(Mine).ToList();
        var isPoKeyed = SapImportSupplierInvoiceExcel.IsPoKeyed(doc);

        // Goods-receipt reference. A PO item flagged for GR-based invoice verification (the
        // default on a stock PO) can only be invoiced against the material document the goods
        // receipt created — SAP asks for it as M8/375 "Fill in mandatory field 'ReferenceDocument,
        // -FiscalYear, -Item'". The number is on the bundle (A-MatDoc_5000002095_...) and the
        // screen keeps it in header.refMatDoc; the year defaults to the posting date's.
        var matDoc = header.GetStr("refMatDoc").Trim();
        var matDocYear = Or(header.GetStr("refMatDocYear").Trim(), FiscalYearOf(header));
        // Assignment and Item Text, as the G/L Account Items grid on screen shows them
        // ("2230000581" / "KIMEX,INV.2230000581"). Keyed against a purchase order there are no
        // G/L lines in the payload, so without this the vendor line in SAP came out with both
        // fields EMPTY — Finance reconciles on Assignment, so it has to be on every line and on
        // the header, not only on the delivery-cost documents that happen to have G/L rows.
        var firstGl = glRows.FirstOrDefault(g => !SapImportSupplierInvoiceExcel.IsTaxAccountRow(g));
        // goodsInvoiceNo is the INVOICE NO. at the head of FORM SHIPPING EXPENSE — the goods
        // supplier's own number ("2230000581"), which the reader files under its own name while
        // invoiceNo stays empty. It stands ahead of the PO number so a document posted straight
        // from storage, without the screen having seeded it, still carries the real invoice.
        var assignment = Or(firstGl?.GetStr("assignment").Trim() ?? "",
            Or(header.GetStr("invoiceNo").Trim(),
               Or(header.GetStr("goodsInvoiceNo").Trim(), poRef)));
        var itemText = Or(firstGl?.GetStr("itemText").Trim() ?? "", assignment);

        // The Text box on Basic Data (BKTXT). The screen keeps it in header.headerText and it
        // wins: somebody who typed there meant that text. Nothing read it before and the box was
        // silently ignored.
        //
        // The fallbacks matter, because a PO-keyed invoice carries no G/L rows at all — the goods
        // are posted against the purchase order — so firstGl is null and the old chain ended at
        // `assignment`, a bare number. Document 5105600157 went to SAP with Text "2230000031"
        // for that reason, where Accounting writes "KIMEX,INV.2230000031": supplier, then the
        // reference. Composed here when there is no row to take it from.
        var headerText = Or(header.GetStr("headerText").Trim(),
            Or(firstGl?.GetStr("itemText").Trim() ?? "", ReferenceText(header)));

        // MIRO's Reference Document Category, the same choice the PO Reference tab shows:
        // 1 = goods, 2 = planned delivery costs, 3 = both.
        var refDocType = header.GetStr("refDocType").Trim();
        var wantsGoods = isPoKeyed || refDocType is "1" or "3";
        var wantsDeliveryCosts = poRef.Length > 0 && refDocType is "2" or "3";

        var purOrdRef = wantsGoods
            ? PurchaseOrderItems(doc, poRef, poItems, currency, header.GetStr("taxCode").Trim(),
                matDoc, matDocYear, assignment, itemText)
            : [];

        // Planned delivery costs. These are NOT G/L lines: the purchase order already booked them
        // as an accrual when the goods were received (document 5000000005 credits 21229108 Local
        // Hand&Fr with ten PK 50 lines from a WE goods receipt), and the invoice clears that
        // accrual by naming the PO's own condition. Posting them as G/L lines instead is what SAP
        // refused with ME/038 "No direct postings can be made to G/L acct. 21229108" — the account
        // takes automatic postings only, in the test tenant and in production alike.
        var deliveryCosts = wantsDeliveryCosts
            ? DeliveryCostItems(doc, poRef, currency, header.GetStr("taxCode").Trim(),
                invoicingParty, itemText, Mine, purOrdRef.Count, poItems, goodsReceiptItems)
            : [];
        purOrdRef.AddRange(deliveryCosts);

        // Costs only: the VAT rows go to to_SupplierInvoiceTax below (sending them as G/L lines as well
        // would book the tax twice), and the withholding row is not a G/L line at all.
        var glAccountItems = new List<object>();
        var itemNo = purOrdRef.Count;
        foreach (var g in glRows)
        {
            if (isPoKeyed) break;                                   // the goods come from the PO
            // Costs sent as planned delivery costs above must not also go out as G/L lines —
            // the document would carry them twice and SAP would refuse the account anyway.
            if (deliveryCosts.Count > 0) break;
            if (SapImportSupplierInvoiceExcel.IsTaxAccountRow(g)) continue;
            if (string.Equals(g.GetStr("itemText").Trim(), "WHT", StringComparison.OrdinalIgnoreCase)) continue;
            var amount = Num(g.Get("amount"));
            if (amount == 0) continue;

            glAccountItems.Add(new Dictionary<string, object?>
            {
                ["SupplierInvoiceItem"] = (++itemNo).ToString(),
                ["CompanyCode"] = companyCode,
                ["GLAccount"] = g.GetStr("glAccount"),
                ["DocumentCurrency"] = currency,
                ["SupplierInvoiceItemAmount"] = F2(amount),
                ["DebitCreditCode"] = string.Equals(g.GetStr("drCr"), "C", StringComparison.OrdinalIgnoreCase) ? "H" : "S",
                ["TaxCode"] = g.GetStr("taxCode"),
                ["AssignmentReference"] = Cut(g.GetStr("assignment"), 18),
                ["SupplierInvoiceItemText"] = Cut(g.GetStr("itemText"), 50),
            });
        }

        // "Calculate Tax" on the Basic Data tab, the same checkbox MIRO has. Ticked, SAP works the
        // tax out itself from the tax code on each line and the document must NOT carry a tax
        // node — sending one then is what SAP rejects as "Property 'to_SupplierInvoiceTax' is invalid".
        // Unticked (the default here), the amounts we read off the tax invoices are sent as they
        // were reviewed, which is what a Thai input-VAT document needs: one row per tax invoice.
        var calcTax = header.GetStr("calculateTax").Trim() is { Length: > 0 } ct
            && !string.Equals(ct, "false", StringComparison.OrdinalIgnoreCase) && ct != "0";

        // One row per tax invoice, carrying its own code and base — what the Tax tab holds after
        // somebody reviewed it, rather than a rate applied to the whole document.
        var taxItems = new List<object>();
        foreach (var t in taxRows)
        {
            if (calcTax) break;
            var amount = Num(t.Get("docCurrencyAmt"));
            if (amount == 0) continue;
            var code = t.GetStr("taxCode").Trim();
            if (code.Length == 0) continue;
            taxItems.Add(new Dictionary<string, object?>
            {
                ["TaxCode"] = code,
                ["DocumentCurrency"] = currency,
                // A_SupplierInvoiceTaxType calls the base "TaxBaseAmountInTransCrcy". Sent as
                // "TaxBaseAmount" the whole node failed to deserialize and SAP answered
                // "Property 'to_SupplierInvoiceTax' is invalid" — a parsing error that names the
                // node, never the field, which is why it took the service's $metadata to find.
                ["TaxBaseAmountInTransCrcy"] = F2(Num(t.Get("baseAmount"))),
                ["TaxAmount"] = F2(amount),
            });
        }

        // Shipping documents (delivery costs, duty, fees — anything that is not a plain goods
        // invoice against a PO) go to SAP PARKED, not posted: AP opens the draft, checks it and
        // posts it themselves. Goods invoices (INVENTORY_PO) keep posting as before.
        var park = !isPoKeyed;

        // Gross = what the lines add up to, with each G/L line SIGNED by its Debit/Credit code.
        // The old sum added every G/L line as positive, so a credit (H) line pushed the gross UP
        // instead of down and the draft opened in SAP with a balance that was not zero.
        var gross = SumOfParts(purOrdRef, glAccountItems, taxItems);
        // A document whose own total was read and reviewed wins over the sum of its parts: the
        // difference is the rounding SAP books as Purchase Variance, and it can only do that if we
        // state the real invoice total.
        // Only when the whole document is being posted: header.totalAmount is the bundle's total,
        // so using it for one supplier's share would bill that supplier for everybody's costs.
        var stated = vendor.Length == 0 ? Num(header.Get("totalAmount")) : 0;
        if (stated > 0)
        {
            // A parked document must open in SAP with Balance 0 — nobody posts it here, so there is
            // no Purchase Variance to soak up the difference. Satang rounding is moved onto the
            // largest line; anything bigger is a real mismatch and is refused before sending.
            gross = park ? BalanceToStated(stated, gross, purOrdRef, glAccountItems, taxItems) : stated;
        }

        var payload = new Dictionary<string, object?>
        {
            ["_target"] = Endpoint,
            ["CompanyCode"] = companyCode,
            ["DocumentDate"] = SapPayloadBuilder.ODataDate(header.Get("invoiceDate")),
            ["PostingDate"] = SapPayloadBuilder.ODataDate(header.Get("postingDate"))
                ?? SapPayloadBuilder.ODataDate(header.Get("invoiceDate")),
            ["InvoicingParty"] = invoicingParty,
            // Each supplier's document carries ITS OWN invoice number — the one on its tax
            // invoice — not the bundle's. Falls back to the document's number, then the PO.
            ["SupplierInvoiceIDByInvcgParty"] = Cut(
                Or(vendor.Length > 0 ? InvoiceNoOf(taxRows) : "",
                   Or(header.GetStr("invoiceNo").Trim(),
                      Or(header.GetStr("goodsInvoiceNo").Trim(), poRef))), 16),
            ["DocumentCurrency"] = currency,
            // NO ExchangeRate here. A_SupplierInvoice has no such property: sending it — even as
            // null, which this dictionary serialises rather than drops — is answered with
            // "Property 'ExchangeRate' is invalid" and the whole document is refused.
            //
            // Nothing is lost by that. An invoice naming a purchase order takes the rate the PO
            // was priced at (PriceDetnExchangeRate, 33.50860 on PO 2230000031), which is the rate
            // the goods receipt accrued at and so the only one that clears the accrual to zero;
            // SAP applies it by itself. Without a PO it uses its own table for the posting date.
            // The Exchange Rate box on screen is read from the PO and shown so the figure is
            // visible before posting — it is not an input SAP accepts.
            ["InvoiceGrossAmount"] = F2(gross),
            ["AccountingDocumentType"] = Or(header.GetStr("sapDocType").Trim(), "RE"),
            // Thai tax reporting needs the business place on the document; SAP rejects the posting
            // without it on these company codes.
            ["BusinessPlace"] = businessPlace,
            ["BusinessSectionCode"] = branch,
            ["TaxIsCalculatedAutomatically"] = calcTax,
            // The vendor (payables) line SAP creates takes these from the header: Assignment is
            // what Finance reconciles on, Header Text is what they read in the line-item list.
            // BKTXT is 25 characters, ZUONR is 18.
            ["AssignmentReference"] = Cut(assignment, 18),
            ["DocumentHeaderText"] = Cut(headerText, 25),
            // The TEXT on the vendor (payables) line is its own field: DocumentHeaderText lands
            // on the document header (BKTXT) and leaves the line's own Text (BSEG-SGTXT) empty,
            // which is what Finance reads in the open-item list. 50 characters.
            ["SupplierPostingLineItemText"] = Cut(itemText, 50),
            ["TaxDeterminationDate"] = SapPayloadBuilder.ODataDate(header.Get("taxDate"))
                ?? SapPayloadBuilder.ODataDate(header.Get("invoiceDate")),
            ["TaxReportingDate"] = SapPayloadBuilder.ODataDate(header.Get("taxReportingDate"))
                ?? SapPayloadBuilder.ODataDate(header.Get("invoiceDate")),
            ["TaxFulfillmentDate"] = SapPayloadBuilder.ODataDate(header.Get("taxFulfillDate"))
                ?? SapPayloadBuilder.ODataDate(header.Get("invoiceDate")),
            ["_source"] = source ?? new Dictionary<string, object?>(),
        };
        // SAP normally takes the payment terms off the purchase order. When the PO does not carry
        // them it refuses the invoice with M8_2/169 "Terms of payment missing. Cannot determine
        // from purchase order", so the key from the vendor master (ocr.Vendor.PaymentTerms) is
        // sent when we have one. Only ever the SAP KEY: the words on the paper ("60 Days After
        // B/L Date") are not a key and SAP would reject them.
        var terms = Or(header.GetStr("paymentTermsCode").Trim(), paymentTerms.Trim());
        if (terms.Length is > 0 and <= 4) payload["PaymentTerms"] = terms;

        if (purOrdRef.Count > 0) payload["to_SuplrInvcItemPurOrdRef"] = purOrdRef;
        if (glAccountItems.Count > 0) payload["to_SupplierInvoiceItemGLAcct"] = glAccountItems;
        if (taxItems.Count > 0) payload["to_SupplierInvoiceTax"] = taxItems;

        // "A" = Parked: the invoice gets its SAP number and waits for AP, unposted and unsimulated.
        if (park) payload["SupplierInvoiceStatus"] = "A";

        var wht = Num(header.Get("whtAmount"));
        if (wht > 0) payload["_wht"] = new Dictionary<string, object?> { ["WithholdingTaxAmount"] = F2(wht) };
        return payload;
    }

    /// <summary>Largest difference between the stated invoice total and the sum of its lines
    /// that is treated as rounding (THB/USD satang across many lines). Above it the document
    /// is refused so a person can see which line is wrong.</summary>
    private const double RoundingTolerance = 1.00;

    private static double ItemAmount(object item) =>
        Num(((Dictionary<string, object?>)item).Get("SupplierInvoiceItemAmount"));

    private static bool IsCredit(object item) =>
        string.Equals(((Dictionary<string, object?>)item).GetStr("DebitCreditCode"), "H",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>What SAP's Balance compares the Gross against: PO-reference lines, G/L lines
    /// (debit +, credit −) and the tax rows.</summary>
    private static double SumOfParts(List<object> purOrdRef, List<object> glAccountItems, List<object> taxItems) =>
        Math.Round(
            purOrdRef.Sum(PoAmount)
            + glAccountItems.Sum(i => IsCredit(i) ? -ItemAmount(i) : ItemAmount(i))
            + taxItems.Sum(i => Num(((Dictionary<string, object?>)i).Get("TaxAmount"))),
            2);

    /// <summary>Makes the lines add up to the stated total so the parked draft opens with
    /// Balance 0. A difference within <see cref="RoundingTolerance"/> is added to the largest
    /// debit line; a bigger one is refused with the figures, before anything reaches SAP.</summary>
    private static double BalanceToStated(
        double stated, double sumOfParts, List<object> purOrdRef, List<object> glAccountItems, List<object> taxItems)
    {
        var diff = Math.Round(stated - sumOfParts, 2);
        if (diff == 0) return stated;

        if (Math.Abs(diff) > RoundingTolerance)
            throw new MgtOcr.Core.HttpApiException(400,
                $"ยอดรวมในเอกสาร {F2(stated)} ไม่เท่ากับผลรวมรายการ {F2(sumOfParts)} (ต่างกัน {F2(diff)}) — "
                + $"เกินเศษปัด {F2(RoundingTolerance)} จึงยังไม่ส่งเข้า SAP เพราะ Draft จะเปิดมาโดย Balance ไม่เป็น 0\n\n"
                + $"รายการตาม PO {F2(purOrdRef.Sum(PoAmount))} · "
                + $"G/L Debit {F2(glAccountItems.Where(i => !IsCredit(i)).Sum(ItemAmount))} · "
                + $"G/L Credit {F2(glAccountItems.Where(IsCredit).Sum(ItemAmount))} · "
                + $"VAT {F2(taxItems.Sum(i => Num(((Dictionary<string, object?>)i).Get("TaxAmount"))))}\n"
                + "ตรวจยอด Total, บรรทัด VAT และบรรทัดที่ลงฝั่ง Credit (เช่น WHT / ภาษีซื้อรอเรียกเก็บ) แล้ว Mapping ใหม่");

        var target = purOrdRef.Concat(glAccountItems.Where(i => !IsCredit(i)))
            .OrderByDescending(ItemAmount)
            .FirstOrDefault();
        if (target == null)
            throw new MgtOcr.Core.HttpApiException(400,
                $"ยอดรวมต่างจากผลรวมรายการ {F2(diff)} แต่ไม่มีบรรทัดฝั่ง Debit ให้ปัดเศษเข้า");

        var row = (Dictionary<string, object?>)target;
        row["SupplierInvoiceItemAmount"] = F2(ItemAmount(target) + diff);
        row["_roundingAdjusted"] = F2(diff);   // "_" keys are stripped before the POST
        return stated;
    }

    private static double PoAmount(object item) =>
        Num(((Dictionary<string, object?>)item).Get("SupplierInvoiceItemAmount"));

    /// <summary>This supplier's own invoice number, read off the tax rows the screen's Tax tab
    /// holds (Tax Invoice No.). Empty when the rows carry none.</summary>
    private static string InvoiceNoOf(IEnumerable<Dictionary<string, object?>> taxRows) =>
        taxRows.Select(t => t.GetStr("taxDocNo").Trim()).FirstOrDefault(v => v.Length > 0) ?? "";

    /// <summary>The suppliers this document has to be split into, in the order they appear.
    ///
    /// One shipping bundle is one piece of paper but several suppliers' invoices, and SAP takes
    /// one invoicing party per document. A document with a single supplier returns that one, so
    /// the caller can treat both cases the same way.</summary>
    public static List<string> VendorsOf(Dictionary<string, object?> doc)
    {
        var header = doc.Get("header") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var main = Or(doc.GetStr("partnerCode").Trim(), header.GetStr("vendorCode").Trim());
        var seen = new List<string>();
        void Add(string v)
        {
            var code = Or(v.Trim(), main);
            if (code.Length > 0 && !seen.Contains(code, StringComparer.OrdinalIgnoreCase)) seen.Add(code);
        }
        // Keyed against a purchase order there is only ever the one supplier.
        if (SapImportSupplierInvoiceExcel.IsPoKeyed(doc)) { Add(main); return seen; }
        foreach (var g in SapImportSupplierInvoiceExcel.GlRowsFor(doc)) Add(g.GetStr("vendorCode"));
        foreach (var t in DeferredVatReversal.TaxRows(doc)) Add(t.GetStr("vendorCode"));
        if (seen.Count == 0) Add(main);
        return seen;
    }

    /// <summary>Does every goods line already name the PO item it bills, assigned by hand on the
    /// PO Reference tab? When it does, the SAP lookup is only a cross-check: the numbers the
    /// invoice will carry are the ones the person read off the purchase order themselves, so the
    /// document can still be built while the gateway authorization for
    /// API_PURCHASEORDER_PROCESS_SRV is missing (403 "no start authorization").</summary>
    public static bool AllItemsAssignedByHand(Dictionary<string, object?> doc)
    {
        if (doc.Get("lines") is not System.Collections.IEnumerable lines) return false;
        var any = false;
        foreach (var raw in lines)
        {
            if (raw is not Dictionary<string, object?> l) continue;
            var ext = l.GetStr("extCode").Trim();
            if (ext is "VAT" or "WHT" or "DUTY") continue;
            if (Num(l.Get("amount")) == 0) continue;
            var assigned = l.Get("extra") is Dictionary<string, object?> ex
                ? ex.GetStr("sapPoItem").Trim() : "";
            if (assigned.Length == 0) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// The purchase order the invoice is posted against. MIRO's own "Purchasing Order" box
    /// (header.PurchasingOrder, on the PO Reference tab) is the SAP purchase order and wins;
    /// header.poRef is only the reference number the reader found on the shipping bundle, which
    /// is the same thing only when the supplier quoted our PO number back at us -- it was not on
    /// the KIMEX bundle (PO 2230000020 against reference 2230000581).
    /// </summary>
    public static string PoNumber(Dictionary<string, object?> header) =>
        Or(header.GetStr("PurchasingOrder").Trim(), header.GetStr("poRef").Trim());

    /// <summary>The invoice's goods lines matched to the purchase order's own items.
    ///
    /// Matched by the code the read found in the description (the lot/article code, e.g.
    /// CSN084379) against the PO item's material and short text, then by position for whatever is
    /// left. A line that matched nothing keeps an EMPTY item number rather than a guessed one —
    /// SAP rejects the document and says which line, which is the right outcome: a wrong item
    /// number posts the invoice against the wrong goods.</summary>
    /// <summary>The bundle's charges as PLANNED DELIVERY COSTS against the purchase order.
    ///
    /// SAP takes them in the same node as the goods, to_SuplrInvcItemPurOrdRef, but keyed by the
    /// PO's own condition rather than by a G/L account: SuplrInvcDeliveryCostCndnType names the
    /// condition (ZFRE freight, ZLH1 local handling, ZED1 excise duty, ZFE2 fee, ZINS insurance,
    /// ZIM1 import tax, ZOVS oversea charge, ZOTH other), and FreightSupplier the supplier that
    /// condition was entered for, which is how one purchase order's costs reach several invoices.
    ///
    /// Both have to be on the line before this can be sent, and neither can be guessed: the
    /// condition decides which accrual is cleared and the supplier decides who is billed. The
    /// purchase order carries them, but the read of it is refused in this tenant (403 on
    /// API_PURCHASEORDER_PROCESS_SRV, SAP_COM_0053), so they are what the person set on the PO
    /// Reference tab.</summary>
    private static List<object> DeliveryCostItems(
        Dictionary<string, object?> doc, string poRef, string headerCurrency, string headerTaxCode,
        string invoicingParty, string itemText,
        Func<Dictionary<string, object?>, bool> mine, int startNo,
        IReadOnlyList<SapPurchaseOrderItem>? poItems = null,
        IReadOnlyList<System.Text.Json.JsonElement>? goodsReceiptItems = null)
    {
        var result = new List<object>();
        if (doc.Get("lines") is not System.Collections.IEnumerable lines) return result;

        // Every delivery-cost condition the purchase order holds, item by item, as SAP returned it.
        // Empty when the PO could not be read; then only lines the person completed by hand go out.
        var poConds = (poItems ?? [])
            .SelectMany(i => i.Conditions.Select(c => (Item: i.Item, Cond: c)))
            .Where(x => x.Cond.ConditionType.Trim().Length > 0)
            .ToList();

        // A PO condition can be present on an item before any goods have arrived. SAP will reject
        // planned delivery costs on that item (M8_2155 for split-valuated stock), so determine
        // eligible items from the net received quantity, reversing 102/122/162 and credit-side
        // movements. Normalize the SAP item number because one API can return "00010" and another
        // "10" for the same item.
        static string ItemKey(string value) =>
            int.TryParse(value.Trim(), out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : value.Trim();
        static string? GrString(System.Text.Json.JsonElement row, string name) =>
            row.ValueKind == System.Text.Json.JsonValueKind.Object
            && row.TryGetProperty(name, out var value)
                ? value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString()
                : value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.ToString() : null
                : null;
        static double GrNumber(System.Text.Json.JsonElement row, string name) =>
            double.TryParse(GrString(row, name), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amount)
                ? amount : 0;
        static bool GrReversal(System.Text.Json.JsonElement row) =>
            string.Equals(GrString(row, "DebitCreditCode"), "H", StringComparison.OrdinalIgnoreCase)
            || GrString(row, "GoodsMovementType") is "102" or "122" or "162";
        var receivedItems = (goodsReceiptItems ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(GrString(row, "PurchaseOrderItem")))
            .GroupBy(row => ItemKey(GrString(row, "PurchaseOrderItem")!))
            .Where(group => group.Sum(row => (GrReversal(row) ? -1 : 1)
                * GrNumber(row, "QuantityInEntryUnit")) > 0.000001)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pass 1: each cost line with the condition it settles. The screen hides the PO item for a
        // "Planned delivery costs" document (category 2) — the charge belongs to a CONDITION, which
        // the goods receipt accrued on several PO items at once — so the item is usually empty here
        // and is worked out from the purchase order in pass 2.
        var missing = new List<string>();
        var charges = new List<(string Cond, string PoItem, string Step, string Count, string Vendor,
            string TaxCode, string Desc, double Amount)>();
        foreach (var raw in lines)
        {
            if (raw is not Dictionary<string, object?> l) continue;
            var ext = l.GetStr("extCode").Trim();
            if (ext is "VAT" or "WHT") continue;          // those travel on their own nodes
            var amount = Num(l.Get("amount"));
            if (amount == 0) continue;
            var extra = l.Get("extra") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            if (!mine(extra)) continue;

            var cndnType = Or(extra.GetStr("deliveryCondType").Trim(), extra.GetStr("condType").Trim());
            if (cndnType.Length == 0 && poConds.Count > 0)
            {
                // Same rule as the screen's "Assign all": the PO condition whose amount the line
                // bills exactly, and only when exactly one condition type answers.
                var hits = poConds
                    .Where(x => Math.Abs((double)x.Cond.Amount - amount) <= 0.01
                             || Math.Abs((double)x.Cond.RateValue - amount) <= 0.01)
                    .Select(x => x.Cond.ConditionType.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (hits.Count == 1) cndnType = hits[0];
            }
            if (cndnType.Length == 0)
            {
                missing.Add($"\"{Cut(l.GetStr("desc"), 40)}\" (ยังไม่ได้เลือก Condition)");
                continue;
            }
            charges.Add((cndnType.ToUpperInvariant(), extra.GetStr("sapPoItem").Trim(),
                extra.GetStr("deliveryCondStep").Trim(), extra.GetStr("deliveryCondCount").Trim(),
                extra.GetStr("vendorCode").Trim(), Or(extra.GetStr("taxCode").Trim(), headerTaxCode),
                l.GetStr("desc").Trim(), amount));
        }

        // Pass 2: one SAP line per (PO item, condition). Several charges on the form usually settle
        // the same condition (DO, storage, trucking, labour… are all ZLH1 Local Handling), and SAP
        // takes one line per PO item and condition — so they are summed first, then spread over the
        // PO items that accrued that condition, in proportion to what each item accrued. That is
        // what MIRO does when it proposes planned delivery costs; the satang left over by rounding
        // goes to the largest share so the lines still add up to the invoice exactly.
        var rows = new List<(string PoItem, string Cond, string Step, string Count, string Vendor,
            string TaxCode, string Text, double Amount)>();
        foreach (var g in charges.GroupBy(c => (c.Cond, c.PoItem, c.Vendor, c.TaxCode)))
        {
            var total = Math.Round(g.Sum(c => c.Amount), 2);
            var text = string.Join(", ", g.Select(c => c.Desc).Where(d => d.Length > 0).Distinct());
            var one = g.Count() == 1 ? g.First() : default;
            var step = g.Count() == 1 ? one.Step : "";
            var count = g.Count() == 1 ? one.Count : "";

            if (g.Key.PoItem.Length > 0)
            {
                if (!receivedItems.Contains(ItemKey(g.Key.PoItem)))
                {
                    missing.Add($"\"{Cut(text, 40)}\" (PO item {g.Key.PoItem} ยังไม่มี GR สุทธิ)");
                    continue;
                }
                rows.Add((g.Key.PoItem, g.Key.Cond, step, count, g.Key.Vendor, g.Key.TaxCode, text, total));
                continue;
            }

            var holders = poConds
                .Where(x => string.Equals(x.Cond.ConditionType.Trim(), g.Key.Cond, StringComparison.OrdinalIgnoreCase)
                    && x.Cond.Amount != 0
                    && receivedItems.Contains(ItemKey(x.Item)))
                .GroupBy(x => x.Item)
                .Select(h => (Item: h.Key, Weight: (double)h.Sum(x => Math.Abs(x.Cond.Amount))))
                .Where(h => h.Weight > 0)
                .OrderBy(h => h.Item.PadLeft(10, '0'))
                .ToList();
            if (holders.Count == 0)
            {
                missing.Add($"\"{Cut(text, 40)}\" (" + (poConds.Count == 0
                    ? "อ่าน PO จาก SAP ไม่ได้ จึงหารายการ PO ให้ไม่ได้"
                    : $"PO {poRef} ไม่มี Condition {g.Key.Cond} ที่ตั้งค้างไว้") + ")");
                continue;
            }

            var weightSum = holders.Sum(h => h.Weight);
            var shares = holders
                .Select(h => (h.Item, h.Weight, Amount: Math.Round(total * h.Weight / weightSum, 2)))
                .ToList();
            var rest = Math.Round(total - shares.Sum(x => x.Amount), 2);
            if (rest != 0)
            {
                var big = shares.IndexOf(shares.OrderByDescending(x => x.Weight).First());
                shares[big] = (shares[big].Item, shares[big].Weight, Math.Round(shares[big].Amount + rest, 2));
            }
            foreach (var sh in shares.Where(x => x.Amount != 0))
                rows.Add((sh.Item, g.Key.Cond, "", "", g.Key.Vendor, g.Key.TaxCode, text, sh.Amount));
        }

        if (missing.Count > 0)
            throw new MgtOcr.Core.HttpApiException(400,
                $"ค่าใช้จ่ายที่จะตัดกับ PO {poRef} ยังไม่ครบ — แต่ละบรรทัดต้องเลือก **Condition** "
                + "(ZFRE / ZLH1 / ZED1 / ZFE2 / ZINS / ZIM1 / ZOVS / ZOTH) ให้ตรงกับที่ตั้งไว้ใน PO "
                + "ระบบจะกระจายยอดไปตามรายการใน PO ที่มี Condition นั้นให้เอง\n\n"
                + "บรรทัดที่ยังขาด: " + string.Join(" · ", missing));

        // Same PO item + condition from two groups (e.g. one with a tax code typed by hand) would be
        // refused by SAP as a duplicate, so they are merged here.
        var no = startNo;
        foreach (var r in rows.GroupBy(r => (r.PoItem, r.Cond, r.Vendor, r.TaxCode)))
        {
            var first = r.First();
            var single = r.Count() == 1;
            var amount = Math.Round(r.Sum(x => x.Amount), 2);
            var text = string.Join(", ", r.Select(x => x.Text).Where(t => t.Length > 0).Distinct());
            result.Add(new Dictionary<string, object?>
            {
                ["SupplierInvoiceItem"] = (++no).ToString(),
                ["PurchaseOrder"] = poRef,
                ["PurchaseOrderItem"] = r.Key.PoItem,
                ["DocumentCurrency"] = headerCurrency,
                ["SupplierInvoiceItemAmount"] = F2(amount),
                ["SuplrInvcDeliveryCostCndnType"] = r.Key.Cond,
                // A condition entered more than once on the same PO item is told apart by its
                // step and counter. Sent only when the person typed them for a single line — on a
                // PO with one condition of each type per item SAP finds it from the type alone.
                ["SuplrInvcDeliveryCostCndnStep"] = single && first.Step.Length > 0 ? first.Step : null,
                ["SuplrInvcDeliveryCostCndnCount"] = single && first.Count.Length > 0 ? first.Count : null,
                // The supplier the condition names on the purchase order. Omitted when it is the
                // one being invoiced, which is the ordinary case — SAP then uses the document's
                // own invoicing party.
                ["FreightSupplier"] = r.Key.Vendor.Length > 0
                    && !string.Equals(r.Key.Vendor, invoicingParty.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? r.Key.Vendor : null,
                ["TaxCode"] = r.Key.TaxCode,
                ["SupplierInvoiceItemText"] = Cut(Or(text, itemText), 50),
                ["_deliveryCost"] = true,
            });
        }

        return result;
    }

    private static List<object> PurchaseOrderItems(
        Dictionary<string, object?> doc, string poRef, IReadOnlyList<SapPurchaseOrderItem> poItems,
        string headerCurrency, string headerTaxCode, string matDoc, string matDocYear,
        string assignment, string itemText)
    {
        var result = new List<object>();
        if (doc.Get("lines") is not System.Collections.IEnumerable lines) return result;

        var unused = poItems.ToList();
        var no = 0;
        foreach (var raw in lines)
        {
            if (raw is not Dictionary<string, object?> l) continue;
            var ext = l.GetStr("extCode").Trim();
            if (ext is "VAT" or "WHT" or "DUTY") continue;
            var amount = Num(l.Get("amount"));
            if (amount == 0) continue;

            // What the person assigned on the PO Reference tab wins: they saw the purchase order
            // next to the invoice and either accepted the automatic match or corrected it.
            var assigned = l.Get("extra") is Dictionary<string, object?> ex
                ? ex.GetStr("sapPoItem").Trim() : "";
            var match = assigned.Length > 0
                ? unused.FirstOrDefault(i => i.Item == assigned)
                : Match(unused, ext, l.GetStr("desc"));
            if (match != null) unused.Remove(match);
            var itemNo = assigned.Length > 0 ? assigned : match?.Item ?? "";

            // A PO-reference row with no item number is not a row SAP can take: it answers
            // M8/375 "Fill in mandatory field 'ReferenceDocument, -FiscalYear, -Item'" for the
            // FIRST row, naming a material-document reference rather than the empty PO item, so
            // the message points nowhere near the line that is actually wrong. Named here
            // instead, with the description the person can see on screen.
            if (itemNo.Length == 0)
            {
                throw new MgtOcr.Core.HttpApiException(400,
                    $"บรรทัด \"{Cut(l.GetStr("desc"), 40)}\" ยังไม่ได้ผูกกับรายการใน PO {poRef} — "
                    + "ผูกให้ครบทุกบรรทัดในแท็บ PO Reference ก่อนส่ง (หรือลบบรรทัดที่ไม่มีใน PO ออก)");
            }

            result.Add(new Dictionary<string, object?>
            {
                ["SupplierInvoiceItem"] = (++no).ToString(),
                ["PurchaseOrder"] = poRef,
                ["PurchaseOrderItem"] = itemNo,
                // SAP rejects the whole document with "Document currency is missing in item
                // 000001 (parameter to_SuplrInvcItemPurOrdRef)" when this is blank, and it IS
                // blank whenever the PO lookup did not run (the item numbers were typed by hand,
                // or the gateway refused the read). The invoice's own currency is the same one
                // the PO carries, so it stands in.
                ["DocumentCurrency"] = Or(match?.Currency ?? "", headerCurrency),
                ["SupplierInvoiceItemAmount"] = F2(amount),
                ["QuantityInPurchaseOrderUnit"] = F3(Num(l.Get("qty"))),
                ["PurchaseOrderQuantityUnit"] = Or(match?.Unit ?? "", l.GetStr("uom")),
                // Same story for the tax code: the PO's when it was read, otherwise what the
                // person set on the line, otherwise the document's.
                ["TaxCode"] = Or(
                    match?.TaxCode ?? "",
                    Or(l.Get("extra") is Dictionary<string, object?> tx ? tx.GetStr("taxCode").Trim() : "",
                       headerTaxCode)),
                // GR-based invoice verification: the material document, its fiscal year and
                // the GR ITEM this invoice line settles. Sent only when the document carries
                // them — a PO item without GR-based IV takes the invoice with the PO reference
                // alone, and sending an empty ReferenceDocument would fail that case instead.
                ["ReferenceDocument"] = matDoc.Length > 0 ? matDoc : null,
                ["ReferenceDocumentFiscalYear"] = matDoc.Length > 0 ? matDocYear : null,
                ["ReferenceDocumentItem"] = matDoc.Length > 0 ? GrItem(l, no) : null,
                // Item Text only: A_SuplrInvcItemPurOrdRefType has SupplierInvoiceItemText but
                // NO AssignmentReference (unlike the G/L item), so the assignment travels on the
                // header instead and SAP copies it onto the vendor line.
                ["SupplierInvoiceItemText"] = Cut(itemText, 50),
                ["_material"] = match?.Material ?? "",
                ["_matchedBy"] = assigned.Length > 0 ? "assigned on screen"
                    : match == null ? "not matched" : "material/lot code",
            });
        }
        return result;
    }

    /// <summary>The goods-receipt item this invoice line settles. What the person read off the
    /// material document and typed on the PO Reference tab (extra.sapGrItem) wins.
    ///
    /// The fallback is the line's POSITION, not its PO item: a material document numbers its own
    /// items 1, 2, 3 … in the order the goods were received, independently of the PO's 10, 40,
    /// 50, 60. Sending the PO item here is what produced M8/429 "Goods receipt item 5000002095
    /// 2026 0010 is invalid" — the GR simply has no item 10. The position is a guess that is
    /// right only when the whole PO was received at once, in PO order, so a document whose GR
    /// was split still needs the numbers typed in.</summary>
    private static string GrItem(Dictionary<string, object?> line, int position)
    {
        var typed = line.Get("extra") is Dictionary<string, object?> ex
            ? ex.GetStr("sapGrItem").Trim() : "";
        return typed.Length > 0 ? typed : position.ToString();
    }

    /// <summary>The fiscal year the goods receipt was posted in. SAP wants it beside the material
    /// document number; the posting date's year is it for a calendar fiscal year (both company
    /// codes here use one).</summary>
    /// <summary>"&lt;SUPPLIER&gt;,INV.&lt;reference&gt;" — the shape Accounting writes on every
    /// line ("KIMEX,INV.2230000031"). The reference is the one the document is posted under, so
    /// the supplier's own invoice number comes first and the shipping form's INVOICE NO. and the
    /// purchase order stand in behind it; the Text and the Reference then always name the same
    /// document, which they did not when the purchase order was corrected after the read.</summary>
    private static string ReferenceText(Dictionary<string, object?> header)
    {
        // The form's SUPPLIER box as printed; the document's own supplier name carries a legal
        // suffix ("Kimex Co.,Ltd."), so that one is cut at its first space or comma.
        var fromForm = header.GetStr("goodsSupplier").Trim();
        var vendor = fromForm;
        if (vendor.Length == 0)
        {
            var name = header.GetStr("vendorName").Trim();
            var cut = name.IndexOfAny([' ', ',']);
            vendor = cut > 0 ? name[..cut] : name;
        }
        vendor = vendor.ToUpperInvariant();
        var no = Or(header.GetStr("invoiceNo").Trim(),
            Or(header.GetStr("goodsInvoiceNo").Trim(), PoNumber(header)));
        if (vendor.Length == 0) return no.Length > 0 ? $"INV.{no}" : "";
        return no.Length > 0 ? $"{vendor},INV.{no}" : vendor;
    }

    private static string FiscalYearOf(Dictionary<string, object?> header)
    {
        var raw = Or(header.GetStr("postingDate").Trim(), header.GetStr("invoiceDate").Trim());
        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
            ? d.Year.ToString()
            : DateTime.Today.Year.ToString();
    }

    private static SapPurchaseOrderItem? Match(List<SapPurchaseOrderItem> items, string extCode, string desc)
    {
        var code = extCode.Trim();
        if (code.Length >= 4)
        {
            var hit = items.FirstOrDefault(i =>
                i.Material.Contains(code, StringComparison.OrdinalIgnoreCase)
                || i.Text.Contains(code, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        // The PO's short text is the product name the description also carries ("MY ROAD V8 …").
        var words = (desc ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
        {
            var needle = string.Join(' ', words.Take(2));
            var hit = items.FirstOrDefault(i => i.Text.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    private static string F2(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    private static string F3(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    private static string Or(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;
    private static string Cut(string? v, int n)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(v ?? "", @"\s+", " ").Trim();
        return s.Length <= n ? s : s[..n];
    }
    private static double Num(object? v) =>
        v is null ? 0 : double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
}
