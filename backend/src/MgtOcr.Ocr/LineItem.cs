namespace MgtOcr.Ocr;

public class LineItem
{
    public string ExtCode { get; set; } = "";
    public string Desc { get; set; } = "";
    public double Qty { get; set; }
    public string DueDate { get; set; } = "";
    public string Uom { get; set; } = "EA";

    // For a "VAT" row: how that input tax may be claimed — "INPUT" when the source document is a
    // tax invoice / receipt (claimable this period, goes on the input-VAT report) or "DEFERRED"
    // when it is only an invoice / billing note (ภาษีซื้อรอเรียกเก็บ, claimed once the tax invoice
    // arrives). Mirrors the Y/N flag in Finance's Supplier Invoice flow. Empty on other rows.
    public string TaxKind { get; set; } = "";

    // --- Input-VAT report fields, filled on a "VAT" row only ---
    // The Input VAT file Finance sends to SAP is one row per tax invoice, so the row has to carry
    // the invoice's own identity, not just its VAT amount. Empty on every other row.
    public string TaxDocNo { get; set; } = "";      // เลขที่ใบกำกับภาษี
    public string TaxDocDate { get; set; } = "";    // วันที่ใบกำกับภาษี, yyyy-MM-dd
    public string IssuerName { get; set; } = "";    // ชื่อผู้ออกใบกำกับภาษี
    public string IssuerTaxId { get; set; } = "";   // เลขประจำตัวผู้เสียภาษี 13 หลัก
    public string IssuerBranch { get; set; } = ""; // สาขา, 00000 = สำนักงานใหญ่
    public double BaseAmount { get; set; }          // ฐานภาษี (มูลค่าก่อน VAT) ของใบนั้น

    // Vendor code from the FORM SHIPPING EXPENSE "VENDOR" column — each cost row is paid to a
    // different vendor, and one MIRO document can only carry one, so this drives the split.
    public string VendorCode { get; set; } = "";
    public double Price { get; set; }
    public double Amount { get; set; }

    // Item Note 1 (SD item long text). OCR-prefilled from a per-line note/remark on the
    // PO, editable by the CS, sent to SAP as the item text ZI01. SO only; blank otherwise.
    public string ItemNote { get; set; } = "";
}
