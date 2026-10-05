namespace MgtOcr.Core.Mapping;

/// <summary>One delivery-date group of a Sales Order document = one SAP / Zoho Sales Order.</summary>
/// <param name="Key">Delivery date (YYYY-MM-DD as stored) or "" when neither the line nor the header has one.</param>
/// <param name="Indexes">Positions of the group's lines in the document's line list.</param>
/// <param name="ItemNos">The same lines' itemNo values (10, 20, ...).</param>
public record SoGroup(string Key, List<int> Indexes, List<string> ItemNos);

/// <summary>
/// Megachem rule: lines that ship on the SAME date go on one Sales Order, different dates are
/// separate Sales Orders. A line's date = its own extra.deliveryDate (OCR-prefilled / CS-edited),
/// else the header deliveryDate. Groups keep first-seen order so "SO 1 / SO 2" stays stable.
/// The frontend mirrors this in deliveryDateGroups() (SapSalesOrderEditor.tsx) - keep both in step.
/// </summary>
public static class SoDeliveryGroups
{
    public static string LineDate(Dictionary<string, object?> line, string? headerDate)
    {
        var own = (line.Get("extra") as Dictionary<string, object?>).GetStr("deliveryDate").Trim();
        return own.Length > 0 ? own : (headerDate ?? "").Trim();
    }

    public static List<SoGroup> Build(Dictionary<string, object?> header, List<Dictionary<string, object?>> lines)
    {
        var headerDate = header.GetStr("deliveryDate");
        var groups = new List<SoGroup>();
        for (var i = 0; i < lines.Count; i++)
        {
            var key = LineDate(lines[i], headerDate);
            var g = groups.FirstOrDefault(x => x.Key == key);
            if (g == null) { g = new SoGroup(key, [], []); groups.Add(g); }
            g.Indexes.Add(i);
            g.ItemNos.Add(lines[i].Get("itemNo")?.ToString() ?? ((i + 1) * 10).ToString());
        }
        return groups;
    }
}
