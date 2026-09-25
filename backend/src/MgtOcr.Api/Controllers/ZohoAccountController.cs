using MgtOcr.Zoho;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Zoho-side counterpart to SapBusinessPartnerController — used for the MGT-side Sales Order
// flow (Green Leaf keeps matching against SAP). Same taxId-first, name-fallback shape.
// GET /api/zoho/account?taxId=0105536003404   (preferred — exact match)
// GET /api/zoho/account?name=oriental          (fallback — contains match)
[ApiController]
[Route("api/zoho")]
public class ZohoAccountController(ZohoAccountClient client, ZohoClient zoho, ZohoDealClient deals) : ControllerBase
{
    [HttpGet("account/by-code/{code}")]
    public async Task<IActionResult> FindByCode(string code)
    {
        var account = await client.FindByAccountCodeAsync(code);
        return account == null ? NotFound(new { detail = "No unique Zoho account matches this Account Code" }) : Ok(account);
    }

    [HttpGet("account")]
    public async Task<IActionResult> Find([FromQuery] string? name, [FromQuery] string? taxId, [FromQuery] string? code, [FromQuery] int top = 20)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(taxId) && string.IsNullOrWhiteSpace(code))
            return BadRequest(new { detail = "Provide 'taxId', 'code' and/or 'name'" });

        // Same cascade as the SAP side: exact Tax ID → Account_Code (substring) → fuzzy name.
        var results = new List<ZohoAccount>();
        if (!string.IsNullOrWhiteSpace(taxId))
            results = await client.FindByTaxIdAsync(taxId, top);
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(code))
            results = await client.FindByAccountCodeContainsAsync(code, top);
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(name))
            results = await client.FindByNameAsync(name, top);

        return Ok(new { count = results.Count, results });
    }

    // GET /api/zoho/account/{id}/full — every populated field Zoho has for this Account, for
    // the "Compare with AI" popup. See ZohoAccountClient.GetFullAccountFieldsAsync for why this
    // is a generic record fetch rather than another hand-picked field list.
    [HttpGet("account/{id}/full")]
    public async Task<IActionResult> GetFull(string id)
    {
        var fields = await client.GetFullAccountFieldsAsync(id);
        return Ok(new { fields = fields.Select(f => new { label = f.Label, value = f.Value }) });
    }

    // GET /api/zoho/account/{id}/shipto — single best Ship-to address for this Account (not a
    // separate search): the Account's own inline fields if present, else the first record from
    // Megachem's separate Ship-to Module. See ZohoAccountClient.GetShipToInfoAsync.
    [HttpGet("account/{id}/shipto")]
    public async Task<IActionResult> GetShipTo(string id)
    {
        var info = await client.GetShipToInfoAsync(id);
        return Ok(info);
    }

    // GET /api/zoho/account/{id}/shiptos — EVERY Ship-to on file for this Account, plus whether
    // the "Ship to มากกว่า 1" (Ship_to_1) checkbox is ticked: the inline Account fields (if any)
    // and, when that checkbox is set, every record from Megachem's separate Ship-to Module too.
    // See ZohoAccountClient.GetAllShipTosAsync.
    [HttpGet("account/{id}/shiptos")]
    public async Task<IActionResult> GetShipTos(string id)
    {
        var result = await client.GetAllShipTosAsync(id);
        return Ok(new { count = result.ShipTos.Count, hasMultipleShipTos = result.HasMultipleShipTos, shipTos = result.ShipTos });
    }

    // GET /api/zoho/account/{id}/soldto — the customer's own (billing) address off this same
    // Account, shown alongside a search result. See ZohoAccountClient.GetSoldToInfoAsync.
    [HttpGet("account/{id}/soldto")]
    public async Task<IActionResult> GetSoldTo(string id)
    {
        var info = await client.GetSoldToInfoAsync(id);
        return Ok(info);
    }

    // GET /api/zoho/modules — diagnostic: lists every module in this Zoho org (real api_name +
    // display labels), so we can check whether a separate "Ship To" module exists instead of
    // guessing. See ZohoClient.GetModulesAsync.
    [HttpGet("modules")]
    public async Task<IActionResult> GetModules()
    {
        var modules = await zoho.GetModulesAsync();
        var simplified = modules.Select(m => new
        {
            apiName = m?["api_name"]?.ToString(),
            pluralLabel = m?["plural_label"]?.ToString(),
            singularLabel = m?["singular_label"]?.ToString(),
        });
        return Ok(new { count = modules.Count, modules = simplified });
    }

    // GET /api/zoho/related-lists — diagnostic: lists Accounts' configured related lists (real
    // api_name + display label). ZohoAccountClient.GetShipToInfoAsync already picks one of these
    // automatically (whichever's label contains "ship") when the Account's own "Ship to ..."
    // sub-fields come up empty — this endpoint is just the manual escape hatch, for checking
    // what it saw or confirming the real api_name if the auto-pick guesses wrong.
    [HttpGet("related-lists")]
    public async Task<IActionResult> GetRelatedLists()
    {
        var lists = await zoho.GetRelatedListsAsync("Accounts");
        var simplified = lists.Select(r => new
        {
            apiName = r?["api_name"]?.ToString(),
            displayLabel = r?["display_label"]?.ToString(),
            name = r?["name"]?.ToString(),
            module = r?["module"]?["api_name"]?.ToString(),
        });
        return Ok(new { count = lists.Count, relatedLists = simplified });
    }

    // GET /api/zoho/account/{id}/deals — every OPEN Deal linked to this Account (Stage not
    // Closed Won/Lost of any kind), each with its Deal Items subform. See
    // GET /api/zoho/deals/by-account-code/{code} — every open Deal whose own Account_Code field
    // matches this customer code (see ZohoDealClient.FindOpenDealsByAccountCodeAsync for why this
    // is queried by Account_Code and not Zoho's internal Account id: no Zoho account link/id
    // needs to exist for this to work, unlike account/{id}/shipto|soldto|full above). Deliberately
    // returns every candidate rather than picking "the" match; Megachem wants the person (with
    // the OCR AI-compare tool if they want it) to choose which Deal a document belongs to.
    [HttpGet("deals/by-account-code/{code}")]
    public async Task<IActionResult> GetDealsByAccountCode(string code)
    {
        var found = await deals.FindOpenDealsByAccountCodeAsync(code);
        return Ok(new { count = found.Count, deals = found });
    }

    // GET /api/zoho/deals/search?q=... — manual fallback shown by the Deal card when the
    // Account_Code lookup above comes up empty. Searches by Deal Name only, with no
    // account-code or stage filter, so a Deal that was missed because its Account link (and so
    // its Account_Code) is wrong/blank, or because it's already Closed, still turns up -- see
    // ZohoDealClient.SearchByNameAsync.
    [HttpGet("deals/search")]
    public async Task<IActionResult> SearchDeals([FromQuery] string q, [FromQuery] int top = 20)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { detail = "Provide 'q'" });
        var found = await deals.SearchByNameAsync(q, top);
        return Ok(new { count = found.Count, deals = found });
    }

    // GET /api/zoho/deals/{id} — one Deal by its Zoho record id (with Deal Items), used to
    // re-fetch full details after the person picks a result from the manual search above.
    [HttpGet("deals/{id}")]
    public async Task<IActionResult> GetDealById(string id)
    {
        var deal = await deals.GetByIdAsync(id);
        return deal is null ? NotFound(new { detail = "Deal not found" }) : Ok(deal);
    }
}
