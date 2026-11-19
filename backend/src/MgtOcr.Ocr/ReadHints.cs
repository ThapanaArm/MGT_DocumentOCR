using System.Text;
using System.Text.Json;

namespace MgtOcr.Ocr;

/// <summary>Sales Order "Reading rules" (sql/39_customer_read_hints.sql): where each field is printed on
/// a given customer's PO. The API side finds the customer in the PDF's text layer before the read and
/// builds this; the Gemini read appends <see cref="PromptText"/> to its prompt, so the rules travel in
/// the same single request (no extra request).</summary>
public sealed class ReadHintRequest
{
    /// <summary>Text appended to the read prompt: the "_positions" instruction, plus this customer's rules when known.</summary>
    public string PromptText { get; init; } = "";
    /// <summary>Fields whose rule was sent (for the note and ocr.DocReadPosition.HintsUsed).</summary>
    public List<string> FieldsSent { get; init; } = [];
    /// <summary>Customer the rules belong to (tax ID or 'N:' + name), null when none was found.</summary>
    public string? CustomerKey { get; init; }
}

/// <summary>One rule as sent to the AI.</summary>
public sealed record ReadHintRule(string Field, int? PageNo, int[]? Box, string? Note, string Source);

public static class ReadHints
{
    /// <summary>Fields a rule can point at, in the order the UI lists them (same order as the SO header on
    /// the document page). "linesTable" = the item table. Every SO header field (Megachem, 17 Nov 2026:
    /// "it should have every field" -- incl. the Sold-to address, so a PO that prints several addresses
    /// can say which one is Sold-to and which one is Ship-to).</summary>
    public static readonly string[] Fields =
        ["docType", "poNo", "poDate", "quoteRef", "customerName", "customerTaxId", "customerAddress",
         "shipToCode", "shipToName", "shipToAddress", "deliveryDate", "currency", "paymentTerms", "incoterms",
         "remark", "subTotal", "vatAmount", "totalAmount", "linesTable"];

    /// <summary>The text appended to the read prompt. Always asks for "_positions"; adds the rules when there are any.</summary>
    public static string BuildPrompt(IReadOnlyList<ReadHintRule> rules, string? customerLabel)
    {
        var sb = new StringBuilder("\n\n");
        sb.Append("เพิ่ม key \"_positions\" ที่ระดับบนสุดของ JSON คำตอบเดียวกัน (ข้าง ๆ header และ lines): ตำแหน่งที่อ่านแต่ละค่าได้ ")
          .Append("รูปแบบ {\"poNo\":{\"image\":1,\"box\":[ymin,xmin,ymax,xmax]}} ")
          .Append("พิกัด 0-1000 ของภาพนั้น, image = ลำดับภาพในคำขอนี้ (เริ่มที่ 1) ")
          .Append("สำหรับ ").Append(string.Join(", ", Fields))
          .Append(" (linesTable = กรอบของตารางรายการสินค้าทั้งตาราง) ให้กรอบครอบเฉพาะค่า ไม่ต้องรวมป้ายชื่อช่อง ค่าที่ไม่พบไม่ต้องใส่");

        if (rules.Count > 0)
        {
            // Precedence (Megachem, 17 Nov 2026, Toyo Ink): the base SO prompt says shipToAddress only when a
            // separate "Ship To / Deliver To" heading exists and that an invoice address is customerAddress.
            // A CS rule that points shipToAddress at a box printed "Invoice Address" lost to those general
            // rules -- Gemini put the address in customerAddress and left shipToAddress empty (then the
            // partner memory filled the HEAD OFFICE address). The customer's rules must win.
            sb.Append("\n\n=== กฎการอ่านเฉพาะลูกค้ารายนี้ — ลำดับความสำคัญสูงสุด ===\n")
              .Append("กฎชุดนี้มีผลเหนือคำอธิบายช่องและกติกาทั่วไปทั้งหมดด้านบน ถ้าขัดกันให้ทำตามกฎชุดนี้ ")
              .Append("รวมถึงกติกาที่ว่า shipToAddress ใส่ได้เฉพาะเมื่อมีหัวข้อ Ship To/Deliver To และกติกาเรื่องที่อยู่สำหรับออกใบกำกับภาษี: ")
              .Append("ถ้ามีกฎของช่อง shipToAddress ให้อ่านที่อยู่ในกรอบนั้นใส่ shipToAddress เสมอ แม้หัวข้อบนเอกสารจะเขียนว่า Invoice Address หรืออย่างอื่น ")
              .Append("และแม้จะซ้ำกับที่อยู่อื่น ส่วน customerAddress ให้ใช้ที่อยู่อื่นที่ไม่ใช่กรอบนั้น (ถ้ามีกฎของ customerAddress ให้ใช้กรอบของกฎนั้น)");
            sb.Append("\n\nกฎการอ่านของลูกค้ารายนี้")
              .Append(string.IsNullOrWhiteSpace(customerLabel) ? "" : $" ({customerLabel})")
              .Append(" จากเอกสารของลูกค้ารายเดียวกันที่ตรวจแล้ว และที่พนักงานกำหนดไว้ ")
              .Append("(ชื่อช่อง = key ใน header ที่ต้องใส่ค่า, หน้า = ลำดับภาพ, กรอบ = [ymin,xmin,ymax,xmax] พิกัด 0-1000):\n");
            foreach (var r in rules)
            {
                sb.Append("- ").Append(r.Field).Append(':');
                if (r.PageNo is int p && r.Box is { Length: 4 } b)
                    sb.Append($" หน้า {p} กรอบ [{b[0]},{b[1]},{b[2]},{b[3]}]");
                if (!string.IsNullOrWhiteSpace(r.Note))
                    sb.Append(" หมายเหตุจากพนักงาน: ").Append(r.Note!.Trim());
                if (!string.Equals(r.Source, "AUTO", StringComparison.OrdinalIgnoreCase)) sb.Append(" (พนักงานกำหนด — ให้ถือตามนี้ก่อน)");
                sb.Append('\n');
            }
            sb.Append("วิธีใช้: ให้มองหาค่าของแต่ละช่องที่กรอบนั้นก่อน ตำแหน่งจริงอาจเลื่อนไปเล็กน้อย ")
              .Append("ถ้ามีหมายเหตุให้ทำตามหมายเหตุ ค่าต้องอ่านจากภาพของเอกสารนี้เสมอ ห้ามเดาหรือคัดลอกค่าจากที่อื่น ")
              .Append("ชื่อช่องในกฎสำคัญกว่าหัวข้อที่พิมพ์บนเอกสาร เช่น กรอบที่พิมพ์ว่า Invoice Address แต่กฎบอกว่าเป็น shipToAddress ให้ใส่ค่านั้นใน shipToAddress ")
              .Append("และค่าในกรอบของช่องหนึ่งห้ามนำไปใส่ช่องอื่น ยกเว้นกฎสองช่องชี้กรอบเดียวกัน (เช่น customerAddress กับ shipToAddress) ให้ใส่ค่าเดียวกันทั้งสองช่อง ไม่ต้องเว้นว่างเพราะซ้ำ ")
              .Append("ถ้าในกรอบนั้นไม่มีค่าที่ตรงกับช่อง ให้หาจากส่วนอื่นของเอกสารตามปกติ ")
              .Append("ถ้าเอกสารนี้ไม่ใช่ของลูกค้ารายนี้ ให้ข้ามกฎชุดนี้ทั้งหมด");
        }
        return sb.ToString();
    }

