using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr.Providers;

// New in the .NET port (no Python equivalent) — OpenAI GPT-4o/GPT-5 Vision, same raw-HTTP/no-SDK
// pattern as the existing Claude/Azure/Typhoon clients. Uses the Chat Completions API with an
// image_url content part carrying a base64 data URI.
public static class OpenAiOcr
{
    private static readonly HttpClient Http = new();

    public static async Task<(ParsedDocument? Doc, string? Error)> VisionExtractAsync(string path, string module, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.OpenAiApiKey))
            return (null, "OpenAiApiKey is empty in config — appsettings Ocr:OpenAiApiKey was not loaded by the running app (check which appsettings.json/appsettings.{Env}.json the process reads, and that it was restarted)");
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var imgs = ext == ".pdf"
                ? PdfRasterizer.RenderPagesToPng(path, maxPages: 3, dpi: 200)
                : [await File.ReadAllBytesAsync(path)];
            if (imgs.Count == 0)
                return (null, $"Could not rasterize '{Path.GetFileName(path)}' to images (renderer produced 0 pages — check the PDF rasterizer on the server)");

            var content = new List<object> { new { type = "text", text = VisionPrompt.Build(module) } };
            content.AddRange(imgs.Select(b => (object)new
            {
                type = "image_url",
                image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(b) },
            }));

            // Newer models in this family (e.g. gpt-5.6-luna) reject a non-default temperature
            // outright ("Unsupported value: 'temperature' does not support 0 with this model.
            // Only the default (1) value is supported.") — so temperature is omitted entirely
            // rather than guessed at a value that might also be rejected.
            var body = new
            {
                model = config.OpenAiModel, max_completion_tokens = 3000,
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
            if (!resp.IsSuccessStatusCode)
                return (null, $"OpenAI HTTP {(int)resp.StatusCode} (model={config.OpenAiModel}): {Trunc(respText, 400)}");

            var raw = ExtractText(respText);
            return (VisionPrompt.ParseResponse(raw, module, "openai", 0.87, raw), null);
        }
        catch (Exception ex)
        {
            return (null, $"OpenAI request failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

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
