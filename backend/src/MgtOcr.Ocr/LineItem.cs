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
}
