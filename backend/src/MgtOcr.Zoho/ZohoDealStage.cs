namespace MgtOcr.Zoho;

public static class ZohoDealStage
{
    // The only Deal Stage the OCR flow works with (Megachem, 16 Nov 2026): a PO's Deal must be at
    // Draft/Qualification -- an existing Deal (even one that already has Sales Orders) and a Deal the
    // flow opens itself. Was Draft/Qualification, Meeting/Memo or Sample & Result (10 Nov 2026).
    public const string Draft = "Draft/Qualification";

    // Spaces are ignored ("Draft / Qualification" = "Draft/Qualification").
    public static bool IsOpenOrDraft(string? stage)
    {
        var value = string.Concat((stage ?? "").Where(c => !char.IsWhiteSpace(c)));
        return value.Equals(Draft, StringComparison.OrdinalIgnoreCase);
    }
}
