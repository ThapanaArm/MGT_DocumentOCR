namespace MgtOcr.Zoho;

public static class ZohoDealStage
{
    // Deal search (Megachem, 10 Nov 2026): only Deals in Stage Draft/Qualification, Meeting/Memo or
    // Sample & Result are candidates for a PO. Spaces are ignored ("Sample & Result" = "Sample&Result").
    // Keep an explicit allow-list so unrelated pipeline stages do not become eligible.
    public static bool IsOpenOrDraft(string? stage)
    {
        var value = string.Concat((stage ?? "").Where(c => !char.IsWhiteSpace(c)));
        return value.Equals("Draft/Qualification", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Meeting/Memo", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Sample&Result", StringComparison.OrdinalIgnoreCase);
    }
}
