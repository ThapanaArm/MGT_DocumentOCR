using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MgtOcr.Api.Auth;

// Enforces department-based document visibility on every DocumentsController action. It works out
// which module a request touches — from the {docId} route (looked up in the DB) or a `module`
// argument — and throws 403 when the signed-in user's department may not see that module. Requests
// not scoped to one module (the list-all query, static lookups, or a module carried inside a JSON
// body) pass through here; those endpoints do their own checking/filtering.
public sealed class DepartmentAccessFilter(
    ICurrentUserAccessor current, DocumentRepository repo, MgtOcr.Core.Config.AppConfig config) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var module = await ResolveModuleAsync(context);
        if (module is { Length: > 0 })
        {
            var user = await current.RequireAsync(context.HttpContext.RequestAborted);
            if (!DepartmentAccess.CanAccess(user, module))
                throw new HttpApiException(403, "You do not have access to this document type (your department can only see its own documents)");
        }
        await EnsureSameCompanyAsync(context);
        await next();
   }

    /// <summary>MGT and GLC share this installation, and a document belongs to whichever of them
    /// imported it. Filtering the list is not enough on its own — the document's own address would
    /// still open it — so every route that names a {docId} is checked here, which covers reading,
    /// saving, mapping, posting, exporting and the file download in one place.
    ///
    /// A document with no company (imported before this existed) is left open to both until
    /// sql/27_company_scope.sql's backfill gives it one.</summary>
    private async Task EnsureSameCompanyAsync(ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue("docId", out var idObj) || idObj is not int docId) return;

        var docCompany = await repo.GetCompanyAsync(docId);
        if (string.IsNullOrWhiteSpace(docCompany)) return; // not found (the action returns 404), or not assigned yet

        var user = await current.RequireAsync(context.HttpContext.RequestAborted);
        var mine = CompanyScope.For(user, config);
        // Empty = Admin, or a user with no company on file: both are left unfiltered, the same way
        // the list queries treat them.
        if (string.IsNullOrWhiteSpace(mine) || string.Equals(mine, docCompany, StringComparison.OrdinalIgnoreCase)) return;

        throw new HttpApiException(403, "This document belongs to another company");
    }

    // The module a request acts on, or null when it is not scoped to one specific module.
    private async Task<string?> ResolveModuleAsync(ActionExecutingContext context)
    {
        var args = context.ActionArguments;
        if (args.TryGetValue("docId", out var idObj) && idObj is int docId)
            return await repo.GetModuleAsync(docId); // null => not found; let the action return 404
        if (args.TryGetValue("module", out var mObj) && mObj is string ms && ms.Trim().Length > 0)
            return ms.Trim().ToUpperInvariant();
        return null;
    }
}
