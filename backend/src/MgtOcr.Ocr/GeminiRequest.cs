using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

public static class GeminiRequest
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public sealed class Scope : IDisposable
    {
        private readonly Scope? _previous = Current.Value;
        internal long? JobId { get; }
        internal int? DocId { get; }
        internal string? Module { get; }
        public List<GeminiRequestUsage> Requests { get; } = [];

        internal Scope(long? jobId, int? docId, string? module)
        {
            JobId = jobId;
            DocId = docId;
            Module = module;
            Current.Value = this;
        }

        public void Dispose() => Current.Value = _previous;
    }

    public static Scope Begin(long? jobId = null, int? docId = null, string? module = null) =>
        new(jobId, docId, module);

    internal static Scope? Active => Current.Value;
    internal static Scope? BeginIfNeeded(string module) => Current.Value == null ? Begin(module: module) : null;

    public static async Task<(int Status, string Body)> SendAsync(HttpClient client, AppConfig config,
        string payload, CancellationToken ct, string operation, int[]? pages = null, int attempt = 1)
    {
        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
            throw new InvalidOperationException("The Greenleafchemical Gemini key is not configured.");

        var started = DateTime.UtcNow;
        var requestId = Guid.NewGuid();
        var scope = Current.Value;
        int? status = null;
        string? body = null;
        var outcome = "NO_RESPONSE";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", config.GeminiApiKey);
            using var response = await client.SendAsync(request, ct);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct);
            outcome = response.IsSuccessStatusCode ? "HTTP_SUCCESS" : "HTTP_ERROR";
            return (status.Value, body);
        }
        catch (OperationCanceledException)
        {
            outcome = "CANCELLED_OR_TIMEOUT";
            throw;
        }
        finally
        {
            var (input, output, thinking, cached, responseId) = ReadUsage(body);
            var (inputRate, outputRate, cachedRate) = config.GeminiModel == "gemini-2.5-flash"
                ? (0.30m, 2.50m, 0.03m) : ((decimal?)null, (decimal?)null, (decimal?)null);
            var usage = new GeminiRequestUsage(requestId, scope?.JobId, scope?.DocId, scope?.Module,
                config.GeminiEnvironment, "Api OCR Greenleafchemical", config.GeminiModel, operation, attempt,
                pages ?? [], started, DateTime.UtcNow, outcome, status, responseId,
                input, output, thinking, cached, inputRate, outputRate, cachedRate);
            scope?.Requests.Add(usage);
            if (config.GeminiUsageSink != null)
                await config.GeminiUsageSink(usage);
        }
    }

    private static (int? Input, int? Output, int? Thinking, int? Cached, string? ResponseId) ReadUsage(string? body)
    {
        if (string.IsNullOrEmpty(body)) return default;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            var id = root.TryGetProperty("responseId", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return (null, null, null, null, id);
            int? Read(string key) => usage.TryGetProperty(key, out var element) &&
                element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n) && n >= 0 ? n : null;
            var input = Read("promptTokenCount");
            var cached = Read("cachedContentTokenCount") ?? 0;
            return (input, Read("candidatesTokenCount"),
                Read("thoughtsTokenCount") ?? (input.HasValue ? 0 : null),
                input >= cached ? cached : null, id);
        }
        catch (JsonException) { return default; }
    }
}
