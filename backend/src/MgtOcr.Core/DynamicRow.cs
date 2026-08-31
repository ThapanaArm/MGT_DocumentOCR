namespace MgtOcr.Core;

// Dapper's dynamic query results are DapperRow instances, which implement IDictionary<string, object>.
// Converting to a plain Dictionary<string, object?> up front lets the mapping engine / document
// repository work with the same dict.get(key)-style access Python uses throughout, instead of
// dynamic member access (which requires the exact column to exist at compile-time-unchecked call sites).
public static class DynamicRow
{
    public static Dictionary<string, object?> ToDict(object row)
    {
        var d = (IDictionary<string, object>)row;
        return d.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
    }

    public static List<Dictionary<string, object?>> ToDictList(IEnumerable<object> rows) =>
        rows.Select(ToDict).ToList();

    // dict.get(key) equivalent — missing key, DBNull, or a null dict itself all come back as null,
    // matching Python's (rec or {}).get(field) pattern used throughout mapping.py/sap.py.
    public static object? Get(this Dictionary<string, object?>? d, string key) =>
        d != null && d.TryGetValue(key, out var v) ? v : null;

    public static string GetStr(this Dictionary<string, object?>? d, string key) =>
        (d.Get(key) ?? "").ToString() ?? "";
}
