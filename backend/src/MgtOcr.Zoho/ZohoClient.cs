using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MgtOcr.Core.Config;

namespace MgtOcr.Zoho;

/// <summary>
/// Zoho CRM API v8 client — ported from another Megachem project's ZohoClient
/// (SapUomSyncServiceCore.Services), adapted to this codebase's conventions:
/// System.Text.Json.Nodes instead of Newtonsoft.Json, AppConfig instead of IConfiguration.
/// Refreshes its own OAuth access token (cached in-memory) and does insert/update-by-search.
///
/// appsettings.json ("ZohoConfig" section):
///   "AccountsUrl":  "https://accounts.zoho.com",
///   "ApiDomain":    "https://www.zohoapis.com",
///   "ClientId":     "1000.xxxx",
///   "ClientSecret": "xxxx",
///   "RefreshToken": "1000.xxxx"
///
/// Registered as a SINGLETON (not AddHttpClient&lt;T&gt; like SapClient) so the access-token
/// cache (_accessToken/_expireAt) is shared across requests instead of being rebuilt every call.
///
/// Note on System.Text.Json.Nodes vs Newtonsoft: a JsonNode throws if you try to attach it to a
/// second parent (Newtonsoft's JObject/JArray silently re-parent instead). Any JsonObject the
/// caller passes in (e.g. "record") may get reused across more than one call in this class
/// (see InsertOrUpdateAsync's insert-then-update-fallback), so every place that wraps a
/// caller-supplied node into a JsonArray/JsonObject calls .DeepClone() first.
/// </summary>
public class ZohoClient(AppConfig config, ILogger<ZohoClient> logger)
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private string? _accessToken;
    private DateTime _expireAt = DateTime.MinValue;

    private string ApiDomain =>
        string.IsNullOrWhiteSpace(config.ZohoApiDomain) ? "https://www.zohoapis.com" : config.ZohoApiDomain.TrimEnd('/');

    private async Task<string> GetAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expireAt)
            return _accessToken;

        var accounts = string.IsNullOrWhiteSpace(config.ZohoAccountsUrl) ? "https://accounts.zoho.com" : config.ZohoAccountsUrl.TrimEnd('/');
        string url = $"{accounts}/oauth/v2/token" +
                     $"?refresh_token={Uri.EscapeDataString(config.ZohoRefreshToken)}" +
                     $"&client_id={Uri.EscapeDataString(config.ZohoClientId)}" +
                     $"&client_secret={Uri.EscapeDataString(config.ZohoClientSecret)}" +
                     $"&grant_type=refresh_token";
        using var resp = await _http.PostAsync(url, null);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho token error {(int)resp.StatusCode}: {body}");

        var j = JsonNode.Parse(body)!.AsObject();
        _accessToken = AsStr(j["access_token"]) ?? throw new Exception("Zoho did not return access_token: " + body);
        int expiresIn = AsInt(j["expires_in"]) ?? 3600;
        _expireAt = DateTime.UtcNow.AddSeconds(expiresIn - 300); // 5-minute safety margin
        logger.LogInformation("[ZOHO] token refreshed, valid until {ExpireAt:u}", _expireAt);
        return _accessToken;
    }

    // ── search by key -> POST(insert) / PUT(update) ──
    public async Task<ZohoUpsertResult> InsertOrUpdateAsync(
        string module, JsonObject record, string keyField, string keyValue, string? sapKey = null)
    {
        string? id = await FindRecordIdAsync(module, keyField, keyValue);
        if (id is not null)
            return await UpdateAsync(module, id, record, sapKey);

        var res = await InsertAsync(module, record, sapKey);
        // Search missed it (Zoho index lag) but it actually already exists -> Zoho answers
        // DUPLICATE_DATA with the existing record's id -> PUT against that id instead
        // (idempotent, does not depend on search-index freshness).
        if (string.Equals(res.Status, "DUPLICATE_DATA", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(res.ZohoId))
            return await UpdateAsync(module, res.ZohoId, record, sapKey);
        return res;
    }

    public async Task<ZohoUpsertResult> InsertAsync(string module, JsonObject record, string? sapKey = null)
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/{module}";
        return await SendWriteAsync(HttpMethod.Post, url, record, token, sapKey);
    }

    public async Task<ZohoUpsertResult> UpdateAsync(string module, string recordId, JsonObject record, string? sapKey = null)
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/{module}/{Uri.EscapeDataString(recordId)}";
        return await SendWriteAsync(HttpMethod.Put, url, record, token, sapKey);
    }

    /// <summary>
    /// GET /crm/v8/{module}/search?criteria=({field}:equals:{value}) — returns the exact-match
    /// record id, or null. Zoho's "equals" behaves like "contains" on text fields, so an exact
    /// match still needs to be filtered client-side. 204 = not found.
    /// </summary>
    public async Task<string?> FindRecordIdAsync(string module, string field, string value)
    {
        string token = await GetAccessTokenAsync();
        string criteria = $"({field}:equals:{value})";
        string url = $"{ApiDomain}/crm/v8/{module}/search?criteria={Uri.EscapeDataString(criteria)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            logger.LogInformation("[ZOHO] search {Field}={Value} -> not found (POST)", field, value);
            return null;
        }
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho search error {(int)resp.StatusCode}: {body}");
        if (JsonNode.Parse(body)?["data"] is not JsonArray data) return null;
        foreach (var d in data)
        {
            if (string.Equals(AsStr(d?[field]), value, StringComparison.Ordinal))
            {
                var id = AsStr(d?["id"]);
                logger.LogInformation("[ZOHO] search {Field}={Value} -> id {Id} (PUT)", field, value, id);
                return id;
            }
        }
        return null;
    }

    private async Task<ZohoUpsertResult> SendWriteAsync(
        HttpMethod method, string url, JsonObject record, string token, string? sapKey)
    {
        var payload = new JsonObject
        {
            ["data"] = new JsonArray(record.DeepClone()),
            ["trigger"] = new JsonArray() // skip workflow/approval; drop this line to let it run
        };
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        logger.LogInformation("[ZOHO] {Method} {Url} -> {Status}: {Body}",
            method, url, (int)resp.StatusCode, body);
        if ((int)resp.StatusCode == 429)
            throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode && string.IsNullOrEmpty(body))
            throw new Exception($"Zoho write error {(int)resp.StatusCode}");
        return ToResult(JsonNode.Parse(body)?["data"]?[0], sapKey);
    }

    // ── batch UPSERT (max 100/call) — for initial load / scheduled jobs ──
    // POST /crm/v8/{module}/upsert + duplicate_check_fields -> Zoho decides insert/update
    // server-side. No per-record search, no index-lag / DUPLICATE_DATA handling needed.
    public async Task<List<ZohoUpsertResult>> UpsertAsync(
        string module, IList<JsonObject> records, IList<string> sapKeys, IList<string> duplicateCheckFields)
    {
        if (records.Count == 0) return new();
        if (records.Count > 100) throw new Exception("Zoho upsert: exceeds 100 records/call — batch it");
        string token = await GetAccessTokenAsync();
        var payload = new JsonObject
        {
            ["data"] = new JsonArray(records.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
            ["duplicate_check_fields"] = new JsonArray(duplicateCheckFields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray())
        };
        string url = $"{ApiDomain}/crm/v8/{module}/upsert";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        logger.LogInformation("[ZOHO] upsert {Module} x{Count} -> {Status}", module, records.Count, (int)resp.StatusCode);
        if ((int)resp.StatusCode == 429) throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode && string.IsNullOrEmpty(body))
            throw new Exception($"Zoho upsert error {(int)resp.StatusCode}");
        var list = new List<ZohoUpsertResult>();
        if (JsonNode.Parse(body)?["data"] is JsonArray data)
            for (int i = 0; i < data.Count; i++)
                list.Add(ToResult(data[i], i < sapKeys.Count ? sapKeys[i] : null));
        return list;
    }

    // ── READ: COQL (/crm/v8/coql) — paginated (limit offset,PAGE) until exhausted.
    // Shares the same token cache as the write path, no extra refresh.
    public async Task<List<JsonObject>> QueryCoqlAsync(
        string module, string selectFields, string where, string orderByField)
    {
        string token = await GetAccessTokenAsync();
        var list = new List<JsonObject>();
        int offset = 0;
        const int PAGE = 200;
        bool more = true;
        while (more)
        {
            string q = $"select {selectFields} from {module} " +
                       $"where {where} " +
                       $"order by {orderByField} limit {offset}, {PAGE}";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiDomain}/crm/v8/coql");
            req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
            req.Content = new StringContent(
                new JsonObject { ["select_query"] = q }.ToJsonString(),
                Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NoContent)
                break;
            string body = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 429)
                throw new Exception("Zoho 429 Too Many Requests: " + body);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Zoho COQL {(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]} | query: {q}");
            var root = JsonNode.Parse(body)!.AsObject();
            if (root["data"] is not JsonArray data || data.Count == 0)
                break;
            foreach (var row in data)
                if (row is JsonObject rowObj) list.Add(rowObj);
            more = AsBool(root["info"]?["more_records"]);
            offset += PAGE;
        }
        logger.LogInformation("[ZOHO] COQL {Module} -> {Count} rows", module, list.Count);
        return list;
    }

    // Zoho v8 returns "code":"SUCCESS" (not "status"); on a duplicate it stashes the id of the
    // record it collided with in ZohoId.
    // On error Zoho always attaches "details" (e.g. the api_name of the offending field); fold
    // that into Message so a caller logging/emailing the result sees which field, not just a
    // generic "invalid data".
    private static ZohoUpsertResult ToResult(JsonNode? d, string? sapKey)
    {
        string? code = AsStr(d?["code"]);
        bool success = string.Equals(code, "SUCCESS", StringComparison.OrdinalIgnoreCase);
        string baseMsg = AsStr(d?["message"]) ?? "";
        string msg = baseMsg;
        if (!success && d?["details"] is JsonObject details && details.Count > 0)
        {
            // api_name = the field Zoho says is the problem (most useful for INVALID_DATA /
            // MANDATORY_NOT_FOUND) -> surface it first.
            string? apiName = AsStr(details["api_name"]);
            var extras = new List<string>();
            if (!string.IsNullOrWhiteSpace(apiName)) extras.Add($"field={apiName}");
            foreach (var kv in details)
            {
                // "id" inside details is the id of the colliding (duplicate) record, not the
                // cause of the error -> don't repeat it in the message.
                if (string.Equals(kv.Key, "api_name", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(kv.Key, "id", StringComparison.OrdinalIgnoreCase)) continue;
                extras.Add($"{kv.Key}={FormatValue(kv.Value)}");
            }
            if (extras.Count > 0) msg = $"{baseMsg} ({string.Join(", ", extras)})";
        }
        // keep the message from growing unbounded wherever it ends up logged/stored
        if (msg.Length > 500) msg = msg[..500];
        return new ZohoUpsertResult
        {
            SapKey = sapKey!,
            ZohoId = (AsStr(d?["details"]?["id"]) ?? AsStr(d?["duplicate_record"]?["id"]))!,
            Status = success ? "success" : (code ?? "error"),
            Message = msg
        };
    }

    // GET /crm/v8/settings/modules — every module in this org (standard + custom), with its
    // real api_name and display labels. Used to find out whether a "Ship To" module actually
    // exists in Megachem's Zoho CRM (separate from the "Ship to ..." sub-fields on Accounts)
    // WITHOUT guessing its api_name — list what's really there instead.
    public async Task<JsonArray> GetModulesAsync()
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/settings/modules";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 429) throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho modules {(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]}");
        return JsonNode.Parse(body)?["modules"] as JsonArray ?? new JsonArray();
    }

    // GET /crm/v8/settings/fields?module={module} — every field defined on a module (standard +
    // custom), INCLUDING subform container fields. The manual's field tables never give a
    // subform container an "API Name" (only its individual columns), so this is how
    // ZohoSalesOrderClient resolves the Ordered Items subform's real api_name instead of
    // guessing it (same "list what's really there" approach as GetModulesAsync/
    // GetRelatedListsAsync above).
    public async Task<JsonArray> GetFieldsAsync(string module)
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/settings/fields?module={Uri.EscapeDataString(module)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NoContent) return new JsonArray();
        string body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 429) throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho fields {(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]}");
        return JsonNode.Parse(body)?["fields"] as JsonArray ?? new JsonArray();
    }

    // GET /crm/v8/settings/related_lists?module={module} — every related list configured on a
    // module (standard + custom), with its real api_name. Used to find out whether Accounts has
    // a related list pointing at a separate "Ship To" style module, without guessing its name.
    public async Task<JsonArray> GetRelatedListsAsync(string module)
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/settings/related_lists?module={Uri.EscapeDataString(module)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NoContent) return new JsonArray();
        string body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 429) throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho related_lists {(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]}");
        return JsonNode.Parse(body)?["related_lists"] as JsonArray ?? new JsonArray();
    }

    // GET /crm/v8/{module}/{id}/{relatedListApiName} — the actual linked records for one related
    // list on one record (e.g. every record in a separate "Ship To" module attached to this
    // Account), once GetRelatedListsAsync above has confirmed the related list's real api_name.
    public async Task<JsonArray> GetRelatedRecordsAsync(string module, string recordId, string relatedListApiName)
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/{module}/{Uri.EscapeDataString(recordId)}/{Uri.EscapeDataString(relatedListApiName)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == HttpStatusCode.NotFound)
            return new JsonArray();
        string body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 429) throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho related records {(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]}");
        return JsonNode.Parse(body)?["data"] as JsonArray ?? new JsonArray();
    }

    // GET /crm/v8/{module}/{id}/__timeline — audit trail of who edited/actioned the record
    public async Task<JsonArray> GetTimelineAsync(string module, string recordId)
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/{module}/{Uri.EscapeDataString(recordId)}/__timeline?per_page=200";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == HttpStatusCode.NotFound)
            return new JsonArray();
        string body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 429) throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho timeline {(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]}");
        return JsonNode.Parse(body)?["__timeline"] as JsonArray ?? new JsonArray();
    }

    /// <summary>
    /// GET /crm/v8/{module}/{id}?fields=... — fetches a single record by id, with any
    /// subform/related-list fields named in "fields". Returns null if not found (404 or 204).
    /// </summary>
    public async Task<JsonObject?> GetByIdAsync(string module, string recordId, string? fields = null)
    {
        string token = await GetAccessTokenAsync();
        string url = $"{ApiDomain}/crm/v8/{module}/{Uri.EscapeDataString(recordId)}";
        if (!string.IsNullOrEmpty(fields))
            url += $"?fields={Uri.EscapeDataString(fields)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation("[ZOHO] GET {Module}/{Id} -> not found ({Status})", module, recordId, (int)resp.StatusCode);
            return null;
        }
        string body = await resp.Content.ReadAsStringAsync();
        logger.LogInformation("[ZOHO] GET {Module}/{Id} -> {Status}", module, recordId, (int)resp.StatusCode);
        if ((int)resp.StatusCode == 429)
            throw new Exception("Zoho 429 Too Many Requests: " + body);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Zoho get error {(int)resp.StatusCode}: {body}");
        var data = JsonNode.Parse(body)?["data"] as JsonArray;
        return (data != null && data.Count > 0 && data[0] is JsonObject first) ? first : null;
    }

    /// <summary>
    /// GET /crm/v8/users?type=AllUsers — all Zoho users (a special endpoint, not a COQL module).
    /// Returns id, full_name, first_name, last_name, email, role, profile, ... per user.
    /// </summary>
    public async Task<List<JsonObject>> GetUsersAsync(string type = "AllUsers")
    {
        string token = await GetAccessTokenAsync();
        var list = new List<JsonObject>();
        int page = 1; const int PER_PAGE = 200; bool more = true;
        while (more)
        {
            string url = $"{ApiDomain}/crm/v8/users?type={Uri.EscapeDataString(type)}&page={page}&per_page={PER_PAGE}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NoContent) break;
            string body = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 429) throw new Exception("Zoho 429 Too Many Requests: " + body);
            if (!resp.IsSuccessStatusCode) throw new Exception($"Zoho users {(int)resp.StatusCode}: {body[..Math.Min(400, body.Length)]}");
            var root = JsonNode.Parse(body)!.AsObject();
            if (root["users"] is not JsonArray users || users.Count == 0) break;
            foreach (var u in users)
                if (u is JsonObject uObj) list.Add(uObj);
            more = AsBool(root["info"]?["more_records"]);
            page++;
        }
        logger.LogInformation("[ZOHO] users -> {Count}", list.Count);
        return list;
    }

    // ── small JsonNode helpers (System.Text.Json.Nodes has no Newtonsoft-style implicit casts) ──
    private static string? AsStr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? AsInt(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static bool AsBool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static string FormatValue(JsonNode? n) =>
        n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : n?.ToString() ?? "";
}

public class ZohoUpsertResult
{
    public string SapKey { get; set; } = null!;
    public string ZohoId { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string Message { get; set; } = null!;
}
