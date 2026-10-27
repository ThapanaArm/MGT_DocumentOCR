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
        string vendorFilter = "")
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
                invoicingParty, itemText, Mine, purOrdRef.Count)
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

        var gross = purOrdRef.Sum(PoAmount)
            + glAccountItems.Sum(i => Num(((Dictionary<string, object?>)i).Get("SupplierInvoiceItemAmount")))
            + taxItems.Sum(i => Num(((Dictionary<string, object?>)i).Get("TaxAmount")));
        // A document whose own total was read and reviewed wins over the sum of its parts: the
        // difference is the rounding SAP books as Purchase Variance, and it can only do that if we
        // state the real invoice total.
        // Only when the whole document is being posted: header.totalAmount is the bundle's total,
        // so using it for one supplier's share would bill that supplier for everybody's costs.
        var stated = vendor.Length == 0 ? Num(header.Get("totalAmount")) : 0;
        if (stated > 0) gross = stated;

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
            // Left empty SAP takes the rate from its own table for the posting date. Filled in,
            // the posting uses exactly this rate — the same thing the purchase order's
            // "Fixed Exch.Rate" does — so an invoice in the supplier's currency clears the
            // accrual the goods receipt made rather than booking an exchange difference against
            // it. Sent only when there is one, and never on a document already in company code
            // currency, where SAP rejects a rate it has no use for.
            ["ExchangeRate"] = !string.Equals(currency, "THB", StringComparison.OrdinalIgnoreCase)
                && Num(header.Get("exchangeRate")) is var fx && fx > 0
                ? F2(fx)
                : null,
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

        var wht = Num(header.Get("whtAmount"));
        if (wht > 0) payload["_wht"] = new Dictionary<string, object?> { ["WithholdingTaxAmount"] = F2(wht) };
        return payload;
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
        Func<Dictionary<string, object?>, bool> mine, int startNo)
    {
        var result = new List<object>();
        if (doc.Get("lines") is not System.Collections.IEnumerable lines) return result;

        var missing = new List<string>();
        var no = startNo;
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
            var poItem = extra.GetStr("sapPoItem").Trim();
            if (cndnType.Length == 0 || poItem.Length == 0)
            {
                missing.Add($"\"{Cut(l.GetStr("desc"), 40)}\" ("
                    + (poItem.Length == 0 ? "ยังไม่ได้ผูกรายการ PO" : $"PO item {poItem}")
                    + (cndnType.Length == 0 ? ", ยังไม่ได้เลือก Condition" : "") + ")");
                continue;
            }

            var row = new Dictionary<string, object?>
            {
                ["SupplierInvoiceItem"] = (++no).ToString(),
                ["PurchaseOrder"] = poRef,
                ["PurchaseOrderItem"] = poItem,
                ["DocumentCurrency"] = headerCurrency,
                ["SupplierInvoiceItemAmount"] = F2(amount),
                ["SuplrInvcDeliveryCostCndnType"] = cndnType,
                // A condition entered more than once on the same PO item is told apart by its
                // step and counter. Sent only when the person has them — on a PO with one
                // condition of each type per item SAP finds it from the type alone.
                ["SuplrInvcDeliveryCostCndnStep"] = extra.GetStr("deliveryCondStep").Trim() is { Length: > 0 } st ? st : null,
                ["SuplrInvcDeliveryCostCndnCount"] = extra.GetStr("deliveryCondCount").Trim() is { Length: > 0 } cnt ? cnt : null,
                // The supplier the condition names on the purchase order. Omitted when it is the
                // one being invoiced, which is the ordinary case — SAP then uses the document's
                // own invoicing party.
                ["FreightSupplier"] = extra.GetStr("vendorCode").Trim() is { Length: > 0 } fs
                    && !string.Equals(fs, invoicingParty.Trim(), StringComparison.OrdinalIgnoreCase) ? fs : null,
                ["TaxCode"] = Or(extra.GetStr("taxCode").Trim(), headerTaxCode),
                ["SupplierInvoiceItemText"] = Cut(Or(l.GetStr("desc").Trim(), itemText), 50),
                ["_deliveryCost"] = true,
            };
            result.Add(row);
        }

        if (missing.Count > 0)
            throw new MgtOcr.Core.HttpApiException(400,
                $"ค่าใช้จ่ายที่จะตัดกับ PO {poRef} ยังไม่ครบ — แต่ละบรรทัดต้องมีทั้ง **รายการใน PO** และ "
                + "**Condition** (ZFRE / ZLH1 / ZED1 / ZFE2 / ZINS / ZIM1 / ZOVS / ZOTH) ที่ตั้งไว้ใน PO "
                + "เพราะ SAP ใช้ condition เป็นตัวบอกว่าจะไปล้าง accrual ตัวไหน เดาแทนไม่ได้\n\n"
                + "บรรทัดที่ยังขาด: " + string.Join(" · ", missing));

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
