using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

// claude_vision_extract() / claude_text_extract(): raw HTTP calls to the Anthropic Messages API
// (no SDK, matching the Python side's deliberate choice) — image-based extraction, and the
// "2-tier" cheaper text-structuring mode (OCR text in, structured JSON out, no image tokens).
public static class ClaudeOcr
{
    private static readonly HttpClient Http = new();

    public static async Task<ParsedDocument?> VisionExtractAsync(string path, string module, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.AnthropicApiKey)) return null;
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
                type = "image",
                source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(b) },
            }));

            var body = new { model = config.AnthropicModel, max_tokens = 3000, messages = new[] { new { role = "user", content = (object)content } } };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", config.AnthropicApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            var raw = ExtractTextBlocks(respText);
            return VisionPrompt.ParseResponse(raw, module, "claude", 0.88, raw);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<ParsedDocument?> TextExtractAsync(string module, string text, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.AnthropicApiKey) || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var prompt = VisionPrompt.Build(module, "text") + "\n\n--- ข้อความจาก OCR ---\n" +
                         (text.Length > 12000 ? text[..12000] : text) + "\n";
            var body = new { model = config.AnthropicModel, max_tokens = 3000, messages = new[] { new { role = "user", content = prompt } } };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", config.AnthropicApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            var raw = ExtractTextBlocks(respText);
            return VisionPrompt.ParseResponse(raw, module, "claude_text", 0.82, text);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractTextBlocks(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                block.TryGetProperty("text", out var txt))
                sb.Append(txt.GetString());
        }
        return sb.ToString();
    }
}
