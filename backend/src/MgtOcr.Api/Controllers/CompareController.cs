using MgtOcr.Core.Config;
using MgtOcr.Ocr;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

public record CompareFieldDto(string Label, string? Value);
public record CompareCandidateDto(string Id, string Label, List<CompareFieldDto> Fields);
public record CompareRequest(
    List<CompareFieldDto> DocFields, List<CompareCandidateDto> Candidates, string? Provider,
    string? Instruction, string? PriorSuggestedCandidateId);

// AI-assisted compare, used by the chat-driven "which one is correct?" flow on any Mapping card
// whose live search (SAP Business Partner today, Zoho CRM Account, and in future
// Vendor/Ship-to/Material lookups) comes back with more than one candidate. Stateless and
// read-only — no docId, touches no document or master data itself; the frontend is what applies
// a match (creating the local master record + setting it), and only when this call's response
// carries decision.action == "select". Same reasoning as CompareAdvisor's own doc comment.
[ApiController]
[Route("api/compare")]
public class CompareController(AppConfig config) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Compare([FromBody] CompareRequest body)
    {
        if (body.Candidates == null || body.Candidates.Count == 0)
            return BadRequest(new { detail = "Provide at least one candidate to compare" });

        var provider = body.Provider is "gemini" or "openai" ? body.Provider : "claude";
        var docFields = (body.DocFields ?? []).Select(f => new CompareField(f.Label, f.Value)).ToList();
        var candidates = body.Candidates
            .Select(c => new CompareCandidate(c.Id, c.Label, (c.Fields ?? []).Select(f => new CompareField(f.Label, f.Value)).ToList()))
            .ToList();

        CompareOutcome? outcome;
        try
        {
            outcome = await CompareAdvisor.CompareAsync(
                docFields, candidates, provider, config, body.Instruction, body.PriorSuggestedCandidateId);
        }
        catch (Exception ex)
        {
            // Surface the real provider error (bad model name, invalid key, quota, timeout, …)
            // instead of the misleading generic "check your API key".
            return StatusCode(502, new { detail = $"AI provider ({provider}) error: {ex.Message}" });
        }
        if (outcome == null)
            return StatusCode(502, new { detail = $"Could not get a comparison from the AI provider ({provider}) — its API key may be empty in appsettings.json, or it returned an unusable response" });

        return Ok(new { verdicts = outcome.Verdicts, decision = outcome.Decision });
    }

    // AI-assisted search-keyword suggestion — a person explicitly clicks "search with AI" after a
    // live external-system search (SAP Business Partner, Material, Ship-to, ...) finds nothing
    // with the normal deterministic search. Never runs on its own; proposes terms only, the
    // frontend re-runs its own normal search with whichever one the person picks.
    [HttpPost("search-keywords")]
    public async Task<IActionResult> SuggestSearchKeywords([FromBody] SearchKeywordsRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Text))
            return BadRequest(new { detail = "Provide 'text' to suggest search keywords for" });

        var provider = body.Provider is "gemini" or "openai" ? body.Provider : "claude";
        var kind = string.IsNullOrWhiteSpace(body.Kind) ? "record" : body.Kind;

        var suggestion = await SearchAdvisor.SuggestKeywordsAsync(body.Text, kind, provider, config);
        if (suggestion == null)
            return StatusCode(502, new { detail = $"Could not get a suggestion from the AI provider ({provider}) — check its API key in appsettings.json" });

        return Ok(new { keywords = suggestion.Keywords, reason = suggestion.Reason });
    }
}

public record SearchKeywordsRequest(string Text, string? Kind, string? Provider);
