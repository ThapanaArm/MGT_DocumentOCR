using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace MgtOcr.Core.Json;

// Mirrors Python's json.dumps(..., ensure_ascii=False): keep Thai/Unicode characters raw instead
// of \uXXXX-escaping them, so HeaderJson/ExtraJson/MemoryJson stored by the .NET port look the same
// as rows Python already wrote to the same live database.
public static class PyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new PythonDateTimeConverter(), new PythonDecimalConverter(), new PythonDoubleConverter() },
    };
}
