using System.Globalization;
using System.Text.RegularExpressions;
using MgtOcr.Core;
using MgtOcr.Sap;

namespace MgtOcr.Api.Export;

public record GoodsInvoiceTaxDecision(string Code = "", string Issue = "",
    List<Dictionary<string, object?>>? TaxRows = null);

internal static class GoodsInvoiceTax
{
    public static bool Applies(Dictionary<string, object?> doc) =>
        doc.GetStr("module") == "AP" && SapImportSupplierInvoiceExcel.IsPoKeyed(doc)
        && (doc.Get("header") as Dictionary<string, object?>).GetStr("refDocType").Trim() is not ("2" or "3");

    public static GoodsInvoiceTaxDecision Resolve(Dictionary<string, object?> doc, string vendor,
        SapSupplierTaxProfile profile)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        var country = profile.Country?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(country))
            return new(Issue: $"SAP has no unambiguous main address country for supplier {vendor}.");

        var goodsBase = ((IEnumerable<Dictionary<string, object?>>)doc["lines"]!)
            .Where(line => line.GetStr("extCode").Trim() is not ("VAT" or "WHT" or "DUTY"))
            .Sum(line => Amount(line.Get("amount")));
        var taxRows = DeferredVatReversal.EffectiveTaxRows(doc);
        var statedVat = Amount(header.Get("vatAmount"));
        var vatRows = taxRows.Where(row => Amount(row.Get("docCurrencyAmt")) > 0).ToList();
        if (vatRows.Any(DeferredVatReversal.IsImportVat))
            return new(Issue: "Import VAT belongs on the Customs document. Review the VAT rows for this goods invoice.");

        if (country != "TH")
            return statedVat > 0 || vatRows.Count != 0
                ? new(Issue: $"SAP lists supplier {vendor} in {country}, but the goods invoice shows VAT. Verify the supplier and invoice.")
                : new("VX");

        var registeredIds = profile.TaxNumbers
            .Where(tax => Regex.IsMatch(tax.Type.Trim(), @"^(?:9)?TH[0-9]+$", RegexOptions.IgnoreCase))
            .Select(tax => ThaiTaxId(tax.Number)).Where(id => id.Length > 0).ToHashSet();
        var invoiceTaxId = ThaiTaxId(header.GetStr("vendorTaxId"));
        var issuerIds = vatRows.Select(row => ThaiTaxId(row.GetStr("issuerTaxId")))
            .Where(id => id.Length > 0).Distinct().ToList();
        if (invoiceTaxId.Length == 0 && issuerIds.Count == 1) invoiceTaxId = issuerIds[0];
        if (!registeredIds.Contains(invoiceTaxId) || issuerIds.Any(id => id != invoiceTaxId))
            return new(Issue: $"The Thai tax ID on the invoice could not be matched to SAP supplier {vendor}.");

        if (statedVat == 0 && vatRows.Count == 0 && header.Get("vatAmount") is not null
            && header.GetStr("vatAmount").Trim().Length > 0)
            return new("VX");
        if (vatRows.Any(row => row.GetStr("vendorCode").Trim().TrimStart('0') is { Length: > 0 } code
            && !string.Equals(code, vendor.Trim().TrimStart('0'), StringComparison.OrdinalIgnoreCase)))
            return new(Issue: "A VAT row belongs to another supplier. Review the supplier and tax rows.");
        var invoiceBase = Amount(header.Get("subTotal"));
        if (vatRows.Count == 0 && statedVat > 0 && invoiceBase > 0)
        {
            var fromInvoice = new Dictionary<string, object?>
            {
                ["label"] = "Input VAT",
                ["drCr"] = "S",
                ["docCurrencyAmt"] = statedVat,
                ["baseAmount"] = invoiceBase,
                ["taxRate"] = 7,
                ["taxDocNo"] = header.Get("invoiceNo"),
                ["taxDocDate"] = header.Get("invoiceDate"),
                ["issuerName"] = header.Get("vendorName"),
            };
            taxRows.Add(fromInvoice);
            vatRows.Add(fromInvoice);
        }
        if (vatRows.Count == 0 || goodsBase <= 0)
            return new(Issue: "Verify the goods amount and the VAT amount on the supplier invoice.");

        var vatBase = vatRows.Sum(row => Amount(row.Get("baseAmount")));
        var vat = vatRows.Sum(row => Amount(row.Get("docCurrencyAmt")));
        var difference = vatBase - goodsBase;
        if (Math.Abs(difference) > 0.01m)
            return new(Issue: $"VAT base {vatBase:F2} differs from goods amount {goodsBase:F2} by {difference:F2}.");
        if (vatRows.Any(row => row.GetStr("taxCode").Trim().ToUpperInvariant() is not ("" or "VX" or "V1")
            || row.GetStr("taxKind").Trim().Equals("DEFERRED", StringComparison.OrdinalIgnoreCase)
            || Math.Abs(Amount(row.Get("baseAmount")) * 0.07m - Amount(row.Get("docCurrencyAmt"))) > 0.01m)
            || (statedVat > 0 && Math.Abs(statedVat - vat) > 0.01m))
            return new(Issue: "The supplier tax rows do not match input VAT at 7%. Review the tax code, VAT amount and base.");

        // Update the real invoice tax rows so MIRO receives one explicit VAT amount, even if OCR seeded VX.
        var normalized = taxRows.Select(row => vatRows.Contains(row)
            ? new Dictionary<string, object?>(row)
            {
                ["taxCode"] = "V1", ["taxKind"] = "INPUT", ["taxRate"] = "7",
                ["issuerTaxId"] = invoiceTaxId, ["vendorCode"] = vendor,
            }
            : row).ToList();
        return new("V1", TaxRows: normalized);
    }

    private static string ThaiTaxId(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"[\s-]", "");
        return Regex.IsMatch(normalized, @"^[0-9]{13}$") ? normalized : "";
    }

    private static decimal Amount(object? value) =>
        decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var amount) ? amount : 0;
}
