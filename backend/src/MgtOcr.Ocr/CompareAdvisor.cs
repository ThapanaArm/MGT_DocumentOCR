using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

public record CompareField(string Label, string? Value);
public record CompareCandidate(string Id, string Label, List<CompareField> Fields);
public record CompareVerdict(string CandidateId, string Match, int Confidence, string Reason);

/// <summary>
/// What the AI thinks should happen next, given the person's chat message (and, on a follow-up
/// turn, the candidate it suggested last time). "Action" is one of:
///   select  — the person's own words told it which candidate to use (an instruction, or a
///             plain confirmation of a candidate it already suggested) -> CandidateId is set.
///   suggest — it has an opinion on the best match, but the person hasn't said to apply it yet.
///   none    — can't tell, or nothing to react to.
/// The AI never returns "select" on its own confidence alone — only in direct response to
/// something the person said. Applying a "select" (creating the local master record + setting
/// it as the match) always happens in the frontend's own code, never here.
/// </summary>
public record CompareDecision(string? CandidateId, string Action, string Reason);

public record CompareOutcome(List<CompareVerdict> Verdicts, CompareDecision? Decision);

/// <summary>
/// AI-assisted "is this the same record?" comparison — used when a live external-system search
/// (SAP Business Partner, Zoho CRM Account, and in future Vendor/Ship-to/Material lookups)
/// returns more than one candidate and a person has to pick. Reuses the same multi-provider
/// (Claude/Gemini/OpenAI) dispatch pattern as <see cref="ChatFix"/>, but as a one-shot
/// question/answer rather than a multi-turn editing chat: no history, no image, structured JSON
/// verdict per candidate.
///
/// This never applies anything itself — it only judges candidates and, when given the person's
/// chat message, returns a CompareDecision of what to do about it (see that type). The actual
/// apply step (creating the local master record + setting it as the match) always happens in
/// the frontend's own code, and only for decision.action == "select" — i.e. only in direct
/// response to something the person explicitly said, never on the AI's confidence alone.
/// </summary>
public static partial class CompareAdvisor
{
    private static readonly HttpClient Http = new();

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObjectRegex();

    public static async Task<CompareOutcome?> CompareAsync(
        List<CompareField> docFields, List<CompareCandidate> candidates, string provider, AppConfig config,
        string? instruction = null, string? priorSuggestedCandidateId = null)
    {
        if (candidates.Count == 0) return new CompareOutcome([], null);

        var docText = string.Join("\n", docFields.Select(f => $"- {f.Label}: {(string.IsNullOrWhiteSpace(f.Value) ? "(blank)" : f.Value)}"));
        var candText = string.Join("\n\n", candidates.Select(c =>
            $"Candidate id \"{c.Id}\" ({c.Label}):\n" +
            string.Join("\n", c.Fields.Select(f => $"  - {f.Label}: {(string.IsNullOrWhiteSpace(f.Value) ? "(blank)" : f.Value)}"))));

        var systemPrompt =
            "You help match a customer/vendor/ship-to/material read off a scanned document against candidate " +
            "master-data records from an external system (SAP Business Partner or Zoho CRM Account). A person is " +
            "chatting with you about which candidate is correct.\n\n" +
            "Rules:\n" +
            "- Judge each candidate independently against the document's fields.\n" +
            "- \"match\" is one of: \"yes\" (clearly the same real-world entity), \"no\" (clearly a different one), " +
            "or \"maybe\" (plausible but something doesn't line up, e.g. a different branch/address/spelling).\n" +
            "- \"confidence\" is an integer 0-100.\n" +
            "- \"reason\" is one short sentence in English citing the specific fields that agree or disagree " +
            "(e.g. \"Tax ID matches exactly but the branch differs\") — never a generic statement.\n\n" +
            "You also decide what to DO about the person's message, in a \"decision\" object:\n" +
            "- \"select\" — the person's message explicitly told you which candidate to use (by name, id, " +
            "position like \"the first one\"/\"ตัวแรก\", or a plain confirmation like \"yes\"/\"ใช่\"/\"ตกลง\" of a " +
            "candidate you already suggested). decision.candidateId MUST be set to one of the given candidate ids.\n" +
            "- \"suggest\" — you have a confident opinion on the best match, but the person has not explicitly " +
            "told you to apply it yet. decision.candidateId is your suggestion.\n" +
            "- \"none\" — you can't tell yet, or the person's message was empty / unrelated to picking one. " +
            "decision.candidateId is null.\n" +
            "Never use \"select\" just because you're confident in a match — only when the person's own words " +
            "authorized it (an instruction, or a confirmation of your own prior suggestion).\n\n" +
            "Respond with ONLY a JSON object, no other text, shaped exactly like this:\n" +
            "{\"verdicts\": [{\"candidateId\": \"...\", \"match\": \"yes\"|\"no\"|\"maybe\", \"confidence\": 0-100, \"reason\": \"...\"}], " +
            "\"decision\": {\"candidateId\": \"...\"|null, \"action\": \"select\"|\"suggest\"|\"none\", \"reason\": \"...\"}}";

        var contextLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(priorSuggestedCandidateId))
            contextLines.Add($"You previously suggested candidate id \"{priorSuggestedCandidateId}\" as the likely match.");
        if (!string.IsNullOrWhiteSpace(instruction))
            contextLines.Add($"The person just said: \"{instruction}\"");
        else
            contextLines.Add("The person has not said anything yet — just compare the candidates (decision.action must be \"none\").");

        var message = $"Document fields:\n{docText}\n\nCandidates:\n{candText}\n\n{string.Join("\n", contextLines)}";

