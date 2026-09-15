using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MgtOcr.Core;
using MgtOcr.Core.Config;
using MgtOcr.Core.Json;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Ocr;

public record ChatFixResult(string Reply, Dictionary<string, object?> Header, List<Dictionary<string, object?>> Lines,
    // Line index (matching the position in Lines/the document's own lines) -> Material code, for
    // any line the person asked the AI to change/select a Material for. On the SAP side this is a
    // SAP Material Code (CustomerMaterial.MaterialCodeSAP); on the Zoho side the frontend also
    // receives Deal Item options (by materialCode) and resolves the same code back to the full Deal
    // Item. Either way, frontend applies these as a plain selection (see sendChat() in
    // DocumentPage.tsx) -- this never itself saves a CustomerMaterial/UomConversion row; only an
    // explicit save action (picking straight from the SAP/Zoho search panel, or "+ Master") does.
    Dictionary<int, string> MaterialCodes,
    // The Ship-to code the person asked the AI to select for this document, if any -- validated
    // against ShipToOptions the same way MaterialCodes is validated against materialOptions. Applies
    // to both SAP and Zoho (both read the same ocr.ShipTo master; see setManualHeader('shipTo', …)).
    string? ShipToCode,
    // The SAP Business Partner id (Account/Customer) the person asked the AI to select, if any --
    // validated against AccountOptions the same way. SAP/GLC documents only -- Zoho's own Account
    // search is a separate, already-existing flow (ZohoCustomerPanel/pendingMatch's /api/compare).
    string? CustomerCode);

// Ported from app/ocr_engine.py's chat_fix_document() + _chat_fix_call_claude/gemini/openai
// (lines 1049-1224) — the "แชทสั่งแก้" AI correction feature. Fixes exactly the point the user
// describes (optionally backed by an attached image), never touches unrelated fields, and replies
// in plain text if the message was a question rather than a correction.
public static partial class ChatFix
{
    private static readonly HttpClient Http = new();

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObjectRegex();

