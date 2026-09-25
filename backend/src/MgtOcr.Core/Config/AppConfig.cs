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

    // Business Partner (customer) master-data lookup — used to match a customer name read off
    // an OCR'd document (Sales Order first) back to its SAP Business Partner record. Separate
    // BaseUrl/AuthHeader from the fields above because this is a read-only GET against
    // API_BUSINESS_PARTNER, which may use a different communication user than the write-side
    // Sap:User/Sap:Password. AuthHeader, if set, is used as-is (e.g. "Basic <base64>") and wins
    // over Sap:User/Sap:Password; leave it blank to fall back to those.
    public string SapBusinessPartnerBaseUrl { get; init; } = "";
    public string SapBusinessPartnerAuthHeader { get; init; } = "";

    // Sales Order (module "SO") posting can use its own dedicated Dev/Prod service root
    // (Sap:SalesOrder:BaseUrl_Dev/BaseUrl_Prod + optional AuthHeader) instead of the generic flat
    // Sap:BaseUrl/Sap:User/Sap:Password — same shape as the Business Partner lookup above. Falls
    // back to the flat fields (in SapClient) when blank, so this still works before
    // Sap:SalesOrder is filled in.
    public string SapSalesOrderBaseUrl { get; init; } = "";
    public string SapSalesOrderAuthHeader { get; init; } = "";

    // --- Write (POST) target, kept separate from the read target ---
    // Reads (Business Partner / Product / Billing / Sales Order lookups) and writes (creating a
    // document in SAP) can point at different tenants: Sap:ActiveEnvironment picks the read one,
    // Sap:WriteEnvironment the write one. With WriteEnvironment = "dev" and ActiveEnvironment =
    // "prod", master data is matched against live production while nothing is ever created there.
    // When Sap:WriteEnvironment is absent these fall back to the read URLs, i.e. the old behaviour.
    public string SapWriteEnvironment { get; init; } = "";
    public string SapWriteBaseUrl { get; init; } = "";
    public string SapSalesOrderWriteBaseUrl { get; init; } = "";
    // Manual Gross Price condition type used for GLC lines (Sap:SalesOrder:PriceConditionType).
    // Not hardcoded: the existing, already-working Excel/Zoho -> SAP Sales Order integration
    // (SalesOrderImportJob.BuildCreateBody) keeps this in config for exactly the same reason —
    // the user isn't 100% sure ZPR0 is fixed for every case, so it's a setting, not a literal.
    public string SapSalesOrderPriceConditionType { get; init; } = "ZPR0";

    // Item Note 1 (SD item long text) sent per Sales Order line via the to_Text
    // navigation. LongTextID = the SAP text ID configured for "Item Note 1"
    // (Sap:SalesOrder:ItemNoteTextId, default "ZI01" per this tenant's VOTXN config).
    // ItemNoteLanguages = comma-separated SAP language keys the note is sent under so it
    // shows whatever the SAP logon language (Sap:SalesOrder:ItemNoteLanguages, default
    // "TH,EN"). Blank ItemNoteTextId = feature off (nothing sent). NOTE: the exact
    // Language key FORMAT this service accepts (ISO "TH"/"EN" vs SAP internal "2"/"E")
    // is not yet verified against $metadata — adjust the config value if SAP rejects it.
    public string SapSalesOrderItemNoteTextId { get; init; } = "ZI01";
    public string SapSalesOrderItemNoteLanguages { get; init; } = "TH,EN";

    // Material (Product) plant-extension lookup — Step 2 of SAP integration for the Sales Order
    // module (after the Business Partner lookup above): confirms a material has been extended to
    // the target Plant in SAP before a Sales Order is posted. Same shape/fallback as the other
    // read-only SAP lookups; blank BaseUrl = not configured yet = the check is skipped.
    public string SapProductBaseUrl { get; init; } = "";
    public string SapProductAuthHeader { get; init; } = "";

    // Billing Document (invoice) lookup — used for the "Last Price" shown when confirming a
    // material match in the GLC Sales Order flow: the last actual selling price SAP billed this
    // customer for this material (see SapBillingClient). Same shape/fallback as the other
    // read-only SAP lookups; blank BaseUrl = not configured yet = the lookup is skipped (returns
    // null, never blocks the save flow). Point this at API_BILLING_DOCUMENT_SRV.
    public string SapBillingBaseUrl { get; init; } = "";
    public string SapBillingAuthHeader { get; init; } = "";

    // One row per legal entity (Sales Organization + Company Code + Plant) this system posts
    // Sales Orders for. Per user: Plant is what identifies the company in practice (e.g. 2100 =
    // GLC/Green Leaf, 1100 = MGT) — CompanyForPlant is keyed on that. Populated from the
    // "MGT"/"GLC" appsettings.json sections in Program.cs. "Name" is the config section name, not
    // a SAP field.
    public CompanyProfile[] Companies { get; init; } = [];
    public CompanyProfile? CompanyForPlant(string? plant) =>
        string.IsNullOrWhiteSpace(plant) ? null : Companies.FirstOrDefault(c => c.DefaultPlant == plant);
    public CompanyProfile? CompanyForSalesOrg(string? salesOrg) =>
        string.IsNullOrWhiteSpace(salesOrg) ? null : Companies.FirstOrDefault(c => c.SalesOrganization == salesOrg);

    // Resolves the appsettings company profile ("MGT"/"GLC") for a signed-in user, given the
    // company fields off CurrentUser/UserCompany. NOT a plain name match: Ms_Company.CompanyCode
    // (MGT_Datawarehouse) stores "MGT" for the first company but "Green Leaf" -- not "GLC" -- for
    // the second, confirmed live via /api/me, so comparing it straight against CompanyProfile.Name
    // only ever resolves MGT and silently returns null for every GLC user (which is exactly the bug
    // that let a company-scoping query run unscoped). The frontend already works around this the
    // same way (AppLayout.tsx keys off "is it MGT?" rather than comparing to the literal "GLC"), so
    // this mirrors that: SalesOrganization is tried first (forward-compatible if Ms_User ever
    // carries it reliably), then "MGT" is matched by name and anything else maps to "GLC" -- safe
    // as long as there are exactly two companies.
    public CompanyProfile? CompanyForUser(string? companyCode, string? salesOrganization)
    {
        var bySalesOrg = CompanyForSalesOrg(salesOrganization);
        if (bySalesOrg is not null) return bySalesOrg;
        if (string.IsNullOrWhiteSpace(companyCode)) return null;
        var name = string.Equals(companyCode, "MGT", StringComparison.OrdinalIgnoreCase) ? "MGT" : "GLC";
        return Companies.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // Zoho CRM (v8 REST API) — used for the MGT-side Sales Order flow: MGT-opened documents
    // find/send their customer match to Zoho CRM instead of SAP (GLC-opened documents keep
    // using the SAP Business Partner lookup above). OAuth2 refresh-token flow; ClientId/
    // ClientSecret/RefreshToken are real secrets and should come from user-secrets / env vars in
    // any shared environment, same as SapPassword/LocalAuthSigningKey.
    public string ZohoAccountsUrl { get; init; } = "https://accounts.zoho.com";
    public string ZohoApiDomain { get; init; } = "https://www.zohoapis.com";
    public string ZohoClientId { get; init; } = "";
    public string ZohoClientSecret { get; init; } = "";
    public string ZohoRefreshToken { get; init; } = "";
    public bool ZohoConfigured =>
        !string.IsNullOrWhiteSpace(ZohoClientId) && !string.IsNullOrWhiteSpace(ZohoClientSecret) && !string.IsNullOrWhiteSpace(ZohoRefreshToken);

    // ---- Authentication (Entra ID) ----
    // A list from day one, not a single tenant: the group's second company is expected to sit in
    // its own Microsoft tenant and that tenant is not available yet. Entries whose TenantId is
    // blank are ignored, so the second company is switched on by filling in configuration only.
    public AuthTenant[] AuthTenants { get; init; } = [];
    // Optional extra accepted audience; the tenants' ClientIds are accepted automatically.
    public string AuthAudience { get; init; } = "";
    // Database holding the shared user master (Ms_User / Ms_UserCompany / Ms_Company). Same SQL
    // Server instance as the OCR database, reached with a three-part name.
    public string UserDatabase { get; init; } = "MGT_Datawarehouse";
    // Development-only: act as this Ms_User while the Entra app registration does not exist yet.
    // Ignored the moment a tenant is configured, and Program.cs refuses to start without one
    // outside Development.
    public string DevFallbackEmail { get; init; } = "";

    public AuthTenant[] ConfiguredTenants =>
        AuthTenants.Where(t => !string.IsNullOrWhiteSpace(t.TenantId)).ToArray();
    public bool AuthConfigured => ConfiguredTenants.Length > 0;

    // ---- Username/password login (OCR-issued JWT) ----
    // SigningKey is a real secret — comes from user-secrets / env var, never appsettings.json.
    public string LocalAuthSigningKey { get; init; } = "";
    public string LocalAuthIssuer { get; init; } = "mgtocr";
    public string LocalAuthAudience { get; init; } = "mgtocr";
    public int LocalAuthLifetimeMinutes { get; init; } = 480;

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

// One Microsoft tenant the system accepts sign-ins from. CompanyId links it back to
// MGT_Datawarehouse.dbo.Ms_Company so a token can be sanity-checked against the company its
// tenant is supposed to represent once phase 2 (per-company separation) lands.
public sealed record AuthTenant(string Name = "", string TenantId = "", string ClientId = "", int CompanyId = 0);

// One legal entity this system can post Sales Orders for. "Name" is the appsettings.json
// section name ("MGT" / "GLC"), not a SAP field. See AppConfig.Companies / CompanyForPlant.
public sealed record CompanyProfile(
    string Name,
    string SalesOrganization,
    string CompanyCode,
    string DefaultPlant,
    string AuthorizationGroup);
