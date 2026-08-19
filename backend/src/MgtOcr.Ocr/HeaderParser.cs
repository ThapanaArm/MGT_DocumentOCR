using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

public class ParsedDocument
{
    public Dictionary<string, object?> Header { get; set; } = [];
    public List<LineItem> Lines { get; set; } = [];
    public double Confidence { get; set; }
    public string Provider { get; set; } = "";
    public string RawText { get; set; } = "";
    public string? Note { get; set; } // mirrors Python's "_note" — non-null means a fallback/failure happened
    public string? SampleName { get; set; }
}

public static class HeaderParser
{
    // _blank_header(): default header shape per module — SO and AP have different fields.
    public static Dictionary<string, object?> BlankHeader(string module) => module == "SO"
        ? new Dictionary<string, object?>
        {
            ["docType"] = "PURCHASE ORDER", ["poNo"] = "", ["poDate"] = "", ["customerName"] = "", ["customerTaxId"] = "",
            ["shipToName"] = "", ["shipToAddress"] = "", ["deliveryDate"] = "", ["currency"] = "THB",
            ["paymentTerms"] = "", ["incoterms"] = "", ["subTotal"] = 0.0, ["vatAmount"] = 0.0,
            ["totalAmount"] = 0.0, ["remark"] = "",
        }
        : new Dictionary<string, object?>
        {
            ["docType"] = "ใบกำกับภาษี/ใบแจ้งหนี้", ["invoiceNo"] = "", ["invoiceDate"] = "", ["postingDate"] = "",
            ["vendorName"] = "", ["vendorTaxId"] = "", ["branch"] = "", ["poRef"] = "", ["currency"] = "THB",
            ["paymentTerms"] = "", ["subTotal"] = 0.0, ["vatRate"] = 7.0, ["vatAmount"] = 0.0, ["whtAmount"] = 0.0, ["totalAmount"] = 0.0,
        };

