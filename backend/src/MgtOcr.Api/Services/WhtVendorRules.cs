using MgtOcr.Core;
using MgtOcr.Data;
using MgtOcr.Ocr;

namespace MgtOcr.Api.Services;

// Withholding tax a supplier does not print on its invoice (AP, 2026-11-18): S.N.R. IMPORT-EXPORT's
// invoice 0433 shows no WHT, but Accounting withholds 3% on its "Shipping Charge" line only
// (2,700.00 → 81.00, type OA / code 09, PRD 5100000710) — not on the trucking or the expenses paid
// on our behalf. Which line, at which rate, is per supplier, so it lives in Master 7 "Withholding
// Tax" (ocr.WhtMap) as a row with the supplier's Tax ID and the words of that line
// (sql/47_wht_vendor_keyword.sql). The read lists the supplier's own charge lines in
// header.agentCharges; this turns the matching ones into WHT rows the Withholding Tax tab picks up.
// Only rows WITH a supplier Tax ID are used here: the generic rows of the table (HANDLING 3% …) would
// withhold on every forwarder's lines. A WHT the invoice prints itself always wins.
public class WhtVendorRules(Db db)
{
    public async Task ApplyAsync(ParsedDocument pd, string? salesOrg)
    {
        try
        {
            if (pd.Lines.Any(l => string.Equals(l.ExtCode, "WHT", StringComparison.OrdinalIgnoreCase))) return;
            var tax = new string(pd.Header.GetStr("vendorTaxId").Where(char.IsDigit).ToArray());
            if (tax.Length != 13) return;
            if (pd.Header.Get("agentCharges") is not System.Collections.IEnumerable list) return;
            var charges = list.OfType<Dictionary<string, object?>>()
                .Select(c => (Desc: c.GetStr("desc").Trim(), Amount: Num(c.Get("amount"))))
                .Where(c => c.Desc.Length > 0 && c.Amount > 0).ToList();
            if (charges.Count == 0) return;

            var org = (salesOrg ?? "").Trim();
            var rules = (await db.QueryAsync("""
                SELECT IncomeType, Keywords, Rate, WhtType, WhtCode
                FROM ocr.WhtMap
                WHERE IsActive = 1 AND VendorTaxId = @tax AND (SalesOrg IS NULL OR SalesOrg = @org)
                ORDER BY CASE WHEN SalesOrg IS NULL THEN 1 ELSE 0 END, Id
                """, new { tax, org })).ToList();
            var issuer = pd.Header.GetStr("vendorName").Trim();
            var invNo = pd.Header.GetStr("invoiceNo").Trim();
            var added = 0.0;
            foreach (var r in rules)
            {
                var words = ((string?)r.Keywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length == 0) continue;
                var baseAmt = Math.Round(charges.Where(c => words.Any(w => c.Desc.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .Sum(c => c.Amount), 2);
                var rate = Convert.ToDouble(r.Rate ?? 0m);
                if (baseAmt <= 0 || rate <= 0) continue;
                var amt = Math.Round(baseAmt * rate / 100, 2, MidpointRounding.AwayFromZero);
                var type = ((string?)r.WhtType ?? "").Trim();
                var code = ((string?)r.WhtCode ?? "").Trim();
                // "[OA/09]" carries the SAP type/code to the Withholding Tax tab (seedWhtItems).
                var tag = type.Length > 0 && code.Length > 0 ? $" [{type}/{code}]" : "";
                pd.Lines.Add(new LineItem
                {
                    ExtCode = "WHT",
                    Desc = $"Withholding Tax {rate:0.##}%{tag} {(string)r.IncomeType} {issuer} {invNo}".Trim(),
                    Qty = 1, Price = amt, Amount = amt, BaseAmount = baseAmt,
                });
                added += amt;
            }
            if (added > 0)
            {
                pd.Header["whtAmount"] = Math.Round(Num(pd.Header.Get("whtAmount")) + added, 2);
                var note = $"WHT {added:N2} คิดจากกฎของผู้ขายใน Master 7 (ใบนี้ไม่ได้พิมพ์ WHT)";
                pd.ConfidenceNote = pd.ConfidenceNote.Length > 0 ? $"{pd.ConfidenceNote} / {note}" : note;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WhtVendorRules] skipped (has sql/47_wht_vendor_keyword.sql been run?): {ex.Message}");
        }
    }

    private static double Num(object? v) =>
        v switch
        {
            null => 0,
            double d => d,
            int i => i,
            long l => l,
            decimal m => (double)m,
            System.Text.Json.JsonElement j when j.ValueKind == System.Text.Json.JsonValueKind.Number => j.GetDouble(),
            _ => double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Replace(",", ""),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0,
        };
}
