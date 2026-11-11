using System.Globalization;
using System.Text.RegularExpressions;

namespace MgtOcr.Core.Mapping;

/// <summary>
/// GLC Sales Orders (15 Oct 2026): a customer PO line that is a charge, not goods — "SHIPPING /
/// ค่าขนส่ง 1 ครั้ง 200.00" — is not mapped to a Material and is not sent as an item. Its amount goes
/// onto the SAP Sales Order HEADER as the freight condition (Sap:SalesOrder:FreightConditionType,
/// default ZFHD), which SAP shows as "Freight" between Net Value and Total Amt+Freight.
///
/// A line is freight when the person said so (line extra.charge = "freight"), or — unless they said
/// it is an item (extra.charge = "item") — when its code or text reads like a shipping charge.
/// The frontend mirrors this in utils/soCharges.ts — keep the two word lists in step.
///
/// When one document becomes several Sales Orders (SO Group / delivery date), the freight is NOT
/// copied onto each of them (the customer would be billed several times): by default the whole
/// amount goes on the first Sales Order; the CS can share it out per Sales Order in Step 3
/// (header.soGroupFreight = { "{soGroup}|{date}": amount }).
/// </summary>
public static class SoCharges
{
    // Exact customer codes for a shipping line.
    private static readonly HashSet<string> FreightCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHIPPING", "SHIPMENT", "FREIGHT", "DELIVERY", "TRANSPORT", "TRANSPORTATION", "CARRIAGE", "ค่าขนส่ง", "ค่าส่ง",
    };

    // Text that marks a shipping charge anywhere in the code or description.
    private static readonly Regex FreightText = new(
        @"ค่าขนส่ง|ค่าจัดส่ง|ค่าบริการขนส่ง|ค่าบริการจัดส่ง|ค่าส่งสินค้า|ค่ารถขนส่ง|ค่าขนย้าย|" +
        @"\bfreight\b|\bshipping\b|\bdelivery (charge|fee|cost)s?\b|\btransport(ation)? (charge|fee|cost)s?\b|\bcarriage\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>"freight" / "item" / "" — the person's own choice on the line, if any.</summary>
    public static string Choice(Dictionary<string, object?> line) =>
        (line.Get("extra") as Dictionary<string, object?>).GetStr("charge").Trim().ToLowerInvariant();

    /// <summary>True when the words on the line read like a shipping charge (ignores the person's choice).</summary>
    public static bool LooksLikeFreight(Dictionary<string, object?> line)
    {
        var code = line.GetStr("extCode").Trim();
        var desc = line.GetStr("desc").Trim();
        if (code.Length > 0 && FreightCodes.Contains(code)) return true;
        if (desc.Length > 0 && FreightCodes.Contains(desc)) return true;
        return FreightText.IsMatch(code) || FreightText.IsMatch(desc);
    }

    public static bool IsFreight(Dictionary<string, object?> line) => Choice(line) switch
    {
        "freight" => true,
        "item" => false,
        _ => LooksLikeFreight(line),
    };

    /// <summary>The line's charge amount: Amount, or Quantity x Unit Price when no amount was read.</summary>
    public static double Amount(Dictionary<string, object?> line)
    {
        var amount = Num(line.Get("amount"));
        if (amount > 0) return amount;
        var q = Num(line.Get("qty"));
        var p = Num(line.Get("price"));
        return q > 0 && p > 0 ? Math.Round(q * p, 2) : 0;
    }

    public static double TotalFreight(List<Dictionary<string, object?>> lines) =>
        Math.Round(lines.Where(IsFreight).Sum(Amount), 2);

    /// <summary>
    /// Freight per Sales Order (group key -> amount). With the person's split (header.soGroupFreight)
    /// for at least one of the current groups, that split is used as given (a group not in it gets 0);
    /// otherwise the whole amount is on the first group. Keys of groups that no longer exist (the lines
    /// were regrouped) are ignored, which falls back to "all on the first one".
    /// </summary>
    public static Dictionary<string, double> FreightByGroup(Dictionary<string, object?> header,
        List<Dictionary<string, object?>> lines, IReadOnlyList<SoGroup> groups)
    {
        var result = groups.ToDictionary(g => g.Key, _ => 0.0);
        var total = TotalFreight(lines);
        if (total <= 0 || groups.Count == 0) return result;

        if (header.Get("soGroupFreight") is Dictionary<string, object?> split
            && split.Keys.Any(k => result.ContainsKey(k)))
        {
            foreach (var (k, v) in split)
                if (result.ContainsKey(k)) result[k] = Math.Max(0, Math.Round(Num(v), 2));
            return result;
        }
        result[groups[0].Key] = total;
        return result;
    }

    private static double Num(object? v) => v switch
    {
        null => 0,
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        string s => double.TryParse(s.Replace(",", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : 0,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
            double.TryParse(je.GetString()?.Replace(",", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var y) ? y : 0,
        _ => double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var z) ? z : 0,
    };
}
