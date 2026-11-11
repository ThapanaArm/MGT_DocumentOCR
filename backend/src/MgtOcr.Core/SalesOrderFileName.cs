using System.Globalization;

namespace MgtOcr.Core;

// MGT Sales Order file name: <PO number>_<delivery date yyMMdd><original extension>, e.g. PO12345_261115.pdf.
// Applied as soon as the order is read (upload / re-read), kept in step when the user edits the PO
// number or delivery date, and reused as the SharePoint archive name.
public static class SalesOrderFileName
{
    public static string? Build(string? poNumber, string? deliveryDate, string sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(poNumber) || !TryParseDate(deliveryDate, out var date)) return null;
        var po = poNumber.Trim();
        foreach (var ch in "\"*:<>?/\\|#%\t\r\n") po = po.Replace(ch, '_');
        po = po.Trim().TrimEnd('.');
        if (po.Length == 0) return null;
        // Leave room for the date and extension within the archive's 120-character segment limit.
        if (po.Length > 100) po = po[..100];
        var extension = Path.GetExtension(sourceFileName).ToLowerInvariant();
        return $"{po}_{date.ToString("yyMMdd", CultureInfo.InvariantCulture)}{extension}";
    }

    /// <summary>Same as <see cref="Build(string?, string?, string)"/>, but when the header has no
    /// delivery date (orders that print a date per line) uses the earliest line date instead.</summary>
    public static string? Build(string? poNumber, string? headerDeliveryDate, IEnumerable<string?> lineDeliveryDates,
        string sourceFileName)
    {
        var date = TryParseDate(headerDeliveryDate, out _)
            ? headerDeliveryDate
            : lineDeliveryDates
                .Select(d => TryParseDate(d, out var x) ? (DateOnly?)x : null)
                .Where(x => x.HasValue).Min()?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Build(poNumber, date, sourceFileName);
    }

    /// <summary>Date whose year names the MGT archive folder "PO.{yyyy}": the PO date, unless it is
    /// missing or more than a year away from the post date (e.g. a Buddhist-era year read as-is),
    /// in which case the post date is used.</summary>
    public static DateTime FolderDate(string? poDate, DateTime postedAt) =>
        TryParseDate(poDate, out var d) && Math.Abs(d.Year - postedAt.Year) <= 1
            ? d.ToDateTime(TimeOnly.MinValue)
            : postedAt;

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
