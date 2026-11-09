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
        IReadOnlyList<System.Text.Json.JsonElement>? goodsReceiptItems = null,
        string taxBaseClearingAccount = "",
        Func<string, string>? sameParty = null)
    {
        // Two codes for the same SAP supplier — the test tenant's 1000134 and production's 4000357
        // are both PROMPT — are one supplier here. Without this the rows tagged 4000357 (the
        // Cargoport and Port Authority VAT) became a document of their own with no lines at all.
        string Party(string code) => sameParty == null ? code.Trim() : sameParty(code.Trim());
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
            return string.Equals(Party(v), Party(vendor), StringComparison.OrdinalIgnoreCase);
        }
        var glRows = SapImportSupplierInvoiceExcel.GlRowsFor(doc).Where(Mine).ToList();
        var taxRows = DeferredVatReversal.EffectiveTaxRows(doc).Where(Mine).ToList();
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
        // Shipping documents: Assignment is the PURCHASE ORDER on every line Accounting keys —
        // payables, cost, deferred VAT and Purchase Variance (PRD 5100001132: 2230000565 on all of
        // them; 5100001131 PROMPT the same). Only the customs import VAT line carries the receipt
        // number instead (0152-091171), set where that line is built.
        if (!isPoKeyed && poRef.Length > 0) assignment = poRef;

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
                invoicingParty, itemText, Mine, purOrdRef.Count, poItems, goodsReceiptItems, taxRows)
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

        // A tax row whose base no line on this invoice stands for. The Customs Department's receipt
        // is the case: it bills a 200.00 fee, but the import VAT on it (V1 26,228.00) is 7 % of the
        // DECLARED VALUE of the goods (374,685.71), which is not a line of this invoice at all. SAP
        // refuses a tax code that no item carries (FF812 "Tax code V1 also exists"). So the missing
        // base is put on the document as two G/L lines on a clearing account — debit WITH the tax
        // code, credit back WITHOUT it (VX). They cancel each other out, the Gross is unchanged, and
        // the input VAT is booked by SAP on its real base with the tax invoice's own details.
        // A shipping bundle posted the way Accounting does (below: lines on VX, every VAT as a G/L
        // line on its tax account with its own base) has no "tax row without a line" to solve — the
        // VAT no longer sits on the Tax tab at all. Solving it here anyway only refused documents
        // the later step books fine (FF812 "D1 17,633.00 … ไม่มีรายการในใบนี้ให้ผูก Tax Code" on a
        // PILOT bundle whose lines are all a different code).
        var vatAsGlLines = !isPoKeyed && !calcTax
            && purOrdRef.Any(i => ((Dictionary<string, object?>)i).Get("_deliveryCost") is true);
        if (!isPoKeyed && !vatAsGlLines)
        {
            double BaseCarried(string code) =>
                purOrdRef.Where(i => string.Equals(((Dictionary<string, object?>)i).GetStr("TaxCode").Trim(), code, StringComparison.OrdinalIgnoreCase))
                    .Sum(PoAmount)
                + glAccountItems.Where(i => string.Equals(((Dictionary<string, object?>)i).GetStr("TaxCode").Trim(), code, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => IsCredit(i) ? -ItemAmount(i) : ItemAmount(i));
            var orphanBases = taxRows
                .Where(t => t.GetStr("taxCode").Trim() is { Length: > 0 } c && !c.Equals("VX", StringComparison.OrdinalIgnoreCase)
                            && Num(t.Get("docCurrencyAmt")) != 0)
                .GroupBy(t => t.GetStr("taxCode").Trim().ToUpperInvariant())
                .Select(g => (Code: g.Key, Base: Math.Round(g.Sum(t => Num(t.Get("baseAmount"))), 2),
                              Docs: string.Join(", ", g.Select(t => t.GetStr("taxDocNo").Trim()).Where(x => x.Length > 0))))
                .ToList();
            // Codes the delivery-cost lines will pick up from the Tax tab (AutoTaxCodes) are
            // already on purOrdRef here, so only what is still uncovered remains.
            foreach (var (code, baseAmt, docs) in orphanBases)
            {
                var missing = Math.Round(baseAmt - BaseCarried(code), 2);
                if (missing <= 1.00) continue;
                if (string.IsNullOrWhiteSpace(taxBaseClearingAccount))
                {
                    // Accounting's own way (posted document 5100001372, THE CUSTOMS DEPARTMENT): the
                    // charge lines carry the VAT code — Fee 199.98 and Import Tax 2,514.14 both on V1 —
                    // and the input tax is the amount on the customs receipt, entered as it stands
                    // (1,935.00, not 7 % of the lines). So the lines still on VX take this code and the
                    // tax row goes as read. SAP books it only with the tax entered, not calculated.
                    if (calcTax)
                        throw new MgtOcr.Core.HttpApiException(400,
                            $"ภาษี {code} {F2(baseAmt)} ({docs}) คิดจากมูลค่าที่ไม่ได้อยู่ในใบนี้ (เช่น VAT นำเข้าของกรมศุลฯ) — "
                            + "ต้องเอาติ๊ก Calculate Tax ออก ให้ส่งยอดภาษีตามใบเสร็จ");
                    // SAP only takes a tax amount up to the base the lines carry ("The tax amount must
                    // not be greater than the tax base"). 5100001372 passed because its Import Tax line
                    // (2,514.14) was larger than the VAT (1,935.00); a receipt that bills only a 200.00
                    // fee next to 26,228.00 of import VAT cannot be posted this way.
                    var free = purOrdRef.Concat(glAccountItems).Cast<Dictionary<string, object?>>()
                        .Where(r => r.GetStr("TaxCode").Trim() is var tc && (tc.Length == 0 || tc.Equals("VX", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    var taxOfCode = taxRows.Where(t => t.GetStr("taxCode").Trim().Equals(code, StringComparison.OrdinalIgnoreCase))
                        .Sum(t => Num(t.Get("docCurrencyAmt")));
                    var lineBase = free.Sum(r => Num(r.Get("SupplierInvoiceItemAmount"))) + BaseCarried(code);
                    if (free.Count > 0 && taxOfCode > lineBase + 0.009)
                    {
                        // Accounting's way for a receipt whose VAT is larger than its lines — posted
                        // document 5105604031 (THE CUSTOMS DEPARTMENT, 0152-091171, PO 2230000565:
                        // FTA goods, no import duty, Fee 200.00 next to import VAT 26,228.00):
                        //   PO lines   Fee ZFE2 200.00 on VX
                        //   G/L item   Dr 11720000 Input tax 26,228.00, tax code V1,
                        //              Assignment 0152-091171, Text THE CUSTOMS DEPARTMENT
                        //   Tax tab    VX, base 200.00, tax 0.00 (entered manually)
                        // The VAT goes in as a G/L line on the input-tax account instead of a tax
                        // row, so SAP's "tax must not exceed the tax base" never applies.
                        foreach (var t in taxRows.Where(t => t.GetStr("taxCode").Trim().Equals(code, StringComparison.OrdinalIgnoreCase)))
                        {
                            var amt = Num(t.Get("docCurrencyAmt"));
                            if (amt == 0) continue;
                            var deferred = string.Equals(t.GetStr("taxKind").Trim(), "DEFERRED", StringComparison.OrdinalIgnoreCase)
                                || code.StartsWith('D');
                            glAccountItems.Add(new Dictionary<string, object?>
                            {
                                ["SupplierInvoiceItem"] = (++itemNo).ToString(),
                                ["CompanyCode"] = companyCode,
                                ["GLAccount"] = deferred ? DeferredVatReversal.GlDeferredInputTax : DeferredVatReversal.GlInputTax,
                                ["DocumentCurrency"] = currency,
                                ["SupplierInvoiceItemAmount"] = F2(amt),
                                ["DebitCreditCode"] = "S",
                                ["TaxCode"] = code,
                                ["AssignmentReference"] = Cut(Or(t.GetStr("taxDocNo").Trim(), assignment), 18),
                                ["SupplierInvoiceItemText"] = Cut(Or(t.GetStr("issuerName").Trim().ToUpperInvariant(), itemText), 50),
                                // A line on a tax account must carry the base it was taxed on, or
                                // SAP refuses to post it: F5A375 "Enter the tax base amounts for
                                // account 0011720000" — here the declared value on the receipt.
                                ["TaxBaseAmountInTransCrcy"] = F2(Num(t.Get("baseAmount")) > 0
                                    ? Num(t.Get("baseAmount")) : Math.Round(amt / 0.07, 2)),
                                ["_vatAsGlLine"] = code,
                            });
                        }
                        taxItems.RemoveAll(x => ((Dictionary<string, object?>)x).GetStr("TaxCode").Trim()
                            .Equals(code, StringComparison.OrdinalIgnoreCase));
                        var vxBase = Math.Round(free.Where(r => purOrdRef.Contains(r)).Sum(r => Num(r.Get("SupplierInvoiceItemAmount"))), 2);
                        if (vxBase > 0 && !taxItems.Any(x => ((Dictionary<string, object?>)x).GetStr("TaxCode").Trim()
                                .Equals("VX", StringComparison.OrdinalIgnoreCase)))
                            taxItems.Add(new Dictionary<string, object?>
                            {
                                ["TaxCode"] = "VX",
                                ["DocumentCurrency"] = currency,
                                ["TaxBaseAmountInTransCrcy"] = F2(vxBase),
                                ["TaxAmount"] = F2(0),
                            });
                        continue;
                    }
                    var marked = 0;
                    foreach (var row in free) { row["TaxCode"] = code; marked++; }
                    if (marked == 0)
                        throw new MgtOcr.Core.HttpApiException(400,
                            $"ภาษี {code} {F2(baseAmt)} ({docs}) ไม่มีรายการในใบนี้ให้ผูก Tax Code — SAP จะไม่รับ (FF812)");
                    continue;
                }
                var text = Cut($"Tax base {code} {docs}".Trim(), 50);
                glAccountItems.Add(new Dictionary<string, object?>
                {
                    ["SupplierInvoiceItem"] = (++itemNo).ToString(),
                    ["CompanyCode"] = companyCode,
                    ["GLAccount"] = taxBaseClearingAccount.Trim(),
                    ["DocumentCurrency"] = currency,
                    ["SupplierInvoiceItemAmount"] = F2(missing),
                    ["DebitCreditCode"] = "S",
                    ["TaxCode"] = code,
                    ["AssignmentReference"] = Cut(assignment, 18),
                    ["SupplierInvoiceItemText"] = text,
                    ["_taxBase"] = code,
                });
                glAccountItems.Add(new Dictionary<string, object?>
                {
                    ["SupplierInvoiceItem"] = (++itemNo).ToString(),
                    ["CompanyCode"] = companyCode,
                    ["GLAccount"] = taxBaseClearingAccount.Trim(),
                    ["DocumentCurrency"] = currency,
                    ["SupplierInvoiceItemAmount"] = F2(missing),
                    ["DebitCreditCode"] = "H",
                    ["TaxCode"] = "VX",
                    ["AssignmentReference"] = Cut(assignment, 18),
                    ["SupplierInvoiceItemText"] = text,
                    ["_taxBase"] = code,
                });
            }
        }

        // ONE document per supplier, the way Accounting posts a shipping bundle (PRD, PO 2230000565:
        // Local 5105604030 and Fee 5105604031, one number each; 5100001131 PROMPT 18,325.99): every
        // delivery-cost line on VX, and each tax invoice's VAT as a G/L line on the tax account —
        // D1 → 11730000 Deferred input tax, V1 → 11720000 Input tax — with its own base. Before, a
        // condition billed under two tax codes (PROMPT's D1 service next to VX advances) had to go
        // out as two documents (M8/321 / FF812), which is not how Accounting books it.
        double accrualDelta = 0;
        if (vatAsGlLines)
        {
            // The invoicing party's own tax invoice keeps a line of its own; the receipts it paid on
            // our behalf (Cargoport, the Port Authority …) go on ONE line per tax code, the way
            // Accounting posts them (5100001131: PROMPT 105.00 D1, then 782.99 D1 = Cargoport 744.35
            // + Port Authority 38.64).
            var ownInvoice = Or(vendor.Length > 0 ? InvoiceNoOf(taxRows) : "", header.GetStr("invoiceNo").Trim());
            var vatLines = new List<(string Code, bool Own, bool Deferred, double Amount, double Base, string DocNo, string Issuer, bool Customs)>();
            foreach (var t in taxRows)
            {
                var code = t.GetStr("taxCode").Trim().ToUpperInvariant();
                var amt = Num(t.Get("docCurrencyAmt"));
                if (code.Length == 0 || code == "VX" || amt == 0) continue;
                // Already booked as a G/L line above (the customs import VAT).
                if (glAccountItems.Any(g => ((Dictionary<string, object?>)g).GetStr("_vatAsGlLine") == code
                        && Math.Abs(Num(((Dictionary<string, object?>)g).Get("SupplierInvoiceItemAmount")) - amt) < 0.005
                        && ((Dictionary<string, object?>)g).GetStr("AssignmentReference") == Cut(Or(t.GetStr("taxDocNo").Trim(), assignment), 18)))
                    continue;
                var deferred = string.Equals(t.GetStr("taxKind").Trim(), "DEFERRED", StringComparison.OrdinalIgnoreCase)
                    || code.StartsWith('D');
                var docNo = t.GetStr("taxDocNo").Trim();
                vatLines.Add((code, ownInvoice.Length > 0 && string.Equals(docNo, ownInvoice, StringComparison.OrdinalIgnoreCase),
                    deferred, amt, Num(t.Get("baseAmount")) > 0 ? Num(t.Get("baseAmount")) : Math.Round(amt / 0.07, 2),
                    docNo, t.GetStr("issuerName").Trim().ToUpperInvariant(), DeferredVatReversal.IsImportVat(t)));
            }
            // Every VAT line carries the invoicing party and ITS invoice, the way Accounting writes
            // it (PRD 5100001131: both D1 lines "PROMPT,INV.PI-2608011", the pass-through receipts
            // included), with the PO as Assignment (PRD 5105604002). The per-receipt detail the
            // tax report needs travels on the Input VAT journal entry, not here.
            static string Short(string name)
            {
                var n = (name ?? "").Trim();
                var cut = n.IndexOfAny([' ', ',']);
                return (cut > 0 ? n[..cut] : n).Trim().ToUpperInvariant();
            }
            var partyName = Short(Or(vatLines.FirstOrDefault(v => v.Own).Issuer ?? "",
                Or(header.GetStr("vendorName").Trim(), vatLines.FirstOrDefault().Issuer ?? "")));
            var vatText = partyName.Length > 0 && ownInvoice.Length > 0 ? $"{partyName},INV.{ownInvoice}" : itemText;
            foreach (var g in vatLines.GroupBy(v => (v.Code, v.Deferred,
                         Key: v.Customs ? "CUSTOMS|" + v.DocNo : v.Own ? "OWN|" + v.DocNo : "PAID-ON-BEHALF")))
            {
                var items = g.ToList();
                var amt = Math.Round(items.Sum(v => v.Amount), 2);
                var docs = items.Select(v => v.DocNo).Where(d => d.Length > 0).Distinct().ToList();
                var text = string.Join(" / ", items.Select(v => v.Issuer.Length > 0 && v.DocNo.Length > 0
                    ? $"{v.Issuer},INV.{v.DocNo}" : Or(v.Issuer, v.DocNo)).Distinct());
                glAccountItems.Add(new Dictionary<string, object?>
                {
                    ["SupplierInvoiceItem"] = "0",
                    ["CompanyCode"] = companyCode,
                    ["GLAccount"] = g.Key.Deferred ? DeferredVatReversal.GlDeferredInputTax : DeferredVatReversal.GlInputTax,
                    ["DocumentCurrency"] = currency,
                    ["SupplierInvoiceItemAmount"] = F2(amt),
                    ["DebitCreditCode"] = "S",
                    ["TaxCode"] = g.Key.Code,
                    ["TaxBaseAmountInTransCrcy"] = F2(Math.Round(items.Sum(v => v.Base), 2)),
                    // Customs import VAT (Accounting, 2026-11-08): Assignment = the customs receipt
                    // number, Text = the department's full name. Every other VAT line: the PO and
                    // the invoicing party's text.
                    ["AssignmentReference"] = g.Key.Key.StartsWith("CUSTOMS|")
                        ? Cut(Or(docs.FirstOrDefault() ?? "", Or(poRef, assignment)), 18)
                        : Cut(Or(poRef, Or(docs.FirstOrDefault() ?? "", assignment)), 18),
                    ["SupplierInvoiceItemText"] = g.Key.Key.StartsWith("CUSTOMS|")
                        ? Cut(Or(items.First().Issuer, vatText), 50)
                        : Cut(vatText, 50),
                    ["_vatAsGlLine"] = g.Key.Code,
                    ["_vatDocs"] = string.Join(", ", docs),
                    ["_vatDetail"] = text,
                });
            }
            // Lines: VX, and one line per PO item + condition again.
            var merged = new List<object>();
            foreach (var grp in purOrdRef.Cast<Dictionary<string, object?>>()
                         .GroupBy(r => r.Get("_deliveryCost") is true
                             ? $"{r.GetStr("PurchaseOrder")}|{r.GetStr("PurchaseOrderItem")}|{r.GetStr("SuplrInvcDeliveryCostCndnType")}|{r.GetStr("FreightSupplier")}"
                             : Guid.NewGuid().ToString()))
            {
                var first = new Dictionary<string, object?>(grp.First());
                if (first.Get("_deliveryCost") is true)
                {
                    first["TaxCode"] = "VX";
                    if (grp.Count() > 1)
                    {
                        first["SupplierInvoiceItemAmount"] = F2(Math.Round(grp.Sum(r => Num(r.Get("SupplierInvoiceItemAmount"))), 2));
                        if (grp.All(r => r.Get("QuantityInPurchaseOrderUnit") != null))
                            first["QuantityInPurchaseOrderUnit"] = F3(Math.Round(grp.Sum(r => Num(r.Get("QuantityInPurchaseOrderUnit"))), 3));
                        first["SuplrInvcDeliveryCostCndnStep"] = null;
                        first["SuplrInvcDeliveryCostCndnCount"] = null;
                    }
                }
                merged.Add(first);
            }
            // Line = what the PO accrued, Gross = what the invoice says, the satang in between is
            // SAP's to book as Purchase Variance — Accounting's way (PRD 5100001132: Fee accrued
            // 200.05, receipt 200.00 → line 200.05, Purchase Variance 0.05 Cr). With the line at the
            // receipt's 200.00 instead, SAP took the difference as a price change of the goods and
            // put it on Stock in Trade (TEST 5100000074: accrued 199.71, 0.29 to 11511001).
            // Only a small difference is treated this way; a bigger one is a real price difference
            // and the line keeps the invoice's amount.
            foreach (var line in merged.Cast<Dictionary<string, object?>>().Where(l => l.Get("_deliveryCost") is true))
            {
                var accrued = Num(line.Get("_accrued"));
                var amount = Num(line.Get("SupplierInvoiceItemAmount"));
                var diff = Math.Round(accrued - amount, 2);
                if (accrued > 0 && Math.Abs(diff) > AccrualVarianceTolerance)
                    line["_accrualUnknown"] =
                        $"{line.GetStr("SuplrInvcDeliveryCostCndnType")} PO {line.GetStr("PurchaseOrder")}/{line.GetStr("PurchaseOrderItem")} "
                        + $"ค้างไว้ {F2(accrued)} แต่ใบเรียกเก็บ {F2(amount)} (ต่าง {F2(-diff)}) — ส่วนต่างจะเข้าต้นทุนสินค้า (Stock in Trade) "
                        + "ตรวจยอด condition ใน PO ก่อน Post";
                if (accrued <= 0 || diff == 0 || Math.Abs(diff) > AccrualVarianceTolerance) continue;
                line["SupplierInvoiceItemAmount"] = F2(accrued);
                line["_accrualAdjusted"] = F2(diff);
                accrualDelta = Math.Round(accrualDelta + diff, 2);
            }
            // The difference goes on a Purchase Variance G/L item of our own, exactly as Accounting
            // keys it (5105604031, G/L Account Items: H 52110001 Purchase Variance 0.05, VX,
            // Assignment 2230000565). Left to SAP's automatic small-difference posting, the TEST
            // tenant's account determination pointed at 52060000, which its chart of accounts does
            // not have. With the line here the document also opens Balance 0.
            if (accrualDelta != 0)
                glAccountItems.Add(new Dictionary<string, object?>
                {
                    ["SupplierInvoiceItem"] = "0",
                    ["CompanyCode"] = companyCode,
                    ["GLAccount"] = PurchaseVarianceAccount,
                    ["DocumentCurrency"] = currency,
                    ["SupplierInvoiceItemAmount"] = F2(Math.Abs(accrualDelta)),
                    // Lines raised to the accrual → credit the variance; lowered → debit it.
                    ["DebitCreditCode"] = accrualDelta > 0 ? "H" : "S",
                    ["TaxCode"] = "VX",
                    // Same Assignment and Text as the payables line, as Accounting keys them.
                    ["AssignmentReference"] = Cut(assignment, 18),
                    ["SupplierInvoiceItemText"] = Cut(DistinctText(itemText, "PURCHASE VARIANCE", "PURCHASE VARIANCE"), 50),
                    ["_purchaseVariance"] = true,
                });
            purOrdRef.Clear();
            purOrdRef.AddRange(merged);
            for (var pi = 0; pi < purOrdRef.Count; pi++)
                ((Dictionary<string, object?>)purOrdRef[pi])["SupplierInvoiceItem"] = (pi + 1).ToString();
            // Tax tab: the lines' VX base with no tax (5105604031: VX, base 200.00, tax 0.00).
            taxItems.Clear();
            var vxBase = Math.Round(purOrdRef.Sum(PoAmount), 2);
            if (vxBase > 0)
                taxItems.Add(new Dictionary<string, object?>
                {
                    ["TaxCode"] = "VX",
                    ["DocumentCurrency"] = currency,
                    ["TaxBaseAmountInTransCrcy"] = F2(vxBase),
                    ["TaxAmount"] = F2(0),
                });
        }

        // G/L items are numbered on their own, from 1 — not after the PO lines. SAP checks each
        // list separately: with one ZFE2 line and the two tax-base lines numbered 2 and 3 it refused
        // the Customs Department's invoice with MM_IV_SI_API/006 "Items must be numbered
        // sequentially. Item 0002 should be number 0001".
        for (var gi = 0; gi < glAccountItems.Count; gi++)
            ((Dictionary<string, object?>)glAccountItems[gi])["SupplierInvoiceItem"] = (gi + 1).ToString();

        // Shipping documents (delivery costs, duty, fees — anything that is not a plain goods
        // invoice against a PO) go to SAP PARKED, not posted: AP opens the draft, checks it and
        // posts it themselves. Goods invoices (INVENTORY_PO) keep posting as before.
        var park = !isPoKeyed;

        // Gross = what the lines add up to, with each G/L line SIGNED by its Debit/Credit code.
        // The old sum added every G/L line as positive, so a credit (H) line pushed the gross UP
        // instead of down and the draft opened in SAP with a balance that was not zero.
        var gross = SumOfParts(purOrdRef, glAccountItems, taxItems);
        // With "Calculate Tax" ticked no tax node is sent and SAP works the VAT out from each
        // line's tax code — but it still adds that VAT to the debits, so the Gross has to carry it
        // too. Left out, the draft opened with Balance = −VAT (5105600180: debits 13,903.12 vs gross
        // 13,766.24, −136.88 = D1 112.00 + V1 24.88). The amounts on the Tax tab are what SAP will
        // calculate for those lines, so they are added here even though the rows are not sent.
        var calcTaxByCode = calcTax
            ? taxRows.Where(t => t.GetStr("taxCode").Trim().Length > 0)
                .GroupBy(t => t.GetStr("taxCode").Trim().ToUpperInvariant())
                .ToDictionary(g => g.Key, g => Math.Round(g.Sum(t => Num(t.Get("docCurrencyAmt"))), 2))
            : new Dictionary<string, double>();
        if (calcTax)
            gross = Math.Round(gross + taxRows
                .Where(t => t.GetStr("taxCode").Trim().Length > 0)
                .Sum(t => Num(t.Get("docCurrencyAmt"))), 2);
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
            // Lines set to the accrual on purpose leave that difference for Purchase Variance —
            // balancing them back onto a line would undo it.
            gross = park && accrualDelta == 0 ? BalanceToStated(stated, gross, purOrdRef, glAccountItems, taxItems) : stated;
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
            // Section Code is OUR tax branch (the screen's "Bus.Place/Sectn" 0000), not the
            // supplier's branch: header.branch is the supplier's ("Branch Code" in KUT-FI-AP-201),
            // and a supplier at branch 00001 would have sent a section code we do not have.
            // The supplier's branch has no field in A_SupplierInvoice — SAP takes it from the
            // business partner master.
            ["BusinessSectionCode"] = businessPlace,
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

        // Payment tab (KUT-FI-AP-201 p.37) — sent only when the document has them; otherwise SAP
        // takes them from the supplier master, as before.
        void Opt(string field, string value, int max)
        {
            value = value.Trim();
            if (value.Length > 0 && value.Length <= max) payload[field] = value;
        }
        Opt("PaymentMethod", header.GetStr("paymentMethod"), 1);
        Opt("PaymentBlockingReason", header.GetStr("paymentBlock"), 1);
        Opt("HouseBank", header.GetStr("houseBank"), 5);
        Opt("HouseBankAccount", header.GetStr("bankAccountId"), 5);
        Opt("BPBankAccountInternalID", header.GetStr("partnerBank"), 4);
        if (SapPayloadBuilder.ODataDate(header.Get("baselineDate")) is { } baseline)
            payload["DueCalculationBaseDate"] = baseline;
        // Basic Data → Transaction: Credit Memo (KG / RS document type, or the screen says so).
        if (header.GetStr("transaction").Contains("credit", StringComparison.OrdinalIgnoreCase)
            || header.GetStr("sapDocType").Trim() is "KG" or "RS")
            payload["SupplierInvoiceIsCreditMemo"] = "X";

        if (purOrdRef.Count > 0) payload["to_SuplrInvcItemPurOrdRef"] = purOrdRef;
        if (glAccountItems.Count > 0) payload["to_SupplierInvoiceItemGLAcct"] = glAccountItems;
        if (taxItems.Count > 0) payload["to_SupplierInvoiceTax"] = taxItems;

        // "A" = Parked: the invoice gets its SAP number and waits for AP, unposted and unsimulated.
        if (park) payload["SupplierInvoiceStatus"] = "A";

        var wht = Num(header.Get("whtAmount"));
        if (wht > 0) payload["_wht"] = new Dictionary<string, object?> { ["WithholdingTaxAmount"] = F2(wht) };

        // Withholding Tax tab (KUT-FI-AP-201 p.40): the rows of the screen's WHT tab
        // (header.whtItems — type, code, base, amount), this supplier's only. Until now they were
        // kept in "_wht", which is stripped before sending, so SAP never saw any withholding.
        // A row without a type or code is not sent: SAP refuses a type the supplier master does
        // not carry, and the type is Accounting's to choose (TI/TJ/TK at invoice, OA/OB/OC at payment).
        var whtNode = new List<object>();
        foreach (var w in ReadWhtRows(header).Where(Mine))
        {
            var type = w.GetStr("wtType").Trim().ToUpperInvariant();
            var code = w.GetStr("whtCode").Trim();
            if (type.Length != 2 || code.Length == 0 || code.Length > 2) continue;
            var baseFc = Num(w.Get("baseFc"));
            var amtFc = Num(w.Get("amtFc"));
            if (baseFc == 0 && amtFc == 0) continue;
            var row = new Dictionary<string, object?>
            {
                ["WithholdingTaxType"] = type,
                ["WithholdingTaxCode"] = code,
                ["DocumentCurrency"] = currency,
            };
            if (baseFc > 0)
            {
                row["WithholdingTaxBaseAmount"] = F2(baseFc);
                row["WhldgTaxBaseIsEnteredManually"] = true;
            }
            if (amtFc > 0)
            {
                row["ManuallyEnteredWhldgTaxAmount"] = F2(amtFc);
                row["WhldgTaxIsEnteredManually"] = true;
            }
            whtNode.Add(row);
        }
        if (whtNode.Count > 0) payload["to_SupplierInvoiceWhldgTax"] = whtNode;
        if (calcTax) payload["_calcTaxByCode"] = calcTaxByCode;
        return payload;
    }

    /// <summary>Splits one supplier's invoice into one SAP document per tax code when SAP could
    /// not take it as one.
    ///
    /// SAP takes ONE line per (PO item, condition) in an invoice, with one tax code (M8/321). A
    /// forwarder's bill often settles one condition under two tax codes — on PO 2230000033 the
    /// whole ZLH1 (17,438 THB) sits on item 10 only, while Prompt bills 1,500 of it as its own
    /// VAT-able service (D1) and the rest as advances (VX). As one document that is either M8/321
    /// or FF812, and the person had to split it by hand in SAP. Here it is split before sending:
    /// each part takes the lines of its tax code, the tax rows of that code, its own gross, and a
    /// reference of its own (the first keeps the original, the next get -2, -3 …) so SAP's
    /// duplicate check and our "already created" check tell them apart. G/L lines go with the
    /// largest part; withholding goes with the part that carries the VAT-able service.
    /// A document with no repeated (PO item, condition) is returned as it is.</summary>
    public static List<Dictionary<string, object?>> SplitByTaxCode(Dictionary<string, object?> payload)
    {
        if (payload.GetValueOrDefault("to_SuplrInvcItemPurOrdRef") is not List<object> items || items.Count < 2)
            return [payload];
        static Dictionary<string, object?> D(object o) => (Dictionary<string, object?>)o;
        static string Key(object o) =>
            $"{D(o).GetStr("PurchaseOrder")}|{D(o).GetStr("PurchaseOrderItem").Trim().TrimStart('0')}|{D(o).GetStr("SuplrInvcDeliveryCostCndnType")}";
        if (items.GroupBy(Key).All(g => g.Count() == 1)) return [payload];

        static string PartKey(object o) =>
            $"{D(o).GetStr("TaxCode").Trim().ToUpperInvariant()}|{D(o).GetStr("FreightSupplier").Trim()}";
        var parts = items.GroupBy(PartKey)
            .Select(g => g.ToList())
            .OrderByDescending(g => g.Sum(PoAmount))
            .ToList();
        // Two lines of the same tax code on the same item and condition cannot be told apart by
        // splitting on tax code; that is a merge the builder already does, so it does not happen.
        if (parts.Any(p => p.GroupBy(Key).Any(g => g.Count() > 1))) return [payload];

        var taxes = payload.GetValueOrDefault("to_SupplierInvoiceTax") as List<object> ?? [];
        var gl = payload.GetValueOrDefault("to_SupplierInvoiceItemGLAcct") as List<object> ?? [];
        var calcByCode = payload.GetValueOrDefault("_calcTaxByCode") as Dictionary<string, double> ?? [];
        var reference = payload.GetStr("SupplierInvoiceIDByInvcgParty");
        static bool Exempt(string code) => code is "" or "VX";

        // Withholding is on the supplier's own service, which is the VAT-able part.
        var whtPart = parts.FindIndex(p => p.Any(i => !Exempt(D(i).GetStr("TaxCode").Trim().ToUpperInvariant())));
        if (whtPart < 0) whtPart = 0;

        var result = new List<Dictionary<string, object?>>();
        for (var k = 0; k < parts.Count; k++)
        {
            var codes = parts[k].Select(i => D(i).GetStr("TaxCode").Trim().ToUpperInvariant()).ToHashSet();
            var part = new Dictionary<string, object?>(payload);
            var no = 0;
            var lines = parts[k].Select(i =>
            {
                var row = new Dictionary<string, object?>(D(i)) { ["SupplierInvoiceItem"] = (++no).ToString() };
                return (object)row;
            }).ToList();
            var partGl = k == 0 ? gl : [];
            // Part 0 also takes the G/L lines (and so their tax codes) and any tax row whose code no
            // part's lines carry — the import VAT with its clearing-account base travels as a unit.
            if (k == 0)
            {
                foreach (var g in gl) codes.Add(D(g).GetStr("TaxCode").Trim().ToUpperInvariant());
                foreach (var t in taxes)
                {
                    var tc = D(t).GetStr("TaxCode").Trim().ToUpperInvariant();
                    if (!parts.Any(p => p.Any(i => D(i).GetStr("TaxCode").Trim().ToUpperInvariant() == tc))) codes.Add(tc);
                }
            }
            var partTaxes = taxes.Where(t => codes.Contains(D(t).GetStr("TaxCode").Trim().ToUpperInvariant())).ToList();

            part["to_SuplrInvcItemPurOrdRef"] = lines;
            if (partTaxes.Count > 0) part["to_SupplierInvoiceTax"] = partTaxes; else part.Remove("to_SupplierInvoiceTax");
            if (partGl.Count > 0) part["to_SupplierInvoiceItemGLAcct"] = partGl; else part.Remove("to_SupplierInvoiceItemGLAcct");
            if (k != whtPart) { part.Remove("_wht"); part.Remove("to_SupplierInvoiceWhldgTax"); }

            var gross = SumOfParts(lines, partGl, partTaxes)
                + codes.Sum(c => calcByCode.TryGetValue(c, out var v) ? v : 0);
            part["InvoiceGrossAmount"] = F2(Math.Round(gross, 2));
            if (k > 0)
            {
                var suffix = "-" + (k + 1);
                part["SupplierInvoiceIDByInvcgParty"] = Cut(reference, 16 - suffix.Length) + suffix;
            }
            part["_splitByTaxCode"] = $"{k + 1}/{parts.Count} ({string.Join(", ", codes)})";
            result.Add(part);
        }
        return result;
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

    /// <summary>Largest gap between an invoice line and the PO's accrual that is still rounding
    /// (left to Purchase Variance) rather than a price difference.</summary>
    private const double AccrualVarianceTolerance = 1.00;

    /// <summary>Purchase Variance (YCOA, Primary Costs) — where Accounting books the satang between
    /// the PO's accrual and the invoice.</summary>
    private const string PurchaseVarianceAccount = "52110001";

    private static double PoAmount(object item) =>
        Num(((Dictionary<string, object?>)item).Get("SupplierInvoiceItemAmount"));

    /// <summary>This supplier's own invoice number, read off the tax rows the screen's Tax tab
    /// holds (Tax Invoice No.). Empty when the rows carry none.</summary>
    /// <summary>header.whtItems as dictionaries (the screen's Withholding Tax tab).</summary>
    private static List<Dictionary<string, object?>> ReadWhtRows(Dictionary<string, object?> header)
    {
        var rows = new List<Dictionary<string, object?>>();
        switch (header.Get("whtItems"))
        {
            case System.Collections.Generic.IEnumerable<object?> list:
                foreach (var o in list)
                    if (o is Dictionary<string, object?> d) rows.Add(d);
                    else if (o is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                        rows.Add(je.EnumerateObject().ToDictionary(p => p.Name, p => (object?)(p.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.Number => p.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                            _ => p.Value.ToString(),
                        })));
                break;
            case System.Text.Json.JsonElement arr when arr.ValueKind == System.Text.Json.JsonValueKind.Array:
                foreach (var je in arr.EnumerateArray())
                    if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
                        rows.Add(je.EnumerateObject().ToDictionary(p => p.Name, p => (object?)(p.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.Number => p.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                            _ => p.Value.ToString(),
                        })));
                break;
        }
        return rows;
    }

    private static string InvoiceNoOf(IEnumerable<Dictionary<string, object?>> taxRows) =>
        taxRows.Select(t => t.GetStr("taxDocNo").Trim()).FirstOrDefault(v => v.Length > 0) ?? "";

    /// <summary>The suppliers this document has to be split into, in the order they appear.
    ///
    /// One shipping bundle is one piece of paper but several suppliers' invoices, and SAP takes
    /// one invoicing party per document. A document with a single supplier returns that one, so
    /// the caller can treat both cases the same way.</summary>
    public static List<string> VendorsOf(Dictionary<string, object?> doc, Func<string, string>? sameParty = null)
    {
        var header = doc.Get("header") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var main = Or(doc.GetStr("partnerCode").Trim(), header.GetStr("vendorCode").Trim());
        var seen = new List<string>();
        void Add(string v)
        {
            var code = Or(v.Trim(), main);
            if (sameParty != null && code.Length > 0) code = sameParty(code);
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
    /// <summary>Gives the cost lines the tax code the Tax tab says they carry.
    ///
    /// The Tax tab holds one row per tax invoice with its code and BASE ("D1 105.00 on 1,500.00 —
    /// PROMPT PI-2608011"). SAP needs lines carrying that code whose amounts add up to that base;
    /// with every line left on VX it refuses the document (FF812 "Tax code D1 also exists") and the
    /// person had to work out which line was the base. Here, for each code whose base is not yet
    /// covered by lines of that code, the VX lines that add up EXACTLY to the missing base are given
    /// the code — one line if one matches, otherwise the smallest combination. Nothing is changed
    /// when no combination matches to the satang (the base is then part of a line, and only a person
    /// can split it), or when a line already has a code other than the default.</summary>
    private static List<(string Cond, string PoItem, string Step, string Count, string Vendor,
        string TaxCode, string Desc, double Amount)> AutoTaxCodes(
        List<(string Cond, string PoItem, string Step, string Count, string Vendor,
            string TaxCode, string Desc, double Amount)> charges,
        IReadOnlyList<Dictionary<string, object?>>? taxRows, string headerTaxCode)
    {
        if (taxRows == null || taxRows.Count == 0 || charges.Count == 0) return charges;
        static bool Exempt(string c) => c.Length == 0 || c.Equals("VX", StringComparison.OrdinalIgnoreCase);
        var defaultCode = Or(headerTaxCode, "VX");
        var need = taxRows
            .Where(t => !Exempt(t.GetStr("taxCode").Trim()) && Num(t.Get("baseAmount")) > 0)
            .GroupBy(t => t.GetStr("taxCode").Trim().ToUpperInvariant())
            .Select(g => (Code: g.Key, Base: Math.Round(g.Sum(t => Num(t.Get("baseAmount"))), 2)))
            .ToList();
        var list = charges.ToList();
        foreach (var (code, baseAmt) in need)
        {
            var covered = list.Where(c => c.TaxCode.Equals(code, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Amount);
            var missing = Math.Round(baseAmt - covered, 2);
            if (missing <= 0.009) continue;
            var free = list.Select((c, i) => (c, i))
                .Where(x => Exempt(x.c.TaxCode) || x.c.TaxCode.Equals(defaultCode, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.c.Amount > 0)
                .ToList();
            if (free.Count == 0 || free.Count > 18) continue;
            int[]? best = null;
            for (var mask = 1; mask < (1 << free.Count); mask++)
            {
                var bits = System.Numerics.BitOperations.PopCount((uint)mask);
                if (best != null && bits >= best.Length) continue;
                double sum = 0;
                for (var b = 0; b < free.Count; b++) if ((mask & (1 << b)) != 0) sum += free[b].c.Amount;
                if (Math.Abs(sum - missing) <= 0.01)
                    best = Enumerable.Range(0, free.Count).Where(b => (mask & (1 << b)) != 0).Select(b => free[b].i).ToArray();
            }
            if (best == null) continue;
            foreach (var i in best) list[i] = list[i] with { TaxCode = code };
        }
        return list;
    }

    private static List<object> DeliveryCostItems(
        Dictionary<string, object?> doc, string poRef, string headerCurrency, string headerTaxCode,
        string invoicingParty, string itemText,
        Func<Dictionary<string, object?>, bool> mine, int startNo,
        IReadOnlyList<SapPurchaseOrderItem>? poItems = null,
        IReadOnlyList<System.Text.Json.JsonElement>? goodsReceiptItems = null,
        IReadOnlyList<Dictionary<string, object?>>? taxRows = null)
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
            // A tax row the read did not tag ("VAT 7% PROMPT LOGISTICS …", "Withholding Tax 3% …")
            // is still a tax row, not a charge against the PO.
            if (System.Text.RegularExpressions.Regex.IsMatch(l.GetStr("desc"), @"^\s*(VAT|WITHHOLDING)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
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
                // Same rule the screen seeds with: the COSTS heading the FORM SHIPPING EXPENSE files
                // the row under names the condition (Local ZLH1, Fee ZFE2, Insurance ZINS, Overseas
                // ZOVS) — used only when the PO holds it, or the PO could not be read.
                var byHeading = extra.GetStr("costType").Trim().ToUpperInvariant() switch
                {
                    "LOCAL" => "ZLH1", "FEE" => "ZFE2", "INSURANCE" => "ZINS", "OVERSEAS" => "ZOVS", _ => "",
                };
                if (byHeading.Length > 0 && (poConds.Count == 0
                    || poConds.Any(x => string.Equals(x.Cond.ConditionType.Trim(), byHeading, StringComparison.OrdinalIgnoreCase))))
                    cndnType = byHeading;
            }
            if (cndnType.Length == 0)
            {
                missing.Add($"\"{Cut(l.GetStr("desc"), 40)}\" (ยังไม่ได้เลือก Condition)");
                continue;
            }
            // A PO item the screen numbered by position (10, 20, 30 … with the "auto" badge) is a
            // guess for goods lines and means nothing for a delivery cost — the condition decides
            // which items it clears — so it is dropped and the item is worked out from the PO.
            var autoItem = extra.GetStr("sapPoItemAuto").Trim() is { Length: > 0 } ai
                && !string.Equals(ai, "false", StringComparison.OrdinalIgnoreCase) && ai != "0";
            charges.Add((cndnType.ToUpperInvariant(), autoItem ? "" : extra.GetStr("sapPoItem").Trim(),
                extra.GetStr("deliveryCondStep").Trim(), extra.GetStr("deliveryCondCount").Trim(),
                extra.GetStr("vendorCode").Trim(), Or(extra.GetStr("taxCode").Trim(), headerTaxCode),
                l.GetStr("desc").Trim(), amount));
        }

        charges = AutoTaxCodes(charges, taxRows, headerTaxCode);

        // Pass 2: one SAP line per (PO item, condition). Several charges on the form usually settle
        // the same condition (DO, storage, trucking, labour… are all ZLH1 Local Handling), and SAP
        // takes one line per PO item and condition — so they are summed first, then spread over the
        // PO items that accrued that condition, in proportion to what each item accrued. That is
        // what MIRO does when it proposes planned delivery costs; the satang left over by rounding
        // goes to the largest share so the lines still add up to the invoice exactly.
        var rows = new List<(string PoItem, string Cond, string Step, string Count, string Vendor,
            string TaxCode, string Text, double Amount)>();
        var groups = charges.GroupBy(c => (c.Cond, c.PoItem, c.Vendor, c.TaxCode))
            .Select(g => (Key: g.Key,
                          Total: Math.Round(g.Sum(c => c.Amount), 2),
                          Text: string.Join(", ", g.Select(c => c.Desc).Where(d => d.Length > 0).Distinct()),
                          Step: g.Count() == 1 ? g.First().Step : "",
                          Count: g.Count() == 1 ? g.First().Count : ""))
            .ToList();

        // Lines where the person chose the PO item themselves go exactly there.
        foreach (var g in groups.Where(g => g.Key.PoItem.Length > 0))
        {
            if (!receivedItems.Contains(ItemKey(g.Key.PoItem)))
            {
                missing.Add($"\"{Cut(g.Text, 40)}\" (PO item {g.Key.PoItem} ยังไม่มี GR สุทธิ)");
                continue;
            }
            rows.Add((g.Key.PoItem, g.Key.Cond, g.Step, g.Count, g.Key.Vendor, g.Key.TaxCode, g.Text, g.Total));
        }

        // The rest are placed per condition. SAP allows ONE line per (PO item, condition) in an
        // invoice (M8/321 "Document contains same order item more than once"), and that line has a
        // single tax code and supplier. So when one condition is billed under several tax codes
        // (ZLH1 as VX for DO/trucking, V1 for storage, D1 for Prompt's own service), each tax code
        // gets its OWN PO items: the largest group takes the largest item plus any spare ones, the
        // others one item each. With a single tax code nothing changes — it is spread over every
        // eligible item in proportion to what each accrued, as MIRO proposes.
        foreach (var byCond in groups.Where(g => g.Key.PoItem.Length == 0)
                                     .GroupBy(g => g.Key.Cond, StringComparer.OrdinalIgnoreCase))
        {
            var cond = byCond.Key;
            var condGroups = byCond.OrderByDescending(g => Math.Abs(g.Total)).ToList();
            var condText = string.Join(", ", condGroups.Select(g => g.Text).Where(t => t.Length > 0));

            var matchingConditions = poConds
                .Where(x => string.Equals(x.Cond.ConditionType.Trim(), cond, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var conditionHolders = matchingConditions
                .Where(x => x.Cond.Amount != 0)
                .GroupBy(x => x.Item)
                .Select(h => (Item: h.Key, Weight: (double)h.Sum(x => Math.Abs(x.Cond.Amount))))
                .Where(h => h.Weight > 0)
                .ToList();
            // Items already taken for this condition by a line with a PO item typed by hand.
            var taken = rows.Where(r => string.Equals(r.Cond, cond, StringComparison.OrdinalIgnoreCase))
                .Select(r => ItemKey(r.PoItem)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var holders = conditionHolders
                .Where(h => receivedItems.Contains(ItemKey(h.Item)) && !taken.Contains(ItemKey(h.Item)))
                .OrderByDescending(h => h.Weight).ThenBy(h => h.Item.PadLeft(10, '0'))
                .ToList();
            if (holders.Count == 0)
            {
                var reason = poConds.Count == 0
                    ? "อ่าน PO จาก SAP ไม่ได้ จึงตรวจ condition ของ PO ไม่ได้"
                    : matchingConditions.Count == 0
                        ? $"เลือก {cond} ใน invoice แล้ว แต่ PO {poRef} ไม่มี condition {cond} ตั้งค้างไว้"
                        : conditionHolders.Count == 0
                            ? $"PO {poRef} มี condition {cond} แต่ไม่มียอดสะสมให้ตัด"
                            : $"PO {poRef} มี condition {cond} ที่ item {string.Join(", ", conditionHolders.Select(h => h.Item))} แต่ยังไม่พบ GR สุทธิของ item ที่ยังว่าง";
                missing.Add($"\"{Cut(condText, 40)}\" ({reason})");
                continue;
            }
            // Assign: one item per group (largest group ↔ largest item), spare items to the largest group.
            // When there are more groups than items, groups share items (round robin); the repeated
            // (item, condition) lines are then sent as separate documents by SplitByTaxCode.
            var assigned = condGroups.Select((g, i) => new List<(string Item, double Weight)> { holders[i % holders.Count] }).ToList();
            for (var i = condGroups.Count; i < holders.Count; i++) assigned[0].Add(holders[i]);

            for (var gi = 0; gi < condGroups.Count; gi++)
            {
                var g = condGroups[gi];
                var mineItems = assigned[gi];
                var weightSum = mineItems.Sum(h => h.Weight);
                var shares = mineItems
                    .Select(h => (h.Item, h.Weight, Amount: Math.Round(g.Total * h.Weight / weightSum, 2)))
                    .ToList();
                var rest = Math.Round(g.Total - shares.Sum(x => x.Amount), 2);
                if (rest != 0)
                {
                    var big = shares.IndexOf(shares.OrderByDescending(x => x.Weight).First());
                    shares[big] = (shares[big].Item, shares[big].Weight, Math.Round(shares[big].Amount + rest, 2));
                }
                var single = mineItems.Count == 1;
                foreach (var sh in shares.Where(x => x.Amount != 0))
                    rows.Add((sh.Item, cond, single ? g.Step : "", single ? g.Count : "",
                        g.Key.Vendor, g.Key.TaxCode, g.Text, sh.Amount));
            }
        }

        // Two lines on the same PO item + condition with different tax codes or suppliers cannot be
        // one SAP line; they stay separate here and SplitByTaxCode sends them as separate documents.

        if (missing.Count > 0)
            throw new MgtOcr.Core.HttpApiException(400,
                $"ยังจับคู่ค่าใช้จ่ายกับ PO {poRef} ไม่ได้ — เลือก Condition ใน invoice อย่างเดียวไม่พอ ต้องมี Condition เดียวกันตั้งไว้บน PO และมี GR สุทธิในรายการ PO นั้นด้วย\n\n"
                + "รายละเอียด: " + string.Join(" · ", missing));

        // Same PO item + condition from two groups (e.g. one with a tax code typed by hand) would be
        // refused by SAP as a duplicate, so they are merged here.
        // Delivery-cost QUANTITY. The goods receipt accrued each condition against the quantity
        // received (PO history: ZLH1 520.40 USD on 3,000 KG), and SAP prices the invoice line per
        // unit. Sent with no quantity, the line is 0 KG: SAP calls the price "too high", ignores the
        // PO item and leaves the whole amount as Balance (5105600203: "Order item 2230000033 00010
        // will be ignored", Balance 15,938.00). So each line carries its share of the received
        // quantity, by its share of what the condition accrued on that item; when the document
        // settles the condition in full (the usual case) the shares add up to the received quantity
        // exactly, split across however many documents the tax codes make.
        var receivedQty = (goodsReceiptItems ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(GrString(row, "PurchaseOrderItem")))
            .GroupBy(row => ItemKey(GrString(row, "PurchaseOrderItem")!))
            .ToDictionary(g => g.Key,
                g => g.Sum(row => (GrReversal(row) ? -1 : 1) * GrNumber(row, "QuantityInEntryUnit")),
                StringComparer.OrdinalIgnoreCase);
        var poItemByKey = (poItems ?? []).GroupBy(i => ItemKey(i.Item))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // The rate the goods receipt itself converted at, worked back from its goods line: the THB
        // the GR posted for the goods (TotalGoodsMvtAmtInCCCrcy) over what those goods cost in the
        // PO's currency (quantity × net price). A delivery cost the PO holds in USD was accrued at
        // that same rate — PO 2230000056: goods 381,998.04 THB for 11,400.00 USD = 33.5086, and ZFE2
        // 5.96 USD × 33.5086 = 199.71, the accrual on the PO history. The PO's own pricing rate
        // (PriceDetnExchangeRate) is not it: on another PO it gave 199.24 against an accrual of
        // 190.99, and the 8.25 between went to Stock in Trade. Null when it cannot be worked out.
        double? GrRateFor(string item)
        {
            if (!poItemByKey.TryGetValue(ItemKey(item), out var pi) || pi.NetPrice <= 0) return null;
            var grRows = (goodsReceiptItems ?? [])
                .Where(r => ItemKey(GrString(r, "PurchaseOrderItem") ?? "") == ItemKey(item))
                .ToList();
            var thb = grRows.Sum(r => (GrReversal(r) ? -1 : 1) * GrNumber(r, "TotalGoodsMvtAmtInCCCrcy"));
            var qty = grRows.Sum(r => (GrReversal(r) ? -1 : 1) * GrNumber(r, "QuantityInEntryUnit"));
            if (thb <= 0 || qty <= 0) return null;
            var rate = thb / (qty * (double)pi.NetPrice);
            // A price per 100 units, or a field the tenant does not send, gives nonsense; a rate
            // far from the PO's own is not trusted.
            if (pi.ExchangeRate > 0 && Math.Abs(rate - (double)pi.ExchangeRate) > (double)pi.ExchangeRate * 0.2) return null;
            return rate;
        }
        bool Differs(string? cur) =>
            !string.IsNullOrWhiteSpace(cur) && !string.Equals(cur.Trim(), headerCurrency, StringComparison.OrdinalIgnoreCase);
        IEnumerable<SapPoCondition> CondsOf(SapPurchaseOrderItem pi, string cond) =>
            pi.Conditions.Where(c => string.Equals(c.ConditionType.Trim(), cond, StringComparison.OrdinalIgnoreCase));
        // Was the CONDITION itself typed in a foreign currency (ZFE2 5.96 USD), or in THB on a USD
        // purchase order (ZLH1 17,437.88 THB)? Only the first is converted at the GR's rate; the
        // second accrues the THB that was typed, whatever the rates.
        bool TypedForeign(SapPurchaseOrderItem pi, string cond) => CondsOf(pi, cond).Any(c => Differs(c.RateCurrency));
        double AccruedInDocCurrency(string item, string cond)
        {
            if (!poItemByKey.TryGetValue(ItemKey(item), out var pi)) return 0;
            var amt = (double)CondsOf(pi, cond).Sum(c => Math.Abs(c.Amount));
            var cur = pi.Conditions.Select(c => c.AmountCurrency).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? pi.Currency;
            if (Differs(cur))
            {
                var rate = TypedForeign(pi, cond)
                    ? GrRateFor(item) ?? (pi.ExchangeRate > 0 ? (double)pi.ExchangeRate : 0)
                    : (pi.ExchangeRate > 0 ? (double)pi.ExchangeRate : 0);
                amt *= rate;
            }
            return amt;
        }
        // Known to the satang: a condition typed in THB, or one typed in a foreign currency whose
        // GR rate could be worked out. Otherwise (API_MATERIAL_DOCUMENT_SRV sends no goods amount
        // on this tenant) the line keeps the invoice's own amount and the person is told.
        // PO 2230000061 settled it: purchasing types the condition in USD at the PO's own rate
        // (ZLH1 13,766.24 THB / 33.5086 = 410.83 USD), and the goods receipt accrues it back at that
        // same rate — PRD's Fee 5.97 USD × 33.5086 = 200.04, exactly the BzWE. So the PO's rate is
        // the accrual's rate. A gap that is more than rounding means the PO holds a different
        // figure (ZFE2 typed 5.70 for 5.97 → 191.00 against a 200.00 receipt): reported, not hidden.
        bool AccrualIsExact(string item, string cond) => poItemByKey.ContainsKey(ItemKey(item));
        // The service does not say reliably which currency ConditionAmount is in for a condition
        // typed in THB on a USD order, so both readings are tried — as it stands and converted at
        // the PO's rate — and the one nearer the invoice line is taken.
        double AccrualNear(string item, string cond, double invoiceAmount)
        {
            var converted = Math.Round(AccruedInDocCurrency(item, cond), 2);
            if (!poItemByKey.TryGetValue(ItemKey(item), out var pi)) return converted;
            var raw = Math.Round((double)CondsOf(pi, cond).Sum(c => Math.Abs(c.Amount)), 2);
            return Math.Abs(raw - invoiceAmount) < Math.Abs(converted - invoiceAmount) ? raw : converted;
        }
        var qtyOf = new Dictionary<(string, string, string, string), double>();
        foreach (var byItemCond in rows.GroupBy(r => (Item: ItemKey(r.PoItem), Cond: r.Cond.ToUpperInvariant())))
        {
            if (!receivedQty.TryGetValue(byItemCond.Key.Item, out var gotQty) || gotQty <= 0) continue;
            var parts = byItemCond.GroupBy(r => (r.PoItem, r.Cond, r.Vendor, r.TaxCode))
                .Select(g => (g.Key, Amount: g.Sum(x => x.Amount))).ToList();
            var docTotal = parts.Sum(l => l.Amount);
            if (docTotal <= 0) continue;
            var accrued = AccruedInDocCurrency(byItemCond.Key.Item, byItemCond.Key.Cond);
            // Always the received quantity, all of it: a shipping invoice settles the condition in
            // full, which is how Accounting posts it (PRD 5105604004: ZINS on 10,146 + 3,382 + 1,600
            // KG, the whole GR). Scaling it by amount ÷ accrual trusted an accrual worked out from
            // the PO's pricing fields — when that came out wrong (TEST 5105600256: 13.528 KG of the
            // GR) SAP cleared only that share of the accrual (43.90 of 1,983) and booked the rest,
            // 1,939.10, to Stock in Trade.
            var docQty = gotQty;
            docQty = Math.Round(docQty, 3);
            var given = 0.0;
            for (var i = 0; i < parts.Count; i++)
            {
                var q = i == parts.Count - 1 ? Math.Round(docQty - given, 3) : Math.Round(docQty * parts[i].Amount / docTotal, 3);
                given += q;
                qtyOf[parts[i].Key] = q;
            }
        }

        var no = startNo;
        foreach (var r in rows.GroupBy(r => (r.PoItem, r.Cond, r.Vendor, r.TaxCode)))
        {
            var first = r.First();
            var single = r.Count() == 1;
            var amount = Math.Round(r.Sum(x => x.Amount), 2);
            var hasQty = qtyOf.TryGetValue(r.Key, out var lineQty) && lineQty > 0;
            var unit = poItemByKey.TryGetValue(ItemKey(r.Key.PoItem), out var poIt) ? poIt.Unit : "";
            var text = string.Join(", ", r.Select(x => x.Text).Where(t => t.Length > 0).Distinct());
            result.Add(new Dictionary<string, object?>
            {
                ["SupplierInvoiceItem"] = (++no).ToString(),
                ["PurchaseOrder"] = poRef,
                ["PurchaseOrderItem"] = r.Key.PoItem,
                ["DocumentCurrency"] = headerCurrency,
                ["SupplierInvoiceItemAmount"] = F2(amount),
                ["SuplrInvcDeliveryCostCndnType"] = r.Key.Cond,
                ["QuantityInPurchaseOrderUnit"] = hasQty && unit.Length > 0 ? F3(lineQty) : null,
                ["PurchaseOrderQuantityUnit"] = hasQty && unit.Length > 0 ? unit : null,
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
                // The payables line's text, not the form's cost descriptions — Accounting's lines
                // carry one text for the whole invoice.
                // SAP blanks an item text that is the SAME string as the payables line's text
                // (5100000125: "KCC,INV.KCCCI260715-012" sent on Local Hand&Fr and Purchase
                // Variance, both came back empty; the D1 lines, worded differently, kept theirs;
                // before, when this line carried the cost description, it showed). So the line
                // carries the payables text followed by what the cost is.
                ["SupplierInvoiceItemText"] = Cut(DistinctText(itemText, text, "DELIVERY COST"), 50),
                ["_deliveryCost"] = true,
                // What the goods receipt accrued for this item + condition, in document currency.
                ["_accrued"] = AccrualIsExact(r.Key.PoItem, r.Key.Cond) ? F2(AccrualNear(r.Key.PoItem, r.Key.Cond, amount)) : "0",
                // A condition the PO holds in a foreign currency whose THB accrual could not be read:
                // AP has to set the line to the PO history's BzWE before posting.

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

    /// <summary>An item text that is never the bare payables text (SAP empties those): the
    /// payables text followed by what the line is.</summary>
    private static string DistinctText(string payablesText, string what, string fallback)
    {
        var p = (payablesText ?? "").Trim();
        var w = Or((what ?? "").Trim(), fallback).ToUpperInvariant();
        if (p.Length == 0) return w;
        if (string.Equals(w, p, StringComparison.OrdinalIgnoreCase)) w = fallback;
        var joined = $"{p} {w}";
        // Cut to 50 from the END of the cost words, so the invoice reference always survives.
        return joined.Length <= 50 ? joined : joined[..50];
    }
    private static string Cut(string? v, int n)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(v ?? "", @"\s+", " ").Trim();
        return s.Length <= n ? s : s[..n];
    }
    private static double Num(object? v) =>
        v is null ? 0 : double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>A supplier's invoice SAP cannot take as it stands: a tax amount larger than the base its
/// lines carry (the Customs Department's receipt — a 200.00 fee next to 26,228.00 of import VAT).
/// In a bundle it holds back that supplier's invoice only; the other suppliers' invoices still go.</summary>
public sealed class TaxOverBaseException(string detail) : MgtOcr.Core.HttpApiException(400, detail);
