using System.Security.Claims;
using MgtOcr.Core.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MgtOcr.Api.Auth;

// Microsoft (Entra ID) sign-in for ONE multi-tenant app registration (MGT-OCR-SSO) shared by every
// company. Because the app accepts accounts from any tenant, the token's audience alone proves
// nothing — the tenant allow-list below is the real gate:
//   * issuer is validated per token: it must be the v2 (login.microsoftonline.com/<tid>/v2.0) or v1
//     (sts.windows.net/<tid>/) issuer of a tenant listed in AzureAd:Tenants, and only that tid's own;
//   * the token must be a delegated one carrying the required scope (app-only tokens have no scp);
//   * the tenant is mapped to a CompanyID and stamped as the "ocr_company_id" claim, which
//     CurrentUserAccessor then requires the person to belong to (Ms_UserCompany).
public static class EntraMultiTenantAuth
{
    /// <summary>Claim added after validation: the CompanyID mapped from the token's tid.</summary>
    public const string CompanyIdClaim = "ocr_company_id";

    public static AuthenticationBuilder AddEntraMultiTenant(
        this AuthenticationBuilder auth, string scheme, AppConfig cfg, ILogger logger)
    {
        var tenants = cfg.ConfiguredTenants;

        // Fail fast on a half-filled entry instead of silently accepting or rejecting everyone.
        // (A completely empty list is a different, legitimate state: Microsoft SSO is simply off.)
        var broken = tenants.Where(t => string.IsNullOrWhiteSpace(t.ClientId) || t.CompanyId <= 0).ToArray();
        if (broken.Length > 0)
            throw new InvalidOperationException(
                "AzureAd:Tenants has entries with a TenantId but no ClientId/CompanyId: " +
                string.Join(", ", broken.Select(t => t.Name.Length > 0 ? t.Name : t.TenantId)) +
                " — refusing to start (a multi-tenant app must not run with an incomplete tenant allow-list).");

        var tenantCompany = tenants
            .GroupBy(t => t.TenantId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CompanyId, StringComparer.OrdinalIgnoreCase);
        var requiredScope = string.IsNullOrWhiteSpace(cfg.AuthRequiredScope) ? "access_as_user" : cfg.AuthRequiredScope.Trim();

        var audiences = new[] { cfg.AuthAudience }
            .Concat(tenants.SelectMany(t => new[] { t.ClientId, $"api://{t.ClientId}" }))
            .Select(a => (a ?? "").Trim())
            .Where(a => a.Length > 0 && a != "api://").Distinct().ToArray();

        return auth.AddJwtBearer(scheme, o =>
        {
            // Signing keys are shared by all tenants, so the /organizations metadata is enough.
            o.Authority = "https://login.microsoftonline.com/organizations/v2.0";
            o.MapInboundClaims = false; // keep raw claim names: tid, oid, scp, preferred_username
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                // The /organizations issuer is a {tenantid} template, so it has to be checked by hand.
                IssuerValidator = (issuer, token, _) =>
                {
                    var tid = TenantOf(token);
                    if (tid is null || !tenantCompany.ContainsKey(tid))
                        throw new SecurityTokenInvalidIssuerException($"Tenant not allowed: {tid}");
                    var ok = string.Equals(issuer, $"https://login.microsoftonline.com/{tid}/v2.0", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(issuer, $"https://sts.windows.net/{tid}/", StringComparison.OrdinalIgnoreCase);
                    if (!ok) throw new SecurityTokenInvalidIssuerException($"Unexpected issuer: {issuer}");
                    return issuer;
                },
                ValidateAudience = true, ValidAudiences = audiences,
                ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            };
            o.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = c => { logger.LogWarning("MS token rejected: {M}", c.Exception.Message); return Task.CompletedTask; },
                OnTokenValidated = c =>
                {
                    var p = c.Principal!;
                    var scp = p.FindFirst("scp")?.Value ?? "";
                    if (!scp.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(requiredScope, StringComparer.Ordinal))
                    {
                        c.Fail("Missing required scope");
                        return Task.CompletedTask;
                    }
                    var tid = p.FindFirst("tid")?.Value;
                    if (tid is null || !tenantCompany.TryGetValue(tid, out var companyId))
                    {
                        c.Fail("Tenant not allowed");
                        return Task.CompletedTask;
                    }
                    ((ClaimsIdentity)p.Identity!).AddClaim(new Claim(CompanyIdClaim, companyId.ToString()));
                    return Task.CompletedTask;
                },
            };
        });
    }

    private static string? TenantOf(SecurityToken token) => token switch
    {
        JsonWebToken jwt => jwt.TryGetPayloadValue<string>("tid", out var t) ? t : null,
        _ => null,
    };
}
