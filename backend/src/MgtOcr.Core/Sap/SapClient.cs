using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Core.Sap;

public record SapPostResult(bool Success, bool Simulated, string SapDocNo, string Endpoint, string Message, object? Raw = null);

// Ported from app/sap.py's post() (lines 116-141). Simulate mode when SAP_BASE_URL is unset
// (the normal case today); live mode does a raw HTTP POST — no SDK, no CSRF token handling
// (preserved as a known gap per the approved migration plan, not fixed here).
public class SapClient(AppConfig config, HttpClient httpClient)
{
    // Only TOP-LEVEL "_"-prefixed keys are stripped before the live POST — nested "_"-keys inside
    // to_Item/to_SuplrInvcItemPurOrdRef arrays are untouched (see SapPayloadBuilder's header comment).
    private static Dictionary<string, object?> StripTopLevelUnderscoreKeys(Dictionary<string, object?> payload) =>
        payload.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value);

    public async Task<SapPostResult> PostAsync(string module, Dictionary<string, object?> payload)
    {
        var endpoint = payload.GetValueOrDefault("_target") as string ?? "";
        var body = StripTopLevelUnderscoreKeys(payload);

        if (string.IsNullOrEmpty(config.SapBaseUrl))
        {
            var docNo = (module == "SO" ? "00" : "51") + Random.Shared.Next(100000, 1000000);
            return new SapPostResult(true, true, docNo, endpoint,
                "โหมดจำลอง: ยังไม่ได้ตั้งค่า SAP_BASE_URL ใน .env (บันทึก payload และ log ไว้แล้ว)");
        }

        var url = $"{config.SapBaseUrl.TrimEnd('/')}/sap/opu/odata/sap/{endpoint}?sap-client={config.SapClient}";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        var authBytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var resp = await httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return new SapPostResult(false, false, "", endpoint,
                    $"ส่งเข้า SAP ไม่สำเร็จ: {(int)resp.StatusCode} {text[..Math.Min(500, text.Length)]}");
            }
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;
            var d = root.TryGetProperty("d", out var dEl) ? dEl : root;
            var sapDocNo = TryGetString(d, "SalesOrder") ?? TryGetString(d, "SupplierInvoice") ?? "";
            return new SapPostResult(true, false, sapDocNo, endpoint, "สร้างเอกสารใน SAP สำเร็จ", JsonSerializer.Deserialize<object>(d.GetRawText()));
        }
        catch (Exception e)
        {
            return new SapPostResult(false, false, "", endpoint, $"ส่งเข้า SAP ไม่สำเร็จ: {e.Message}");
        }
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
