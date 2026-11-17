using MgtOcr.Ocr;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/ocr/providers in app/main.py — the reading engine shown in the UI (Gemini only).
[ApiController]
[Route("api/ocr")]
public class OcrController(OcrEngine engine) : ControllerBase
{
    [HttpGet("providers")]
    public IActionResult GetProviders() => Ok(engine.Providers);

    // POST api/ocr/test-extract (Phase 2 scaffolding) was removed on 17 Nov 2026: it read any server
    // path sent in the request body. Documents are read only through /api/documents/upload.
}
