using System.Globalization;
using System.Text.RegularExpressions;

namespace MgtOcr.Zoho;

/// <summary>
/// Subject of the Quote and the Sales Orders the OCR flow creates in Zoho (Megachem, 19 Nov 2026):
/// <list type="bullet">
/// <item>Quote: <c>AccountName_MMYY</c></item>
/// <item>Sales Order with the same materials as its Deal (Deal A,B,C / SO A,B,C -- quantities not
/// compared): <c>AccountName_MMYY_All_Items</c></item>
/// <item>Sales Order with only part of the Deal (Deal A,B,C / SO A,B): <c>AccountName_MMYY_Bill1</c>,
/// then Bill2, Bill3... counted on across every Sales Order of that Deal in Zoho.</item>
/// </list>
/// MMYY = the month the record is created, Thai time (Gregorian). Zoho's Subject holds 50 characters,
/// so a long Account Name is shortened -- the _MMYY / _All_Items / _BillN ending is always kept.
/// </summary>
public static class ZohoSubjects
{
    public const int MaxLength = 50;
    public const string AllItemsSuffix = "All_Items";

    private static readonly Regex BillRe = new(@"_Bill(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>MMYY of the given time (default: now), in Thai time.</summary>
    public static string MonthTag(DateTimeOffset? at = null) =>
        (at ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(7)).ToString("MMyy", CultureInfo.InvariantCulture);

    /// <summary><c>AccountName_MMYY</c>, plus <c>_suffix</c> when given; fitted to 50 characters.</summary>
    public static string Build(string accountName, string monthTag, string? suffix = null)
    {
        var tail = "_" + monthTag + (string.IsNullOrEmpty(suffix) ? "" : "_" + suffix);
        var account = (accountName ?? "").Trim();
        var room = MaxLength - tail.Length;
        if (account.Length > room) account = account[..Math.Max(0, room)].TrimEnd();
        return account + tail;
    }

    /// <summary>Quote Subject: <c>AccountName_MMYY</c>.</summary>
    public static string Quote(string accountName, DateTimeOffset? at = null) => Build(accountName, MonthTag(at));

    /// <summary>Sales Order Subject: <c>AccountName_MMYY_All_Items</c> or <c>AccountName_MMYY_Bill{billNo}</c>.</summary>
    public static string SalesOrder(string accountName, bool allItems, int billNo, DateTimeOffset? at = null) =>
        Build(accountName, MonthTag(at), allItems ? AllItemsSuffix : $"Bill{Math.Max(1, billNo)}");

    /// <summary>
    /// The start every Sales Order Subject of this account and month shares (<c>AccountName_MMYY_</c>) --
    /// used to recognise an order an earlier, failed attempt may already have created.
    /// </summary>
    public static string SalesOrderPrefix(string accountName, DateTimeOffset? at = null)
    {
        // Same shortening as SalesOrder(): measured with the longest ending a Subject of this account can get.
        var full = Build(accountName, MonthTag(at), "Bill999");
        return full[..^"Bill999".Length];
    }

    /// <summary>
    /// True when the Sales Order holds exactly the Deal's materials (same set of Material Codes, trimmed,
    /// case-insensitive; quantities and repeated lines do not matter). A Deal without coded items never
    /// counts as "all items".
    /// </summary>
    public static bool SameMaterials(IEnumerable<string?> salesOrderCodes, IEnumerable<string?> dealCodes)
    {
        static HashSet<string> Set(IEnumerable<string?> codes) => codes
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deal = Set(dealCodes);
        return deal.Count > 0 && Set(salesOrderCodes).SetEquals(deal);
    }

    /// <summary>Highest N among Subjects ending in <c>_BillN</c> (0 when there is none).</summary>
    public static int MaxBillNo(IEnumerable<string?> subjects) => subjects
        .Select(s => s is null ? null : BillRe.Match(s))
        .Where(m => m is { Success: true })
        .Select(m => int.TryParse(m!.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
        .DefaultIfEmpty(0)
        .Max();
}
