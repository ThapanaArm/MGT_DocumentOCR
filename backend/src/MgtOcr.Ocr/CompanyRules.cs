namespace MgtOcr.Ocr;

/// <summary>Which company's accounting rules one read follows.
///
/// MGT and GLC share this installation but not their paperwork: the shipping cost form, the vendor
/// each cost lands on and the tax treatment all differ. Everything written before MGT came into
/// scope was GLC's rules, so GLC stays the default — a document with no company stamped on it (an
/// older row, a re-read of an import from before the company column existed) keeps behaving exactly
/// as it did, and only a document explicitly stamped 1000 takes the MGT path.</summary>
public enum InvoiceCompany
{
    Glc,
    Mgt,
}

public static class CompanyRules
{
    public const string MgtSalesOrg = "1000";
    public const string GlcSalesOrg = "2000";

    public static InvoiceCompany For(string? salesOrg) =>
        (salesOrg ?? "").Trim() == MgtSalesOrg ? InvoiceCompany.Mgt : InvoiceCompany.Glc;

    public static bool IsMgt(string? salesOrg) => For(salesOrg) == InvoiceCompany.Mgt;
}
