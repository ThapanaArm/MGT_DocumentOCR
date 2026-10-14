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
///   • delivery costs, duty and fees        -> to_SuplrInvcItemGLAcct, one row per COSTS heading
///     exactly as the file produces them.
///   • VAT                                  -> to_SuplrInvcTax, one row per tax invoice with its
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
        string paymentTerms = "")
    {
        var header = doc.Get("header") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var poRef = PoNumber(header);
        var currency = Or(header.GetStr("currency").Trim(), "THB");
        var businessPlace = Or(header.GetStr("businessPlace").Trim(), HeadOffice);
        var branch = Or(header.GetStr("branch").Trim(), HeadOffice);

        // The same rows the file is built from.
        var glRows = SapImportSupplierInvoiceExcel.GlRowsFor(doc);
        var taxRows = DeferredVatReversal.TaxRows(doc);
        var isPoKeyed = SapImportSupplierInvoiceExcel.IsPoKeyed(doc);

        // Goods-receipt reference. A PO item flagged for GR-based invoice verification (the
        // default on a stock PO) can only be invoiced against the material document the goods
        // receipt created — SAP asks for it as M8/375 "Fill in mandatory field 'ReferenceDocument,
        // -FiscalYear, -Item'". The number is on the bundle (A-MatDoc_5000002095_...) and the
        // screen keeps it in header.refMatDoc; the year defaults to the posting date's.
        var matDoc = header.GetStr("refMatDoc").Trim();
        var matDocYear = Or(header.GetStr("refMatDocYear").Trim(), FiscalYearOf(header));
        var purOrdRef = isPoKeyed
            ? PurchaseOrderItems(doc, poRef, poItems, currency, header.GetStr("taxCode").Trim(),
                matDoc, matDocYear)
            : [];

        // Costs only: the VAT rows go to to_SuplrInvcTax below (sending them as G/L lines as well
        // would book the tax twice), and the withholding row is not a G/L line at all.
        var glAccountItems = new List<object>();
        var itemNo = purOrdRef.Count;
        foreach (var g in glRows)
        {
            if (isPoKeyed) break;                                   // the goods come from the PO
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

        // One row per tax invoice, carrying its own code and base — what the Tax tab holds after
        // somebody reviewed it, rather than a rate applied to the whole document.
        var taxItems = new List<object>();
        foreach (var t in taxRows)
        {
            var amount = Num(t.Get("docCurrencyAmt"));
            if (amount == 0) continue;
            var code = t.GetStr("taxCode").Trim();
            if (code.Length == 0) continue;
            taxItems.Add(new Dictionary<string, object?>
            {
                ["TaxCode"] = code,
                ["TaxBaseAmount"] = F2(Num(t.Get("baseAmount"))),
                ["TaxAmount"] = F2(amount),
            });
        }

        var gross = purOrdRef.Sum(PoAmount)
            + glAccountItems.Sum(i => Num(((Dictionary<string, object?>)i).Get("SupplierInvoiceItemAmount")))
            + taxItems.Sum(i => Num(((Dictionary<string, object?>)i).Get("TaxAmount")));
        // A document whose own total was read and reviewed wins over the sum of its parts: the
        // difference is the rounding SAP books as Purchase Variance, and it can only do that if we
        // state the real invoice total.
        var stated = Num(header.Get("totalAmount"));
        if (stated > 0) gross = stated;

        var payload = new Dictionary<string, object?>
        {
            ["_target"] = Endpoint,
            ["CompanyCode"] = companyCode,
            ["DocumentDate"] = SapPayloadBuilder.ODataDate(header.Get("invoiceDate")),
            ["PostingDate"] = SapPayloadBuilder.ODataDate(header.Get("postingDate"))
                ?? SapPayloadBuilder.ODataDate(header.Get("invoiceDate")),
            ["InvoicingParty"] = invoicingParty,
            ["SupplierInvoiceIDByInvcgParty"] = Cut(Or(header.GetStr("invoiceNo").Trim(), poRef), 16),
            ["DocumentCurrency"] = currency,
            ["InvoiceGrossAmount"] = F2(gross),
            ["AccountingDocumentType"] = Or(header.GetStr("sapDocType").Trim(), "RE"),
            // Thai tax reporting needs the business place on the document; SAP rejects the posting
            // without it on these company codes.
            ["BusinessPlace"] = businessPlace,
            ["BusinessSectionCode"] = branch,
            ["TaxIsCalculatedAutomatically"] = false,
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
        if (glAccountItems.Count > 0) payload["to_SuplrInvcItemGLAcct"] = glAccountItems;
        if (taxItems.Count > 0) payload["to_SuplrInvcTax"] = taxItems;

        var wht = Num(header.Get("whtAmount"));
        if (wht > 0) payload["_wht"] = new Dictionary<string, object?> { ["WithholdingTaxAmount"] = F2(wht) };
        return payload;
    }

    private static double PoAmount(object item) =>
        Num(((Dictionary<string, object?>)item).Get("SupplierInvoiceItemAmount"));

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
    private static List<object> PurchaseOrderItems(
        Dictionary<string, object?> doc, string poRef, IReadOnlyList<SapPurchaseOrderItem> poItems,
        string headerCurrency, string headerTaxCode, string matDoc, string matDocYear)
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
