using MgtOcr.Sap;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Step 2 of SAP integration for the Sales Order module (after SapBusinessPartnerController):
// confirm a material has been extended to a given Plant in SAP before creating a Sales Order.
// Per user: Plant tells you the company (2100 = GLC, 1100 = MGT).
// GET /api/sap/material-plant?material=ACRY01-ID-DR-01&plant=2100   (single check)
// GET /api/sap/material-plants?material=ACRY01-ID-DR-01             (list all plants extended)
[ApiController]
[Route("api/sap")]
public class SapProductController(SapProductClient client) : ControllerBase
{
    [HttpGet("materials")]
    public async Task<IActionResult> SearchMaterials([FromQuery] string? description, [FromQuery] int top = 30)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length < 2)
            return BadRequest(new { detail = "Provide at least 2 characters in 'description'" });

        var results = await client.SearchByDescriptionAsync(description, top);
        return Ok(new { count = results.Count, results });
    }

    [HttpGet("material-plant")]
    public async Task<IActionResult> CheckPlant([FromQuery] string? material, [FromQuery] string? plant)
    {
        if (string.IsNullOrWhiteSpace(material) || string.IsNullOrWhiteSpace(plant))
            return BadRequest(new { detail = "Provide both 'material' and 'plant'" });

        var extended = await client.IsExtendedToPlantAsync(material, plant);
        return Ok(new { material, plant, extended });
    }

    [HttpGet("material-plants")]
    public async Task<IActionResult> ListPlants([FromQuery] string? material)
    {
        if (string.IsNullOrWhiteSpace(material))
            return BadRequest(new { detail = "Provide 'material'" });

        var plants = await client.FindPlantsAsync(material);
        return Ok(new { material, count = plants.Count, plants });
    }

    // GET /api/sap/material-detail?product=SOCA01-CN-BG-11
    // Best-effort Base Unit / Material Group / alternative-unit conversion ratios, used to
    // pre-fill the "confirm material details" step shown when a SAP search result is saved.
    // detail is null when SAP isn't configured, the material isn't found, or the lookup fails —
    // the frontend must treat this as an optional default, never a blocker.
    [HttpGet("material-detail")]
    public async Task<IActionResult> MaterialDetail([FromQuery] string? product)
    {
        if (string.IsNullOrWhiteSpace(product))
            return BadRequest(new { detail = "Provide 'product'" });

        var detail = await client.GetMaterialDetailAsync(product);
        return Ok(new { detail });
    }
}
