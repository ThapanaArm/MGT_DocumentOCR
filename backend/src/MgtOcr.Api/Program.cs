using DotNetEnv;
using MgtOcr.Core.Json;
using MgtOcr.Core.Config;
using MgtOcr.Data;
using MgtOcr.Ocr;

var builder = WebApplication.CreateBuilder(args);

// .env lives at the repo root and is shared with the Python system that's still running
// side-by-side during the migration (see the approved plan, Phase 0/8) — load it directly
// instead of duplicating secrets into appsettings.json.
var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".."));
var envPath = Path.Combine(repoRoot, ".env");
if (File.Exists(envPath))
{
    Env.Load(envPath);
}
else
{
    // Fail loudly rather than silently falling back to empty credentials — a wrong repoRoot
    // calculation here previously caused a confusing "Login failed for user 'sa'" instead of
    // an obvious "config file not found" error.
    throw new FileNotFoundException($".env not found at expected repo root path: {envPath}");
}

string Get(string key, string fallback = "") => Environment.GetEnvironmentVariable(key)?.Trim() ?? fallback;

// Mirrors config.py's auto-detection: if TESSERACT_CMD/TESSDATA_PREFIX aren't set explicitly,
// fall back to the well-known local install path this project already ships with.
var defaultTesseractCmd = @"C:\Program Files\Tesseract-OCR\tesseract.exe";
var tesseractCmd = Get("TESSERACT_CMD");
if (tesseractCmd == "" && File.Exists(defaultTesseractCmd)) tesseractCmd = defaultTesseractCmd;
var defaultTessdata = Path.Combine(repoRoot, "tessdata");
var tessdataPrefix = Get("TESSDATA_PREFIX");
if (tessdataPrefix == "" && Directory.Exists(defaultTessdata)) tessdataPrefix = defaultTessdata;

var appConfig = new AppConfig
{
    DbServer = Get("DB_SERVER", @"1P69044\SQLEXPRESS"),
    DbName = Get("DB_NAME", "MGT_Document_OCR"),
    DbUser = Get("DB_USER", "sa"),
    DbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "", // not trimmed, mirrors config.py
    DbDriver = Get("DB_DRIVER", "ODBC Driver 17 for SQL Server"),
    AppHost = Get("APP_HOST", "0.0.0.0"),
    // Deliberately NOT reading the shared APP_PORT (that's Python's port, 8090, and Python stays
    // running side-by-side on it throughout the migration per the plan) — a separate port here.
    AppPort = int.TryParse(Get("DOTNET_APP_PORT", "8091"), out var p) ? p : 8091,
    OwnCompanyKeywords = Get("OWN_COMPANY_KEYWORDS", "MEGACHEM").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
    OwnTaxId = Get("OWN_TAX_ID"),
    OcrProvider = Get("OCR_PROVIDER", "auto"),
    TesseractCmd = tesseractCmd,
    TessdataPrefix = tessdataPrefix,
    AzureDiEndpoint = Get("AZURE_DI_ENDPOINT"),
    AzureDiKey = Get("AZURE_DI_KEY"),
    AnthropicApiKey = Get("ANTHROPIC_API_KEY"),
    AnthropicModel = Get("ANTHROPIC_MODEL", "claude-sonnet-5"),
    TyphoonApiKey = Get("TYPHOON_API_KEY"),
    TyphoonModel = Get("TYPHOON_MODEL", "typhoon-ocr"),
    GeminiApiKey = Get("GEMINI_API_KEY"),
    GeminiModel = Get("GEMINI_MODEL", "gemini-2.5-flash"),
    OpenAiApiKey = Get("OPENAI_API_KEY"),
    OpenAiModel = Get("OPENAI_MODEL", "gpt-4o"),
    SapBaseUrl = Get("SAP_BASE_URL"),
    SapUser = Get("SAP_USER"),
    SapPassword = Environment.GetEnvironmentVariable("SAP_PASSWORD") ?? "",
    SapClient = Get("SAP_CLIENT", "100"),
    SapCompanyCode = Get("SAP_COMPANY_CODE", "1000"),
    SapDefaultPlant = Get("SAP_DEFAULT_PLANT", "1000"),
    UploadDir = Path.Combine(repoRoot, "uploads"),
};
Directory.CreateDirectory(appConfig.UploadDir);

builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<MasterRepository>();
builder.Services.AddSingleton<OcrEngine>();
builder.Services.AddSingleton(sp => new DocumentRepository(sp.GetRequiredService<Db>(), appConfig.UploadDir));
builder.Services.AddHttpClient<MgtOcr.Core.Sap.SapClient>();

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

app.UseCors("frontend");
app.UseAuthorization();
app.MapControllers();

// Mirrors app.mount("/", StaticFiles(directory=str(config.PUBLIC_DIR), html=True)) in Python:
// serves frontend/ unmodified — index.html at "/", app.js/style.css/assets/* at their existing paths.
var publicDir = Path.Combine(repoRoot, "frontend");
if (Directory.Exists(publicDir))
{
    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(publicDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

app.Run();
