using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MgtOcr.Core;
using MgtOcr.Core.Config;
using MgtOcr.Core.Json;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Ocr;

public record ChatFixResult(string Reply, Dictionary<string, object?> Header, List<Dictionary<string, object?>> Lines);

// Ported from app/ocr_engine.py's chat_fix_document() + _chat_fix_call_claude/gemini/openai
// (lines 1049-1224) — the "แชทสั่งแก้" AI correction feature. Fixes exactly the point the user
// describes (optionally backed by an attached image), never touches unrelated fields, and replies
// in plain text if the message was a question rather than a correction.
public static partial class ChatFix
{
    private static readonly HttpClient Http = new();

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObjectRegex();

    public static async Task<ChatFixResult?> ChatFixDocumentAsync(string module, Dictionary<string, object?> header,
        List<Dictionary<string, object?>> lines, List<Dictionary<string, object?>> history, string message,
        string? imageB64, string imageMediaType, string provider, AppConfig config)
    {
        var systemPrompt =
            "คุณคือผู้ช่วยแก้ไขข้อมูลเอกสาร (ใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อ) ที่อ่านมาจาก OCR ในระบบ OCR-to-SAP\n" +
            "ด้านล่างนี้คือข้อมูล header และ lines ปัจจุบันของเอกสารนี้ในรูปแบบ JSON (เป็นค่าล่าสุด " +
            "รวมการแก้ไขจากบทสนทนาก่อนหน้าแล้ว):\n\n" +
            $"header:\n{JsonSerializer.Serialize(header, PyJson.Options)}\n\n" +
            $"lines:\n{JsonSerializer.Serialize(lines, PyJson.Options)}\n\n" +
            "กติกา:\n" +
            "- ผู้ใช้อาจพิมพ์คำสั่งแก้ไข หรือถามคำถามเกี่ยวกับเอกสารนี้ก็ได้ (ถามตอบต่อเนื่องได้ตามบทสนทนาก่อนหน้า)\n" +
            "- ถ้าเป็นคำสั่งแก้ไข ให้แก้เฉพาะจุดที่ผู้ใช้ระบุเท่านั้น ห้ามเปลี่ยนค่าอื่นที่ไม่เกี่ยวข้องแม้จะดูแปลกตา\n" +
            "- ถ้ามีภาพแนบมาในข้อความล่าสุด ให้ใช้ภาพเป็นหลักฐานยืนยันค่าที่ถูกต้อง (เช่น อ่านตัวเลข/ชื่อจากภาพโดยตรง) " +
            "ประกอบกับคำอธิบายของผู้ใช้\n" +
            "- โครงสร้างและชื่อ field ของ header/lines ต้องเหมือนเดิมทุกประการ ห้ามเพิ่ม/ลบ field ห้ามเพิ่ม/ลบรายการใน lines " +
            "เว้นแต่ผู้ใช้ขอให้เพิ่ม/ลบรายการโดยตรง\n" +
            "- ถ้าเป็นคำถาม (ไม่ใช่คำสั่งแก้ไข) ให้ตอบคำถามใน reply แล้วคืน header/lines เดิมโดยไม่แก้ไขอะไร\n" +
            "- ตัวเลขต้องเป็นตัวเลขล้วน ไม่มีคอมมา\n" +
            "- ตอบกลับเป็น JSON ล้วน ๆ เท่านั้นทุกครั้ง ไม่ว่าข้อความก่อนหน้าในบทสนทนาจะเป็นรูปแบบใด " +
            "ตามโครงสร้างนี้ ห้ามมีข้อความอื่นนอก JSON:\n" +
            "{\"reply\": \"ข้อความสั้น ๆ ยืนยันว่าแก้อะไรไป หรือคำตอบคำถาม (ภาษาไทย)\", " +
            "\"header\": { ...header ที่แก้ไขแล้ว (หรือเดิมถ้าไม่ได้แก้)... }, " +
            "\"lines\": [ ...lines ที่แก้ไขแล้ว (หรือเดิมถ้าไม่ได้แก้)... ]}";

        try
        {
            var raw = provider switch
            {
                "gemini" => await CallGeminiAsync(systemPrompt, history, message, imageB64, imageMediaType, config),
                "openai" => await CallOpenAiAsync(systemPrompt, history, message, imageB64, imageMediaType, config),
                _ => await CallClaudeAsync(systemPrompt, history, message, imageB64, imageMediaType, config),
            };
            if (raw == null) return null;

            Dictionary<string, object?>? parsed = null;
            var m = JsonObjectRegex().Match(raw);
            if (m.Success)
            {
                try { parsed = JsonBodyHelpers.Unwrap(JsonSerializer.Deserialize<Dictionary<string, object?>>(m.Value) ?? new()); }
                catch (JsonException) { parsed = null; }
            }
            if (parsed == null)
            {
                var replyText = raw.Trim();
                if (replyText.Length == 0) return null;
                return new ChatFixResult(replyText, new Dictionary<string, object?>(header), lines.Select(l => new Dictionary<string, object?>(l)).ToList());
            }

            var h = HeaderParser.BlankHeader(module);
            if (parsed.Get("header") is Dictionary<string, object?> ph)
                foreach (var kv in ph) if (h.ContainsKey(kv.Key)) h[kv.Key] = kv.Value;

            var outLines = new List<Dictionary<string, object?>>();
            if (parsed.Get("lines") is List<object?> pl)
            {
                foreach (var lnObj in pl.Take(60))
                {
                    if (lnObj is not Dictionary<string, object?> ln) continue;
                    var uom = ln.GetStr("uom");
                    outLines.Add(new Dictionary<string, object?>
                    {
                        ["extCode"] = ln.GetStr("extCode"), ["desc"] = ln.GetStr("desc"),
                        ["qty"] = Num(ln.Get("qty")), ["uom"] = uom.Length > 0 ? uom : "EA",
                        ["price"] = Num(ln.Get("price")), ["amount"] = Num(ln.Get("amount")),
                    });
                }
            }
            var reply = parsed.GetStr("reply");
            return new ChatFixResult(reply.Length > 0 ? reply : "แก้ไขเรียบร้อยแล้ว", h, outLines);
        }
        catch
        {
            return null;
        }
    }

