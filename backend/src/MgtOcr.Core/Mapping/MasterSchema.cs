using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Core.Mapping;

/// <summary>Read adapter for the document mapping contract. SQL writes use only real columns.</summary>
public static class MasterSchema
{
    public static MasterData ForSalesOrg(MasterData data, string salesOrg)
    {
        static bool Active(Dictionary<string, object?> row) =>
            !row.TryGetValue("IsActive", out var value) && !row.TryGetValue("Isactive", out value)
            || value is true || value?.ToString() == "1";
        static Dictionary<string, object?> Alias(Dictionary<string, object?> row, params (string Actual, string Alias)[] fields)
        {
            var copy = new Dictionary<string, object?>(row);
            foreach (var (actual, alias) in fields)
                if (row.TryGetValue(actual, out var value)) copy[alias] = value;
            return copy;
        }
        bool InOrg(Dictionary<string, object?> row) => string.IsNullOrEmpty(salesOrg) || row.GetStr("SalesOrg") == salesOrg;
        var customers = data.Customers.Where(Active).Where(InOrg).Select(r => Alias(r,
            ("ComcompyCodeSAP", "CustomerCode"), ("ComcompyCodeSAP", "SapCustomerCode"),
            ("CompanyName", "NameTh"), ("CompanyNameSAP", "NameEn"))).ToList();
        var customerMaterials = data.CustomerMaterials.Where(Active).Where(InOrg).Select(r => Alias(r,
            ("MaterialCodeCode", "ExtCode"), ("MaterialCodeName", "ExtDesc"), ("MaterialCodeSAP", "MaterialCode"))).ToList();
        var materials = customerMaterials.Where(r => r.GetStr("MaterialCode").Length > 0)
            .GroupBy(r => r.GetStr("MaterialCode")).Select(g => new Dictionary<string, object?>
            {
                ["MaterialCode"] = g.Key, ["SapMaterialCode"] = g.Key,
                ["Description"] = g.First().GetStr("ExtDesc"),
            }).ToList();
        return new MasterData
        {
            Customers = customers,
            ShipTos = data.ShipTos.Where(Active).Select(r => Alias(r, ("ShipToAddress", "Address"))).ToList(),
            CustomerMaterials = customerMaterials, Materials = materials,
            Vendors = data.Vendors, VendorMaterials = data.VendorMaterials,
            Uoms = data.Uoms
                .Where(r => string.IsNullOrEmpty(r.GetStr("SalesOrg")) || InOrg(r))
                .OrderByDescending(r => !string.IsNullOrEmpty(r.GetStr("SalesOrg")))
                .ToList(),
        };
    }
}