    /// <summary>The "_positions" object of an answer as compact JSON (only known fields with a valid box); null when there is none.</summary>
    public static string? ExtractPositions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains("_positions")) return null;
        var s = raw.IndexOf('{'); var e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[s..(e + 1)]);
            if (!doc.RootElement.TryGetProperty("_positions", out var p) || p.ValueKind != JsonValueKind.Object) return null;
            var clean = new Dictionary<string, object>();
            foreach (var prop in p.EnumerateObject())
            {
                if (!Fields.Contains(prop.Name) || prop.Value.ValueKind != JsonValueKind.Object) continue;
                var image = prop.Value.TryGetProperty("image", out var im) && im.TryGetInt32(out var iv) ? iv : 1;
                if (!prop.Value.TryGetProperty("box", out var bx) || bx.ValueKind != JsonValueKind.Array) continue;
                var box = bx.EnumerateArray().Select(v => v.TryGetDouble(out var d) ? (int)Math.Round(d) : -1).ToArray();
                if (!ValidBox(box)) continue;
                clean[prop.Name] = new { image = Math.Max(1, image), box };
            }
            return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Fields that may share one printed value: when a rule's box sits on the same place as
    /// another field of the same kind, that field's value is copied (address -> address, name -> name).</summary>
    public static readonly string[][] SameKind =
        [["customerAddress", "shipToAddress"], ["customerName", "shipToName"]];

    /// <summary>How much two boxes overlap: intersection / the smaller box (0..1). 1 = one sits inside the other.</summary>
    public static double Overlap(int[] a, int[] b)
    {
        var h = Math.Min(a[2], b[2]) - Math.Max(a[0], b[0]);
        var w = Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]);
        if (h <= 0 || w <= 0) return 0;
        var small = Math.Min((a[2] - a[0]) * (a[3] - a[1]), (b[2] - b[0]) * (b[3] - b[1]));
        return small <= 0 ? 0 : (double)h * w / small;
    }

    /// <summary>[ymin,xmin,ymax,xmax] inside 0-1000 with a positive size.</summary>
    public static bool ValidBox(int[]? b) =>
        b is { Length: 4 } && b.All(v => v is >= 0 and <= 1000) && b[2] > b[0] && b[3] > b[1];
}
