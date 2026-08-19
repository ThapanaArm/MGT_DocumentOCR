namespace MgtOcr.Data;

// Ported from the MASTERS dict + ORDER_BY dict in app/main.py (lines 60-80).
public record MasterDefinition(string Table, string Key, bool Identity, string[] Cols, string OrderBy);

public static class MasterDefinitions
{
    public static readonly Dictionary<string, MasterDefinition> All = new()
    {
        ["customers"] = new("ocr.Customer", "CustomerCode", false,
            ["CustomerCode", "SapCustomerCode", "NameTh", "NameEn", "TaxId", "Branch",
             "SalesOrg", "DistChannel", "Division", "Currency", "PaymentTerms"],
            "CustomerCode"),

        ["shiptos"] = new("ocr.ShipTo", "ShipToCode", false,
            ["ShipToCode", "SapShipToCode", "CustomerCode", "ShipToName", "Address"],
            "CustomerCode, ShipToCode"),

        ["materials"] = new("ocr.Material", "MaterialCode", false,
            ["MaterialCode", "SapMaterialCode", "Description", "Uom", "Plant", "MatGroup"],
            "MaterialCode"),

        ["custmaterials"] = new("ocr.CustomerMaterial", "Id", true,
            ["CustomerCode", "ExtCode", "ExtDesc", "MaterialCode"],
            "CustomerCode, ExtCode"),

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
