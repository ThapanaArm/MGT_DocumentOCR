using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

public record ZohoLastPrice(string MaterialCode, decimal Price, string? UpdateDate, string? SalesOrderName);

/// <summary>
/// Zoho CRM "Last Prices" module (29 Oct 2026) -- the last price each customer paid for each
/// Material. Zoho itself creates/updates a row when a Sales Order turns Complete (manual
/// AO-CRM-UM-2026-003 section 5.10, pages 305-312): Last_Price, Account_Name (Lookup),
/// Account_Code, Material_Name (Lookup), Material_Code, Update_Date, Sales_Order_Name (Lookup).
/// Read-only here: the OCR flow shows it next to the document's price and copies it into the
/// Last_Price column of the Deal / Quote / Sales Order items it writes.
/// </summary>
public class ZohoLastPriceClient(ZohoClient zoho)
{
    private string? _module;

    private async Task<string> ResolveModuleAsync() =>
        _module ??= await ZohoRecordFields.ResolveModuleAsync(zoho, "Last Prices", "Last Price", "Last_Prices");

    /// <summary>Last price per Material Code (upper-cased key) for one customer, newest Update_Date
    /// first when Zoho holds more than one row for the same Material.</summary>
    public async Task<Dictionary<string, ZohoLastPrice>> FindForAccountAsync(string accountCode, IEnumerable<string> materialCodes)
    {
        var result = new Dictionary<string, ZohoLastPrice>(StringComparer.OrdinalIgnoreCase);
        var code = (accountCode ?? "").Trim();
        var codes = materialCodes.Select(c => (c ?? "").Trim()).Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (code.Length == 0 || codes.Count == 0) return result;

        var module = await ResolveModuleAsync();
        static string Q(string s) => s.Replace("'", "''");
        var inList = string.Join(", ", codes.Select(c => $"'{Q(c)}'"));
        // A row typed in by hand may only have the Account / Material lookups set, not the Account Code /
        // Material Code text fields Zoho's own workflow fills -- so match through the lookups too
        // (COQL lookup traversal). Falls back to the plain text-field query if Zoho refuses that.
        List<JsonObject> rows;
        try
        {
            rows = await zoho.QueryCoqlAsync(module,
                "id, Last_Price, Material_Code, Account_Code, Update_Date, Sales_Order_Name, Material_Name.Product_Code, Account_Name.Account_Code",
                $"((Account_Code = '{Q(code)}' or Account_Name.Account_Code = '{Q(code)}') and (Material_Code in ({inList}) or Material_Name.Product_Code in ({inList})))",
                "Update_Date desc");
        }
        catch
        {
            rows = await zoho.QueryCoqlAsync(module, "id, Last_Price, Material_Code, Account_Code, Update_Date, Sales_Order_Name",
                $"(Account_Code = '{Q(code)}' and Material_Code in ({inList}))", "Update_Date desc");
        }
        foreach (var r in rows)
        {
            var mat = new[] { ZohoRecordFields.Str(r, "Material_Code"), ZohoRecordFields.Str(r, "Material_Name.Product_Code") }
                .Select(x => (x ?? "").Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            if (mat.Length == 0 || result.ContainsKey(mat)) continue; // newest row first
            if (ZohoDealClient.Dec(r, "Last_Price") is not decimal price) continue;
            result[mat] = new ZohoLastPrice(mat, price, ZohoRecordFields.Str(r, "Update_Date"),
                ZohoDealClient.LookupName(r["Sales_Order_Name"]));
        }
        return result;
    }
}
