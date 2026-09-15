using System.Globalization;
using MgtOcr.Data;
using MgtOcr.Sap;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Save a CustomerMaterial together with its unit-of-measure conversion rows in ONE call.
// This is the backend "glue" behind the (planned) UoM popup: it pulls the material's real units
// from SAP (API_PRODUCT_SRV, via SapProductClient.GetMaterialDetailAsync) and writes both the
// CustomerMaterial and the ocr.UomConversion rows in a single transaction, so a material can never
// be saved without its units. The UI can call this once and refine ExtUom (the wording the CUSTOMER
// prints on their PO — SAP only knows the codes) later.
//
//   POST /api/custmaterials
//   {
//     "salesOrg":"1000", "customerCode":"1000053",
//     "materialCodeCode":"188127", "materialCodeName":"Sodium carbonate light",
//     "materialCodeSap":"SOCA01-CN-BG-11",
//     "autoFetchUom":true,                 // pull base+alternative units from SAP as defaults
//     "uoms":[                              // OR/also: rows the user confirmed in the popup
//       {"extUom":"Kilogram","sapUom":"KG","factor":1,"sapUomIso":"KGM","note":"customer writes 'Kilogram'"}
//     ]
//   }
[ApiController]
[Route("api/custmaterials")]
public class CustomerMaterialController(
    MasterRepository repo, SapProductClient sap, ILogger<CustomerMaterialController> logger) : ControllerBase
{
    public record UomInput(string? ExtUom, string? SapUom, decimal? Factor, string? SapUomIso, string? Note);

    public record SaveRequest(
        string? SalesOrg,
        string? CustomerCode,
        string? MaterialCodeCode,
        string? MaterialCodeName,
        string? MaterialCodeSap,
        bool? Isactive,
        List<UomInput>? Uoms,
        bool AutoFetchUom = true);

    // ISO codes for SAP base units. Mirrors MappingEngine.UomIso (SAP's product API returns the
    // internal unit but not its ISO code); kept here so this controller doesn't reach into the
    // mapping engine's private table. Keep the two in sync if either changes.
    private static readonly Dictionary<string, string> UomIso = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KG"] = "KGM", ["G"] = "GRM", ["TON"] = "TNE", ["L"] = "LTR", ["ML"] = "MLT", ["M"] = "MTR",
        ["EA"] = "PCE", ["PC"] = "PCE", ["PCS"] = "PCE", ["BOX"] = "BX", ["BAG"] = "BG", ["DRUM"] = "DR", ["AU"] = "ACT",
    };

    private static string IsoFor(string? unit) => UomIso.GetValueOrDefault((unit ?? "").Trim(), "");

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveRequest req, CancellationToken ct)
    {
        // Same required fields + SalesOrg rule as MastersController's "custmaterials" validation.
        if (string.IsNullOrWhiteSpace(req.SalesOrg)) return BadRequest(new { detail = "Please enter SalesOrg" });
        if (string.IsNullOrWhiteSpace(req.CustomerCode)) return BadRequest(new { detail = "Please enter CustomerCode" });
        if (string.IsNullOrWhiteSpace(req.MaterialCodeCode)) return BadRequest(new { detail = "Please enter MaterialCodeCode" });
        if (string.IsNullOrWhiteSpace(req.MaterialCodeSap)) return BadRequest(new { detail = "Please enter MaterialCodeSAP" });
        if (req.SalesOrg is not ("1000" or "2000")) return BadRequest(new { detail = "SalesOrg must be 1000 (MGT) or 2000 (GLC)" });

        var sapCode = req.MaterialCodeSap.Trim();

        var custMaterial = new Dictionary<string, object?>
        {
            ["SalesOrg"] = req.SalesOrg,
            ["CustomerCode"] = req.CustomerCode,
            ["MaterialCodeCode"] = req.MaterialCodeCode,
            ["MaterialCodeName"] = req.MaterialCodeName ?? "",
            ["MaterialCodeSAP"] = sapCode,
            ["Isactive"] = req.Isactive ?? true,
        };

        // 1) Fetch the material's units from SAP (best-effort — never blocks the save).
        SapMaterialDetail? detail = null;
        if (req.AutoFetchUom || req.Uoms is not { Count: > 0 })
        {
            detail = await sap.GetMaterialDetailAsync(sapCode);
        }

        // 2) Build the UoM rows to persist.
        //    - Explicit rows from the popup win (they carry the customer's real wording in ExtUom).
        //    - Otherwise seed defaults from SAP: the base unit as an identity row (factor 1) plus one
        //      row per alternative unit (1 AltUnit = Numerator/Denominator base units). ExtUom
        //      defaults to the SAP code here; the popup can later add the customer's own wording.
        var uomRows = new List<Dictionary<string, object?>>();

        if (req.Uoms is { Count: > 0 })
        {
            foreach (var u in req.Uoms)
            {
                var ext = u.ExtUom?.Trim();
                var sapU = u.SapUom?.Trim();
                if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(sapU)) continue;
                uomRows.Add(new Dictionary<string, object?>
                {
                    ["MaterialCode"] = sapCode,
                    ["ExtUom"] = ext,
                    ["SapUom"] = sapU,
                    ["SapUomIso"] = string.IsNullOrWhiteSpace(u.SapUomIso) ? IsoFor(sapU) : u.SapUomIso!.Trim(),
                    ["Factor"] = u.Factor is > 0 ? u.Factor.Value : 1m,
                    ["Note"] = u.Note ?? "",
                });
            }
        }
        else if (detail is { BaseUnit.Length: > 0 })
        {
            var baseUnit = detail.BaseUnit.Trim();
            var baseIso = IsoFor(baseUnit);

            // base unit as an identity conversion (1:1)
            uomRows.Add(new Dictionary<string, object?>
            {
                ["MaterialCode"] = sapCode,
                ["ExtUom"] = baseUnit,
                ["SapUom"] = baseUnit,
                ["SapUomIso"] = baseIso,
                ["Factor"] = 1m,
                ["Note"] = "SAP base unit",
            });

            foreach (var alt in detail.AltUnits)
            {
                if (string.IsNullOrWhiteSpace(alt.Unit) || alt.Denominator == 0) continue;
                var factor = alt.Numerator / alt.Denominator; // 1 AltUnit = factor base units
                uomRows.Add(new Dictionary<string, object?>
                {
                    ["MaterialCode"] = sapCode,
                    ["ExtUom"] = alt.Unit.Trim(),
                    ["SapUom"] = baseUnit,
                    ["SapUomIso"] = baseIso,
                    ["Factor"] = factor,
                    ["Note"] = $"From SAP: 1 {alt.Unit.Trim()} = {factor.ToString("0.####", CultureInfo.InvariantCulture)} {baseUnit}",
                });
            }
        }

        // 3) Persist CustomerMaterial + UoM rows in one transaction.
        int uomCreated, uomSkipped;
        try
        {
            (uomCreated, uomSkipped) = await repo.CreateCustomerMaterialWithUomsAsync(custMaterial, uomRows, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CUSTMAT SAVE] failed SalesOrg={org} Cust={cust} Mat={mat}",
                req.SalesOrg, req.CustomerCode, sapCode);
            return StatusCode(500, new { detail = "Save failed: " + ex.Message });
        }

        // NOTE: ocr.Material has been dropped, so there is no material master to write the SAP base
        // unit back to. That's fine — with no base unit the SO mapping's ConvertUom relies entirely
        // on the ocr.UomConversion rows saved above (it no longer short-circuits on a base-unit match).

        return Ok(new
        {
            ok = true,
            uomCreated,
            uomSkipped,
            sapConfigured = detail != null,
            sapDetail = detail,   // BaseUnit + AltUnits, so the UI can render the popup without a second call
        });
    }
}
