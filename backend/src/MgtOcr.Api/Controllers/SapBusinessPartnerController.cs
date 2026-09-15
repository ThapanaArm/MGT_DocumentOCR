using MgtOcr.Sap;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Step 1 of SAP integration for the Sales Order module: confirm the connection to SAP works and
// that a customer read off a document can be matched to its SAP Business Partner record.
// GET /api/sap/business-partner?taxId=0105536003404   (preferred — exact match)
// GET /api/sap/business-partner?name=oriental          (fallback — fuzzy name match)
// Passing both tries taxId first and only falls back to name if that comes up empty.
[ApiController]
[Route("api/sap")]
public class SapBusinessPartnerController(SapBusinessPartnerClient client) : ControllerBase
{
    [HttpGet("business-partner")]
    public async Task<IActionResult> Find([FromQuery] string? name, [FromQuery] string? taxId, [FromQuery] int top = 20)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(taxId))
            return BadRequest(new { detail = "Provide 'taxId' and/or 'name'" });

        var results = new List<BusinessPartner>();
        if (!string.IsNullOrWhiteSpace(taxId))
            results = await client.FindByTaxIdAsync(taxId, top);

        if (results.Count == 0 && !string.IsNullOrWhiteSpace(name))
            results = await client.FindByNameAsync(name, top);

        return Ok(new { count = results.Count, results });
    }

    // GET /api/sap/business-partner/1001406/partners?function=SH   -- this Sold-to's Ship-tos
    // GET /api/sap/business-partner/1001406/partners               -- every partner function on file
    // Read-only lookup of the Ship-to/Sold-to links SAP already has for a Sold-to's sales area
    // (A_CustSalesPartnerFunc), so a document's Ship-to can be matched against what SAP knows
    // instead of the local shiptos master alone. See FindPartnerFunctionsAsync's NOTE re: unverified
    // field names.
    [HttpGet("business-partner/{customerId}/partners")]
    public async Task<IActionResult> FindPartners(string customerId, [FromQuery] string? function, [FromQuery] int top = 50)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            return BadRequest(new { detail = "Provide customerId" });

        var results = await client.FindPartnerFunctionsAsync(customerId, function, top);
        return Ok(new { count = results.Count, results });
    }

    // GET /api/sap/business-partner/1000053/raw -- diagnostic only, see GetRawByIdAsync's doc
    // comment. Returns SAP's complete, unfiltered response for one Business Partner so a field's
    // actual stored content (casing, exact text) can be inspected directly instead of guessed at.
    [HttpGet("business-partner/{id}/raw")]
    public async Task<IActionResult> GetRaw(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { detail = "Provide id" });

        var raw = await client.GetRawByIdAsync(id);
        return Content(raw, "application/json");
    }
}
