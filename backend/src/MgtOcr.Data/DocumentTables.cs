namespace MgtOcr.Data;

// Ported from _tables()/_tables_for_id() in app/main.py (lines 122-137).
// Sales Order has its own physical tables (ocr.SalesOrder/SalesOrderLine/SalesOrderChat) —
// same columns as ocr.Document/DocumentLine/DocumentChat (shared by AP/II/PODP, split by the
// Module column) but a disjoint DocId range so document numbers never collide across the split.
public static class DocumentTables
{
    public const int SoIdBase = 100_000_000;

    public record TableSet(string Doc, string Line, string Chat);

    public static TableSet For(string module) => module == "SO"
        ? new TableSet("ocr.SalesOrder", "ocr.SalesOrderLine", "ocr.SalesOrderChat")
        : new TableSet("ocr.Document", "ocr.DocumentLine", "ocr.DocumentChat");

    public static TableSet ForId(int docId) => For(docId >= SoIdBase ? "SO" : "");
}
