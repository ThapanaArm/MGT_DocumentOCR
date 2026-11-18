namespace MgtOcr.Ocr;

// Page memory for AP/II bundles — which page of a long bundle is what (sql/43_page_memory.sql).
//
// A shipping bundle is read around one ANCHOR page: the FORM SHIPPING EXPENSE sheet (or the
// PURCHASE ORDER sheet for "Goods/service items"). GeminiOcr finds it with a regex over the PDF's
// text layer, which a scan does not have, so on most AP files the anchor was never found and the
// read fell back to "the first six pages". The page classifier looks at the top of every page
// instead and says what each one is; the header strips of anchor pages from documents that were
// posted are kept and shown to the classifier next time as "this is what that page looks like".
public static class PageRoles
{
    public const string FormShippingExpense = "FORM_SHIPPING_EXPENSE";
    public const string PurchaseOrder = "PURCHASE_ORDER";
    public const string GoodsInvoice = "GOODS_INVOICE";
    public const string Insurance = "INSURANCE";
    public const string TaxInvoice = "TAX_INVOICE";
    public const string Invoice = "INVOICE";
    public const string Receipt = "RECEIPT";
    public const string Customs = "CUSTOMS";
    public const string WhtCertificate = "WHT_CERTIFICATE";
    public const string DeliveryOrder = "DELIVERY_ORDER";
    public const string PackingList = "PACKING_LIST";
    public const string BillOfLading = "BILL_OF_LADING";
    public const string Certificate = "CERTIFICATE";
    public const string Continuation = "CONTINUATION";
    public const string Blank = "BLANK";
    public const string Other = "OTHER";
    public const string Unclassified = "UNCLASSIFIED";

    public static readonly string[] All =
    [
        FormShippingExpense, PurchaseOrder, GoodsInvoice, Insurance, TaxInvoice, Invoice, Receipt, Customs, WhtCertificate,
        DeliveryOrder, PackingList, BillOfLading, Certificate, Continuation, Blank, Other,
    ];

    /// <summary>Pages that never carry an amount or a tax the read needs. Deliberately short: a page
    /// left in costs a few seconds, a page wrongly left out loses a VAT row. CONTINUATION and OTHER
    /// are never skipped — a continuation page is often where an invoice's VAT total is printed.</summary>
    public static readonly HashSet<string> NoMoney = [PackingList, BillOfLading, Certificate, Blank];

    /// <summary>Where to look on a page of this type (AP, 2026-11-18 "OCR Document Scope" / Focus Area):
    /// the instruction that goes out with each image. Empty = read under the normal rules.</summary>
    public static string Focus(string role, bool isPoRead) => role switch
    {
        FormShippingExpense =>
            "หน้าหลัก อ่าน: หัวฟอร์ม (SUPPLIER, PO. NO., INVOICE NO., B/L NO., EX-RATE), ตาราง DESCRIPTION ทุกแถวที่มียอด " +
            "และกล่อง VENDOR CODE ท้ายฟอร์ม — ลายมือเซ็น หมายเหตุ และตารางอัตราแลกเปลี่ยนด้านล่างไม่ต้องอ่าน",
        PurchaseOrder => isPoRead
            ? "อ่าน: เลข PO, ผู้ขาย, ตารางสินค้า (Item / Description / Quantity / Unit / Unit Price / Amount) ทุกหน้า และ Total Amount"
            : "",
        GoodsInvoice => isPoRead
            ? "ใบ Invoice ของผู้ขายสินค้า อ่านเฉพาะ: เลขที่และวันที่ Invoice, ชื่อผู้ขาย, เลขผู้เสียภาษี/สาขา, ยอดก่อน VAT, VAT, ยอดรวม " +
              "— ห้ามใช้รายการในใบนี้เป็น lines (lines มาจาก PURCHASE ORDER)"
            : "",
        TaxInvoice or Invoice or Receipt =>
            "เอกสารประกอบ อ่านเฉพาะ: ผู้ออก, เลขผู้เสียภาษี, สาขา, เลขที่และวันที่, ตารางเลขที่ใบแจ้งหนี้ที่ชำระ (ถ้ามี) " +
            "และส่วนสรุปท้ายใบ (ไม่มี VAT / ก่อน VAT / VAT 7% / รวม / หัก ณ ที่จ่าย) — ไม่ต้องอ่านรายการทีละบรรทัด",
        Insurance =>
            "ใบเบี้ยประกันภัย อ่านเฉพาะ: บริษัทประกัน, เลขผู้เสียภาษี, เลขที่และวันที่, เบี้ยประกัน, อากรแสตมป์, VAT, ยอดรวม",
        Customs =>
            "เอกสารกรมศุลกากร: ใบเสร็จ อ่านเลขที่ชำระอากร/วันที่ (มุมขวาบน) และแต่ละรายการที่รับเงินกับยอด " +
            "(อากรขาเข้า / ภาษีมูลค่าเพิ่ม / ค่าธรรมเนียม / ค่าทำการล่วงเวลา); ใบขนสินค้า อ่านเลขที่ใบขนเท่านั้น",
        WhtCertificate => "หนังสือรับรองหัก ณ ที่จ่าย: ใช้ยืนยันยอดหัก ณ ที่จ่ายเท่านั้น ห้ามนับเป็นเอกสารประกอบซ้ำ",
        DeliveryOrder => "ใบปล่อยสินค้า: ถ้าไม่มียอดเงิน ไม่ต้องใส่ใน sourceDocs",
        Continuation => "หน้าต่อของเอกสารภาพก่อนหน้า: อ่านยอดสรุป / VAT ที่พิมพ์ต่อในหน้านี้รวมกับใบก่อนหน้า",
        _ => "",
    };

    /// <summary>The anchor page for the document type picked on the Import page.</summary>
    public static string AnchorFor(bool isInventoryPo) => isInventoryPo ? PurchaseOrder : FormShippingExpense;
}

/// <summary>What is known about one page of the file (1-based PageNo).</summary>
public class PageInfo
{
    public int PageNo { get; set; }
    public string Role { get; set; } = PageRoles.Unclassified;
    public double Confidence { get; set; }
    /// <summary>The classifier's own answer, kept even when the text layer overrides Role — this is
    /// what shows how often the classifier is right (null when the classifier did not run).</summary>
    public string? AiRole { get; set; }
    public double? AiConfidence { get; set; }
    /// <summary>"AI" = page classifier, "TEXT" = found by the text-layer regex (certain).</summary>
    public string Method { get; set; } = "AI";
    /// <summary>The classifier says the page may carry document data (false only for NoMoney roles).</summary>
    public bool Keep { get; set; } = true;
    /// <summary>The page was actually sent to the extraction read.</summary>
    public bool Used { get; set; } = true;
    /// <summary>Header strip (JPEG) — kept for anchor pages only, so a posted document can serve as
    /// the reference picture for the next one.</summary>
    public byte[]? HeaderJpeg { get; set; }
}

/// <summary>Reference header strips from earlier, posted documents (loaded by the API before a read).</summary>
public class PageMemoryHint
{
    public List<PageReference> References { get; set; } = [];
    /// <summary>Position hints (Ocr:PositionHints:Enabled): "_positions" from earlier documents of
    /// the same type, one line per vendor tax ID — which box held which field, and the line table.</summary>
    public string? Positions { get; set; }
}

public record PageReference(string Role, byte[] HeaderJpeg);

/// <summary>Result of one page-classification call.</summary>
public class PageClassification
{
    public List<PageInfo> Pages { get; set; } = [];
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public int? ThinkingTokens { get; set; }
    public int DurationMs { get; set; }
}
