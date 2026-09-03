namespace MgtOcr.Core.Config;

// Ported from app/config.py — every field here mirrors a value read from .env there.
// Keep field names close to the Python names so cross-referencing during the port stays easy.
public class AppConfig
{
    public required string DbServer { get; init; }
    public required string DbName { get; init; }
    public required string DbUser { get; init; }
    public required string DbPassword { get; init; }
    public required string DbDriver { get; init; }

    public string AppHost { get; init; } = "0.0.0.0";
    public int AppPort { get; init; } = 8080;

    public string[] OwnCompanyKeywords { get; init; } = ["MEGACHEM"];
    public string OwnTaxId { get; init; } = "";

    public string OcrProvider { get; init; } = "auto";
    public string TesseractCmd { get; init; } = "";
    public string TessdataPrefix { get; init; } = "";
    public string AzureDiEndpoint { get; init; } = "";
    public string AzureDiKey { get; init; } = "";
    public string AnthropicApiKey { get; init; } = "";
    public string AnthropicModel { get; init; } = "claude-sonnet-5";
    public string TyphoonApiKey { get; init; } = "";
    public string TyphoonModel { get; init; } = "typhoon-ocr";
    // New in the .NET port (no Python equivalent) — same raw-HTTP, no-SDK pattern as the
    // existing Claude/Azure/Typhoon clients.
    public string GeminiApiKey { get; init; } = "";
    public string GeminiModel { get; init; } = "gemini-2.5-flash";
    public string OpenAiApiKey { get; init; } = "";
    public string OpenAiModel { get; init; } = "gpt-4o";

    public string SapBaseUrl { get; init; } = "";
    public string SapUser { get; init; } = "";
    public string SapPassword { get; init; } = "";
    public string SapClient { get; init; } = "100";
    public string SapCompanyCode { get; init; } = "1000";
    public string SapDefaultPlant { get; init; } = "1000";

    public required string UploadDir { get; init; }

    // Note: app/config.py builds an ODBC connection string (Driver={ODBC Driver 17...}) for pyodbc.
    // Microsoft.Data.SqlClient talks TDS directly and uses ADO.NET connection string syntax instead —
    // there is no "Driver=" concept here. Same server/database/credentials/TrustServerCertificate
    // semantics as the Python side, just the .NET-native string format.
    // If a full connection string is supplied via ConnectionStrings (Default / dbDW),
    // use it as-is; otherwise compose one from the Database:* parts (back-compat).
    public string DbConnectionString { get; init; } = "";
    public string ConnectionString =>
        !string.IsNullOrWhiteSpace(DbConnectionString)
            ? DbConnectionString
            : $"Server={DbServer};Database={DbName};User Id={DbUser};Password={DbPassword};" +
              "TrustServerCertificate=True;";
}
