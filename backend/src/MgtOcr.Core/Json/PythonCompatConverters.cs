using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MgtOcr.Core.Json;

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
//
// Shared between the MVC response layer (Program.cs's AddJsonOptions) AND PyJson.Options (used to
// serialize HeaderJson/ExtraJson/MemoryJson before writing to the DB) — a hand-built double like a
// split document's summed line amount must get the same ".0" treatment at WRITE time as any value
// read back FROM the DB gets at response time, or the two paths silently disagree (see PyJson.cs).
public class PythonDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        if (value.TimeOfDay == TimeSpan.Zero)
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return;
        }
        // Python's datetime.isoformat() appends ".ffffff" only when microsecond != 0 — most columns
        // are datetime2(0) (always whole seconds, e.g. AuditLog columns without an explicit scale get
        // datetime2's default 100ns precision from SYSDATETIME()). pyodbc/CPython's datetime only
        // keeps microsecond (6-digit) resolution, truncating SQL Server's 100ns tick, so divide by 10.
        var micros = (value.Ticks % TimeSpan.TicksPerSecond) / 10;
        var s = value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (micros != 0) s += "." + micros.ToString("D6", CultureInfo.InvariantCulture);
        writer.WriteStringValue(s);
    }
}

// Same trailing-".0" fixup as PythonDecimalConverter, but for hand-built `double` values (e.g.
// dashboard trend percentages, cost aggregates, a split document's summed line amount) that never
// pass through a SQL `decimal` column and so wouldn't otherwise hit PythonDecimalConverter —
// Python's json.dumps keeps ".0" on any float.
public class PythonDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E') && !double.IsNaN(value) && !double.IsInfinity(value)) s += ".0";
        writer.WriteRawValue(s);
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
