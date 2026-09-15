namespace MgtOcr.Data;

public partial class DocumentRepository
{
    // The actual module of a stored document. AP/II/PODP live in ocr.Document (split by the Module
    // column); SO rows live in ocr.SalesOrder, whose DocIds sit in a disjoint high range. Returns
    // null when no such document exists. Used by DepartmentAccessFilter to authorize {docId} routes.
    public async Task<string?> GetModuleAsync(int docId)
    {
        if (docId >= DocumentTables.SoIdBase) return "SO";
        dynamic? row = await db.QueryOneAsync("SELECT Module FROM ocr.Document WHERE DocId=@docId", new { docId });
        return row?.Module as string;
    }
}
