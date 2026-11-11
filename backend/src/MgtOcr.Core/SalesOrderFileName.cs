using System.Globalization;

namespace MgtOcr.Core;

public static class SalesOrderFileName
{
    public static string? Build(string? poNumber, string? deliveryDate, string sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(poNumber)
            || !DateOnly.TryParseExact(deliveryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return null;
        var po = poNumber.Trim();
        foreach (var ch in "\"*:<>?/\\|#%\t\r\n") po = po.Replace(ch, '_');
        po = po.Trim().TrimEnd('.');
        if (po.Length == 0) return null;
        // Leave room for the date and extension within the archive's 120-character segment limit.
        if (po.Length > 100) po = po[..100];
        var extension = Path.GetExtension(sourceFileName).ToLowerInvariant();
        return $"{po}_{date.ToString("yyMMdd", CultureInfo.InvariantCulture)}{extension}";
    }
}
