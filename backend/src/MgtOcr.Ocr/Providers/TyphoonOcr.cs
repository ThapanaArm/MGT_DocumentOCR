using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr.Providers;

// typhoon_text(): Typhoon OCR (opentyphoon.ai) — Thai/English open-weight OCR model from SCB
// 10X, called via its OpenAI-compatible chat/completions endpoint (image sent as one page per
// call). Returns (text, error) — errors are surfaced explicitly rather than swallowed, since
// Typhoon's rate limit is fairly low (2 req/s, 20 req/min) and users should be told WHY a
// document didn't read (rate limit is a different problem than a bad file or a wrong API key).
public static class TyphoonOcr
{
    private static readonly HttpClient Http = new();

    public static async Task<(string Text, string Err)> ExtractTextAsync(string path, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.TyphoonApiKey)) return ("", "ยังไม่ได้ตั้งค่า TYPHOON_API_KEY ใน .env");
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var imgs = ext == ".pdf"
                ? PdfRasterizer.RenderPagesToPng(path, maxPages: 5, dpi: 200)
                : [await File.ReadAllBytesAsync(path)];

            var parts = new List<string>();
            for (var i = 0; i < imgs.Count; i++)
            {
                if (i > 0) await Task.Delay(600); // respect the 2 req/s rate limit on multi-page documents

                var dataUri = "data:image/png;base64," + Convert.ToBase64String(imgs[i]);
                var body = new
                {
                    model = config.TyphoonModel, temperature = 0, max_tokens = 8000,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new
                                {
                                    type = "text",
                                    text = "ถอดข้อความในภาพนี้ทีละบรรทัดตามที่ปรากฏจริง เรียงจากบนลงล่าง ซ้ายไปขวา แต่ละบรรทัดที่แยกกัน " +
                                           "ในเอกสารต้นฉบับให้ขึ้นบรรทัดใหม่เสมอ ห้ามนำข้อความจากหลายบรรทัด/หลายจุดมารวมเป็นรายการ " +
                                           "คั่นด้วยจุลภาคเดียวเด็ดขาด\n" +
                                           "สำหรับตารางรายการสินค้า/บริการ: แต่ละแถวของตารางต้องอยู่บรรทัดเดียวกัน โดยเรียง " +
                                           "รหัสสินค้า(ถ้ามี) รายละเอียด จำนวน หน่วยนับ ราคาต่อหน่วย จำนวนเงิน คั่นแต่ละคอลัมน์ด้วย " +
                                           "เครื่องหมาย | เช่น 'ค่าบริการทดสอบ | 1 | EA | 1200.00 | 1200.00'\n" +
                                           "ตอบเฉพาะข้อความที่อ่านได้เท่านั้น ห้ามสรุป อธิบายเพิ่มเติม หรือขึ้นต้นด้วยหัวข้อใด ๆ",
                                },
                                new { type = "image_url", image_url = new { url = dataUri } },
                            },
                        },
                    },
                };

                var attempt = 0;
                while (true)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.opentyphoon.ai/v1/chat/completions")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                    };
                    req.Headers.Add("Authorization", $"Bearer {config.TyphoonApiKey}");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    using var resp = await Http.SendAsync(req, cts.Token);
                    var respText = await resp.Content.ReadAsStringAsync(cts.Token);

                    if (resp.IsSuccessStatusCode)
                    {
                        var content = ExtractContent(respText);
                        var m = Regex.Match(content, "\"natural_text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Singleline);
                        if (m.Success) content = Regex.Unescape(m.Groups[1].Value); // some SDK versions wrap the result as JSON — unwrap if present
                        parts.Add(content);
                        break;
                    }

                    if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
                    {
                        attempt++;
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
                        continue;
                    }
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                        return ("", "Typhoon OCR ติด rate limit (2 req/s, 20 req/min) ลองใหม่อีกครั้งในอีกสักครู่");
                    if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        return ("", $"TYPHOON_API_KEY ไม่ถูกต้องหรือหมดอายุ (HTTP {(int)resp.StatusCode})");
                    var detail = respText.Length > 200 ? respText[..200] : respText;
                    return ("", $"Typhoon OCR ตอบกลับผิดพลาด (HTTP {(int)resp.StatusCode}): {detail}");
                }
            }
            return (string.Join("\n", parts), "");
        }
        catch (Exception e)
        {
            return ("", $"เชื่อมต่อ Typhoon OCR ไม่สำเร็จ: {e.Message}");
        }
    }

    private static string ExtractContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return "";
        if (choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
            return c.GetString() ?? "";
        return "";
    }
}
