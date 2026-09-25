namespace MgtOcr.Data;

// Ported from the MASTERS dict + ORDER_BY dict in app/main.py (lines 60-80).
public record MasterDefinition(string Table, string Key, bool Identity, string[] Cols, string OrderBy);

public static class MasterDefinitions
{
    public static readonly Dictionary<string, MasterDefinition> All = new()
    {
        // "apmaterials" (ocr.Material) removed — the material master is no longer used; material
        // mapping now lives entirely in ocr.CustomerMaterial. The "materials" list handed to the
        // mapping engine is derived from CustomerMaterial (see MasterRepository.LoadAllAsync).
        // Address is NOT stored broken out into sub-fields. The full address the person sees when a
        // SAP/Zoho record is pulled comes from the LIVE fetched record (SapBusinessPartner /
        // ZohoAccount), shown field-by-field in the search panels — it was never meant to be
        // persisted here, so ocr.Customer/ocr.ShipTo keep only their own columns (ShipTo still has
        // its single Address for the doc-vs-master fuzzy match). This also means sql/21_address_
        // subfields.sql is unnecessary; nothing reads or writes those columns.
        ["customers"] = new("ocr.Customer", "id", true,
            ["SalesOrg", "CompanyName", "ComcompyCodeSAP", "CompanyNameSAP", "TaxId", "Branch",
             "DistChannel", "Division", "Currency", "PaymentTerms", "IsActive"],
            "SalesOrg, ComcompyCodeSAP, id"),

        ["shiptos"] = new("ocr.ShipTo", "id", true,
            ["SalesOrg", "ShipToCode", "SapShipToCode", "CustomerCode", "ShipToName", "ShipToAddress", "IsActive"],
            "SalesOrg, CustomerCode, ShipToCode"),

        ["custmaterials"] = new("ocr.CustomerMaterial", "Id", true,
            ["SalesOrg", "CustomerCode", "MaterialCodeCode", "MaterialCodeName", "MaterialCodeSAP", "Isactive"],
            "SalesOrg, CustomerCode, MaterialCodeCode, Id"),

        ["vendors"] = new("ocr.Vendor", "VendorCode", false,
            ["VendorCode", "SapVendorCode", "VendorName", "TaxId", "Branch", "Currency",
             "PaymentTerms", "ReconAcct", "WhtCode"],
            "VendorCode"),

        ["venmaterials"] = new("ocr.VendorMaterial", "Id", true,
            ["VendorCode", "ExtCode", "ExtDesc", "MaterialCode"],
            "VendorCode, ExtCode"),

        // SalesOrg scopes a unit rule to one company (NULL = every company); the rest of the model
        // is unchanged (ExtUom label -> valid SapUom code, Factor converts the quantity). Global
        // rules sort first, then per-company.
        ["uoms"] = new("ocr.UomConversion", "Id", true,
            ["SalesOrg", "MaterialCode", "ExtUom", "SapUom", "SapUomIso", "Factor", "Note"],
            "CASE WHEN SalesOrg IS NULL THEN 0 ELSE 1 END, SalesOrg, CASE WHEN MaterialCode IS NULL THEN 0 ELSE 1 END, MaterialCode, ExtUom"),

        // NOTE: Payment-terms code->text mapping is intentionally NOT an editable master here. It
        // lives in the shared dbo.SysDataMapping table (Subject='Payment_Terms'), which is
        // maintained centrally (also used by the Zoho account sync), so the OCR app only READS it
        // for display (see MasterRepository.LoadAllAsync's "paymentterms" entry) and does not expose
        // generic CRUD over that shared table.
    };
}