        // The provider call is intentionally OUTSIDE the try/catch below: an HTTP failure (bad API
        // key, unknown model, quota, timeout) throws with the real status + response body so the
        // controller can show it, instead of being swallowed into a misleading "check your API key".
        // Only an empty key returns null (a genuine "not configured" case).
        var raw = provider switch
        {
            "gemini" => await CallGeminiAsync(systemPrompt, message, config),
            "openai" => await CallOpenAiAsync(systemPrompt, message, config),
            _ => await CallClaudeAsync(systemPrompt, message, config),
        };
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var m = JsonObjectRegex().Match(raw);
            if (!m.Success)
                throw new InvalidOperationException($"AI reply contained no JSON object. Reply: {Trunc(raw, 400)}");
            var parsed = JsonSerializer.Deserialize<RawWrapper>(m.Value, JsonOpts);
            if (parsed?.Verdicts == null)
                throw new InvalidOperationException($"AI reply had no 'verdicts'. Reply: {Trunc(raw, 400)}");

            // Keep only verdicts/decisions for candidates that were actually sent, so a
            // hallucinated id never surfaces in the UI or gets applied.
            var knownIds = candidates.Select(c => c.Id).ToHashSet();
            var verdicts = parsed.Verdicts
                .Where(v => v.CandidateId != null && knownIds.Contains(v.CandidateId))
                .Select(v => new CompareVerdict(v.CandidateId!, NormalizeMatch(v.Match), Math.Clamp(v.Confidence, 0, 100), v.Reason ?? ""))
                .ToList();

            CompareDecision? decision = null;
            if (parsed.Decision is { } d && !string.IsNullOrWhiteSpace(d.Action))
            {
                var candidateId = d.CandidateId != null && knownIds.Contains(d.CandidateId) ? d.CandidateId : null;
                var action = NormalizeAction(d.Action);
                // "select"/"suggest" without a valid candidate id is meaningless — fall back to
                // "none" rather than let the frontend try to apply a match to nothing.
                if (candidateId == null && action != "none") action = "none";
                decision = new CompareDecision(candidateId, action, d.Reason ?? "");
            }

            return new CompareOutcome(verdicts, decision);
        }
        catch (Exception ex)
        {
            // Surface what the AI actually returned so the real cause is visible instead of a
            // silent "no verdict" / generic key message.
            throw new InvalidOperationException($"AI reply could not be parsed: {ex.Message}", ex);
        }
    }

    private static string NormalizeMatch(string? m) => m?.Trim().ToLowerInvariant() switch
    {
        "yes" => "yes",
        "no" => "no",
        _ => "maybe",
    };

    private static string NormalizeAction(string? a) => a?.Trim().ToLowerInvariant() switch
    {
        "select" => "select",
        "suggest" => "suggest",
        _ => "none",
    };

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private record RawWrapper(List<RawVerdict>? Verdicts, RawDecision? Decision);
    private record RawVerdict(string? CandidateId, string? Match, int Confidence, string? Reason);
    private record RawDecision(string? CandidateId, string? Action, string? Reason);

    private static async Task<string?> CallClaudeAsync(string systemPrompt, string message, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.AnthropicApiKey)) return null;
        var body = new
        {
            model = config.AnthropicModel,
            max_tokens = 1500,
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
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Claude HTTP {(int)resp.StatusCode} (model={config.AnthropicModel}): {Trunc(respText, 400)}");
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
        // Mirror the WORKING OCR Gemini call (see GeminiOcr): fold the system prompt into the user
        // text, and use only { temperature, maxOutputTokens } — no responseMimeType ("JSON mode")
        // and no systemInstruction, which is what made this call fail while OCR (same key + model)
        // kept working. The JSON body is extracted from the reply downstream regardless.
        var body = new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = systemPrompt + "\n\n" + message } } } },
            // Higher cap than OCR: newer "thinking" Gemini models can spend part of the budget on
            // internal reasoning, so a small cap can return an empty/truncated answer.
            generationConfig = new { temperature = 0, maxOutputTokens = 8192 },
        };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var resp = await Http.SendAsync(req, cts.Token);
        var respText = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini HTTP {(int)resp.StatusCode} (model={config.GeminiModel}): {Trunc(respText, 400)}");
        using var doc = JsonDocument.Parse(respText);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
            foreach (var c in cands.EnumerateArray())
                if (c.TryGetProperty("content", out var cc) && cc.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var p in parts.EnumerateArray())
                        if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        var text = sb.ToString();
        if (!string.IsNullOrWhiteSpace(text)) return text;
        // 200 OK but no text — surface WHY (finishReason such as MAX_TOKENS/SAFETY, or promptFeedback)
        // instead of silently returning empty and showing a misleading "check your API key".
        string why = "empty text";
        if (cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0
            && cands[0].TryGetProperty("finishReason", out var fr))
            why = $"finishReason={fr.GetString()}";
        else if (doc.RootElement.TryGetProperty("promptFeedback", out var pf))
            why = "promptFeedback=" + pf.GetRawText();
        throw new HttpRequestException($"Gemini returned no usable text ({why}, model={config.GeminiModel}). Raw: {Trunc(respText, 300)}");
    }

    private static async Task<string?> CallOpenAiAsync(string systemPrompt, string message, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.OpenAiApiKey)) return null;
        var body = new
        {
            model = config.OpenAiModel,
            max_tokens = 1500,
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
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI HTTP {(int)resp.StatusCode} (model={config.OpenAiModel}): {Trunc(respText, 400)}");
        using var doc = JsonDocument.Parse(respText);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("content", out var contentEl))
            return contentEl.GetString() ?? "";
        return "";
    }
}
