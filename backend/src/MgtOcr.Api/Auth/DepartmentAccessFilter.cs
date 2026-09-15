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
public sealed class DepartmentAccessFilter(ICurrentUserAccessor current, DocumentRepository repo) : IAsyncActionFilter
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
        await next();
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
