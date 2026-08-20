using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

// azure_extract() / _from_azure(): Azure AI Document Intelligence, prebuilt-invoice model —
// submit the file, poll the async operation-location URL until it succeeds/fails.
public static class AzureOcr
{
    private static readonly HttpClient Http = new();

    public static async Task<JsonDocument?> ExtractAsync(string path, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.AzureDiEndpoint) || string.IsNullOrEmpty(config.AzureDiKey)) return null;
        try
        {
            var url = config.AzureDiEndpoint.TrimEnd('/') +
                      "/documentintelligence/documentModels/prebuilt-invoice:analyze?api-version=2024-11-30";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(await File.ReadAllBytesAsync(path)),
            };
            req.Headers.Add("Ocp-Apim-Subscription-Key", config.AzureDiKey);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await Http.SendAsync(req, cts1.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var opLocation = resp.Headers.TryGetValues("operation-location", out var vals) ? vals.FirstOrDefault() : null;
            if (opLocation == null) return null;

            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(2000);
                using var g = new HttpRequestMessage(HttpMethod.Get, opLocation);
                g.Headers.Add("Ocp-Apim-Subscription-Key", config.AzureDiKey);
                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var gr = await Http.SendAsync(g, cts2.Token);
                var data = JsonDocument.Parse(await gr.Content.ReadAsStringAsync(cts2.Token));
                var status = data.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (status == "succeeded") return data;
                if (status == "failed") return null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static ParsedDocument FromAzure(JsonDocument data, string module)
    {
        var root = data.RootElement;
        var doc = root.TryGetProperty("analyzeResult", out var ar) && ar.TryGetProperty("documents", out var docs) &&
                  docs.ValueKind == JsonValueKind.Array && docs.GetArrayLength() > 0 ? docs[0] : default;
        JsonElement fields = default;
        if (doc.ValueKind == JsonValueKind.Object) doc.TryGetProperty("fields", out fields);

        string G(string name, string sub = "valueString")
        {
            if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return "";
            if (v.TryGetProperty(sub, out var sv) && sv.ValueKind == JsonValueKind.String) return sv.GetString() ?? "";
            if (v.TryGetProperty("content", out var cv) && cv.ValueKind == JsonValueKind.String) return cv.GetString() ?? "";
            return "";
        }
        double GNum(string name)
        {
            if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return 0;
            if (v.TryGetProperty("valueCurrency", out var cur) && cur.ValueKind == JsonValueKind.Object &&
                cur.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number) return amt.GetDouble();
            return TextHelpers.F(v.TryGetProperty("content", out var c) ? c.GetString() : null);
        }

        var lines = new List<LineItem>();
        if (fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("Items", out var items) &&
            items.ValueKind == JsonValueKind.Object && items.TryGetProperty("valueArray", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (!it.TryGetProperty("valueObject", out var o) || o.ValueKind != JsonValueKind.Object) continue;
                string OStr(string name, string sub = "valueString") =>
                    o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty(sub, out var sv) ? sv.GetString() ?? "" : "";
                double ONum(string name)
                {
                    if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return 0;
                    if (v.TryGetProperty("valueNumber", out var n) && n.ValueKind == JsonValueKind.Number) return n.GetDouble();
                    if (v.TryGetProperty("valueCurrency", out var cur) && cur.ValueKind == JsonValueKind.Object &&
                        cur.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number) return amt.GetDouble();
                    return 0;
                }
                lines.Add(new LineItem
                {
                    ExtCode = OStr("ProductCode"), Desc = OStr("Description"), Qty = ONum("Quantity"),
                    Uom = OStr("Unit") is { Length: > 0 } u ? u : "EA", Price = ONum("UnitPrice"), Amount = ONum("Amount"),
                });
            }
        }

        var name = module == "AP" ? G("VendorName") : G("CustomerName");
        var tax = module == "AP" ? G("VendorTaxId") : G("CustomerTaxId");
        var h = HeaderParser.BlankHeader(module);
        if (module == "AP")
        {
            var d = G("InvoiceDate", "valueDate");
            h["invoiceNo"] = G("InvoiceId"); h["invoiceDate"] = d; h["postingDate"] = d;
            h["vendorName"] = name; h["vendorTaxId"] = tax;
            h["subTotal"] = GNum("SubTotal"); h["vatAmount"] = GNum("TotalTax");
            h["totalAmount"] = GNum("InvoiceTotal"); h["vatRate"] = 7.0;
        }
        else
        {
            h["poNo"] = G("PurchaseOrder"); h["poDate"] = G("InvoiceDate", "valueDate");
            h["customerName"] = name; h["customerTaxId"] = tax;
            h["shipToName"] = G("ShippingAddressRecipient"); h["shipToAddress"] = G("ShippingAddress");
            h["totalAmount"] = GNum("InvoiceTotal");
        }

        var confidence = doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number
            ? cf.GetDouble() : 0.9;
        var content = ar.ValueKind == JsonValueKind.Object && ar.TryGetProperty("content", out var ct) ? ct.GetString() ?? "" : "";
        return new ParsedDocument
        {
            Header = h, Lines = lines, Confidence = confidence, Provider = "azure",
            RawText = content.Length > 20000 ? content[..20000] : content,
        };
    }
}
