using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

// New in the .NET port (no Python equivalent) — Google Gemini Vision, same raw-HTTP/no-SDK
// pattern as the existing Claude/Azure/Typhoon clients. Uses the Generative Language API's
// generateContent endpoint with inline base64 image parts.
public static class GeminiOcr
{
    private static readonly HttpClient Http = new();

    public static async Task<ParsedDocument?> VisionExtractAsync(string path, string module, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey)) return null;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var imgs = ext == ".pdf"
                ? PdfRasterizer.RenderPagesToPng(path, maxPages: 3, dpi: 200)
                : [await File.ReadAllBytesAsync(path)];
            if (imgs.Count == 0) return null;

            var parts = new List<object> { new { text = VisionPrompt.Build(module) } };
            parts.AddRange(imgs.Select(b => (object)new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(b) } }));

            var body = new
            {
                contents = new[] { new { role = "user", parts = (object)parts } },
                generationConfig = new { temperature = 0, maxOutputTokens = 3000 },
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            var raw = ExtractText(respText);
            return VisionPrompt.ParseResponse(raw, module, "gemini", 0.87, raw);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            return "";
        var sb = new StringBuilder();
        if (cands[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in parts.EnumerateArray())
                if (p.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
        }
        return sb.ToString();
    }
}
