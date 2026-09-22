using MgtOcr.Api.Auth;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Json;
using MgtOcr.Core.Config;
using MgtOcr.Data;
using MgtOcr.Ocr;

var builder = WebApplication.CreateBuilder(args);

// Configuration now comes from appsettings.json (+ appsettings.{Environment}.json and
// environment variables, merged by the .NET config system — env vars use "__" for nesting,
// e.g. Database__Password, and override the JSON for production/secret injection).
// repoRoot is still used for the uploads folder, the bundled tessdata, and webfront/dist.
var cfg = builder.Configuration;
var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".."));

string Get(string key, string fallback = "") => (cfg[key] ?? fallback).Trim();
// "dev" -> "Dev", "prod" -> "Prod" — matches the BaseUrl_Dev/BaseUrl_Prod key casing regardless
// of how the environment name is cased in config.
string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

// AddMgtOcrAuth() has to report what it enabled (or loudly warn that it did not) before the host
// exists, so it gets its own short-lived logger rather than writing to Console directly.
using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLog = startupLoggerFactory.CreateLogger("MgtOcr.Startup");

// Blank TenantId entries are kept rather than filtered here — AppConfig.ConfiguredTenants does the
// filtering, so appsettings.json can carry a placeholder row for the company that is not live yet.
var authTenants = cfg.GetSection("AzureAd:Tenants").Get<AuthTenant[]>() ?? [];

// Auto-detection: if Ocr:TesseractCmd / Ocr:TessdataPrefix aren't set explicitly, fall back to
// the well-known local install path this project already ships with.
var defaultTesseractCmd = @"C:\Program Files\Tesseract-OCR\tesseract.exe";
var tesseractCmd = Get("Ocr:TesseractCmd");
if (tesseractCmd == "" && File.Exists(defaultTesseractCmd)) tesseractCmd = defaultTesseractCmd;
var defaultTessdata = Path.Combine(repoRoot, "tessdata");
var tessdataPrefix = Get("Ocr:TessdataPrefix");
if (tessdataPrefix == "" && Directory.Exists(defaultTessdata)) tessdataPrefix = defaultTessdata;

// Sap:BusinessPartner, Sap:SalesOrder, Sap:Product and ZohoConfig each carry a "dev"/"prod"
// (Sap) or "sandbox"/"prod" (Zoho) pair of value sets plus their own switch key, so flipping ONE
// value in appsettings.json (or an env-var override, e.g. Sap__ActiveEnvironment=prod) moves
// every SAP integration's target environment together without editing URLs/secrets in place.
var sapBpEnv = Get("Sap:ActiveEnvironment", "dev");
var zohoEnv = Get("ZohoConfig:ActiveEnvironment", "sandbox");
startupLog.LogInformation("[CONFIG] Sap active environment (BusinessPartner/SalesOrder/Product/Billing) = {Env}", sapBpEnv);
startupLog.LogInformation("[CONFIG] ZohoConfig active environment = {Env}", zohoEnv);

