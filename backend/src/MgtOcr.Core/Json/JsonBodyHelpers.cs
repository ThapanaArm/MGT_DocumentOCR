using System.Text.Json;

namespace MgtOcr.Core.Json;

// ASP.NET Core deserializes `[FromBody] Dictionary<string, object?>` with each value boxed as a
// System.Text.Json.JsonElement, not a plain CLR primitive — Python's request.json() gives you
// plain str/float/int/bool/None directly. Unwrap here so downstream code (Dapper parameter
// binding, mapping engine, etc.) sees ordinary .NET values just like the Python side does.
public static class JsonBodyHelpers
{
    public static Dictionary<string, object?> Unwrap(Dictionary<string, object?> body) =>
        body.ToDictionary(kv => kv.Key, kv => UnwrapValue(kv.Value));

    public static object? UnwrapValue(object? value)
    {
        if (value is not JsonElement el) return value;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            // Cast to `object` explicitly: without it, C#'s conditional operator unifies `long`
            // and `double` branches to `double` (long -> double is an implicit numeric conversion),
            // silently turning every whole-number JSON value into a double and losing Python's
            // int-vs-float distinction (e.g. a demo header's "vatRate": 7 round-tripping as 7.0).
            JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => UnwrapValue(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(e => UnwrapValue(e)).ToList(),
            _ => el.ToString(),
        };
    }
}
