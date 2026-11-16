using MgtOcr.Core;

namespace MgtOcr.Api.Export;

/// <summary>
/// A supplier's abbreviation — "KCC", "PROMPT", "KIMEX" — the word Accounting puts in front of
/// the invoice number in every item text ("KCC,INV.KCCCI260715-012"). It is typed by the user on
/// the Vendor card and kept in ocr.Vendor.ShortName (sql/37_vendor_short_name.sql); cutting the
/// first word off the supplier's name, which is what the texts used before, is only the fallback
/// for a supplier that has no abbreviation yet (and such a document cannot be sent).
///
/// The controller puts the abbreviations on the document header before the builders run:
/// <see cref="MainKey"/> for the document's own supplier and <see cref="MapKey"/> for every
/// supplier code / SAP code / Tax ID in the master, so a tax row from another issuer resolves too.
/// </summary>
public static class SupplierShortNames
{
    public const string MainKey = "vendorShortName";
    public const string MapKey = "supplierShortNames";
    public const int MaxLength = 20;

    /// <summary>Writes the abbreviations onto the header. Returns the main supplier's ("" = none).</summary>
    public static string Apply(Dictionary<string, object?> header,
        IEnumerable<Dictionary<string, object?>> vendors, Dictionary<string, object?>? mainVendor)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vendors)
        {
            var abbr = Clean(v.GetStr("ShortName"));
            if (abbr.Length == 0) continue;
            foreach (var key in new[] { v.GetStr("VendorCode"), v.GetStr("SapVendorCode"), v.GetStr("TaxId") })
                if (key.Trim().Length > 0) map[key.Trim()] = abbr;
        }
        header[MapKey] = map;
        var main = Clean(mainVendor.GetStr("ShortName"));
        header[MainKey] = main;
        return main;
    }

    /// <summary>The abbreviation for a code or Tax ID, "" when the master has none.</summary>
    public static string Lookup(Dictionary<string, object?>? header, params string?[] keys)
    {
        if (header.Get(MapKey) is not System.Collections.IDictionary map) return "";
        foreach (var k in keys)
        {
            var key = (k ?? "").Trim();
            if (key.Length == 0) continue;
            foreach (System.Collections.DictionaryEntry e in map)
                if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)
                    && Clean(e.Value?.ToString()) is { Length: > 0 } a)
                    return a;
        }
        return "";
    }

    /// <summary>The document's own supplier: its abbreviation, else the first word of its name.</summary>
    public static string Main(Dictionary<string, object?> header) =>
        Clean(header.GetStr(MainKey)) is { Length: > 0 } a ? a : FirstWord(header.GetStr("vendorName"));

    /// <summary>"PROMPT LOGISTICS CO.,LTD" -> "PROMPT" — the old rule, kept only as the fallback.</summary>
    public static string FirstWord(string? name)
    {
        var n = (name ?? "").Trim();
        var cut = n.IndexOfAny([' ', ',']);
        return (cut > 0 ? n[..cut] : n).Trim().ToUpperInvariant();
    }

    public static string Clean(string? abbr)
    {
        var a = (abbr ?? "").Trim().ToUpperInvariant();
        return a.Length > MaxLength ? a[..MaxLength] : a;
    }

    /// <summary>Texts that were generated before the abbreviation existed start with the first word
    /// of the name ("KOREA,INV.…"); they are moved over to the abbreviation ("KCC,INV.…"). A text
    /// somebody typed that does not start that way is left alone.</summary>
    public static void RewriteGeneratedTexts(Dictionary<string, object?> header)
    {
        var abbr = Clean(header.GetStr(MainKey));
        var old = FirstWord(header.GetStr("vendorName"));
        if (abbr.Length == 0 || old.Length == 0 || abbr == old) return;
        string Fix(string t) => t.StartsWith(old + ",INV.", StringComparison.OrdinalIgnoreCase)
            ? abbr + t[old.Length..]
            : t;
        if (header.GetStr("headerText") is { Length: > 0 } ht) header["headerText"] = Fix(ht);
        if (header.Get("glItems") is System.Collections.IEnumerable rows and not string)
            foreach (var r in rows.OfType<Dictionary<string, object?>>())
                if (r.GetStr("itemText") is { Length: > 0 } it) r["itemText"] = Fix(it);
    }
}