var appConfig = new AppConfig
{
    DbServer = Get("Database:Server", @"1P69044\SQLEXPRESS"),
    DbName = Get("Database:Name", "MGT_Document_OCR"),
    DbUser = Get("Database:User", "sa"),
    DbPassword = cfg["Database:Password"] ?? "", // not trimmed — a password may contain spaces
    DbDriver = Get("Database:Driver", "ODBC Driver 17 for SQL Server"),
    // Prefer a full ConnectionStrings entry (ConnectionStrings:Default, then dbDW) if present.
    DbConnectionString = (cfg.GetConnectionString("Default") ?? cfg.GetConnectionString("dbDW") ?? "").Trim(),
    AppHost = Get("App:Host", "0.0.0.0"),
    AppPort = int.TryParse(Get("App:Port", "8091"), out var p) ? p : 8091,
    OwnCompanyKeywords = Get("App:OwnCompanyKeywords", "MEGACHEM").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
    OwnTaxId = Get("App:OwnTaxId"),
    OcrProvider = Get("Ocr:Provider", "auto"),
    TesseractCmd = tesseractCmd,
    TessdataPrefix = tessdataPrefix,
    AzureDiEndpoint = Get("Ocr:AzureDiEndpoint"),
    AzureDiKey = Get("Ocr:AzureDiKey"),
    AnthropicApiKey = Get("Ocr:AnthropicApiKey"),
    AnthropicModel = Get("Ocr:AnthropicModel", "claude-sonnet-5"),
    TyphoonApiKey = Get("Ocr:TyphoonApiKey"),
    TyphoonModel = Get("Ocr:TyphoonModel", "typhoon-ocr"),
    GeminiApiKey = Get("Ocr:GeminiApiKey"),
    GeminiModel = Get("Ocr:GeminiModel", "gemini-2.5-flash"),
    OpenAiApiKey = Get("Ocr:OpenAiApiKey"),
    OpenAiModel = Get("Ocr:OpenAiModel", "gpt-4o"),
    SapBaseUrl = Get("Sap:BaseUrl"),
    SapUser = Get("Sap:User"),
    SapPassword = cfg["Sap:Password"] ?? "",
    SapClient = Get("Sap:Client", "100"),
    SapCompanyCode = Get("Sap:CompanyCode", "1000"),
    SapDefaultPlant = Get("Sap:DefaultPlant", "1000"),
    // "BaseUrl_Dev"/"BaseUrl_Prod" per sapBpEnv above, falling back to the old flat "BaseUrl"
    // key so an appsettings.json that hasn't been split into Dev/Prod yet still works.
    SapBusinessPartnerBaseUrl = Get($"Sap:BusinessPartner:BaseUrl_{Cap(sapBpEnv)}", Get("Sap:BusinessPartner:BaseUrl")),
    SapBusinessPartnerAuthHeader = Get("Sap:BusinessPartner:AuthHeader"),
    SapSalesOrderBaseUrl = Get($"Sap:SalesOrder:BaseUrl_{Cap(sapBpEnv)}", Get("Sap:SalesOrder:BaseUrl")),
    SapSalesOrderAuthHeader = Get("Sap:SalesOrder:AuthHeader"),
    SapSalesOrderPriceConditionType = Get("Sap:SalesOrder:PriceConditionType", "ZPR0"),
    SapSalesOrderItemNoteTextId = Get("Sap:SalesOrder:ItemNoteTextId", "ZI01"),
    SapSalesOrderItemNoteLanguages = Get("Sap:SalesOrder:ItemNoteLanguages", "TH,EN"),
    SapProductBaseUrl = Get($"Sap:Product:BaseUrl_{Cap(sapBpEnv)}", Get("Sap:Product:BaseUrl")),
    SapProductAuthHeader = Get("Sap:Product:AuthHeader"),
    SapBillingBaseUrl = Get($"Sap:Billing:BaseUrl_{Cap(sapBpEnv)}", Get("Sap:Billing:BaseUrl")),
    SapBillingAuthHeader = Get("Sap:Billing:AuthHeader"),
    // MGT/GLC: SalesOrganization/CompanyCode/DefaultPlant per company (see CompanyProfile).
    // Defaults match what was already hardcoded per-module before this existed, so an
    // appsettings.json without these sections still behaves exactly as before.
    Companies =
    [
        new CompanyProfile("MGT", Get("MGT:SalesOrganization", "1000"), Get("MGT:CompanyCode", "1000"), Get("MGT:DefaultPlant", "1100"), Get("MGT:AuthorizationGroup", "0001")),
        new CompanyProfile("GLC", Get("GLC:SalesOrganization", "2000"), Get("GLC:CompanyCode", "2000"), Get("GLC:DefaultPlant", "2100"), Get("GLC:AuthorizationGroup", "0002")),
    ],
    ZohoAccountsUrl = Get($"ZohoConfig:{zohoEnv}:AccountsUrl", "https://accounts.zoho.com"),
    ZohoApiDomain = Get($"ZohoConfig:{zohoEnv}:ApiDomain", "https://www.zohoapis.com"),
    ZohoClientId = Get($"ZohoConfig:{zohoEnv}:ClientId"),
    ZohoClientSecret = cfg[$"ZohoConfig:{zohoEnv}:ClientSecret"] ?? "", // not trimmed — a secret may (rarely) matter byte-for-byte
    ZohoRefreshToken = Get($"ZohoConfig:{zohoEnv}:RefreshToken"),
    AuthTenants = authTenants,
    AuthAudience = Get("AzureAd:Audience"),
    UserDatabase = Get("AzureAd:UserDatabase", "MGT_Datawarehouse"),
    DevFallbackEmail = Get("AzureAd:DevFallbackEmail"),
    LocalAuthSigningKey = Get("Auth:JwtSigningKey"),
    LocalAuthIssuer = Get("Auth:Issuer", "mgtocr"),
    LocalAuthAudience = Get("Auth:Audience", "mgtocr"),
    LocalAuthLifetimeMinutes = int.TryParse(Get("Auth:LifetimeMinutes", "480"), out var lm) ? lm : 480,
    UploadDir = Path.Combine(repoRoot, "uploads"),
};
Directory.CreateDirectory(appConfig.UploadDir);

builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<MasterRepository>();
builder.Services.AddSingleton<OcrEngine>();
builder.Services.AddSingleton(sp => new DocumentRepository(sp.GetRequiredService<Db>(), appConfig.UploadDir));
builder.Services.AddScoped<MgtOcr.Api.Auth.DepartmentAccessFilter>();
builder.Services.AddHttpClient<MgtOcr.Sap.SapClient>();
builder.Services.AddHttpClient<MgtOcr.Sap.SapBusinessPartnerClient>();
builder.Services.AddHttpClient<MgtOcr.Sap.SapProductClient>();
builder.Services.AddHttpClient<MgtOcr.Sap.SapBillingClient>();
builder.Services.AddHttpClient<MgtOcr.Sap.SapSalesOrderClient>();
// Singleton (not AddHttpClient<T>, unlike the SAP clients above) so its in-memory OAuth
// access-token cache is shared across requests instead of being torn down each call.
builder.Services.AddSingleton<MgtOcr.Zoho.ZohoClient>();
builder.Services.AddSingleton<MgtOcr.Zoho.ZohoAccountClient>();
builder.Services.AddSingleton<MgtOcr.Zoho.ZohoDealClient>();
builder.Services.AddSingleton<MgtOcr.Zoho.ZohoSalesOrderClient>();

builder.Services.AddMgtOcrAuth(appConfig, builder.Environment, startupLog);

builder.Services.AddControllers().AddJsonOptions(o =>
{
    // /api/documents/* uses camelCase (hand-built dicts in Python); /api/masters/* returns raw
    // PascalCase SQL columns as dynamic objects, which bypass this policy entirely since Dapper's
    // dynamic rows serialize using their original property names regardless of naming policy.
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.Converters.Add(new PythonDateTimeConverter());
    o.JsonSerializerOptions.Converters.Add(new PythonDecimalConverter());
    o.JsonSerializerOptions.Converters.Add(new PythonDoubleConverter());
});
builder.Services.AddOpenApi();

// The React dev server (Vite, default :5173) runs as a separate process during development.
builder.Services.AddCors(o => o.AddPolicy("frontend", p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.WebHost.UseUrls($"http://{appConfig.AppHost}:{appConfig.AppPort}");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Matches FastAPI/Starlette's default behavior for any exception that isn't an explicit
// HTTPException: a plain-text "Internal Server Error" body (content-type text/plain), NOT
// {"detail": ...} — verified directly against the live Python instance on :8090 (curl -i), which
// returns exactly `HTTP/1.1 500`, `content-type: text/plain; charset=utf-8`, body "Internal Server
// Error". Endpoints that raise their own HTTPException-equivalent (BadRequest/NotFound with a
// {detail} body) never reach this — it only catches genuinely unhandled exceptions, same as Python.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception)
    {
        context.Response.Clear();
        context.Response.StatusCode = 500;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Internal Server Error");
    }
});

// Mirrors FastAPI's HTTPException handling: {"detail": ...} with the given status. Registered
// after the generic catch-all above so it runs closer to the endpoint and gets first chance to
// handle an HttpApiException — only exceptions it doesn't catch fall through to the generic one.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (MgtOcr.Core.HttpApiException ex)
    {
        context.Response.Clear();
        context.Response.StatusCode = ex.Status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { detail = ex.Detail });
    }
});

// Serve the built React SPA (webfront/dist) as static files, same-origin.
// In development the Vite dev server (:5173) proxies /api here instead, so this
// path only matters for the production build — run `npm run build` in webfront/.
// Registered before routing so real asset files are served before the SPA fallback.
var publicDir = Path.Combine(repoRoot, "webfront", "dist");
Microsoft.Extensions.FileProviders.PhysicalFileProvider? spaFiles = null;
if (Directory.Exists(publicDir))
{
    spaFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(publicDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFiles });
}

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// SPA client-side routing fallback: any non-/api path that is not a real file
// returns index.html so React Router can handle it (e.g. /doc/5, /master, /list/AP).
// Lowest priority — never intercepts /api/* (matched by controllers) or static assets.
if (spaFiles != null)
{
    // Anonymous on purpose: this returns index.html, and the browser has no token until the app
    // inside index.html has run and signed the user in. The API endpoints stay protected.
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaFiles })
       .AllowAnonymous();
}

app.Run();