    private static string ImgSuffix(Dictionary<string, object?> h, string role) =>
        h.Get("hasImage") is true && role == "user" ? " [แนบภาพประกอบ]" : "";

    private static async Task<string?> CallClaudeAsync(string systemPrompt, List<Dictionary<string, object?>> history,
        string message, string? imageB64, string imageMediaType, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.AnthropicApiKey)) return null;
        var messages = new List<object>();
        foreach (var h in history.TakeLast(12))
        {
            var role = h.GetStr("role") == "assistant" ? "assistant" : "user";
            var text = h.GetStr("text").Trim();
            var suffix = ImgSuffix(h, role);
            if (text.Length > 0 || suffix.Length > 0) messages.Add(new { role, content = text + suffix });
        }
        object curContent = !string.IsNullOrEmpty(imageB64)
            ? new object[] { new { type = "image", source = new { type = "base64", media_type = imageMediaType, data = imageB64 } }, new { type = "text", text = message } }
            : message;
        messages.Add(new { role = "user", content = curContent });

        var body = new { model = config.AnthropicModel, max_tokens = 3000, system = systemPrompt, messages };
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
        using var doc = JsonDocument.Parse(respText);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var b in content.EnumerateArray())
            if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var txt))
                sb.Append(txt.GetString());
        return sb.ToString();
    }

    private static async Task<string?> CallGeminiAsync(string systemPrompt, List<Dictionary<string, object?>> history,
        string message, string? imageB64, string imageMediaType, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey)) return null;
        var contents = new List<object>();
        foreach (var h in history.TakeLast(12))
        {
            var role = h.GetStr("role") == "assistant" ? "model" : "user";
            var text = h.GetStr("text").Trim();
            var suffix = ImgSuffix(h, role == "model" ? "assistant" : "user");
            if (text.Length > 0 || suffix.Length > 0) contents.Add(new { role, parts = new[] { new { text = text + suffix } } });
        }
        var curParts = new List<object> { new { text = message.Length > 0 ? message : " " } };
        if (!string.IsNullOrEmpty(imageB64)) curParts.Add(new { inline_data = new { mime_type = imageMediaType, data = imageB64 } });
        contents.Add(new { role = "user", parts = curParts });

        var body = new
        {
            contents, systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            generationConfig = new { responseMimeType = "application/json", maxOutputTokens = 3000 },
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
        using var doc = JsonDocument.Parse(respText);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
            foreach (var c in cands.EnumerateArray())
                if (c.TryGetProperty("content", out var cc) && cc.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var p in parts.EnumerateArray())
                        if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        return sb.ToString();
    }

    private static async Task<string?> CallOpenAiAsync(string systemPrompt, List<Dictionary<string, object?>> history,
        string message, string? imageB64, string imageMediaType, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.OpenAiApiKey)) return null;
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var h in history.TakeLast(12))
        {
            var role = h.GetStr("role") == "assistant" ? "assistant" : "user";
            var text = h.GetStr("text").Trim();
            var suffix = ImgSuffix(h, role);
            if (text.Length > 0 || suffix.Length > 0) messages.Add(new { role, content = text + suffix });
        }
        object curContent = !string.IsNullOrEmpty(imageB64)
            ? new object[] { new { type = "image_url", image_url = new { url = $"data:{imageMediaType};base64,{imageB64}" } }, new { type = "text", text = message.Length > 0 ? message : " " } }
            : (message.Length > 0 ? message : " ");
        messages.Add(new { role = "user", content = curContent });

        var body = new { model = config.OpenAiModel, max_tokens = 3000, response_format = new { type = "json_object" }, messages };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Authorization", "Bearer " + config.OpenAiApiKey);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var resp = await Http.SendAsync(req, cts.Token);
        var respText = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(respText);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("content", out var contentEl))
            return contentEl.GetString() ?? "";
        return "";
    }
}
