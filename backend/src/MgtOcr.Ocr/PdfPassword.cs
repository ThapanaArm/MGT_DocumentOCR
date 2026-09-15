namespace MgtOcr.Ocr;

// Ambient per-request PDF open-password. The password the user types at upload needs to reach every
// place that opens the PDF — PdfPig text extraction and PDFium rasterization — but those sit behind
// several OCR providers. Rather than thread the password through every provider signature, set it
// once at the top of OcrEngine.ExtractAsync; AsyncLocal carries it down the async call chain and
// scopes it to that one logical call.
public static class PdfPassword
{
    private static readonly System.Threading.AsyncLocal<string?> _current = new();
    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
