using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

public static class LineParser
{
    public static readonly HashSet<string> Uoms = new(StringComparer.Ordinal)
    {
        "KG", "G", "L", "ML", "M", "PC", "PCS", "EA", "BAG", "DRUM", "SET", "BOX", "TON",
        "AU", "กก.", "กรัม", "ลิตร", "ชิ้น", "ถุง", "ถัง", "กล่อง", "ตัน",
        // Service units common in Non-Trade invoices (charges/rentals, not counted goods)
        "งาน", "นาย", "เดือน", "ครั้ง", "คน", "ชม.", "วัน", "สัญญา",
    };
    public static readonly Dictionary<string, string> UomAlias = new()
    {
        ["KILOGRAM"] = "KG", ["KILOGRAMS"] = "KG", ["KILOGRAMME"] = "KG", ["KILOGRAMMES"] = "KG",
        ["LITER"] = "L", ["LITERS"] = "L", ["LITRE"] = "L", ["LITRES"] = "L",
        ["GRAM"] = "G", ["GRAMS"] = "G", ["PIECE"] = "EA", ["PIECES"] = "EA", ["UNIT"] = "EA", ["UNITS"] = "EA",
    };

    // Allows a leading '-' (e.g. a negative discount token "-48.00").
    public static readonly Regex NumToken = new(@"^-?[\d,]+(?:\.\d+)?$", RegexOptions.Compiled);
    public static readonly Regex CodeToken = new(@"^[A-Z0-9][A-Z0-9\-_/.]{3,24}$", RegexOptions.Compiled);

    // Words indicating a total/summary/table-header/signature-block line, not a real line item —
    // used both to filter candidate rows and to filter candidate description lines. Thai literals
    // always wrapped with Th() since OCR often inserts spaces between characters (e.g. "ร ว ม").
    public static readonly string LineSkip =
        TextHelpers.Th("รวม") + "|Total|" + TextHelpers.Th("ภาษี") + "|VAT|Sub\\s*-?\\s*total|" + TextHelpers.Th("ยอด") +
        "|Grand|^Item\\b|Description\\b|" + TextHelpers.Th("รายละเอียด") + "|^" + TextHelpers.Th("จำนวน") + @"\b|^" +
        TextHelpers.Th("ราคา") + @"\b|" + TextHelpers.Th("หน่วย") + "|" + TextHelpers.Th("ลำดับที่") + "|Quantity\\b|Unit\\s*Price\\b" +
        "|Withholding|" + TextHelpers.Th("หัก") + @"\s*" + TextHelpers.Th("ณ") + @"\s*" + TextHelpers.Th("ที่จ่าย") +
        @"|^Amount\b|^Net\s*Payment\b|^" + TextHelpers.Th("จำนวนเงิน") + @"\b|^Date\b|^" + TextHelpers.Th("วันที่") + @"\b" +
        @"|Signature|" + TextHelpers.Th("ลงนาม") + "|" + TextHelpers.Th("ผู้มีอำนาจ");

