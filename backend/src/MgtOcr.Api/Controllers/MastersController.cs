using MgtOcr.Core.Json;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from the /api/masters* routes in app/main.py (lines 193-245).
[ApiController]
[Route("api/masters")]
public class MastersController(MasterRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repo.LoadAllAsync());

    [HttpGet("{kind}")]
    public async Task<IActionResult> GetList(string kind, [FromQuery] string q = "")
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "ไม่รู้จักตาราง master นี้" });
        return Ok(await repo.ListAsync(m, q));
    }

    [HttpPost("{kind}")]
    public async Task<IActionResult> Create(string kind, [FromBody] Dictionary<string, object?> body)
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "ไม่รู้จักตาราง master นี้" });
        var ok = await repo.CreateAsync(m, JsonBodyHelpers.Unwrap(body));
        if (!ok) return BadRequest(new { detail = "ไม่มีข้อมูลที่จะบันทึก" });
        return Ok(new { ok = true });
    }

    [HttpPut("{kind}/{key}")]
    public async Task<IActionResult> Update(string kind, string key, [FromBody] Dictionary<string, object?> body)
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "ไม่รู้จักตาราง master นี้" });
        var ok = await repo.UpdateAsync(m, key, JsonBodyHelpers.Unwrap(body));
        return Ok(new { ok });
    }

    [HttpDelete("{kind}/{key}")]
    public async Task<IActionResult> Delete(string kind, string key)
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "ไม่รู้จักตาราง master นี้" });
        var (ok, fkError) = await repo.DeleteAsync(m, key);
        if (fkError != null)
            return BadRequest(new { detail = $"ลบไม่ได้ เนื่องจากมีข้อมูลอื่นอ้างอิงอยู่ ({fkError})" });
        return Ok(new { ok });
    }
}
