using MgtOcr.Ocr;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/ocr/providers in app/main.py — lists selectable OCR engines for the UI dropdown.
[ApiController]
[Route("api/ocr")]
public class OcrController(OcrEngine engine) : ControllerBase
{
    [HttpGet("providers")]
    public IActionResult GetProviders() => Ok(engine.Providers);

    // Temporary scaffolding for Phase 2 verification (golden-file testing against the real PDF
    // corpus) ahead of the full /api/documents/upload flow (Phase 3) — not part of the ported API
    // surface, remove once Phase 3's real upload endpoint supersedes it.
    public record TestExtractRequest(string Path, string Module, string? Provider);
    [HttpPost("test-extract")]
    public async Task<IActionResult> TestExtract([FromBody] TestExtractRequest req)
    {
        var doc = await engine.ExtractAsync(req.Path, req.Module, req.Provider);
        return Ok(doc);
    }
}
