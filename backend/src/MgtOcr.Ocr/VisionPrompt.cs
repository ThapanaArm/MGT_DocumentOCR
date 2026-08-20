using System.Text.Json;
using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

// Shared JSON-schema extraction prompt + response parser used by every "send the page image (or
// its OCR'd text) straight to an LLM and get back structured JSON" provider (Claude Vision,
// Claude-text-structuring, and — new in the .NET port — Gemini Vision and OpenAI Vision). Ported
// from _claude_prompt()/claude_vision_extract()'s response-parsing tail in ocr_engine.py, but
// factored out since it's now shared by more than one model family.
public static class VisionPrompt
{
    // mode="image": ask the model to read the page image itself (Claude/Gemini/OpenAI Vision).
    // mode="text": ask the model to structure OCR'd text handed to it (claude_text 2-tier mode).
    public static string Build(string module, string mode = "image")
    {
        string fields = module == "SO" ? """
        {
          "header": {
            "docType": "ประเภทเอกสาร เช่น PURCHASE ORDER",
            "poNo": "เลขที่ใบสั่งซื้อของลูกค้า",
            "poDate": "วันที่เอกสาร รูปแบบ YYYY-MM-DD",
            "customerName": "ชื่อลูกค้า (นิติบุคคลที่ออกใบสั่งซื้อ ไม่ใช่บริษัทผู้ขาย/ผู้รับเอกสาร)",
            "customerTaxId": "เลขทะเบียนนิติบุคคล/ผู้เสียภาษี 13 หลักของลูกค้า",
            "shipToName": "ชื่อสถานที่ส่งของ", "shipToAddress": "ที่อยู่จัดส่งเต็ม",
            "deliveryDate": "วันที่ต้องการรับสินค้า YYYY-MM-DD",
            "currency": "รหัสสกุลเงิน 3 ตัวอักษร เช่น THB", "paymentTerms": "เงื่อนไขการชำระเงิน",
            "incoterms": "Incoterms ถ้ามี", "subTotal": 0, "vatAmount": 0, "totalAmount": 0, "remark": ""
          },
          "lines": [{"extCode": "รหัสสินค้าตามเอกสาร", "desc": "ชื่อ/รายละเอียดสินค้า",
                     "qty": 0, "uom": "หน่วยนับ", "price": 0, "amount": 0}]
        }
        """ : """
        {
          "header": {
            "docType": "ประเภทเอกสาร เช่น ใบกำกับภาษี/ใบแจ้งหนี้",
            "invoiceNo": "เลขที่ใบกำกับภาษี/ใบแจ้งหนี้", "invoiceDate": "วันที่เอกสาร YYYY-MM-DD",
            "postingDate": "วันที่เดียวกับ invoiceDate ถ้าไม่มีระบุแยก",
            "vendorName": "ชื่อผู้ขาย/ผู้ออกใบกำกับภาษี (ไม่ใช่บริษัทผู้ซื้อ/ผู้รับเอกสาร)",
            "vendorTaxId": "เลขทะเบียนนิติบุคคล/ผู้เสียภาษี 13 หลักของผู้ขาย", "branch": "สาขาของผู้ขาย",
            "poRef": "เลขที่ใบสั่งซื้ออ้างอิงถ้ามี",
            "currency": "รหัสสกุลเงิน 3 ตัวอักษร เช่น THB", "paymentTerms": "เงื่อนไขการชำระเงิน",
            "subTotal": 0, "vatRate": 7, "vatAmount": 0, "whtAmount": 0, "totalAmount": 0
          },
          "lines": [{"extCode": "รหัสสินค้า/บริการถ้ามี", "desc": "ชื่อ/รายละเอียดสินค้าหรือบริการ",
                     "qty": 0, "uom": "หน่วยนับ", "price": 0, "amount": 0}]
        }
        """;

        var intro = mode == "image"
            ? "อ่านเอกสารในภาพนี้ (ใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อภาษาไทยหรืออังกฤษ) แล้วดึงข้อมูลออกมา\n"
            : "ข้อความด้านล่างนี้ได้จากการอ่าน OCR เอกสารใบกำกับภาษี/ใบแจ้งหนี้/ใบสั่งซื้อ อาจมีช่องว่างแทรก" +
              "ระหว่างตัวอักษรไทยผิดปกติ ตัวเลข/ตัวอักษรบางจุดอ่านผิด หรือลำดับคอลัมน์สลับกัน " +
              "ให้ตีความเนื้อหาอย่างชาญฉลาดแล้วดึงข้อมูลออกมาให้ถูกต้องที่สุด\n";

        return intro +
            $"ตอบกลับเป็น JSON ล้วน ๆ ตามโครงสร้างนี้เท่านั้น ห้ามมีข้อความอื่นนอก JSON:\n{fields}\n\n" +
            "กติกา:\n" +
            "- ตัวเลขทุกช่อง (qty, price, amount, subTotal, vatAmount, totalAmount ฯลฯ) ต้องเป็นตัวเลขล้วน " +
            "ไม่มีคอมมา/สัญลักษณ์สกุลเงิน\n" +
            "- วันที่ทุกช่องต้องอยู่ในรูปแบบ YYYY-MM-DD (แปลง พ.ศ. เป็น ค.ศ. โดยลบ 543)\n" +
            "- ช่องไหนหาไม่เจอในเอกสารให้ใส่สตริงว่าง \"\" หรือ 0 ตามชนิดข้อมูล อย่าเดา\n" +
            "- ห้ามใช้ชื่อ/เลขทะเบียนของบริษัทที่เป็น 'ผู้รับเอกสาร' (Megachem (Thailand)) เป็นชื่อคู่ค้าเด็ดขาด\n" +
            "- ตรวจสอบผลรวม: subTotal + vatAmount ควรใกล้เคียง totalAmount";
    }

    // Parses a model's raw text response (expected to contain one JSON object, possibly with
    // surrounding prose despite instructions not to) into a ParsedDocument, or null if no JSON
    // object could be found/parsed.
    public static ParsedDocument? ParseResponse(string raw, string module, string provider, double confidence, string rawTextForRecord)
    {
        var m = Regex.Match(raw, @"\{.*\}", RegexOptions.Singleline);
        if (!m.Success) return null;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(m.Value); }
        catch (JsonException) { return null; }

        var h = HeaderParser.BlankHeader(module);
        if (parsed.RootElement.TryGetProperty("header", out var hEl) && hEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in hEl.EnumerateObject())
                if (h.ContainsKey(prop.Name))
                    h[prop.Name] = JsonElementToObject(prop.Value);
        }

        var lines = new List<LineItem>();
        if (parsed.RootElement.TryGetProperty("lines", out var lEl) && lEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var ln in lEl.EnumerateArray().Take(60))
            {
                lines.Add(new LineItem
                {
                    ExtCode = GetStr(ln, "extCode"), Desc = GetStr(ln, "desc"),
                    Qty = GetNum(ln, "qty"), Uom = GetStr(ln, "uom") is { Length: > 0 } u ? u : "EA",
                    Price = GetNum(ln, "price"), Amount = GetNum(ln, "amount"),
                });
            }
        }

        var text = rawTextForRecord.Length > 20000 ? rawTextForRecord[..20000] : rawTextForRecord;
        return new ParsedDocument { Header = h, Lines = lines, Confidence = confidence, Provider = provider, RawText = text };
    }

    private static string GetStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double GetNum(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => TextHelpers.F(v.GetString()),
            _ => 0,
        };
    }
    private static object? JsonElementToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
