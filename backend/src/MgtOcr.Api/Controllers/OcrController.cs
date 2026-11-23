using MgtOcr.Ocr;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/ocr/providers in app/main.py — lists the OCR engine for the UI (Gemini only).
// POST /api/ocr/test-extract was removed with the other reading engines (23 Nov 2026): it read any
// server path named in the request body.
[ApiController]
[Route("api/ocr")]
public class OcrController(OcrEngine engine) : ControllerBase
{
    [HttpGet("providers")]
    public IActionResult GetProviders() => Ok(engine.Providers);
}
