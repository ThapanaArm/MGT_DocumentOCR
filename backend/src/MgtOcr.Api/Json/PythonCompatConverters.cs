using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MgtOcr.Api.Json;

// Ported behavior from main.py's clean()/rows() helpers (main.py:28-37), which convert every
// Decimal -> float and every datetime/date -> ISO string before returning JSON. Two formatting
// details need explicit matching so the frontend (and any diff-based verification against the
// still-running Python instance) sees byte-identical output:
//
// 1. Python's `datetime.isoformat(sep=" ")` uses a SPACE between date and time, not "T".
//    ADO.NET/Dapper give both SQL `date` and `datetime2` columns back as plain DateTime with no
//    way to tell which SQL type they came from — mirror Python's isinstance(date)-vs-datetime
//    branch with a heuristic instead: exact midnight (00:00:00) means it came from a `date`
//    column in every real row in this schema (no `datetime2(0)` timestamp in this app is ever
//    written at exactly midnight), so format those as bare "yyyy-MM-dd".
// 2. Python's `float(decimal_value)` naturally drops trailing zeros (1.000000 -> 1.0); .NET's
//    `decimal` preserves the SQL column's declared scale exactly. Route decimals through double
//    the same way Python does, accepting the same precision trade-off Python already accepts.
public class PythonDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var s = value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        writer.WriteStringValue(s);
    }
}

public class PythonDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        // STJ's default double formatting drops the ".0" on whole numbers (1.0 -> "1"); Python's
        // json.dumps always keeps a decimal point on float values (1.0 stays "1.0") — append it
        // back so a value like a UomConversion.Factor of 1 serializes identically to Python.
        var s = ((double)value).ToString(CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
        writer.WriteRawValue(s);
    }
}
