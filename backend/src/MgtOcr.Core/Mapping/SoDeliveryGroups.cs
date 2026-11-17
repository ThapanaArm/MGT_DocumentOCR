using System.Globalization;

namespace MgtOcr.Core.Mapping;

/// <summary>One Sales Order to create from a Sales Order document.</summary>
/// <param name="Key">Stable group id "{SoGroupNo}|{Date}" -- the frontend builds the same key (soGroups() in
/// SapSalesOrderEditor.tsx) to line up its rows with GET so-posts.</param>
/// <param name="Indexes">Positions of the group's lines in the document's line list.</param>
/// <param name="ItemNos">The same lines' itemNo values (10, 20, ...).</param>
/// <param name="SoGroupNo">The person's own "SO Group" number on the lines (extra.soGroup, default "1").</param>
/// <param name="Date">Delivery date (YYYY-MM-DD as stored) or "" when neither the line nor the header has one.</param>
/// <param name="Deal">MGT/Zoho: the Deal key of the group's lines (Zoho Deal id, or "__new__", "__new__2" ... for a
/// Deal the send opens), "" when the lines carry none. One PO may hold items of several Deals (16 Nov 2026).</param>
public record SoGroup(string Key, List<int> Indexes, List<string> ItemNos, string SoGroupNo = "1", string Date = "", string Deal = "");

/// <summary>
/// How one Sales Order document becomes several SAP / Zoho Sales Orders (12 Oct 2026, replaces both
/// "split into child documents" and the date-only grouping). Each line lands in exactly ONE group, keyed by
///   1. its SO Group -- a number the CS types per line to keep products on separate Sales Orders
///      (no business rule, the person decides; blank/invalid = 1), and
///   2. its delivery date -- line extra.deliveryDate, else the header deliveryDate (Megachem rule: one
///      Sales Order per delivery date).
/// Because a line has one key, it can never be sent twice, whichever way the order is cut. The ship-to is
/// chosen per SO Group (header.soGroupShipTos, see ShipToFor); a group without one uses the document's.
/// Groups are ordered by SO Group number, then first-seen date, so "SO 1 / SO 2" stays stable.
/// The frontend mirrors this in soGroups() (SapSalesOrderEditor.tsx) - keep both in step.
/// </summary>
public static class SoDeliveryGroups
{
    public static string LineDate(Dictionary<string, object?> line, string? headerDate)
    {
        var own = (line.Get("extra") as Dictionary<string, object?>).GetStr("deliveryDate").Trim();
        return own.Length > 0 ? own : (headerDate ?? "").Trim();
    }

    /// <summary>The line's SO Group number as a canonical string ("1", "2", ...); blank/invalid = "1".</summary>
    public static string LineSoGroup(Dictionary<string, object?> line)
    {
        var raw = (line.Get("extra") as Dictionary<string, object?>).GetStr("soGroup").Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : "1";
    }

    public static string KeyOf(string soGroupNo, string date) => soGroupNo + "|" + date;

    /// <summary>MGT/Zoho: the Deal a line belongs to (line extra.zohoDeal) -- a Zoho Deal id, or "__new__",
    /// "__new__2" ... for a new Deal opened on Send; "" when not set (GLC never sets it).</summary>
    public static string LineDeal(Dictionary<string, object?> line) =>
        (line.Get("extra") as Dictionary<string, object?>).GetStr("zohoDeal").Trim();

    /// <summary>itemNo of line <paramref name="i"/> (10, 20, ... when the line has none).</summary>
    public static string ItemNoOf(Dictionary<string, object?> line, int i) =>
        line.Get("itemNo")?.ToString() ?? ((i + 1) * 10).ToString(CultureInfo.InvariantCulture);

    /// <param name="skipCharges">GLC: leave out freight lines (SoCharges.IsFreight) - they are not items
    /// of any Sales Order, their amount goes on a Sales Order header instead (SoCharges.FreightByGroup).</param>
    /// <param name="dealOf">MGT/Zoho: the Deal key of line i (default: its stored extra.zohoDeal). A Zoho Sales
    /// Order links to ONE Deal, so when the lines belong to more than one Deal the Deal is a third part of the
    /// key ("{SoGroup}|{Date}|{Deal}") and each Deal gets its own Sales Orders (16 Nov 2026). With a single
    /// Deal (or none, GLC) the key stays "{SoGroup}|{Date}" as before.</param>
    public static List<SoGroup> Build(Dictionary<string, object?> header, List<Dictionary<string, object?>> lines,
        bool skipCharges = false, Func<Dictionary<string, object?>, int, string>? dealOf = null)
    {
        var headerDate = header.GetStr("deliveryDate");
        string DealOf(int i) => (dealOf?.Invoke(lines[i], i) ?? LineDeal(lines[i])).Trim();
        var included = Enumerable.Range(0, lines.Count).Where(i => !(skipCharges && SoCharges.IsFreight(lines[i]))).ToList();
        var splitByDeal = included.Select(DealOf).Distinct(StringComparer.Ordinal).Count() > 1;
        var groups = new List<SoGroup>();
        foreach (var i in included)
        {
            var no = LineSoGroup(lines[i]);
            var date = LineDate(lines[i], headerDate);
            var deal = DealOf(i);
            var key = splitByDeal ? KeyOf(no, date) + "|" + deal : KeyOf(no, date);
            var g = groups.FirstOrDefault(x => x.Key == key);
            if (g == null) { g = new SoGroup(key, [], [], no, date, deal); groups.Add(g); }
            g.Indexes.Add(i);
            g.ItemNos.Add(ItemNoOf(lines[i], i));
        }
        // OrderBy is stable: within one SO Group the first-seen date order is kept.
        return groups.OrderBy(g => int.Parse(g.SoGroupNo, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>
    /// The ship-to the person chose for one SO Group (header.soGroupShipTos["2"] = { code, name, address, ... }),
    /// or null when that group uses the document's own ship-to. GLC stores the SAP ship-to party code in
    /// "code"; MGT stores the whole Zoho ship-to (ZohoShipToInfo fields, camelCase).
    /// </summary>
    public static Dictionary<string, object?>? ShipToFor(Dictionary<string, object?> header, string soGroupNo)
    {
        if (header.Get("soGroupShipTos") is not Dictionary<string, object?> all) return null;
        if (all.Get(soGroupNo) is not Dictionary<string, object?> st) return null;
        return st.GetStr("code").Trim().Length > 0 || st.GetStr("address").Trim().Length > 0 ? st : null;
    }
}
