namespace MgtOcr.Ocr;

public class DemoSample
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required double Confidence { get; init; }
    public required Dictionary<string, object?> Header { get; init; }
    public required List<LineItem> Lines { get; init; }
}

// Ported verbatim from the DEMO dict in ocr_engine.py — pure data, used by the "sample document"
// testing flow and as the source pool for the demo-fallback path when real extraction fails.
public static class DemoData
{
    public static readonly Dictionary<string, List<DemoSample>> Demo = new()
    {
        ["SO"] =
        [
            new DemoSample
            {
                Name = "PO-SCI-6801234.pdf", Label = "ใบสั่งซื้อลูกค้า — ข้อมูลครบ", Confidence = 0.97,
                Header = new Dictionary<string, object?>
                {
                    ["docType"] = "PURCHASE ORDER", ["poNo"] = "PO-6801234", ["poDate"] = "2026-08-10",
                    ["customerName"] = "บริษัท สยาม เคมิคอล อินดัสทรี จำกัด", ["customerTaxId"] = "0105533012345",
                    ["shipToName"] = "คลังสินค้า บางปู",
                    ["shipToAddress"] = "นิคมอุตสาหกรรมบางปู ซ.7 ต.แพรกษา อ.เมือง สมุทรปราการ 10280",
                    ["deliveryDate"] = "2026-08-25", ["currency"] = "THB", ["paymentTerms"] = "เครดิต 30 วัน",
                    ["incoterms"] = "DDP", ["totalAmount"] = 385000.0, ["remark"] = "ส่งของช่วงเช้า 08:00-11:00",
                },
                Lines =
                [
                    new() { ExtCode = "SCI-TIO2-902", Desc = "TIO2 R902 ถุง 25 กก.", Qty = 5000, Uom = "KG", Price = 62, Amount = 310000 },
                    new() { ExtCode = "SCI-CACO3-800", Desc = "แคลเซียมคาร์บอเนต CC800", Qty = 2500, Uom = "KG", Price = 30, Amount = 75000 },
                ],
            },
            new DemoSample
            {
                Name = "PO-TPG-2026-0842.pdf", Label = "ใบสั่งซื้อลูกค้า — สินค้าไม่พบใน Master", Confidence = 0.91,
                Header = new Dictionary<string, object?>
                {
                    ["docType"] = "PURCHASE ORDER", ["poNo"] = "TPG-2026-0842", ["poDate"] = "2026-08-12",
                    ["customerName"] = "THAI POLYMER GROUP PCL.", ["customerTaxId"] = "0107536000123",
                    ["shipToName"] = "โรงงานอยุธยา (โรจนะ)",
                    ["shipToAddress"] = "สวนอุตสาหกรรมโรจนะ ต.คานหาม อ.อุทัย พระนครศรีอยุธยา 13210",
                    ["deliveryDate"] = "2026-08-28", ["currency"] = "THB", ["paymentTerms"] = "เครดิต 60 วัน",
                    ["incoterms"] = "DDP", ["totalAmount"] = 640000.0, ["remark"] = "",
                },
                Lines =
                [
                    new() { ExtCode = "TPG-PP1100", Desc = "PP HOMO 1100N", Qty = 10000, Uom = "KG", Price = 52, Amount = 520000 },
                    new() { ExtCode = "TPG-XY-500", Desc = "XYLENE INDUSTRIAL GRADE", Qty = 3000, Uom = "L", Price = 40, Amount = 120000 },
                ],
            },
            new DemoSample
            {
                Name = "PO-UNKNOWN-9931.jpg", Label = "ใบสั่งซื้อ — ลูกค้าใหม่ (ไม่พบใน Master)", Confidence = 0.86,
                Header = new Dictionary<string, object?>
                {
                    ["docType"] = "PURCHASE ORDER", ["poNo"] = "PO-9931", ["poDate"] = "2026-08-13",
                    ["customerName"] = "บริษัท นิว เวิลด์ เทรดดิ้ง จำกัด", ["customerTaxId"] = "0105566001111",
                    ["shipToName"] = "คลังสินค้าลาดกระบัง",
                    ["shipToAddress"] = "ถ.ฉลองกรุง แขวงลำปลาทิว เขตลาดกระบัง กรุงเทพฯ 10520",
                    ["deliveryDate"] = "2026-08-30", ["currency"] = "THB", ["paymentTerms"] = "เงินสด",
                    ["incoterms"] = "EXW", ["totalAmount"] = 124000.0, ["remark"] = "",
                },
                Lines = [new() { ExtCode = "NW-EP828", Desc = "อีพ็อกซี่เรซิน EP-828", Qty = 2000, Uom = "KG", Price = 62, Amount = 124000 }],
            },
        ],
        ["AP"] =
        [
            new DemoSample
            {
                Name = "INV-UC-25080456.pdf", Label = "ใบกำกับภาษี/ใบส่งของ — ข้อมูลครบ", Confidence = 0.96,
                Header = new Dictionary<string, object?>
                {
                    ["docType"] = "ใบกำกับภาษี/ใบส่งของ", ["invoiceNo"] = "UC-25080456", ["invoiceDate"] = "2026-08-08",
                    ["postingDate"] = "2026-08-08", ["vendorName"] = "บริษัท ยูนิเวอร์แซล เคมีคอล ซัพพลาย จำกัด",
                    ["vendorTaxId"] = "0105546007788", ["branch"] = "สำนักงานใหญ่", ["poRef"] = "4500012345",
                    ["currency"] = "THB", ["paymentTerms"] = "เครดิต 30 วัน", ["subTotal"] = 268000.0, ["vatRate"] = 7.0,
                    ["vatAmount"] = 18760.0, ["whtAmount"] = 0.0, ["totalAmount"] = 286760.0,
                },
                Lines =
                [
                    new() { ExtCode = "UC-TL-100", Desc = "TOLUENE INDUSTRIAL", Qty = 4000, Uom = "L", Price = 42, Amount = 168000 },
                    new() { ExtCode = "UC-MEK-995", Desc = "MEK 99.5 PCT", Qty = 2000, Uom = "L", Price = 50, Amount = 100000 },
                ],
            },
            new DemoSample
            {
                Name = "INV-NC-LOG-6808.pdf", Label = "ใบแจ้งหนี้ค่าขนส่ง — มีภาษีหัก ณ ที่จ่าย", Confidence = 0.93,
                Header = new Dictionary<string, object?>
                {
                    ["docType"] = "ใบแจ้งหนี้", ["invoiceNo"] = "NC-LOG-6808", ["invoiceDate"] = "2026-08-11",
                    ["postingDate"] = "2026-08-11", ["vendorName"] = "บริษัท เอ็น.ซี. โลจิสติกส์ เซอร์วิส จำกัด",
                    ["vendorTaxId"] = "0115551002233", ["branch"] = "สำนักงานใหญ่", ["poRef"] = "",
                    ["currency"] = "THB", ["paymentTerms"] = "เครดิต 15 วัน", ["subTotal"] = 85000.0, ["vatRate"] = 7.0,
                    ["vatAmount"] = 5950.0, ["whtAmount"] = 2550.0, ["totalAmount"] = 88400.0,
                },
                Lines = [new() { ExtCode = "NC-FREIGHT", Desc = "ค่าขนส่ง เดือน ก.ค. 2569", Qty = 1, Uom = "AU", Price = 85000, Amount = 85000 }],
            },
            new DemoSample
            {
                Name = "INV-SCAN-77120.jpg", Label = "ใบแจ้งหนี้สแกน — ผู้ขายและสินค้าไม่พบ", Confidence = 0.78,
                Header = new Dictionary<string, object?>
                {
                    ["docType"] = "ใบกำกับภาษี", ["invoiceNo"] = "77120", ["invoiceDate"] = "2026-08-12",
                    ["postingDate"] = "2026-08-12", ["vendorName"] = "หจก. รุ่งเรือง เคมีภัณฑ์",
                    ["vendorTaxId"] = "0103552009999", ["branch"] = "สำนักงานใหญ่", ["poRef"] = "",
                    ["currency"] = "THB", ["paymentTerms"] = "เงินสด", ["subTotal"] = 52000.0, ["vatRate"] = 7.0,
                    ["vatAmount"] = 3640.0, ["whtAmount"] = 0.0, ["totalAmount"] = 55640.0,
                },
                Lines = [new() { ExtCode = "RR-ACET", Desc = "ACETONE 99%", Qty = 1300, Uom = "L", Price = 40, Amount = 52000 }],
            },
        ],
    };

    public static ParsedDocument DemoDoc(string module, int index = 0)
    {
        var list = Demo[module];
        var s = list[((index % list.Count) + list.Count) % list.Count];
        return new ParsedDocument
        {
            Header = new Dictionary<string, object?>(s.Header),
            Lines = s.Lines.Select(l => new LineItem { ExtCode = l.ExtCode, Desc = l.Desc, Qty = l.Qty, DueDate = l.DueDate, Uom = l.Uom, Price = l.Price, Amount = l.Amount }).ToList(),
            Confidence = s.Confidence, Provider = "demo", RawText = "", SampleName = s.Name,
        };
    }
}
