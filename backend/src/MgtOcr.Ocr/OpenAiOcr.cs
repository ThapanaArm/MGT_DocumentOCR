using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

// New in the .NET port (no Python equivalent) — OpenAI GPT-4o/GPT-5 Vision, same raw-HTTP/no-SDK
// pattern as the existing Claude/Azure/Typhoon clients. Uses the Chat Completions API with an
// image_url content part carrying a base64 data URI.
public static class OpenAiOcr
{
    private static readonly HttpClient Http = new();

    public static async Task<ParsedDocument?> VisionExtractAsync(string path, string module, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.OpenAiApiKey)) return null;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var imgs = ext == ".pdf"
                ? PdfRasterizer.RenderPagesToPng(path, maxPages: 3, dpi: 200)
                : [await File.ReadAllBytesAsync(path)];
            if (imgs.Count == 0) return null;

            var content = new List<object> { new { type = "text", text = VisionPrompt.Build(module) } };
            content.AddRange(imgs.Select(b => (object)new
            {
                type = "image_url",
                image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(b) },
            }));

            var body = new
            {
                model = config.OpenAiModel, max_tokens = 3000, temperature = 0,
                messages = new[] { new { role = "user", content = (object)content } },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Authorization", $"Bearer {config.OpenAiApiKey}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            var raw = ExtractText(respText);
            return VisionPrompt.ParseResponse(raw, module, "openai", 0.87, raw);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return "";
        if (choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
            return c.GetString() ?? "";
        return "";
    }
}
