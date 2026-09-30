using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
using MgtOcr.Core.Json;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace MgtOcr.Api.Controllers;

// Ported from the /api/masters* routes in app/main.py (lines 193-245).
[ApiController]
[Route("api/masters")]
public class MastersController(MasterRepository repo, ICurrentUserAccessor currentUser, AppConfig config) : ControllerBase
{
    /// <summary>The company whose master data this person may see — "1000" MGT, "2000" GLC, or ""
    /// for Admin and for a user record that names no company (see CompanyScope).</summary>
    private async Task<string> CompanyScopeAsync() =>
        CompanyScope.For(await currentUser.RequireAsync(), config);

    /// <summary>Refuses to touch a master row that belongs to the other company. A row with no
    /// company yet is editable by both, which is what keeps the existing data workable until
    /// sql/27_company_scope.sql's backfill assigns it.</summary>
    private async Task<IActionResult?> DenyIfOtherCompanyAsync(MasterDefinition m, string key)
    {
        var scope = await CompanyScopeAsync();
        if (scope.Length == 0) return null;
        var owner = await repo.RowCompanyAsync(m, key);
        if (string.IsNullOrWhiteSpace(owner)) return null; // shared/legacy row, or table without a company
        return string.Equals(owner, scope, StringComparison.OrdinalIgnoreCase)
            ? null
            : StatusCode(403, new { detail = "This record belongs to another company" });
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool includeInactive = false)
    {
        var user = await currentUser.RequireAsync();
        var profile = config.CompanyForUser(user.PrimaryCompany?.CompanyCode, user.SalesOrganization);
        return Ok(await repo.LoadAllAsync(includeInactive, profile?.CompanyCode));
    }

    [HttpGet("{kind}")]
    public async Task<IActionResult> GetList(string kind, [FromQuery] string q = "")
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "Unknown master table" });
        try
        {
            return Ok(await repo.ListAsync(m, q, await CompanyScopeAsync()));
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return MissingTable(m);
        }
    }

    [HttpPost("{kind}")]
    public async Task<IActionResult> Create(string kind, [FromBody] Dictionary<string, object?> body)
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "Unknown master table" });
        var values = JsonBodyHelpers.Unwrap(body);
        // The new row belongs to the company of whoever is creating it. Stamped here rather than
        // trusted from the body, so nobody can file a record under the other company.
        if (MasterRepository.CompanyColumn(m) is { } createCol
            && CompanyScope.Own(await currentUser.RequireAsync(), config) is { Length: > 0 } scope)
            values[createCol] = scope;
        var error = Validate(kind, values, isCreate: true);
        if (error != null) return BadRequest(new { detail = error });
        try
        {
            var ok = await repo.CreateAsync(m, values);
            if (!ok) return BadRequest(new { detail = "No data to save" });
            return Ok(new { ok = true });
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return MissingTable(m);
        }
    }

    [HttpPut("{kind}/{key}")]
    public async Task<IActionResult> Update(string kind, string key, [FromBody] Dictionary<string, object?> body)
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "Unknown master table" });
        if (await DenyIfOtherCompanyAsync(m, key) is { } denied) return denied;
        var values = JsonBodyHelpers.Unwrap(body);
        // A row cannot be moved to the other company by editing it either: the company is taken
        // from the row itself — or from the editor when the row has none yet — never from the body.
        // It is pinned rather than dropped because SalesOrg is a required field on these forms.
        if (MasterRepository.CompanyColumn(m) is { } editCol)
        {
            var owner = await repo.RowCompanyAsync(m, key);
            var pinned = string.IsNullOrWhiteSpace(owner)
                ? CompanyScope.Own(await currentUser.RequireAsync(), config)
                : owner;
            if (!string.IsNullOrWhiteSpace(pinned)) values[editCol] = pinned;
        }
        var error = Validate(kind, values, isCreate: false);
        if (error != null) return BadRequest(new { detail = error });
        try
        {
            var ok = await repo.UpdateAsync(m, key, values);
            return ok ? Ok(new { ok }) : NotFound(new { detail = "Record not found or no data to save" });
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return MissingTable(m);
        }
    }

    // Status switch on the Master Mapping page. Flips ONLY the table's active flag (IsActive /
    // Isactive) — it does not re-send or re-validate the other columns, so a record with a blank
    // optional field can still be switched on/off. Inactive rows are excluded from GET /api/masters
    // (default) and from the Sales Order mapping engine (LoadForMappingAsync), so deactivating a
    // Customer / Ship-to / CustomerMaterial here stops it being matched or suggested.
    public record SetActiveRequest(bool Active);

    [HttpPut("{kind}/{key}/active")]
    public async Task<IActionResult> SetActive(string kind, string key, [FromBody] SetActiveRequest req)
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "Unknown master table" });
        if (MasterRepository.ActiveColumn(m) is null)
            return BadRequest(new { detail = "This master table has no active/inactive status" });
        await currentUser.RequireAsync();
        if (await DenyIfOtherCompanyAsync(m, key) is { } denied) return denied;
        try
        {
            var ok = await repo.SetActiveAsync(m, key, req.Active);
            return ok ? Ok(new { ok, active = req.Active }) : NotFound(new { detail = "Record not found" });
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return MissingTable(m);
        }
    }

    [HttpDelete("{kind}/{key}")]
    public async Task<IActionResult> Delete(string kind, string key)
    {
        if (!MasterRepository.TryGetKind(kind, out var m))
            return NotFound(new { detail = "Unknown master table" });
        if (await DenyIfOtherCompanyAsync(m, key) is { } denied) return denied;
        try
        {
            var (ok, fkError) = await repo.DeleteAsync(m, key);
            if (fkError != null)
                return BadRequest(new { detail = $"Cannot delete: other records reference it ({fkError})" });
            return Ok(new { ok });
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return MissingTable(m);
        }
    }

    // SQL error 208 = "Invalid object name": the master's backing table doesn't exist (e.g.
    // ocr.Material was dropped). Return a clean 409 with a readable message instead of a raw 500,
    // so the UI can tell the user the feature's table is gone rather than crashing. The AP-material
    // architecture decision (remove the feature vs. re-create the table) is deferred; this only
    // stops the crash.
    private ObjectResult MissingTable(MasterDefinition m) =>
        Conflict(new { detail = $"Table '{m.Table}' does not exist in the database (it may have been dropped) — this function is temporarily unavailable" });

    // isCreate=false (editing an existing row) drops the SAP/Zoho code from the required set, so a
    // record that is not fully mapped yet can still be saved/corrected. Creating a brand-new record
    // still requires it, so new rows always start complete.
    private static string? Validate(string kind, Dictionary<string, object?> values, bool isCreate)
    {
        string[] required = kind switch
        {
            "customers" => isCreate ? new[] { "SalesOrg", "CompanyName", "ComcompyCodeSAP" } : new[] { "SalesOrg", "CompanyName" },
            "shiptos" => isCreate ? new[] { "SalesOrg", "CustomerCode", "SapShipToCode" } : new[] { "SalesOrg", "CustomerCode" },
            "custmaterials" => isCreate ? new[] { "SalesOrg", "CustomerCode", "MaterialCodeCode", "MaterialCodeSAP" } : new[] { "SalesOrg", "CustomerCode", "MaterialCodeCode" },
            _ => Array.Empty<string>(),
        };
        foreach (var field in required)
            if (!values.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value?.ToString()))
                return $"Please enter {field}";
        if (values.TryGetValue("SalesOrg", out var org) && string.IsNullOrWhiteSpace(org?.ToString()))
            return "Please enter SalesOrg (CompanyCode)";
        return null;
    }
}
