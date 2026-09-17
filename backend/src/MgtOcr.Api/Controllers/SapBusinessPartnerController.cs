using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
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
public class SapBusinessPartnerController(
    SapBusinessPartnerClient client, AppConfig config, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet("business-partner")]
    public async Task<IActionResult> Find([FromQuery] string? name, [FromQuery] string? taxId,
        [FromQuery] int top = 20)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(taxId))
            return BadRequest(new { detail = "Provide 'taxId' and/or 'name'" });

        // The AuthorizationGroup scope comes from the SIGNED-IN user's own company, never from a
        // client-supplied companyCode — same reason "user" moved from a request field to the
        // token: a caller could otherwise pass any companyCode and read another company's data
        // through this endpoint, defeating the whole point of the AuthorizationGroup filter below.
        // CompanyForUser (not a plain Companies lookup) because Ms_Company.CompanyCode is "MGT" /
        // "Green Leaf" in this database, not the appsettings company profile's Name ("MGT"/"GLC")
        // or its SAP CompanyCode ("1000"/"2000") — see AppConfig.CompanyForUser's own comment.
        var user = await currentUser.RequireAsync();
        var profile = config.CompanyForUser(user.PrimaryCompany?.CompanyCode, user.SalesOrganization);
        var authorizationGroup = profile?.AuthorizationGroup;
        if (string.IsNullOrWhiteSpace(authorizationGroup))
            return BadRequest(new { detail = "No AuthorizationGroup is configured for this account's company" });

        var results = new List<BusinessPartner>();
        if (!string.IsNullOrWhiteSpace(taxId))
            results = await client.FindByTaxIdAsync(taxId, authorizationGroup, top);

        if (results.Count == 0 && !string.IsNullOrWhiteSpace(name))
            results = await client.FindByNameAsync(name, authorizationGroup, top);

        return Ok(new { count = results.Count, results });
    }

    // GET /api/sap/business-partner/1001406/partners?function=SH   -- this Sold-to's Ship-tos
    // GET /api/sap/business-partner/1001406/partners               -- every partner function on file
    // Read-only lookup of the Ship-to/Sold-to links SAP already has for a Sold-to's sales area
    // (A_CustSalesPartnerFunc), so a document's Ship-to can be matched against what SAP knows
    // instead of the local shiptos master alone. See FindPartnerFunctionsAsync's NOTE re: unverified
    // field names.
    [HttpGet("business-partner/{customerId}/partners")]
    public async Task<IActionResult> FindPartners(string customerId, [FromQuery] string? function,
        [FromQuery] string? salesOrganization, [FromQuery] int top = 50)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            return BadRequest(new { detail = "Provide customerId" });

        var results = await client.FindPartnerFunctionsAsync(customerId, function, salesOrganization, top);
        return Ok(new { count = results.Count, results });
    }

    // GET /api/sap/business-partner/1000043/payment-terms?salesOrganization=2000&companyCode=2000
    // The customer's Payment Terms from the SAP customer master -- company-code level first
    // (A_CustomerCompany.PaymentTerms), sales-area level as fallback (A_CustomerSalesArea
    // .CustomerPaymentTerms), same source and priority as the ZohoAccountPushJob account sync.
    // salesOrganization / companyCode scope which row to read (GLC = 2000 / 2000) and come from the
    // client (the document's own sales org, which follows the company being worked as) rather than
    // the token, so an admin simulating GLC reads GLC's terms -- there's no cross-company data-leak
    // concern here the way there is on the name/tax search (this reads ONE already-matched
    // customer's own payment terms, not a searchable list). Best-effort: paymentTerms is null (never
    // an error) when SAP isn't configured, has none on file, or the lookup fails.
    [HttpGet("business-partner/{customerId}/payment-terms")]
    public async Task<IActionResult> PaymentTerms(string customerId,
        [FromQuery] string? salesOrganization, [FromQuery] string? companyCode)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            return BadRequest(new { detail = "Provide customerId" });

        var paymentTerms = await client.GetPaymentTermsAsync(customerId, salesOrganization, companyCode);
        return Ok(new { customerId, paymentTerms });
    }

    // GET /api/sap/business-partner/1000043/sales-areas?salesOrganization=2000
    // The customer's sales areas (A_CustomerSalesArea) for a sales org -- each with its own
    // DistributionChannel / Division / SalesGroup / CustomerPaymentTerms. The GLC Customer card uses
    // this to auto-use the single area, or let the person choose when the customer has more than one.
    [HttpGet("business-partner/{customerId}/sales-areas")]
    public async Task<IActionResult> SalesAreas(string customerId, [FromQuery] string? salesOrganization)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            return BadRequest(new { detail = "Provide customerId" });

        var areas = await client.GetSalesAreasAsync(customerId, salesOrganization);
        return Ok(new { customerId, count = areas.Count, areas });
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
