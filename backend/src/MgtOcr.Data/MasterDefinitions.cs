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
        ["customers"] = new("ocr.Customer", "id", true,
            ["SalesOrg", "CompanyName", "ComcompyCodeSAP", "CompanyNameSAP", "TaxId", "Branch",
             "DistChannel", "Division", "Currency", "PaymentTerms", "IsActive"],
            "SalesOrg, ComcompyCodeSAP, id"),

        ["shiptos"] = new("ocr.ShipTo", "id", true,
            ["ShipToCode", "SapShipToCode", "CustomerCode", "ShipToName", "ShipToAddress", "IsActive"],
            "CustomerCode, ShipToCode"),

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

        ["uoms"] = new("ocr.UomConversion", "Id", true,
            ["MaterialCode", "ExtUom", "SapUom", "SapUomIso", "Factor", "Note"],
            "CASE WHEN MaterialCode IS NULL THEN 0 ELSE 1 END, MaterialCode, ExtUom"),
    };
}
