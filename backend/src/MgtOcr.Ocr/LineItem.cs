namespace MgtOcr.Ocr;

public class LineItem
{
    public string ExtCode { get; set; } = "";
    public string Desc { get; set; } = "";
    public double Qty { get; set; }
    public string DueDate { get; set; } = "";
    public string Uom { get; set; } = "EA";
    public double Price { get; set; }
    public double Amount { get; set; }

    // Item Note 1 (SD item long text). OCR-prefilled from a per-line note/remark on the
    // PO, editable by the CS, sent to SAP as the item text ZI01. SO only; blank otherwise.
    public string ItemNote { get; set; } = "";
}
