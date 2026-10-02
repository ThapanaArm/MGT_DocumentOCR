using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Api.Services;

public record GraphUploadResult(string ItemId, string RemotePath, string? WebUrl);

// One step of the admin connection test (token -> library -> upload -> delete).
public record ArchiveTestStep(string Step, bool Ok, string Detail);

// Minimal Microsoft Graph client for the SharePoint archive: app-only (client-credentials) token
// per target, upload to a document library (drive) and download by item id. Nothing here runs
// unless Archive:Enabled=true and a target is configured (see AppConfig.ArchiveTargets).
public class GraphArchiveClient(AppConfig config, IHttpClientFactory httpFactory, ILogger<GraphArchiveClient> log)
{
    private const int SmallLimit = 4 * 1024 * 1024;          // Graph simple upload limit
    private const int ChunkSize = 320 * 1024 * 16;            // must be a multiple of 320 KiB

    private readonly Dictionary<string, (string Token, DateTime Expires)> _tokens = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Most specific usable target wins: company+module, company+"*", "*"+module, then "*"+"*".
    // Null = this company/module is not archived (yet) - callers skip it, they never mark it failed.
    public SharePointTarget? TargetFor(string companyCode, string module)
    {
        static bool Eq(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        var usable = config.ArchiveTargets.Where(t => t.IsUsable).ToList();
        return usable.FirstOrDefault(t => Eq(t.Company, companyCode) && Eq(t.Module, module))
            ?? usable.FirstOrDefault(t => Eq(t.Company, companyCode) && t.Module.Trim() == "*")
            ?? usable.FirstOrDefault(t => t.Company.Trim() == "*" && Eq(t.Module, module))
            ?? usable.FirstOrDefault(t => t.Company.Trim() == "*" && t.Module.Trim() == "*");
    }

    private HttpClient Http() => httpFactory.CreateClient(nameof(GraphArchiveClient));

    private async Task<string> TokenAsync(SharePointTarget t, CancellationToken ct)
    {
        var key = $"{t.TenantId}|{t.ClientId}";
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(key, out var c) && c.Expires > DateTime.UtcNow.AddMinutes(2)) return c.Token;
            using var res = await Http().PostAsync(
                $"https://login.microsoftonline.com/{t.TenantId}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = t.ClientId, ["client_secret"] = t.ClientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials",
                }), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Graph token request failed ({(int)res.StatusCode}): {Trim(body)}");
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var secs = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3000;
            _tokens[key] = (token, DateTime.UtcNow.AddSeconds(secs));
            return token;
        }
        finally { _tokenLock.Release(); }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
    private static string Seg(string s) => Uri.EscapeDataString(s);
    // One SharePoint folder/file name: no \" * : < > ? / \ | # %, no leading/trailing spaces or
    // trailing dots, at most 120 characters. A customer called "A/B Co" becomes "A_B Co".
    public static string SafeName(string s)
    {
        foreach (var ch in "\"*:<>?/\\|#%\t\r\n") s = s.Replace(ch, '_');
        s = s.Trim().TrimEnd('.').Trim();
        if (s.Length > 120) s = s[..120].Trim().TrimEnd('.');
        return s;
    }

    /// <summary>Builds a folder path from separate parts, each made safe on its own, so a "/" inside
    /// a name never creates an extra folder. Blank parts become "_Unknown".</summary>
    public static string JoinFolder(params string?[] parts) =>
        string.Join('/', parts.Select(p => SafeName(p ?? "")).Select(p => p == "" ? "_Unknown" : p));

    /// <summary>Folder layout: {RootFolder}/{subPath}/{yyyy}/{MM}/{file} - or, for a DateFirst target
    /// (GLC), {RootFolder}/{yyyy}/{MM}/{subPath}/{file} - where subPath is what the post decided
    /// (MGT Sales Order: "{Sales}/{Code}_{Customer}", GLC Sales Order: "{Code}_{Customer}") or, when none was stored,
    /// "{Company}/{Module}". The name keeps the on-disk (unique) name so two uploads with the same
    /// original name never collide.</summary>
    public static string BuildRemotePath(SharePointTarget t, string company, string module, DateTime? postedAt, string localPath, string? subPath = null)
    {
        var d = postedAt ?? DateTime.Now;
        var parts = new List<string>();
        foreach (var p in t.RootFolder.Split('/', StringSplitOptions.RemoveEmptyEntries)) parts.Add(SafeName(p));
        if (t.DateFirst) { parts.Add(d.ToString("yyyy")); parts.Add(d.ToString("MM")); }
        if (!string.IsNullOrWhiteSpace(subPath))
            parts.AddRange(subPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName).Where(p => p != ""));
        else
        {
            parts.Add(SafeName(company));
            if (!string.IsNullOrWhiteSpace(module)) parts.Add(SafeName(module.Trim().ToUpperInvariant()));
        }
        if (!t.DateFirst) { parts.Add(d.ToString("yyyy")); parts.Add(d.ToString("MM")); }
        parts.Add(SafeName(Path.GetFileName(localPath)));
        return string.Join('/', parts);
    }

    public async Task<GraphUploadResult> UploadAsync(SharePointTarget t, string localPath, string remotePath, CancellationToken ct)
    {
        var fi = new FileInfo(localPath);
        if (!fi.Exists) throw new FileNotFoundException("Local file not found", localPath);
        var token = await TokenAsync(t, ct);
        var encoded = string.Join('/', remotePath.Split('/').Select(Seg));
        var baseUrl = $"https://graph.microsoft.com/v1.0/drives/{t.DriveId}/root:/{encoded}:";
        var http = Http();

        string body;
        if (fi.Length <= SmallLimit)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/content?@microsoft.graph.conflictBehavior=replace");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await using var fs = File.OpenRead(localPath);
            req.Content = new StreamContent(fs);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var res = await http.SendAsync(req, ct);
            body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Graph upload failed ({(int)res.StatusCode}): {Trim(body)}");
        }
        else
        {
            string uploadUrl;
            using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/createUploadSession"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent("{\"item\":{\"@microsoft.graph.conflictBehavior\":\"replace\"}}", Encoding.UTF8, "application/json");
                using var res = await http.SendAsync(req, ct);
                var s = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Graph createUploadSession failed ({(int)res.StatusCode}): {Trim(s)}");
                using var d = JsonDocument.Parse(s);
                uploadUrl = d.RootElement.GetProperty("uploadUrl").GetString()!;
            }

            body = "";
            await using var fs = File.OpenRead(localPath);
            var buf = new byte[ChunkSize];
            long sent = 0;
            while (sent < fi.Length)
            {
                var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(ChunkSize, fi.Length - sent)), ct);
                if (n <= 0) break;
                using var chunk = new HttpRequestMessage(HttpMethod.Put, uploadUrl);   // no auth header on the session URL
                chunk.Content = new ByteArrayContent(buf, 0, n);
                chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(sent, sent + n - 1, fi.Length);
                using var res = await http.SendAsync(chunk, ct);
                var s = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Graph chunk upload failed ({(int)res.StatusCode}): {Trim(s)}");
                sent += n;
                if (sent >= fi.Length) body = s;
            }
            if (body == "") throw new InvalidOperationException("Graph upload session ended without an item response");
        }

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString()!;
        var webUrl = doc.RootElement.TryGetProperty("webUrl", out var w) ? w.GetString() : null;

        // Verify size before the caller is allowed to delete anything locally.
        if (doc.RootElement.TryGetProperty("size", out var sz) && sz.GetInt64() != fi.Length)
            throw new InvalidOperationException($"Uploaded size {sz.GetInt64()} != local size {fi.Length}");

        log.LogInformation("Archived {Local} -> {Remote}", localPath, remotePath);
        return new GraphUploadResult(id, remotePath, webUrl);
    }

    /// <summary>Admin connection test: gets a token, reads the library, uploads a tiny text file into
    /// {RootFolder}/_connection-test/ and deletes it again. Stops at the first failing step and says
    /// what that status code usually means, so the admin page can show it without reading logs.</summary>
    public async Task<List<ArchiveTestStep>> TestAsync(SharePointTarget t, CancellationToken ct)
    {
        var steps = new List<ArchiveTestStep>();
        string token;
        try { token = await TokenAsync(t, ct); steps.Add(new("Get app token", true, "OK")); }
        catch (Exception e) { steps.Add(new("Get app token", false, e.Message + " - check TenantId / ClientId / ClientSecret (use the secret VALUE, not the Secret ID, and check it has not expired)")); return steps; }

        var http = Http();
        async Task<(int Code, string Body)> Send(HttpMethod m, string url, HttpContent? content = null)
        {
            using var req = new HttpRequestMessage(m, url) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, ct);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        static string Hint(int code) => code switch
        {
            401 => " - token rejected",
            403 => " - the app has no access to this site: a SharePoint admin must grant it WRITE on the site (Grant-PnPAzureADAppSitePermission)",
            404 => " - library not found: check DriveId",
            _ => "",
        };

        var lib = await Send(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/drives/{Seg(t.DriveId)}?$select=id,name,webUrl");
        if (lib.Code is < 200 or > 299) { steps.Add(new("Open document library", false, $"HTTP {lib.Code}{Hint(lib.Code)}: {Trim(lib.Body)}")); return steps; }
        using (var d = JsonDocument.Parse(lib.Body))
            steps.Add(new("Open document library", true,
                $"{(d.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "?")} - {(d.RootElement.TryGetProperty("webUrl", out var w) ? w.GetString() : "")}"));

        var parts = t.RootFolder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName).ToList();
        parts.Add("_connection-test"); parts.Add($"test-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var path = string.Join('/', parts.Select(Seg));
        var up = await Send(HttpMethod.Put, $"https://graph.microsoft.com/v1.0/drives/{Seg(t.DriveId)}/root:/{path}:/content",
            new StringContent($"MGT OCR SharePoint connection test {DateTime.Now:yyyy-MM-dd HH:mm:ss}", Encoding.UTF8, "text/plain"));
        if (up.Code is < 200 or > 299) { steps.Add(new("Upload test file", false, $"HTTP {up.Code}{Hint(up.Code)}: {Trim(up.Body)}")); return steps; }
        string itemId;
        using (var d = JsonDocument.Parse(up.Body))
        {
            itemId = d.RootElement.GetProperty("id").GetString()!;
            steps.Add(new("Upload test file", true, string.Join('/', parts)));
        }

        var del = await Send(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/drives/{Seg(t.DriveId)}/items/{Seg(itemId)}");
        steps.Add(del.Code is >= 200 and <= 299
            ? new("Delete test file", true, "OK (the empty _connection-test folder can be removed by hand)")
            : new("Delete test file", false, $"HTTP {del.Code}{Hint(del.Code)} - upload works, remove the test file by hand"));
        return steps;
    }

    /// <summary>Opens the archived file for streaming back to the browser. Caller disposes the response.</summary>
    public async Task<HttpResponseMessage> OpenAsync(SharePointTarget t, string itemId, CancellationToken ct)
    {
        var token = await TokenAsync(t, ct);
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/drives/{t.DriveId}/items/{Seg(itemId)}/content");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Graph answers 302 to a pre-authenticated download URL; HttpClient follows it (and drops the auth header cross-host).
        var res = await Http().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) { var c = (int)res.StatusCode; res.Dispose(); throw new InvalidOperationException($"Graph download failed ({c})"); }
        return res;
    }
}
