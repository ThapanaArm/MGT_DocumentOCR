using System.Security.Cryptography;
using System.Text;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
using MgtOcr.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MgtOcr.Api.Auth;

// Two ways in, one gate.
//
//   MicrosoftJwt — tokens issued by Entra ID (SSO). Only wired up when a tenant is configured; the
//                  group's second company can be switched on later by filling config.
//   LocalJwt     — tokens OCR issues itself after a username/password login. Always available, so
//                  password sign-in works even before Entra is set up.
//
// A "smart" policy scheme peeks each token's issuer and forwards it to the right validator. Both
// converge on the same Ms_User lookup afterwards (by the e-mail claim), so authorization is
// identical no matter which door was used — and Ms_User, not the token, is what actually keeps
// strangers out (an unknown e-mail gets 403 either way).
public static class AuthExtensions
{
    public const string Smart = "smart";
    public const string MicrosoftScheme = "MicrosoftJwt";
    public const string LocalScheme = "LocalJwt";

    public static IServiceCollection AddMgtOcrAuth(
        this IServiceCollection services, AppConfig cfg, IWebHostEnvironment env, ILogger logger)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddSingleton<UserRepository>();
        services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();

        // ---- our own signing key (password path) ----
        var signingKey = cfg.LocalAuthSigningKey.Trim();
        if (signingKey.Length < 32)
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException(
                    "Auth:JwtSigningKey is missing or too short (need >= 32 chars). Set it via user-secrets " +
                    "or an environment variable — never in appsettings.json. Refusing to start in production " +
                    "with a weak/absent key: password-login tokens could be forged.");
            // Development convenience: a random ephemeral key so the app runs before a real one is set.
            // Tokens then stop working after a restart (everyone re-logs in) — fine for dev, never prod.
            signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            logger.LogWarning("Auth:JwtSigningKey not set — using a random DEV key. Password tokens reset on restart.");
        }
        var localOptions = new LocalAuthOptions(
            signingKey,
            string.IsNullOrWhiteSpace(cfg.LocalAuthIssuer) ? "mgtocr" : cfg.LocalAuthIssuer,
            string.IsNullOrWhiteSpace(cfg.LocalAuthAudience) ? "mgtocr" : cfg.LocalAuthAudience,
            cfg.LocalAuthLifetimeMinutes > 0 ? cfg.LocalAuthLifetimeMinutes : 480);
        services.AddSingleton(localOptions);
        services.AddSingleton<LocalTokenService>();
        services.AddScoped<PasswordService>();

        var msEnabled = cfg.AuthConfigured;
        var microsoftIssuers = cfg.ConfiguredTenants
            .Select(t => $"https://login.microsoftonline.com/{t.TenantId.Trim()}/v2.0").ToArray();

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = Smart;
            o.DefaultChallengeScheme = Smart;
        });

        // Route by the token's issuer: Microsoft-issued -> MicrosoftJwt, everything else -> LocalJwt.
        auth.AddPolicyScheme(Smart, Smart, o =>
            o.ForwardDefaultSelector = ctx =>
            {
                if (msEnabled && LooksMicrosoft(ctx.Request.Headers.Authorization)) return MicrosoftScheme;
                return LocalScheme;
            });

        if (msEnabled)
        {
            var audiences = new[] { cfg.AuthAudience }
                .Concat(cfg.ConfiguredTenants.SelectMany(t => new[] { t.ClientId, $"api://{t.ClientId}" }))
                .Select(a => (a ?? "").Trim())
                .Where(a => a.Length > 0 && a != "api://").Distinct().ToArray();

            auth.AddJwtBearer(MicrosoftScheme, o =>
            {
                o.Authority = "https://login.microsoftonline.com/organizations/v2.0";
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuers = microsoftIssuers,
                    ValidateAudience = true, ValidAudiences = audiences,
                    ValidateLifetime = true, ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                };
                o.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = c => { logger.LogWarning("MS token rejected: {M}", c.Exception.Message); return Task.CompletedTask; },
                };
            });
        }

        auth.AddJwtBearer(LocalScheme, o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = localOptions.Issuer,
                ValidateAudience = true, ValidAudience = localOptions.Audience,
                ValidateLifetime = true, ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(localOptions.SigningKey)),
                ClockSkew = TimeSpan.FromMinutes(2),
            };
        });

        // Every endpoint requires a signed-in user unless it says [AllowAnonymous]. A new controller
        // is therefore protected by default — the right default for a system that posts into SAP.
        services.AddAuthorization(o =>
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        logger.LogInformation("Auth ready — password login: on; Microsoft SSO: {Ms}",
            msEnabled ? $"on ({microsoftIssuers.Length} tenant(s))" : "off (no tenant configured yet)");
        return services;
    }

    // Peek the issuer without validating — just enough to choose a validator. Anything unparseable
    // falls through to LocalJwt, which will then reject it properly.
    private static bool LooksMicrosoft(string authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var token = authorizationHeader["Bearer ".Length..].Trim();
            var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
            return (jwt.Issuer ?? "").Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
