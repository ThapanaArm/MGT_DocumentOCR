using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

public record SearchKeywordSuggestion(List<string> Keywords, string Reason);

/// <summary>
/// AI-assisted search-keyword suggestion — used when a live external-system search (SAP
/// Business Partner name search today; Material description / Ship-to search once those get
/// live-search panels of their own) finds nothing with the deterministic significant-word split,
/// and a person explicitly asks for AI help via a manual button. This never runs automatically —
/// only on request — and it never searches anything itself: given the raw text read off the
/// document and what kind of record it should match, it proposes up to a few alternative search
/// terms more likely to appear verbatim in the target system's own stored name/description
/// (accounting for abbreviated legal-entity wrappers, OCR noise, transliteration, common trade
/// names, etc.). The caller re-runs its normal deterministic search with whichever suggestion the
/// person picks.
/// Reuses the same multi-provider (Claude/Gemini/OpenAI) dispatch pattern as
/// <see cref="CompareAdvisor"/> and <see cref="ChatFix"/> — duplicated rather than shared, same
/// as those two, to keep each advisor self-contained.
/// </summary>
public static partial class SearchAdvisor
{
    private static readonly HttpClient Http = new();

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObjectRegex();

    public static async Task<SearchKeywordSuggestion?> SuggestKeywordsAsync(
        string rawText, string kind, string provider, AppConfig config)
    {
        var text = rawText?.Trim() ?? "";
        if (text.Length == 0) return null;

        var systemPrompt =
            $"You help find better search terms for a {kind} record in an external master-data system " +
            "(SAP or a CRM), given text read off a scanned document. A plain substring search for the whole " +
            "text often fails because the system's own stored name/description is shorter, abbreviated, in a " +
            "different language or script, or formatted differently (legal-entity wrappers like \"Co., Ltd.\"/" +
            "\"บริษัท ... จำกัด\" dropped, OCR noise, extra punctuation, etc.).\n\n" +
            "Suggest up to 3 short alternative search terms most likely to appear verbatim in the system's " +
            "stored record — e.g. the core brand/trade name on its own, a recognizable abbreviation, or a " +
            "transliteration — ordered most-promising first. Never just repeat the original text unchanged as " +
            "a term. If you genuinely can't do better than the original, return an empty keywords list and say " +
            "why in \"reason\".\n\n" +
            "Respond with ONLY a JSON object, no other text, shaped exactly like this:\n" +
            "{\"keywords\": [\"...\", ...], \"reason\": \"one short sentence\"}";

        var message = $"Text read off the document ({kind}): \"{text}\"";

        try
        {
            var raw = provider switch
            {
                "gemini" => await CallGeminiAsync(systemPrompt, message, config),
                "openai" => await CallOpenAiAsync(systemPrompt, message, config),
                _ => await CallClaudeAsync(systemPrompt, message, config),
            };
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var m = JsonObjectRegex().Match(raw);
            if (!m.Success) return null;
            var parsed = JsonSerializer.Deserialize<RawSuggestion>(m.Value, JsonOpts);
            if (parsed?.Keywords == null) return null;

            var keywords = parsed.Keywords
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Where(k => !string.Equals(k, text, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            return new SearchKeywordSuggestion(keywords, parsed.Reason ?? "");
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private record RawSuggestion(List<string>? Keywords, string? Reason);

    // Identical dispatch shape to CompareAdvisor's Call*Async methods (see that file for the
    // reasoning) — max_tokens trimmed down since this response is a handful of short strings.
    private static async Task<string?> CallClaudeAsync(string systemPrompt, string message, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.AnthropicApiKey)) return null;
        var body = new
        {
            model = config.AnthropicModel,
            max_tokens = 500,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = message } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", config.AnthropicApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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

    private static async Task<string?> CallGeminiAsync(string systemPrompt, string message, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey)) return null;
        var body = new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = message } } } },
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            generationConfig = new { responseMimeType = "application/json", maxOutputTokens = 500 },
        };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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

    private static async Task<string?> CallOpenAiAsync(string systemPrompt, string message, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.OpenAiApiKey)) return null;
        var body = new
        {
            model = config.OpenAiModel,
            max_tokens = 500,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = message },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Authorization", "Bearer " + config.OpenAiApiKey);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