    // Lines with a single lone number that are usually NOT an amount — registration/postal/phone
    // numbers, addresses (deliberately excludes "ประจำเดือน"/"ปี" since those appear in genuine
    // service-charge descriptions too, e.g. "ค่าบริการ...ประจำเดือนพฤษภาคม", a real line to keep).
    public static readonly Regex NotAmountContext = new(
        TextHelpers.Th("เลขประจำตัว") + "|" + TextHelpers.Th("เลขทะเบียน") + @"|Tax\s*ID|" + TextHelpers.Th("ที่อยู่") + "|" + TextHelpers.Th("แขวง") +
        "|" + TextHelpers.Th("เขต") + "|" + TextHelpers.Th("ถนน") + "|" + TextHelpers.Th("ซอย") + "|" + TextHelpers.Th("หมู่") + "|" + TextHelpers.Th("โทร") +
        @"|Tel\b|Fax\b|E-?mail|www\.|" + TextHelpers.Th("เลขที่") + @"|No\.|" + TextHelpers.Th("รหัส") + @"|Ref\.|Branch|" +
        TextHelpers.Th("สาขา"), RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A number that "looks like" real money — needs a thousands separator or exactly-2-decimal
    // ending, and not too long — guards against tax-IDs/postal codes/phone numbers/Buddhist years,
    // which are unformatted digit runs.
    public static bool LooksLikeMoney(string v)
    {
        var digitsOnly = Regex.Replace(v, @"\D", "");
        if (digitsOnly.Length > 9) return false;
        return v.Contains(',') || Regex.IsMatch(v, @"\.\d{2}$");
    }

    // Lines that "restate" an amount with its label on the same line (e.g. "Amount 1,200.00" or
    // "จำนวนเงิน 1,200.00") — common in receipts where the label/value sit in their own boxes.
    private static readonly Regex AmountRestatement = new(
        @"^(?:Amount|Net\s*Payment|" + TextHelpers.Th("จำนวนเงิน") + @")\b[:\-\s]*([\d,]+\.\d{2})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Column-header labels that sometimes land on the same line as the real description (not a
    // standalone header row, which LineSkip already handles) — strip before using as a description.
    private static readonly Regex DescLabelPrefix = new(
        @"^(?:" + TextHelpers.Th("รายการสินค้าหรือบริการ") + "|" + TextHelpers.Th("รายละเอียด") +
        @"|Description(?:\s+of\s+goods?\s*/?\s*service)?)\s*[:\-]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // parse_lines(): find product/service rows — a line with >= 3 trailing numbers (qty/price/amount).
    // Works at the token level so unit/number stripping never eats into the middle of a word.
    // Handles forms with extra inserted columns (packing/discount/reference/date) by searching for
    // a plausible (qty x price = amount) set among ALL numbers in the row, instead of guessing a
    // fixed position.
    public static List<LineItem> ParseLines(string text)
    {
        var rawLines = TextHelpers.SplitLines(text);
        var outList = new List<LineItem>();

        for (var li = 0; li < rawLines.Length; li++)
        {
            var toks = TextHelpers.SplitWs(rawLines[li]);
            if (toks.Length < 4) continue;
            var s = string.Join(" ", toks);
            if (Regex.IsMatch(s, LineSkip, RegexOptions.IgnoreCase)) continue;

            // Strip a leading row-sequence-number token before searching for (qty x price = amount)
            // e.g. "1 ค่าบริการล้าง... 9,891.00 9,891.00" — otherwise the search mistakes "1" (the
            // sequence number) for "qty", since 1 x anything trivially equals that same value,
            // consuming the leftmost slot and leaving nothing for the description.
            if (toks.Length >= 5 && Regex.IsMatch(toks[0], @"^\d{1,3}$") && !NumToken.IsMatch(toks[1]))
                toks = toks[1..];

            var idx = new List<int>();
            for (var i = 0; i < toks.Length; i++) if (NumToken.IsMatch(toks[i])) idx.Add(i);
            if (idx.Count < 3) continue;

            // Search for (qty x price = amount) — start from the RIGHTMOST number first, since the
            // amount column sits at the far right in almost every form; this avoids false positives
            // from unrelated numbers that happen to multiply out close to a value (e.g. sequence
            // number x price ~ a number embedded in the middle of the product name). Besides
            // "multiply", also try "subtract" (full charge - discount = net), common in
            // service/telecom bills, e.g. 'ค่าบริการ YTEL 1234  78.00  -48.00  30.00' (78-48=30).
            (int A, int B, int C)? triple = null;
            var rel = "mul";
            for (var c = idx.Count - 1; c >= 0; c--)
            {
                var amt = TextHelpers.F(toks[idx[c]]);
                // Guard against abnormally long numbers (13-digit tax IDs/phone numbers/reference
                // codes) being mistaken for an amount.
                if (amt == 0 || Regex.Replace(toks[idx[c]], @"\D", "").Length > 9) continue;

                (int A, int B)? found = null;
                var frel = "mul";
                for (var a = 0; a < c && found == null; a++)
                {
                    for (var b = a + 1; b < c; b++)
                    {
                        var q = TextHelpers.F(toks[idx[a]]);
                        var p = TextHelpers.F(toks[idx[b]]);
                        var tolMul = Math.Max(0.5, Math.Abs(amt) * 0.01);
                        // Subtraction-relation tolerance capped at 2 baht absolute (unlike
                        // multiplication) — the difference between two large numbers (e.g. 2569
                        // and 18) can easily land inside 1% of a large amount by pure coincidence,
                        // even when unrelated (e.g. a Buddhist-era year that leaked in from a date
                        // in the same row).
                        var tolSub = Math.Min(tolMul, 2.0);
                        if (q > 0 && p > 0 && Math.Abs(q * p - amt) <= tolMul) { found = (idx[a], idx[b]); frel = "mul"; break; }
                        if (q > 0 && Math.Abs(q - p - amt) <= tolSub) { found = (idx[a], idx[b]); frel = "sub"; break; } // full - discount(+) = net
                        if (q > 0 && p < 0 && Math.Abs(q + p - amt) <= tolSub) { found = (idx[a], idx[b]); frel = "sub"; break; } // full + discount(already -) = net
                    }
                }
                if (found != null) { triple = (found.Value.A, found.Value.B, idx[c]); rel = frel; break; }
            }
            if (triple == null) continue;

            var (iQty, iPrice, iAmt) = triple.Value;
            double qty, price, amount;
            if (rel == "mul")
            {
                qty = TextHelpers.F(toks[iQty]); price = TextHelpers.F(toks[iPrice]); amount = TextHelpers.F(toks[iAmt]);
            }
            else
            {
                // Subtraction relation (charge - discount = net): treat as 1 unit, full price =
                // charge, amount = net after discount.
                qty = 1.0; price = TextHelpers.F(toks[iQty]); amount = TextHelpers.F(toks[iAmt]);
            }

            // Unit: look right after the qty column (or anywhere in the row if not found there) —
            // supports full-word units too (e.g. "Kilogram").
            string UomOf(string t)
            {
                var u = t.ToUpperInvariant();
                return UomAlias.TryGetValue(u, out var alias) ? alias : (Uoms.Contains(u) ? u : "");
            }
            var uom = toks.Skip(iQty + 1).Select(UomOf).FirstOrDefault(u => u != "")
                      ?? toks.Select(UomOf).FirstOrDefault(u => u != "") ?? "";

            // Due date: any date token anywhere in the row.
            var due = "";
            foreach (var t in toks)
            {
                if (TextHelpers.FullMatch(DateFinder.DateRe, t))
                {
                    var d = DateFinder.FindDates(t);
                    if (d.Count > 0) { due = d[0]; break; }
                }
            }

            // Description = every token before the first numeric column — strip a leading
            // sequence-number/date/pack-size (only strip PURE numbers at the very start, so a
            // number embedded mid-name like "MEK 99.5 PCT" is untouched since 99.5 isn't first).
            var minIdx = new[] { iQty, iPrice, iAmt }.Min();
            var rest = toks[..minIdx].Where(t => t != "|").ToList(); // strip Typhoon OCR's column separator
            while (rest.Count > 0 && (TextHelpers.FullMatch(DateFinder.DateRe, rest[0]) || NumToken.IsMatch(rest[0])))
                rest.RemoveAt(0);

            var code = "";
            if (rest.Count > 0 && CodeToken.IsMatch(rest[0]) && !TextHelpers.FullMatch(DateFinder.DateRe, rest[0]) &&
                (Regex.IsMatch(rest[0], "[-_/]") || Regex.IsMatch(rest[0], @"^[A-Z]{1,5}\d{2,}$")))
            {
                code = rest[0]; rest.RemoveAt(0);
            }
            var desc = string.Join(" ", rest);
            if (desc.Length > 300) desc = desc[..300];

            if (desc.Length == 0)
            {
                // Some forms put code+name on the line BEFORE the numbers row, e.g.
                // "00001  200338  Cetearyl alcohol 1618" then "11,000.000 Kilogram ...".
                for (var j = li - 1; j >= Math.Max(0, li - 2); j--)
                {
                    var pt = rawLines[j].Trim();
                    if (pt.Length == 0 || Regex.IsMatch(pt, LineSkip, RegexOptions.IgnoreCase)) break;
                    var ptoks = TextHelpers.SplitWs(pt).ToList();
                    if (ptoks.Count > 1 && Regex.IsMatch(ptoks[0], @"^0*\d{1,6}$")) ptoks.RemoveAt(0); // sequence number
                    if (code.Length == 0 && ptoks.Count > 0 && Regex.IsMatch(ptoks[0], "^[A-Z0-9]{4,15}$", RegexOptions.IgnoreCase))
                    { code = ptoks[0]; ptoks.RemoveAt(0); }
                    if (ptoks.Count > 0)
                    {
                        desc = string.Join(" ", ptoks);
                        if (desc.Length > 300) desc = desc[..300];
                    }
                    break;
                }
            }
            if (desc.Length == 0)
            {
                // Some forms put the product description on the NEXT line (code+numbers on one
                // line, product name on another).
                for (var j = li + 1; j < Math.Min(rawLines.Length, li + 3); j++)
                {
                    var nt = rawLines[j].Trim();
                    if (nt.Length == 0 || Regex.IsMatch(nt, LineSkip, RegexOptions.IgnoreCase)) break;
                    var digitRatio = (double)nt.Count(char.IsDigit) / Math.Max(1, nt.Length);
                    if (digitRatio > 0.3) break; // the next line is a new item row, not a description
                    desc = nt.Length > 300 ? nt[..300] : nt;
                    break;
                }
            }
            else
            {
                // Some forms wrap the description onto a new line because the product name is
                // longer than the column width, e.g. "ค่าบริการล้าง 1.00 EA 9,891.00 9,891.00"
                // followed by "เครื่องปรับอากาศ(6/6)" — a continuation, not a new item. Append it
                // if the following line is short text with no amount of its own.
                for (var j = li + 1; j < Math.Min(rawLines.Length, li + 3); j++)
                {
                    var nt = rawLines[j].Trim();
                    if (nt.Length == 0) continue;
                    if (Regex.IsMatch(nt, LineSkip, RegexOptions.IgnoreCase) || NotAmountContext.IsMatch(nt)) break;
                    if (TextHelpers.SplitWs(nt).Any(t => NumToken.IsMatch(t) && LooksLikeMoney(t))) break; // has its own amount -> new row
                    desc = (TextHelpers.CleanDesc(desc) + TextHelpers.CleanDesc(nt));
                    if (desc.Length > 300) desc = desc[..300];
                    break;
                }
            }
            desc = TextHelpers.CleanDesc(desc);

            outList.Add(new LineItem
            {
                ExtCode = code, Desc = desc, Qty = qty, DueDate = due,
                Uom = uom != "" ? uom : "EA", Price = price, Amount = amount,
            });
        }

        if (outList.Count == 0) outList = ParseSingleAmountLines(rawLines);
        if (outList.Count == 0) outList = ParseDescThenAmountLines(rawLines);
        return outList.Count > 60 ? outList[..60] : outList;
    }

    // _parse_single_amount_lines(): some service (Non-Trade) invoices are just "description + a
    // single amount", with no separate qty/unit-price columns, e.g.
    // 'ค่าบริการพนักงานรับ-ส่งเอกสาร ประจำเดือน พฤษภาคม 2569  24,000.00'. Only used as a fallback
    // when the primary pass (find a qty x price = amount set) found nothing at all — much
    // stricter than the primary pass, since there's no other column to confirm the number is real.
    public static List<LineItem> ParseSingleAmountLines(string[] rawLines)
    {
        var outList = new List<LineItem>();
        foreach (var ln in rawLines)
        {
            var toks = TextHelpers.SplitWs(ln);
            if (toks.Length < 2) continue;
            var s = string.Join(" ", toks);
            if (Regex.IsMatch(s, LineSkip, RegexOptions.IgnoreCase) || NotAmountContext.IsMatch(s)) continue;

            var money = toks.Select((t, i) => (i, t)).Where(x => NumToken.IsMatch(x.t) && LooksLikeMoney(x.t)).ToList();
            if (money.Count != 1) continue; // exactly one "looks-like-money" number allowed (sequence numbers/years don't count)
            var (iAmt, tok) = money[0];
            var amt = TextHelpers.F(tok);
            if (amt < 10) continue;

            var desc = string.Join(" ", toks[..iAmt]).Trim();
            var m = Regex.Match(desc, @"^0*(\d{1,3})[.)\s]\s*(.+)$"); // strip a leading sequence number, e.g. "1 ค่าบริการ..." or "1. ค่าบริการ..."
            if (m.Success) desc = m.Groups[2].Value;
            if (desc.Length < 6 || Regex.Replace(desc, "[^ก-๙A-Za-z]", "").Length < 4) continue; // needs real letters, not symbols/digits alone

            var clean = TextHelpers.CleanDesc(desc);
            outList.Add(new LineItem
            {
                ExtCode = "", Desc = clean.Length > 300 ? clean[..300] : clean, Qty = 1.0, DueDate = "",
                Uom = "EA", Price = amt, Amount = amt,
            });
        }
        return outList.Count > 60 ? outList[..60] : outList;
    }

    // _parse_desc_then_amount_lines(): some receipts/tax invoices (e.g. Intertek) place
    // "DESCRIPTION"/"AMOUNT" as separate boxed headers, followed by the real values on different
    // lines (different positions on the page) — so description and amount never share a line,
    // unlike ParseSingleAmountLines. Pairs a bare-amount line (or a "label + amount" line like
    // "Amount 1,200.00", which LineSkip deliberately prevents from becoming its own item) with the
    // nearest preceding normal-text line (skipping labels/blank lines). Last-resort fallback — the
    // strictest of all, since there's no other context confirming the pairing is correct.
    public static List<LineItem> ParseDescThenAmountLines(string[] rawLines)
    {
        var outList = new List<LineItem>();
        var usedDescAt = new HashSet<int>();
        for (var i = 0; i < rawLines.Length; i++)
        {
            var toks = TextHelpers.SplitWs(rawLines[i]);
            double? amt = null;
            if (toks.Length == 1 && NumToken.IsMatch(toks[0]) && LooksLikeMoney(toks[0]))
            {
                amt = TextHelpers.F(toks[0]);
            }
            else
            {
                var m2 = AmountRestatement.Match(rawLines[i].Trim());
                if (m2.Success) amt = TextHelpers.F(m2.Groups[1].Value);
            }
            if (amt is null || amt < 10) continue;

            string desc = ""; var descI = -1;
            for (var j = i - 1; j >= Math.Max(-1, i - 6) && j >= 0; j--) // look back up to 5 lines for the nearest description
            {
                var pt = rawLines[j].Trim();
                if (pt.Length == 0 || usedDescAt.Contains(j)) continue;
                if (NotAmountContext.IsMatch(pt)) continue;
                var pt2 = DescLabelPrefix.Replace(pt, "").Trim(); // strip a column-header label that may prefix the real description
                if (Regex.IsMatch(pt2, LineSkip, RegexOptions.IgnoreCase)) continue;
                if (NumToken.IsMatch(pt2.Replace(" ", "")) || Regex.Replace(pt2, "[^ก-๙A-Za-z]", "").Length < 4) continue; // skip pure-number/symbol lines
                desc = pt2; descI = j; break;
            }
            if (desc.Length == 0) continue;
            usedDescAt.Add(descI);
            var clean = TextHelpers.CleanDesc(desc);
            outList.Add(new LineItem
            {
                ExtCode = "", Desc = clean.Length > 300 ? clean[..300] : clean, Qty = 1.0, DueDate = "",
                Uom = "EA", Price = amt.Value, Amount = amt.Value,
            });
        }
        return outList.Count > 60 ? outList[..60] : outList;
    }
}
