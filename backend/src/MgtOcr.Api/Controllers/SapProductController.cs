using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
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
public class SapProductController(
    SapProductClient client, SapBillingClient billingClient, AppConfig config, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet("materials")]
    public async Task<IActionResult> SearchMaterials([FromQuery] string? description, [FromQuery] string? plant, [FromQuery] int top = 30)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length < 2)
            return BadRequest(new { detail = "Provide at least 2 characters in 'description'" });

        if (string.IsNullOrWhiteSpace(plant))
            return BadRequest(new { detail = "Provide 'plant' for a company-scoped material search" });
        var results = await client.SearchByDescriptionAsync(description, plant, top);
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

    // GET /api/sap/last-price?customer=1000043&material=ACRY01-ID-DR-01
    // GLC Sales Order flow, material-confirm step: the last actual price SAP billed THIS customer
    // for THIS material (see SapBillingClient for the calculation). Plant is resolved from the
    // signed-in user's own company profile, never from a client-supplied value -- same reason the
    // AuthorizationGroup scope on /business-partner comes from CompanyForUser and not a query param.
    // price is null (never an error) when SAP Billing isn't configured, there's no prior billing
    // line for this pair, or the lookup fails -- the frontend must treat this as optional, not a
    // blocker to picking the material.
    [HttpGet("last-price")]
    public async Task<IActionResult> LastPrice([FromQuery] string? customer, [FromQuery] string? material)
    {
        if (string.IsNullOrWhiteSpace(customer) || string.IsNullOrWhiteSpace(material))
            return BadRequest(new { detail = "Provide both 'customer' and 'material'" });

        var user = await currentUser.RequireAsync();
        var profile = config.CompanyForUser(user.PrimaryCompany?.CompanyCode, user.SalesOrganization);

        var price = await billingClient.GetLastPriceAsync(customer, material, profile?.DefaultPlant);
        return Ok(new { customer, material, price });
    }

    // Diagnostic only (see SapBillingClient's doc comments). Search by material, by customer, or
    // both — at least one is required:
    //   ?material=ACRY01-ID-DR-01                 -- full raw dump of that material's billing lines
    //   ?material=ACRY01-ID-DR-01&customer=1000043 -- that material's lines for that customer (compact
    //                                                 summary); customer matches SoldToParty by "contains"
    //   ?customer=1000043                          -- ALL that customer's billing lines (compact summary,
    //                                                 queried via A_BillingDocument header); exact SoldToParty
    // Used to inspect SAP's own field values/format directly (e.g. via Postman) when a /last-price
    // lookup unexpectedly comes back null. Each summary line includes the pricePerUnit last-price
    // would compute.
    [HttpGet("last-price/raw")]
    public async Task<IActionResult> LastPriceRaw([FromQuery] string? material, [FromQuery] string? customer, [FromQuery] int top = 50)
    {
        var hasMaterial = !string.IsNullOrWhiteSpace(material);
        var hasCustomer = !string.IsNullOrWhiteSpace(customer);
        if (!hasMaterial && !hasCustomer)
            return BadRequest(new { detail = "Provide 'material' and/or 'customer'" });

        var raw = hasMaterial
            ? await billingClient.GetRawByMaterialAsync(material!, top, customer)
            : await billingClient.GetRawByCustomerAsync(customer!, top);
        return Content(raw, "application/json");
    }
}