    // _po_number_date(): some forms (e.g. Henkel/European buyers) write PO number + buyer code +
    // date glued together, e.g. "4593442527 / GGA / 09.03.2026" — specific enough to match
    // without relying on a label (labels can be split across lines by OCR and become unmatchable).
    public static (string PoNo, string PoDate) PoNumberDate(string text)
    {
        var m = Regex.Match(text, @"\b(\d{6,12})\s*/\s*[A-Z]{2,5}\s*/\s*(\d{1,2}\.\d{1,2}\.\d{2,4})\b", RegexOptions.IgnoreCase);
        if (!m.Success) return ("", "");
        var parts = m.Groups[2].Value.Split('.');
        return (m.Groups[1].Value, TextHelpers.IsoDate(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2])));
    }

    // _sane_vat_rate(): almost all Thai VAT is 7% (or 0% if exempt) — an implausible calculated
    // rate (usually a garbled OCR digit throwing the VAT amount way off) falls back to 7%, which
    // also protects the DB column (decimal(5,2)) from an overflow.
    public static double SaneVatRate(double vat, double sub)
    {
        if (sub == 0 || vat == 0) return 7;
        var rate = Math.Round(vat / sub * 100, 2);
        return rate is >= 0 and <= 30 ? rate : 7;
    }

    // _tax_summary(): some forms summarize tax/total in a separate table, e.g.
    // "Tax  Tax  Total Order" followed by "7.000  11,200.00  171,200.00" — returns (vat, total)
    // from the number row under a header containing "Total"+"Order" together.
    public static (double Vat, double Total) TaxSummary(string text)
    {
        var lines = TextHelpers.SplitLines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"Total\s*Order|Order\s*Total", RegexOptions.IgnoreCase))
            {
                for (var j = i + 1; j < Math.Min(lines.Length, i + 3); j++)
                {
                    var nums = Regex.Matches(lines[j], AmountFinder.Num).Select(m => m.Value).ToList();
                    if (nums.Count >= 2)
                    {
                        var vat = TextHelpers.F(nums[^2]);
                        var total = TextHelpers.F(nums[^1]);
                        if (total > 0) return (vat, total);
                    }
                }
            }
        }
        return (0.0, 0.0);
    }

    // _partner_tax(): counterparty's tax id — skips our own company's tax id.
    public static string PartnerTax(string text, string ownTaxId)
    {
        var own = Regex.Replace(ownTaxId ?? "", @"\D", "");
        foreach (Match m in Regex.Matches(text, CompanyFinder.TaxLabel, RegexOptions.IgnoreCase))
        {
            var winStart = m.Index + m.Length;
            var window = text.Substring(winStart, Math.Min(250, text.Length - winStart));
            var t = CompanyFinder.Tax13.Match(window);
            if (t.Success)
            {
                var v = Regex.Replace(t.Value, @"\D", "");
                if (v != "" && v != own) return v;
            }
        }
        foreach (Match t in CompanyFinder.Tax13.Matches(Regex.Replace(text, @"[\s\-]", "")))
        {
            var v = Regex.Replace(t.Value, @"\D", "");
            if (v != own) return v;
        }
        return "";
    }

    // _ship_block(): delivery address from the form's right-hand column — empty if the columns
    // couldn't be split (deliberately no fallback to a single-line regex here, since '\s' can
    // cross a newline and jump to an unrelated following line — the caller, ParseText, falls back
    // to the address right under the counterparty's name at the top of the document instead,
    // which is safer).
    public static (string Name, string Addr) ShipBlock(PdfBlocksResult? blocks, IEnumerable<string> ownCompanyKeywords)
    {
        var rows = (blocks?.Right ?? [])
            .Where(r => r.Length > 0 && !Regex.IsMatch(r, @"^(INVOICE\s*ADDRESS|TEL|FAX|E-?MAIL|\*+|REQUIRED|Tax\s*ID)", RegexOptions.IgnoreCase))
            .Select(r => Regex.Replace(r, @"\(\s*\d{6,}\s*\)", "").Trim())
            .Where(r => r.Length > 2)
            .ToList();
        if (rows.Count > 0)
        {
            var name = Regex.Replace(rows[0], @"\s*(PURCHASING\s+OFFICER|ATTN\.?\s.*|TEL\s*[:.].*|CONTACT.*)$", "", RegexOptions.IgnoreCase).Trim();
            var addr = string.Join(" ", rows.Skip(1));
            if (addr.Length > 400) addr = addr[..400];
            if (CompanyFinder.IsOwnCompany(name, ownCompanyKeywords) && rows.Count > 1)
            {
                var left = (blocks?.Left ?? []).Where(r => r.Length > 2).ToList();
                if (left.Count > 0)
                {
                    name = left[0];
                    addr = string.Join(" ", left.Skip(1));
                    if (addr.Length > 400) addr = addr[..400];
                }
            }
            return (name.Length > 200 ? name[..200] : name, addr);
        }
        return ("", "");
    }

    // parse_text(): the main header+lines extraction orchestrator — runs after raw text has been
    // obtained from whichever text-extraction tier (embedded PDF text, Tesseract, Typhoon, ...).
    public static ParsedDocument ParseText(
        string text, string module, PdfBlocksResult? blocks, string provider,
        IEnumerable<string> ownCompanyKeywords, string ownTaxId)
    {
        var h = BlankHeader(module);
        var lines = LineParser.ParseLines(text);

        var total = AmountFinder.FindAmount(text, [
            @"PURCHASE\s*ORDER\s*TOTAL", @"GRAND\s*TOTAL", TextHelpers.Th("รวมทั้งสิ้น"),
            TextHelpers.Th("ยอดรวมสุทธิ"), TextHelpers.Th("จำนวนเงินรวมทั้งสิ้น"), TextHelpers.Th("จำนวนเงินรวมทั้งสิน"),
            @"NET\s*(?:AMOUNT|TOTAL)", @"Total\s*Amount", @"(?<!TAX\s)\bTOTAL\b(?!\s*TAX)",
        ]);
        var sub = AmountFinder.FindAmount(text, [
            TextHelpers.Th("รวมเป็นเงิน"), TextHelpers.Th("มูลค่าสินค้า"), TextHelpers.Th("ราคาสินค้า"), @"Sub\s*-?\s*total",
            @"Amount\s*before", @"TOTAL\s*BEFORE",
        ]);
        var vat = AmountFinder.FindAmount(text, [@"TOTAL\s*TAX", TextHelpers.Th("ภาษีมูลค่าเพิ่ม"), @"\bVAT\b", TextHelpers.Th("ภาษี") + @"\s*7"]);
        var wht = AmountFinder.FindAmount(text, [TextHelpers.Th("หัก") + @"\s*" + TextHelpers.Th("ณ") + @"\s*" + TextHelpers.Th("ที่จ่าย"), @"WHT", @"Withholding"]);
        var sumLines = Math.Round(lines.Sum(l => l.Amount), 2);
        if (vat == 0 && total == 0) (vat, total) = TaxSummary(text); // not found yet -> try a separate tax-summary table
        if (sub == 0) sub = (total != 0 && vat != 0) ? Math.Round(total - vat, 2) : sumLines;
        if (total == 0) total = sub != 0 ? Math.Round(sub + vat, 2) : sumLines;

        var cur = "THB";
        var mc = Regex.Match(text, @"CURRENCY\s*[:\s]\s*([A-Z]{3})|สกุลเงิน\s*[:\s]\s*([A-Z]{3})", RegexOptions.IgnoreCase);
        if (mc.Success) cur = (mc.Groups[1].Success ? mc.Groups[1].Value : mc.Groups[2].Value).ToUpperInvariant();

        var terms = "";
        var mt = Regex.Match(text, @"(?<!INCO)TERMS?\D{0,60}?(\d{1,3})\s*(?:DAYS?|วัน)", RegexOptions.IgnoreCase);
        if (!mt.Success) mt = Regex.Match(text, @"เครดิต\D{0,20}?(\d{1,3})\s*วัน");
        if (!mt.Success) mt = Regex.Match(text, @"(?:TERMS|PAYMENT)\D{0,80}?(\d{1,3})\s*[Dd](?![A-Za-z])", RegexOptions.IgnoreCase); // shorthand European e.g. "120d"
        if (mt.Success)
        {
            terms = mt.Groups[1].Value + " วัน";
        }
        else
        {
            // Requires a number too, to avoid a false positive on plain text e.g. "Terms of payment" -> "of"
            var m2 = Regex.Match(text, @"(?<!INCO)(?:PAYMENT\s*)?TERMS\s*[:\s]\s*(CASH|COD|เงินสด|[A-Z]{1,3}\d{2,4})", RegexOptions.IgnoreCase);
            if (m2.Success)
            {
                terms = m2.Groups[1].Value.Trim();
            }
            else
            {
                // Separator does NOT cross a newline ([ \t]* not \s*) — avoids jumping to an
                // unrelated following sentence/clause, e.g. a contract clause starting "10 ...".
                var m3 = Regex.Match(text, @"\bCond(?:ition)?s?\s*[:\.][ \t]*([^\n]{3,40})", RegexOptions.IgnoreCase);
                if (m3.Success) terms = Regex.Replace(m3.Groups[1].Value, @"\s{2,}", " ").Trim().TrimEnd('.');
            }
        }

        var inco = "";
        var mi = Regex.Match(text, @"INCOTERMS?\s*[:\s]\s*([A-Z]{3}\b[^\n]{0,20})", RegexOptions.IgnoreCase);
        if (mi.Success) inco = mi.Groups[1].Value.Trim();

        if (module == "AP")
        {
            var d = DateFinder.FindDate(text, [TextHelpers.Th("วันที่ใบกำกับภาษี"), TextHelpers.Th("วันที่ใบแจ้งหนี้"), @"INVOICE\s*DATE", @"DATE"]);
            h["invoiceNo"] = AmountFinder.FindDocNo(text, [
                TextHelpers.Th("เลขที่ใบกำกับภาษี"), TextHelpers.Th("เลขที่ใบแจ้งหนี้"), @"INVOICE\s*NO\.?", @"INV\.?\s*NO\.?", TextHelpers.Th("เลขที่"), @"No\.?",
            ]);
            h["invoiceDate"] = d; h["postingDate"] = d;
            h["vendorName"] = CompanyFinder.FindCompany(text, ownCompanyKeywords);
            h["vendorTaxId"] = PartnerTax(text, ownTaxId);
            h["poRef"] = AmountFinder.FindDocNo(text, [@"อ้างอิง\s*PO", @"P\.?O\.?\s*No\.?", @"Purchase\s*Order"]);
            h["currency"] = cur; h["paymentTerms"] = terms;
            h["subTotal"] = sub; h["vatAmount"] = vat; h["whtAmount"] = wht; h["totalAmount"] = total;
            h["vatRate"] = SaneVatRate(vat, sub);
        }
        else
        {
            var due = lines.Where(l => l.DueDate != "").Select(l => l.DueDate).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var (custName, custPos) = CompanyFinder.FindCompanyWithPos(text, ownCompanyKeywords);
            var (shipName, shipAddr) = ShipBlock(blocks, ownCompanyKeywords);
            if (shipName == "" && custName != "")
            {
                // Some forms have no matchable "SHIP TO" label (e.g. it wraps mid-name) — the
                // delivery address is usually the same as the address listed under the
                // counterparty's name at the top of the document.
                shipName = custName;
                shipAddr = CompanyFinder.AddressAfter(TextHelpers.SplitLines(text), custPos);
            }
            var (poNo2, poDate2) = PoNumberDate(text); // "number / code / date" glued format (e.g. Henkel)
            var poNo = poNo2 != "" ? poNo2 : AmountFinder.FindDocNo(text, [
                @"P\.?O\.?\s*No\.?", @"ใบสั่งซื้อเลขที่", @"เลขที่ใบสั่งซื้อ", @"PURCHASE\s*ORDER\s*(?:NO\.?|NUMBER)", @"เลขที่", @"ORDER\s*NO\.?",
            ]);
            var poDate = poDate2 != "" ? poDate2 : DateFinder.FindDate(text, [
                @"PRINT\s*DATE", @"P\.?O\.?\s*DATE", @"ORDER\s*DATE", @"วันที่เอกสาร", @"วันที่",
                // A bare "DATE" label is the last resort — avoids matching "Delivery/Due/Required
                // date" (a different meaning: when the goods should arrive, not the document date).
                @"(?<!Delivery )(?<!Due )(?<!Required )(?<!Ship )(?<!Requested )DATE",
            ]);
            h["poNo"] = poNo; h["poDate"] = poDate;
            h["customerName"] = custName;
            h["customerTaxId"] = PartnerTax(text, ownTaxId);
            h["shipToName"] = shipName; h["shipToAddress"] = shipAddr;
            h["deliveryDate"] = due.Count > 0 ? due[0] : DateFinder.FindDate(text, [
                @"DELIVERY\s*DATE", @"DUE\s*DATE", @"REQUIRED\s*DATE", @"วันที่ส่งของ", @"กำหนดส่ง",
            ]);
            h["currency"] = cur; h["paymentTerms"] = terms; h["incoterms"] = inco;
            h["subTotal"] = sub; h["vatAmount"] = vat; h["totalAmount"] = total;
        }

        var filled = h.Count(kv => (Convert.ToString(kv.Value) ?? "").Trim() is not ("" or "0" or "0.0"));
        var conf = 0.35 + 0.03 * filled + Math.Min(0.2, 0.05 * lines.Count);
        if (provider == "ocr") conf -= 0.1; // OCR text is noisier than text embedded in the file
        var rawText = text.Length > 20000 ? text[..20000] : text;

        return new ParsedDocument
        {
            Header = h, Lines = lines,
            Confidence = Math.Round(Math.Min(Math.Max(conf, 0.1), 0.9), 2),
            Provider = provider, RawText = rawText,
        };
    }
}
