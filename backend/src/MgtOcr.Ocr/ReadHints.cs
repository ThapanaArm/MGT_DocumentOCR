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
    /// <summary>Fields a rule can point at, in the order the UI lists them. "linesTable" = the item table.</summary>
    public static readonly string[] Fields =
        ["poNo", "poDate", "quoteRef", "deliveryDate", "customerName", "customerTaxId",
         "shipToName", "shipToAddress", "paymentTerms", "remark", "linesTable"];

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
                if (r.Source == "CS") sb.Append(" (พนักงานกำหนด — ให้ถือตามนี้ก่อน)");
                sb.Append('\n');
            }
            sb.Append("วิธีใช้: ให้มองหาค่าของแต่ละช่องที่กรอบนั้นก่อน ตำแหน่งจริงอาจเลื่อนไปเล็กน้อย ")
              .Append("ถ้ามีหมายเหตุให้ทำตามหมายเหตุ ค่าต้องอ่านจากภาพของเอกสารนี้เสมอ ห้ามเดาหรือคัดลอกค่าจากที่อื่น ")
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

    /// <summary>[ymin,xmin,ymax,xmax] inside 0-1000 with a positive size.</summary>
    public static bool ValidBox(int[]? b) =>
        b is { Length: 4 } && b.All(v => v is >= 0 and <= 1000) && b[2] > b[0] && b[3] > b[1];
}
