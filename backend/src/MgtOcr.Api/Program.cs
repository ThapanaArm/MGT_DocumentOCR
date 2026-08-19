using DotNetEnv;
using MgtOcr.Api.Json;
using MgtOcr.Core.Config;
using MgtOcr.Data;

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
    TesseractCmd = Get("TESSERACT_CMD"),
    TessdataPrefix = Get("TESSDATA_PREFIX"),
    AzureDiEndpoint = Get("AZURE_DI_ENDPOINT"),
    AzureDiKey = Get("AZURE_DI_KEY"),
    AnthropicApiKey = Get("ANTHROPIC_API_KEY"),
    AnthropicModel = Get("ANTHROPIC_MODEL", "claude-sonnet-5"),
    TyphoonApiKey = Get("TYPHOON_API_KEY"),
    TyphoonModel = Get("TYPHOON_MODEL", "typhoon-ocr"),
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

builder.Services.AddControllers().AddJsonOptions(o =>
{
    // /api/documents/* uses camelCase (hand-built dicts in Python); /api/masters/* returns raw
    // PascalCase SQL columns as dynamic objects, which bypass this policy entirely since Dapper's
    // dynamic rows serialize using their original property names regardless of naming policy.
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.Converters.Add(new PythonDateTimeConverter());
    o.JsonSerializerOptions.Converters.Add(new PythonDecimalConverter());
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

app.UseCors("frontend");
app.UseAuthorization();
app.MapControllers();

app.Run();