    private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    public static async Task<(ChatFixResult? Result, string? Error)> ChatFixDocumentAsync(string module, Dictionary<string, object?> header,
        List<Dictionary<string, object?>> lines, List<Dictionary<string, object?>> history, string message,
        string? imageB64, string imageMediaType, string provider, AppConfig config,
        // The customer's available CustomerMaterial rows (own rows first, then other customers'), plus
        // (for a Zoho document with a Deal already picked) that Deal's own Ordered Items -- so the AI
        // can pick a real Material code by name instead of inventing one. Empty for modules that have
        // no per-line Material concept (AP/II). See DocumentsController for how this is built.
        List<(string Code, string Description, bool Mine)> materialOptions,
        // This document's available Ship-to addresses (always scoped to its own customer -- Ship-to
        // has no cross-customer borrowing concept the way Material does). Empty for modules with no
        // Ship-to concept, or before a Customer is matched.
        List<(string Code, string Address)> shipToOptions,
        // A live SAP Business Partner search (by the document's current customer name/Tax ID), so
        // the AI can offer a real Account/Customer to switch to when the current match "feels wrong"
        // instead of only ever being able to edit the raw customerName/customerAddress text fields.
        // SAP/GLC SO documents only -- see DocumentsController for how this is searched.
        List<(string Code, string Description)> accountOptions)
    {
        var materialBlock = "";
        if (materialOptions.Count > 0)
        {
            var mb = new StringBuilder();
            mb.AppendLine(
                "รายการ Material ที่เลือกให้แต่ละบรรทัดได้ -- รูปแบบ \"รหัส : ชื่อ\" " +
                "(บรรทัดที่ขึ้นต้นด้วย * คือของลูกค้า/ดีลของเอกสารนี้โดยตรง ส่วนที่เหลือยืมมาจากลูกค้ารายอื่นแต่ยังเลือกได้):");
            foreach (var o in materialOptions)
                mb.AppendLine($"{(o.Mine ? "*" : "-")} {o.Code} : {o.Description}");
            materialBlock = mb.ToString();
        }

        var shipToBlock = "";
        if (shipToOptions.Count > 0)
        {
            var sb2 = new StringBuilder();
            sb2.AppendLine("รายการ Ship-to (ที่อยู่จัดส่ง) ที่เลือกให้เอกสารนี้ได้ -- รูปแบบ \"รหัส : ที่อยู่\":");
            foreach (var o in shipToOptions)
                sb2.AppendLine($"- {o.Code} : {o.Address}");
            shipToBlock = sb2.ToString();
        }

        var accountBlock = "";
        if (accountOptions.Count > 0)
        {
            var ab = new StringBuilder();
            ab.AppendLine(
                "รายการ SAP Business Partner (Account/ลูกค้า) ที่ค้นจาก SAP สด ๆ ด้วยชื่อ/เลขผู้เสียภาษีปัจจุบันของเอกสารนี้ -- " +
                "รูปแบบ \"รหัส : ชื่อ + ที่อยู่\":");
            foreach (var o in accountOptions)
                ab.AppendLine($"- {o.Code} : {o.Description}");
            accountBlock = ab.ToString();
        }

        var systemPrompt =
            "คุณคือผู้ช่วยแก้ไขข้อมูลเอกสาร (ใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อ) ที่อ่านมาจาก OCR ในระบบ OCR-to-SAP\n" +
            "ด้านล่างนี้คือข้อมูล header และ lines ปัจจุบันของเอกสารนี้ในรูปแบบ JSON (เป็นค่าล่าสุด " +
            "รวมการแก้ไขจากบทสนทนาก่อนหน้าแล้ว):\n\n" +
            $"header:\n{JsonSerializer.Serialize(header, PyJson.Options)}\n\n" +
            $"lines:\n{JsonSerializer.Serialize(lines, PyJson.Options)}\n\n" +
            (materialBlock.Length > 0 ? $"{materialBlock}\n" : "") +
            (shipToBlock.Length > 0 ? $"{shipToBlock}\n" : "") +
            (accountBlock.Length > 0 ? $"{accountBlock}\n" : "") +
            "กติกา:\n" +
            "- ผู้ใช้อาจพิมพ์คำสั่งแก้ไข หรือถามคำถามเกี่ยวกับเอกสารนี้ก็ได้ (ถามตอบต่อเนื่องได้ตามบทสนทนาก่อนหน้า)\n" +
            "- ถ้าเป็นคำสั่งแก้ไข ให้แก้เฉพาะจุดที่ผู้ใช้ระบุเท่านั้น ห้ามเปลี่ยนค่าอื่นที่ไม่เกี่ยวข้องแม้จะดูแปลกตา\n" +
            "- ถ้ามีภาพแนบมาในข้อความล่าสุด ให้ใช้ภาพเป็นหลักฐานยืนยันค่าที่ถูกต้อง (เช่น อ่านตัวเลข/ชื่อจากภาพโดยตรง) " +
            "ประกอบกับคำอธิบายของผู้ใช้\n" +
            "- โครงสร้างและชื่อ field ของ header/lines ต้องเหมือนเดิมทุกประการ ห้ามเพิ่ม/ลบ field ห้ามเพิ่ม/ลบรายการใน lines " +
            "เว้นแต่ผู้ใช้ขอให้เพิ่ม/ลบรายการโดยตรง\n" +
            (materialBlock.Length > 0
                ? "- ถ้าผู้ใช้สั่งให้เปลี่ยนหรือเลือก Material/รหัสสินค้าของบรรทัดใด ให้เลือกรหัสที่ตรงที่สุดจากรายการ Material ด้านบนเท่านั้น " +
                  "แล้วใส่ไว้ในฟิลด์ \"materialCode\" ของบรรทัดนั้นในผลลัพธ์ ห้ามคิดรหัสขึ้นเองถ้าไม่มีในรายการ (ถ้าหาไม่เจอให้ปล่อย materialCode ว่างแล้วอธิบายใน reply แทน) " +
                  "บรรทัดที่ไม่ได้เปลี่ยน Material ให้ใส่ materialCode เป็นค่าว่าง\n"
                : "") +
            (shipToBlock.Length > 0
                ? "- ถ้าผู้ใช้สั่งให้เปลี่ยนหรือเลือก Ship-to/ที่อยู่จัดส่งของเอกสารนี้ ให้เทียบกับที่อยู่ที่ผู้ใช้พิมพ์มาหรือในภาพแนบ " +
                  "แล้วเลือกรหัสที่ตรงที่สุดจากรายการ Ship-to ด้านบนเท่านั้น ใส่ไว้ในฟิลด์ \"shipToCode\" ที่ระดับบนสุดของผลลัพธ์ (นอกเหนือจาก header/lines) " +
                  "ห้ามคิดรหัสขึ้นเองถ้าไม่มีในรายการ (ถ้าหาไม่เจอให้ปล่อย shipToCode ว่างแล้วอธิบายใน reply แทน) ถ้าไม่ได้ขอเปลี่ยน Ship-to ให้ใส่ shipToCode เป็นค่าว่าง\n"
                : "") +
            (accountBlock.Length > 0
                ? "- ถ้าผู้ใช้บอกว่าลูกค้า/Account ที่จับคู่ไว้ไม่ถูก หรือขอให้ค้นหา/เปลี่ยนลูกค้าใหม่ ให้เลือกรหัสที่ตรงที่สุดจากรายการ SAP Business Partner ด้านบนเท่านั้น " +
                  "ใส่ไว้ในฟิลด์ \"customerCode\" ที่ระดับบนสุดของผลลัพธ์ ห้ามคิดรหัสขึ้นเองถ้าไม่มีในรายการ (ถ้าไม่มีรายการไหนตรงเลยให้ปล่อย customerCode ว่าง " +
                  "แล้วบอกใน reply ว่าค้นหาใน SAP ไม่เจอ ลองพิมพ์ชื่อ/เลขผู้เสียภาษีให้ชัดขึ้น) ถ้าไม่ได้ขอเปลี่ยนลูกค้าให้ใส่ customerCode เป็นค่าว่าง\n"
                : "") +
            "- ถ้าเป็นคำถาม (ไม่ใช่คำสั่งแก้ไข) ให้ตอบคำถามใน reply แล้วคืน header/lines เดิมโดยไม่แก้ไขอะไร\n" +
            "- ตัวเลขต้องเป็นตัวเลขล้วน ไม่มีคอมมา\n" +
            "- ตอบกลับเป็น JSON ล้วน ๆ เท่านั้นทุกครั้ง ไม่ว่าข้อความก่อนหน้าในบทสนทนาจะเป็นรูปแบบใด " +
            "ตามโครงสร้างนี้ ห้ามมีข้อความอื่นนอก JSON:\n" +
            "{\"reply\": \"ข้อความสั้น ๆ ยืนยันว่าแก้อะไรไป หรือคำตอบคำถาม (ภาษาไทย)\", " +
            "\"header\": { ...header ที่แก้ไขแล้ว (หรือเดิมถ้าไม่ได้แก้)... }, " +
            "\"lines\": [ ...lines ที่แก้ไขแล้ว (หรือเดิมถ้าไม่ได้แก้ -- แต่ละบรรทัดมี field เดิมทั้งหมด" +
            (materialBlock.Length > 0 ? " บวก \\\"materialCode\\\" ตามกติกาด้านบนถ้ามีการเปลี่ยน Material" : "") +
            ")... ]" +
            (shipToBlock.Length > 0 ? ", \"shipToCode\": \"...รหัส Ship-to ตามกติกาด้านบน หรือค่าว่าง...\"" : "") +
            (accountBlock.Length > 0 ? ", \"customerCode\": \"...รหัส SAP Business Partner ตามกติกาด้านบน หรือค่าว่าง...\"" : "") +
            "}";

        try
        {
            var (raw, callErr) = provider switch
            {
                "gemini" => await CallGeminiAsync(systemPrompt, history, message, imageB64, imageMediaType, config),
                "openai" => await CallOpenAiAsync(systemPrompt, history, message, imageB64, imageMediaType, config),
                _ => await CallClaudeAsync(systemPrompt, history, message, imageB64, imageMediaType, config),
            };
            if (raw == null) return (null, callErr);

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
                if (replyText.Length == 0) return (null, "AI provider returned an empty reply");
                return (new ChatFixResult(replyText, new Dictionary<string, object?>(header), lines.Select(l => new Dictionary<string, object?>(l)).ToList(), new Dictionary<int, string>(), null, null), null);
            }

            var h = HeaderParser.BlankHeader(module);
            if (parsed.Get("header") is Dictionary<string, object?> ph)
                foreach (var kv in ph) if (h.ContainsKey(kv.Key)) h[kv.Key] = kv.Value;

            // Only accept a materialCode the AI actually saw offered -- never let a hallucinated code
            // slip through to the frontend (which would apply it as a real Material selection).
            var validCodes = materialOptions.Count > 0
                ? new HashSet<string>(materialOptions.Select(o => o.Code), StringComparer.OrdinalIgnoreCase)
                : null;
            var materialCodes = new Dictionary<int, string>();
            var outLines = new List<Dictionary<string, object?>>();
            if (parsed.Get("lines") is List<object?> pl)
            {
                for (var idx = 0; idx < pl.Count && idx < 60; idx++)
                {
                    if (pl[idx] is not Dictionary<string, object?> ln) continue;
                    var uom = ln.GetStr("uom");
                    outLines.Add(new Dictionary<string, object?>
                    {
                        ["extCode"] = ln.GetStr("extCode"), ["desc"] = ln.GetStr("desc"),
                        ["qty"] = Num(ln.Get("qty")), ["uom"] = uom.Length > 0 ? uom : "EA",
                        ["price"] = Num(ln.Get("price")), ["amount"] = Num(ln.Get("amount")),
                    });
                    var mc = ln.GetStr("materialCode").Trim();
                    if (mc.Length > 0 && (validCodes == null || validCodes.Contains(mc)))
                        materialCodes[idx] = mc;
                }
            }
            string? shipToCode = null;
            if (shipToOptions.Count > 0)
            {
                var sc = parsed.GetStr("shipToCode").Trim();
                if (sc.Length > 0 && shipToOptions.Any(o => string.Equals(o.Code, sc, StringComparison.OrdinalIgnoreCase)))
                    shipToCode = sc;
            }

            string? customerCode = null;
            if (accountOptions.Count > 0)
            {
                var cc = parsed.GetStr("customerCode").Trim();
                if (cc.Length > 0 && accountOptions.Any(o => string.Equals(o.Code, cc, StringComparison.OrdinalIgnoreCase)))
                    customerCode = cc;
            }

            var reply = parsed.GetStr("reply");
            return (new ChatFixResult(reply.Length > 0 ? reply : "แก้ไขเรียบร้อยแล้ว", h, outLines, materialCodes, shipToCode, customerCode), null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ImgSuffix(Dictionary<string, object?> h, string role) =>
        h.Get("hasImage") is true && role == "user" ? " [แนบภาพประกอบ]" : "";

    private static async Task<(string? Raw, string? Error)> CallClaudeAsync(string systemPrompt, List<Dictionary<string, object?>> history,
        string message, string? imageB64, string imageMediaType, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.AnthropicApiKey))
            return (null, "AnthropicApiKey is empty in config — appsettings Ocr:AnthropicApiKey was not loaded by the running app (check which appsettings.json/appsettings.{Env}.json the process reads, and that it was restarted)");
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
        if (!resp.IsSuccessStatusCode)
            return (null, $"Claude HTTP {(int)resp.StatusCode} (model={config.AnthropicModel}): {Trunc(respText, 400)}");
        using var doc = JsonDocument.Parse(respText);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return ("", null);
        var sb = new StringBuilder();
        foreach (var b in content.EnumerateArray())
            if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var txt))
                sb.Append(txt.GetString());
        return (sb.ToString(), null);
    }

    private static async Task<(string? Raw, string? Error)> CallGeminiAsync(string systemPrompt, List<Dictionary<string, object?>> history,
        string message, string? imageB64, string imageMediaType, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey))
            return (null, "GeminiApiKey is empty in config — appsettings Ocr:GeminiApiKey was not loaded by the running app (check which appsettings.json/appsettings.{Env}.json the process reads, and that it was restarted)");
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
        if (!resp.IsSuccessStatusCode)
            return (null, $"Gemini HTTP {(int)resp.StatusCode} (model={config.GeminiModel}): {Trunc(respText, 400)}");
        using var doc = JsonDocument.Parse(respText);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
            foreach (var c in cands.EnumerateArray())
                if (c.TryGetProperty("content", out var cc) && cc.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var p in parts.EnumerateArray())
                        if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        return (sb.ToString(), null);
    }

    private static async Task<(string? Raw, string? Error)> CallOpenAiAsync(string systemPrompt, List<Dictionary<string, object?>> history,
        string message, string? imageB64, string imageMediaType, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.OpenAiApiKey))
            return (null, "OpenAiApiKey is empty in config — appsettings Ocr:OpenAiApiKey was not loaded by the running app (check which appsettings.json/appsettings.{Env}.json the process reads, and that it was restarted)");
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

        // Newer models in this family (e.g. gpt-5.6-luna) reject the old 'max_tokens' name
        // outright ("Unsupported parameter: 'max_tokens' is not supported with this model. Use
        // 'max_completion_tokens' instead.") — matches the fix already applied to OpenAiOcr.cs.
        var body = new { model = config.OpenAiModel, max_completion_tokens = 3000, response_format = new { type = "json_object" }, messages };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Authorization", "Bearer " + config.OpenAiApiKey);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var resp = await Http.SendAsync(req, cts.Token);
        var respText = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            return (null, $"OpenAI HTTP {(int)resp.StatusCode} (model={config.OpenAiModel}): {Trunc(respText, 400)}");
        using var doc = JsonDocument.Parse(respText);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("content", out var contentEl))
            return (contentEl.GetString() ?? "", null);
        return ("", null);
    }
}
