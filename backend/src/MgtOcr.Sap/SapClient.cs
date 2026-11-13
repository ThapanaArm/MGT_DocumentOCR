using System.Net.Http.Headers;
using MgtOcr.Core;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Sap;

public record SapPostResult(bool Success, bool Simulated, string SapDocNo, string Endpoint, string Message, object? Raw = null);

// Ported from app/sap.py's post() (lines 116-141). Does a raw HTTP POST that first fetches a CSRF
// token from the OData service (SAP Gateway rejects modifying requests without one). There is no
// simulation mode any more: an unset write URL is reported as a failure. `Simulated` stays in the
// result for API compatibility and is always false.
public class SapClient(AppConfig config, HttpClient httpClient)
{
    // Strip every "_"-prefixed key at ALL levels before the live POST. These keys (_target, _source,
    // _wht at the top level and _internalMaterial/_isoUnit/_docQuantity/_uomFactor nested inside the
    // to_Item / to_SuplrInvcItemPurOrdRef line arrays) are internal bookkeeping for logging and
    // simulation only. SAP's OData validator rejects any property it doesn't know, so a nested one
    // that leaks into a line item fails the whole POST with
    // /IWCOR/CX_DS_EP_PROPERTY_ERROR "Property '_internalMaterial' is invalid".
    private static Dictionary<string, object?> StripUnderscoreKeys(Dictionary<string, object?> payload) =>
        (Dictionary<string, object?>)StripUnderscore(payload)!;

    private static object? StripUnderscore(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> dict:
                return dict
                    .Where(kv => !kv.Key.StartsWith('_'))
                    .ToDictionary(kv => kv.Key, kv => StripUnderscore(kv.Value));
            // A string is IEnumerable<char>, so match it before the sequence case and leave it alone.
            case string:
                return value;
            // Line-item arrays (to_Item, to_PricingElement, ...) are IEnumerable of nested
            // dictionaries — recurse into each element so their nested "_" keys are stripped too.
            case System.Collections.IEnumerable seq:
                return seq.Cast<object?>().Select(StripUnderscore).ToList();
            default:
                return value;
        }
    }

    /// <summary>Reads a supplier invoice back from API_SUPPLIERINVOICE_PROCESS_SRV with its PO lines,
    /// so what SAP holds can be set against what was sent. Never throws: (false, message) on any
    /// failure, because a reconciliation that cannot read one invoice should still report the rest.</summary>
    public async Task<(bool Ok, string Body)> GetSupplierInvoiceAsync(string supplierInvoice, string fiscalYear)
    {
        var baseUrl = config.SapSupplierInvoiceBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Sap:SupplierInvoice base URL is not configured");
        static string Esc(string v) => (v ?? "").Trim().Replace("'", "''");
        var url = $"{baseUrl.TrimEnd('/')}/A_SupplierInvoice(SupplierInvoice='{Esc(supplierInvoice)}',FiscalYear='{Esc(fiscalYear)}')"
                  + $"?$expand=to_SuplrInvcItemPurOrdRef&$format=json&sap-client={config.SapClient}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(config.SapSupplierInvoiceAuthHeader))
                req.Headers.TryAddWithoutValidation("Authorization", config.SapSupplierInvoiceAuthHeader);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return (resp.IsSuccessStatusCode, text);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>Every supplier-invoice line booked against a purchase order (goods and planned
    /// delivery costs alike), raw JSON of A_SuplrInvcItemPurOrdRef. (false, message) on failure.</summary>
    public async Task<(bool Ok, string Body)> GetInvoiceLinesForPoAsync(string purchaseOrder)
    {
        var baseUrl = config.SapSupplierInvoiceBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Sap:SupplierInvoice base URL is not configured");
        var filter = Uri.EscapeDataString($"PurchaseOrder eq '{(purchaseOrder ?? "").Trim().Replace("'", "''")}'");
        var url = $"{baseUrl.TrimEnd('/')}/A_SuplrInvcItemPurOrdRef?$filter={filter}&$top=1000&$format=json&sap-client={config.SapClient}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(config.SapSupplierInvoiceAuthHeader))
                req.Headers.TryAddWithoutValidation("Authorization", config.SapSupplierInvoiceAuthHeader);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return (resp.IsSuccessStatusCode, text);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    public async Task<SapPostResult> PostAsync(string module, Dictionary<string, object?> payload)
    {
        var endpoint = payload.GetValueOrDefault("_target") as string ?? "";
        var body = StripUnderscoreKeys(payload);

        // "SO" (Sales Order) prefers its own dedicated service root (Sap:SalesOrder:BaseUrl_Dev/
        // Prod, e.g. ".../API_SALES_ORDER_SRV") once configured, same as SapBusinessPartnerClient
        // — that root already ends in the service name, so only the entity name (the part of
        // `endpoint` after the last "/") gets appended to it, not the full "SRV/A_Entity" path.
        // Falls back to the old flat Sap:BaseUrl (+ "/sap/opu/odata/sap/{endpoint}") when blank,
        // so this keeps working exactly as before until Sap:SalesOrder is filled in.
        // This is the ONLY place the app creates anything in SAP, so it is the only place that
        // uses the WRITE base URLs (Sap:WriteEnvironment). Everything else — Business Partner,
        // Product, Billing, Sales Order lookups — keeps reading from Sap:ActiveEnvironment. With
        // WriteEnvironment = dev and ActiveEnvironment = prod, master data is matched against live
        // production data while no document is ever posted into production.
        var useDedicatedSoUrl = module == "SO" && !string.IsNullOrEmpty(config.SapSalesOrderWriteBaseUrl);
        // A supplier invoice posts to its own service root as well (Sap:SupplierInvoice:BaseUrl_*,
        // on its own environment key that defaults to dev). Leaving BaseUrl_Prod empty is what
        // keeps a mis-set environment from ever creating a document in the live tenant.
        var useDedicatedApUrl = module is "AP" or "II" && !string.IsNullOrEmpty(config.SapSupplierInvoiceBaseUrl);
        var useDedicatedUrl = useDedicatedSoUrl || useDedicatedApUrl;
        var baseUrl = useDedicatedSoUrl ? config.SapSalesOrderWriteBaseUrl
            : useDedicatedApUrl ? config.SapSupplierInvoiceBaseUrl
            : config.SapWriteBaseUrl;

        // Simulation mode removed (12 Oct 2026, per Megachem): a missing write URL used to return a
        // made-up document number as a SUCCESS, which marked the document POSTED and locked it
        // although nothing existed in SAP. A missing URL is now a plain failure the person can see
        // and retry once the setting is fixed.
        if (string.IsNullOrEmpty(baseUrl))
        {
            var missing = module == "SO"
                ? "Sap:SalesOrder:BaseUrl_<Dev|Prod> (or Sap:BaseUrl_<Dev|Prod>)"
                : "Sap:BaseUrl_<Dev|Prod>";
            return new SapPostResult(false, false, "", endpoint,
                $"SAP is not configured: {missing} is empty in appsettings.json, so nothing was sent to SAP");
        }

        var url = useDedicatedUrl
            ? $"{baseUrl.TrimEnd('/')}/{endpoint[(endpoint.LastIndexOf('/') + 1)..]}?sap-client={config.SapClient}"
            : $"{baseUrl.TrimEnd('/')}/sap/opu/odata/sap/{endpoint}?sap-client={config.SapClient}";

        // Service-root URL used to fetch the CSRF token. For the dedicated SO service, baseUrl IS
        // the service root (".../API_SALES_ORDER_SRV"); for the flat layout the service root is
        // everything up to and including the service name (the part of `endpoint` before its last
        // "/", e.g. "API_SALES_ORDER_SRV" out of "API_SALES_ORDER_SRV/A_SalesOrder").
        var serviceName = endpoint.Contains('/') ? endpoint[..endpoint.LastIndexOf('/')] : endpoint;
        var tokenUrl = useDedicatedUrl
            ? $"{baseUrl.TrimEnd('/')}/?sap-client={config.SapClient}"
            : $"{baseUrl.TrimEnd('/')}/sap/opu/odata/sap/{serviceName}/?sap-client={config.SapClient}";

        // Applies whichever auth the SO/other path uses to a request. Shared by the token fetch and
        // the POST so both requests authenticate identically (the CSRF token is bound to the
        // authenticated session, so a mismatch would re-trigger a 403).
        void ApplyAuth(HttpRequestMessage r)
        {
            if (module == "SO" && !string.IsNullOrWhiteSpace(config.SapSalesOrderAuthHeader))
            {
                r.Headers.TryAddWithoutValidation("Authorization", config.SapSalesOrderAuthHeader);
            }
            else if (useDedicatedApUrl && !string.IsNullOrWhiteSpace(config.SapSupplierInvoiceAuthHeader))
            {
                r.Headers.TryAddWithoutValidation("Authorization", config.SapSupplierInvoiceAuthHeader);
            }
            else
            {
                var authBytes = Encoding.UTF8.GetBytes($"{config.SapUser}:{config.SapPassword}");
                r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            // Step 1: fetch a CSRF token. SAP returns it in the "x-csrf-token" response header and
            // sets the session cookie(s) it is bound to; those cookies flow to the POST below via
            // the HttpClient's CookieContainer (enabled by default on AddHttpClient<SapClient>()),
            // so this fetch and the POST must run on the same HttpClient instance.
            using var fetchReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
            ApplyAuth(fetchReq);
            fetchReq.Headers.TryAddWithoutValidation("X-CSRF-Token", "Fetch");
            fetchReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var fetchResp = await httpClient.SendAsync(fetchReq, cts.Token);

            string? csrfToken = null;
            if (fetchResp.Headers.TryGetValues("x-csrf-token", out var tokenValues))
            {
                csrfToken = tokenValues.FirstOrDefault();
            }
            if (string.IsNullOrWhiteSpace(csrfToken))
            {
                var fetchText = await fetchResp.Content.ReadAsStringAsync(cts.Token);
                return new SapPostResult(false, false, "", endpoint,
                    $"Failed to fetch CSRF token from SAP: {(int)fetchResp.StatusCode} " +
                    $"{fetchText[..Math.Min(300, fetchText.Length)]}");
            }

            // Step 2: POST with the fetched token (cookies are attached automatically).
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            ApplyAuth(req);
            req.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var resp = await httpClient.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return new SapPostResult(false, false, "", endpoint,
                    $"Failed to post to SAP: {(int)resp.StatusCode} {text[..Math.Min(500, text.Length)]}");
            }
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;
            var d = root.TryGetProperty("d", out var dEl) ? dEl : root;
            var sapDocNo = TryGetString(d, "SalesOrder") ?? TryGetString(d, "SupplierInvoice") ?? "";
            // What SAP says it actually did with a supplier invoice. Sent "A" (parked) and got a
            // posted document back once (5105600162), and the message only ever said "created" —
            // so the status from SAP's own response is now written into the message, which lands
            // in ocr.PostLog. Sent vs returned is then visible per attempt without a debugger.
            var sentStatus = body.GetValueOrDefault("SupplierInvoiceStatus") as string;
            var gotStatus = TryGetString(d, "SupplierInvoiceStatus");
            var message = sentStatus is null && gotStatus is null
                ? "Document created in SAP successfully"
                : $"Document created in SAP successfully — SupplierInvoiceStatus sent '{sentStatus ?? "(none)"}', "
                  + $"returned by SAP '{gotStatus ?? "(not in response)"}'";

            // GLC Sales Orders: the deep insert adds our Manual Price Gross (ZPR0) row, but the
            // pricing procedure ALSO determines its own ZPR0 row with no rate (shown with a red
            // status in VA03), so every item ends up with two ZPR0 lines. Remove the empty one(s)
            // after create. Best-effort only: the order already exists with the right price, so a
            // failure here never fails the post — it is just noted in the message.
            if (module == "SO" && !string.IsNullOrEmpty(sapDocNo)
                && !string.IsNullOrWhiteSpace(config.SapSalesOrderPriceConditionType))
            {
                var serviceRoot = useDedicatedSoUrl
                    ? baseUrl.TrimEnd('/')
                    : $"{baseUrl.TrimEnd('/')}/sap/opu/odata/sap/{serviceName}";
                var cleanup = await RemoveEmptyDuplicatePriceConditionsAsync(
                    serviceRoot, sapDocNo, config.SapSalesOrderPriceConditionType, csrfToken, ApplyAuth, cts.Token);
                if (!string.IsNullOrEmpty(cleanup)) message += " — " + cleanup;
            }
            return new SapPostResult(true, false, sapDocNo, endpoint, message, JsonSerializer.Deserialize<object>(d.GetRawText()));
        }
        catch (Exception e)
        {
            return new SapPostResult(false, false, "", endpoint, $"Failed to post to SAP: {e.Message}");
        }
    }

    /// <summary>
    /// After a supplier invoice is posted: fills the Assignment and Text its accounting document
    /// left EMPTY, the way Accounting keys them (PRD 5100001119: every line "DSM,INV.2831356514",
    /// Assignment = the PO). API_SUPPLIERINVOICE_PROCESS_SRV cannot do it at creation: the PO-ref
    /// item has no AssignmentReference, and an item text equal to the payables line's text comes
    /// back blank (5100000125). So the lines are read back from the journal (Operational Journal
    /// Entry Item cube) and changed with Journal Entry - Change (Asynchronous, SAP_COM_0002).
    /// Best effort and never throws: the invoice is already posted; the message says what happened.
    /// </summary>
    public Task<(bool Ok, string Message)> FillJournalEntryTextsAsync(
        string supplierInvoice, string fiscalYear, string companyCode, string text, string assignment)
        => FillJournalEntryLinesAsync(supplierInvoice, fiscalYear, companyCode, text, assignment, byAccountingDocument: false);

    /// <summary>The same fill for a journal entry known by its own number (a JV this app posted
    /// with Journal Entry - Post, whose tax lines have no Assignment field).</summary>
    public Task<(bool Ok, string Message)> FillAccountingDocumentLinesAsync(
        string accountingDocument, string fiscalYear, string companyCode, string text, string assignment,
        string accountOverride = "", string assignmentOverride = "")
        => FillJournalEntryLinesAsync(accountingDocument, fiscalYear, companyCode, text, assignment, byAccountingDocument: true,
            accountOverride, assignmentOverride);

    private async Task<(bool Ok, string Message)> FillJournalEntryLinesAsync(
        string supplierInvoice, string fiscalYear, string companyCode, string text, string assignment,
        bool byAccountingDocument, string accountOverride = "", string assignmentOverride = "")
    {
        // One G/L account whose lines take a different Assignment (the deferred VAT JV's input tax
        // line: a third party's Tax ID). Compared without leading zeros.
        static string Acct(string? a) => (a ?? "").Trim().TrimStart('0');
        accountOverride = Acct(accountOverride);
        assignmentOverride = (assignmentOverride ?? "").Trim();
        if (assignmentOverride.Length > 18) assignmentOverride = assignmentOverride[..18];
        var baseUrl = config.SapSupplierInvoiceBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Sap:SupplierInvoice base URL is not configured");
        text = (text ?? "").Trim();
        assignment = (assignment ?? "").Trim();
        if (text.Length == 0 && assignment.Length == 0 && assignmentOverride.Length == 0) return (true, "nothing to fill");
        if (text.Length > 50) text = text[..50];
        if (assignment.Length > 18) assignment = assignment[..18];
        static string Q(string v) => (v ?? "").Trim().Replace("'", "''");
        // The SAP_COM_0002 communication user (Sap:JournalEntry:AuthHeader), falling back to the
        // supplier invoice one -- see AppConfig.SapJournalEntryAuthHeader.
        void Auth(HttpRequestMessage r)
        {
            var auth = !string.IsNullOrWhiteSpace(config.SapJournalEntryAuthHeader)
                ? config.SapJournalEntryAuthHeader : config.SapSupplierInvoiceAuthHeader;
            if (!string.IsNullOrWhiteSpace(auth))
                r.Headers.TryAddWithoutValidation("Authorization", auth);
        }

        // 1. The accounting document's lines. An MM invoice's journal entry carries the invoice as
        //    its original reference: supplier invoice number + fiscal year (5105600246 + 2026).
        var cube = baseUrl.Replace("API_SUPPLIERINVOICE_PROCESS_SRV", "API_OPLACCTGDOCITEMCUBE_SRV", StringComparison.OrdinalIgnoreCase);
        var filter = Uri.EscapeDataString(byAccountingDocument
            ? $"CompanyCode eq '{Q(companyCode)}' and AccountingDocument eq '{Q(supplierInvoice)}' and FiscalYear eq '{Q(fiscalYear)}'"
            : $"CompanyCode eq '{Q(companyCode)}' and OriginalReferenceDocument eq '{Q(supplierInvoice)}{Q(fiscalYear)}'");
        var readUrl = $"{cube.TrimEnd('/')}/A_OperationalAcctgDocItemCube?$filter={filter}"
            + "&$select=AccountingDocument,AccountingDocumentItem,FiscalYear,FinancialAccountType,GLAccount,DocumentItemText,AssignmentReference"
            + $"&$top=999&$format=json&sap-client={config.SapClient}";
        var lines = new List<(string Doc, string Year, int Item, bool Change, string NewText, string NewAssignment)>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var req = new HttpRequestMessage(HttpMethod.Get, readUrl);
            Auth(req);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await httpClient.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                return (false, $"reading the journal entry failed: {(int)resp.StatusCode} {body[..Math.Min(300, body.Length)]}");
            using var json = JsonDocument.Parse(body);
            var d = json.RootElement.TryGetProperty("d", out var dd) ? dd : json.RootElement;
            var results = d.TryGetProperty("results", out var rr) ? rr : d;
            if (results.ValueKind != JsonValueKind.Array) return (false, "journal entry read returned no lines");
            foreach (var it in results.EnumerateArray())
            {
                // The payables line itself took both from the invoice header; leave it alone.
                var type = TryGetString(it, "FinancialAccountType") ?? "";
                if (type is "K" or "D") continue;
                var curText = (TryGetString(it, "DocumentItemText") ?? "").Trim();
                var curAssign = (TryGetString(it, "AssignmentReference") ?? "").Trim();
                // Empty, or the "<payables text> <what it is>" stand-in the invoice was created with.
                var fixText = text.Length > 0 && (curText.Length == 0
                    || curText.StartsWith(text + " ", StringComparison.OrdinalIgnoreCase));
                var lineAssignment = accountOverride.Length > 0 && assignmentOverride.Length > 0
                    && Acct(TryGetString(it, "GLAccount")) == accountOverride
                        ? assignmentOverride : assignment;
                var fixAssign = lineAssignment.Length > 0 && curAssign.Length == 0;
                if (!int.TryParse(TryGetString(it, "AccountingDocumentItem"), out var itemNo)) continue;
                lines.Add((TryGetString(it, "AccountingDocument") ?? "", TryGetString(it, "FiscalYear") ?? fiscalYear,
                    itemNo, fixText || fixAssign, fixText ? text : "", fixAssign ? lineAssignment : ""));
            }
        }
        catch (Exception e)
        {
            return (false, $"reading the journal entry failed: {e.Message}");
        }
        var todo = lines.Where(l => l.Change && l.Doc.Length > 0).ToList();
        if (lines.Count == 0) return (false, $"no journal entry found for supplier invoice {supplierInvoice}/{fiscalYear}");
        if (todo.Count == 0) return (true, $"{lines[0].Doc}: every line already has Assignment and Text");

        // 2. One change request for all of them. Children are unqualified (the WSDL schema has no
        //    elementFormDefault), only the message element carries the namespace.
        static string X(string v) => System.Security.SecurityElement.Escape(v) ?? "";
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        // The service is asynchronous and its WSDL binding requires WS-Addressing with WS-Reliable
        // Messaging (wsaw:UsingAddressing + wsrmp:RMAssertion): SAP de-duplicates on the
        // wsa:MessageID, and an empty soapenv:Header is answered with a bare 500 "Web service
        // processing error" (5105600275). Each request gets its own MessageID.
        var changeUrl = $"{new Uri(baseUrl).GetLeftPart(UriPartial.Authority)}/sap/bc/srt/scs_ext/sap/journalentrybulkchangerequest_";
        const string soapAction = "http://sap.com/xi/SAPSCORE/SFIN/JournalEntryBulkChangeRequest_In/JournalEntryBulkChangeRequest_InRequest";
        var msgUuid = Guid.NewGuid();
        sb.Append("<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:sfin=\"http://sap.com/xi/SAPSCORE/SFIN\" xmlns:wsa=\"http://www.w3.org/2005/08/addressing\">");
        sb.Append("<soapenv:Header>");
        sb.Append($"<wsa:MessageID>urn:uuid:{msgUuid:D}</wsa:MessageID>");
        sb.Append($"<wsa:Action>{soapAction}</wsa:Action>");
        sb.Append($"<wsa:To>{X(changeUrl)}</wsa:To>");
        sb.Append("</soapenv:Header><soapenv:Body><sfin:JournalEntryBulkChangeRequestMessage>");
        sb.Append($"<MessageHeader><ID>OCR-{X(supplierInvoice)}-{DateTime.UtcNow:HHmmssfff}</ID><UUID>{msgUuid:D}</UUID><CreationDateTime>{now}</CreationDateTime></MessageHeader>");
        foreach (var l in todo)
        {
            sb.Append("<JournalEntryGLItem>");
            sb.Append($"<MessageHeader><ID>OCR-{X(l.Doc)}-{l.Item}</ID><CreationDateTime>{now}</CreationDateTime></MessageHeader>");
            sb.Append($"<ItemKey><AccountingDocument>{X(l.Doc)}</AccountingDocument><CompanyCode>{X(companyCode)}</CompanyCode>"
                + $"<FiscalYear>{X(l.Year)}</FiscalYear><AccountingDocumentItemID>{l.Item}</AccountingDocumentItemID></ItemKey>");
            if (l.NewText.Length > 0)
                sb.Append($"<DocumentItemTextChange><DocumentItemText>{X(l.NewText)}</DocumentItemText>"
                    + "<FieldValueChangeIsRequested>true</FieldValueChangeIsRequested></DocumentItemTextChange>");
            if (l.NewAssignment.Length > 0)
                sb.Append($"<AssignmentReferenceChange><AssignmentReference>{X(l.NewAssignment)}</AssignmentReference>"
                    + "<FieldValueChangeIsRequested>true</FieldValueChangeIsRequested></AssignmentReferenceChange>");
            sb.Append("</JournalEntryGLItem>");
        }
        sb.Append("</sfin:JournalEntryBulkChangeRequestMessage></soapenv:Body></soapenv:Envelope>");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var req = new HttpRequestMessage(HttpMethod.Post, changeUrl);
            Auth(req);
            req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{soapAction}\"");
            req.Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml");
            using var resp = await httpClient.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            // Asynchronous: 202 means SAP queued it. A refusal while processing shows in the
            // message monitor, not here.
            return resp.IsSuccessStatusCode
                ? (true, $"{todo[0].Doc}: Assignment/Text sent for line(s) {string.Join(", ", todo.Select(t => t.Item))}")
                : (false, $"Journal Entry - Change refused: {(int)resp.StatusCode} {SoapFault(body)}");
        }
        catch (Exception e)
        {
            return (false, $"Journal Entry - Change failed: {e.Message}");
        }
    }

    /// <summary>One deferred-VAT transfer: D1 (deferred input tax) -> V1 (input tax) for ONE tax
    /// invoice, posted as its own journal entry so the input tax report lists each tax invoice with
    /// its own number (Reference) and issuer (Text).</summary>
    /// <param name="TaxPartner">The SAP Business Partner who issued the tax invoice (found by its
    /// Tax ID). A G/L-only entry has no supplier line, so the Thai input tax report had no partner
    /// to read the Tax Number from and the column stayed blank (PROMPT 1000134, TH3 0115551002992).
    /// It goes on the header as the country-specific business partner. "" = none found.</param>
    /// <param name="InputTaxAccount">The input tax (V1) G/L account, and <paramref name="InputTaxAssignment"/>
    /// what its line carries as Assignment: a third party's Tax ID (AP, 2026-11-12). Every other
    /// line gets <paramref name="Assignment"/> (the PO).</param>
    public record DeferredVatJv(string Reference, string Text, string Assignment, decimal Amount, decimal Base, DateTime? TaxDate,
        string TaxPartner = "", string InputTaxAccount = "", string InputTaxAssignment = "");

    /// <summary>Posts each entry with Journal Entry - Post (Synchronous), SAP_COM_0002
    /// (journalentrycreaterequestconfi), the same entry the "Journal Entry (Deferred VAT)" Excel
    /// uploads: Dr input tax V1 / Cr deferred input tax D1, both as DIRECT tax postings
    /// (ProductTaxItem + IsDirectTaxPosting -- a tax account cannot take a plain G/L item, and the
    /// tax base has no other place in this API). ProductTaxItem has no Assignment field, so each
    /// posted entry is then given its Assignment (the PO) -- and its Text if SAP dropped it -- with
    /// Journal Entry - Change, exactly as a supplier invoice is after posting.
    /// Stops at the first refusal so a half-posted bundle is visible, never silent.</summary>
    public async Task<List<(string Reference, bool Ok, string AccountingDocument, string Message)>> PostDeferredVatJournalEntriesAsync(
        IReadOnlyList<DeferredVatJv> entries, string companyCode, DateTime postingDate, string headerText, string currency)
    {
        var results = new List<(string, bool, string, string)>();
        var baseUrl = config.SapSupplierInvoiceBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            results.Add(("", false, "", "Sap:SupplierInvoice base URL is not configured"));
            return results;
        }
        var host = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var url = $"{host}/sap/bc/srt/scs_ext/sap/journalentrycreaterequestconfi";
        const string soapAction = "http://sap.com/xi/SAPSCORE/SFIN/JournalEntryCreateRequestConfirmation_In/JournalEntryCreateRequestConfirmation_InRequest";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static string X(string v) => System.Security.SecurityElement.Escape(v ?? "") ?? "";
        static string Cut(string v, int n) { v = (v ?? "").Trim(); return v.Length <= n ? v : v[..n]; }
        string Amt(decimal v) => v.ToString("0.00", inv);
        string D(DateTime d) => d.ToString("yyyy-MM-dd", inv);
        var user = Cut(string.IsNullOrWhiteSpace(config.SapUser) ? "OCR" : config.SapUser, 12);
        var cur = X(Cut(string.IsNullOrWhiteSpace(currency) ? "THB" : currency, 5));

        var missing = new[] { "V1", "D1" }.Where(c => !config.SapJournalEntryTaxConditionType.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            results.Add(("", false, "", "Set the condition type of tax code(s) " + string.Join(", ", missing)
                + " in appsettings.json → Sap:JournalEntry:TaxConditionType (SAP refuses a tax line without it: \"KSCHL is empty\"). "
                + "Find it on a posted entry: Display journal entry → Taxes → Cond. Type / Trans. columns."));
            return results;
        }

        foreach (var e in entries)
        {
            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", inv);
            var uuid = Guid.NewGuid();
            var taxDate = e.TaxDate ?? postingDate;
            var sb = new StringBuilder();
            sb.Append("<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:sfin=\"http://sap.com/xi/SAPSCORE/SFIN\" xmlns:wsa=\"http://www.w3.org/2005/08/addressing\">");
            sb.Append($"<soapenv:Header><wsa:MessageID>urn:uuid:{uuid:D}</wsa:MessageID><wsa:Action>{soapAction}</wsa:Action><wsa:To>{X(url)}</wsa:To></soapenv:Header>");
            sb.Append("<soapenv:Body><sfin:JournalEntryBulkCreateRequest>");
            sb.Append($"<MessageHeader><ID>OCRJV-{X(Cut(e.Reference, 16))}-{DateTime.UtcNow:HHmmss}</ID><UUID>{uuid:D}</UUID><CreationDateTime>{now}</CreationDateTime></MessageHeader>");
            sb.Append("<JournalEntryCreateRequest>");
            sb.Append($"<MessageHeader><ID>OCRJV-{X(Cut(e.Reference, 16))}</ID><CreationDateTime>{now}</CreationDateTime></MessageHeader>");
            sb.Append("<JournalEntry>");
            // Order is the WSDL's xsd:sequence.
            sb.Append("<OriginalReferenceDocumentType>BKPFF</OriginalReferenceDocumentType>");
            sb.Append("<BusinessTransactionType>RFBU</BusinessTransactionType>");
            sb.Append("<AccountingDocumentType>SA</AccountingDocumentType>");
            if (e.Reference.Length > 0) sb.Append($"<DocumentReferenceID>{X(Cut(e.Reference, 16))}</DocumentReferenceID>");
            if (headerText.Length > 0) sb.Append($"<DocumentHeaderText>{X(Cut(headerText, 25))}</DocumentHeaderText>");
            sb.Append($"<CreatedByUser>{X(user)}</CreatedByUser>");
            sb.Append($"<CompanyCode>{X(Cut(companyCode, 4))}</CompanyCode>");
            sb.Append($"<DocumentDate>{D(postingDate)}</DocumentDate>");
            sb.Append($"<PostingDate>{D(postingDate)}</PostingDate>");
            sb.Append($"<TaxReportingDate>{D(postingDate)}</TaxReportingDate>");
            sb.Append($"<TaxDeterminationDate>{D(taxDate)}</TaxDeterminationDate>");
            // Country/Region-specific Business Partner 1, which the Thai reports use for a
            // document without a supplier line: the tax invoice's issuer.
            if (e.TaxPartner.Trim().Length > 0)
                sb.Append($"<JrnlEntryCntrySpecificBp1>{X(Cut(e.TaxPartner, 10))}</JrnlEntryCntrySpecificBp1>");
            void TaxItem(string code, decimal amount, decimal taxBase, string dc)
            {
                sb.Append("<ProductTaxItem>");
                sb.Append($"<TaxCode>{code}</TaxCode>");
                // KSCHL / KTOSL of this tax code (Sap:JournalEntry:TaxConditionType / TaxItemClassification):
                // without the condition type SAP answers "KSCHL is empty".
                if (config.SapJournalEntryTaxItemClassification.TryGetValue(code, out var ktosl) && ktosl.Length > 0)
                    sb.Append($"<TaxItemClassification>{X(ktosl)}</TaxItemClassification>");
                if (config.SapJournalEntryTaxConditionType.TryGetValue(code, out var kschl) && kschl.Length > 0)
                    sb.Append($"<ConditionType>{X(kschl)}</ConditionType>");
                sb.Append($"<TaxDeterminationDate>{D(taxDate)}</TaxDeterminationDate>");
                sb.Append("<IsDirectTaxPosting>true</IsDirectTaxPosting>");
                sb.Append($"<AmountInTransactionCurrency currencyCode=\"{cur}\">{Amt(amount)}</AmountInTransactionCurrency>");
                sb.Append($"<DebitCreditCode>{dc}</DebitCreditCode>");
                if (e.Text.Length > 0) sb.Append($"<ProductiveTaxText>{X(Cut(e.Text, 50))}</ProductiveTaxText>");
                sb.Append($"<TaxBaseAmountInTransCrcy currencyCode=\"{cur}\">{Amt(taxBase)}</TaxBaseAmountInTransCrcy>");
                sb.Append("</ProductTaxItem>");
            }
            TaxItem("V1", e.Amount, e.Base, "S");     // Dr 11720000 input tax
            TaxItem("D1", -e.Amount, -e.Base, "H");   // Cr 11730000 deferred input tax
            sb.Append("</JournalEntry></JournalEntryCreateRequest></sfin:JournalEntryBulkCreateRequest></soapenv:Body></soapenv:Envelope>");

            string body;
            int status;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                var auth = !string.IsNullOrWhiteSpace(config.SapJournalEntryAuthHeader)
                    ? config.SapJournalEntryAuthHeader : config.SapSupplierInvoiceAuthHeader;
                if (!string.IsNullOrWhiteSpace(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
                req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{soapAction}\"");
                req.Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml");
                using var resp = await httpClient.SendAsync(req, cts.Token);
                body = await resp.Content.ReadAsStringAsync(cts.Token);
                status = (int)resp.StatusCode;
            }
            catch (Exception ex)
            {
                results.Add((e.Reference, false, "", $"Journal Entry - Post failed: {ex.Message}"));
                break;
            }

            // A synchronous confirmation: the document number, or 0000000000 and a log saying why.
            var docNo = System.Text.RegularExpressions.Regex.Match(body ?? "", @"<AccountingDocument>(\d+)</AccountingDocument>").Groups[1].Value;
            var notes = System.Text.RegularExpressions.Regex.Matches(body ?? "", @"<Note>([^<]*)</Note>")
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim())).Where(n => n.Length > 0).Distinct().ToList();
            var ok = status is >= 200 and < 300 && docNo.Length > 0 && docNo.Trim('0').Length > 0;
            if (!ok)
            {
                var why = notes.Count > 0 ? string.Join(" | ", notes) : SoapFault(body ?? "");
                results.Add((e.Reference, false, "", $"SAP refused the journal entry ({status}): {(why.Length > 600 ? why[..600] : why)}"));
                break;
            }
            var year = postingDate.Year;   // calendar fiscal year (GLC / MGT)
            var fill = await FillAccountingDocumentLinesAsync(docNo, year.ToString(inv), companyCode, e.Text, e.Assignment,
                e.InputTaxAccount, e.InputTaxAssignment);
            // SAP answers with the 10-digit key (0100000755); people read and search it as 100000755.
            var shown = docNo.TrimStart('0');
            results.Add((e.Reference, true, shown,
                $"Posted {shown}"
                + (e.TaxPartner.Trim().Length > 0 ? $" · Tax partner {e.TaxPartner.Trim()}" : " · NO tax partner (no SAP BP for this Tax ID)")
                + (fill.Ok ? $" · Assignment/Text: {fill.Message}" : $" · Assignment/Text NOT DONE — {fill.Message}")));
        }
        return results;
    }

    /// <summary>The readable part of a SOAP fault (faultstring + any detail text), not the envelope.</summary>
    private static string SoapFault(string body)
    {
        var parts = System.Text.RegularExpressions.Regex.Matches(body ?? "",
                @"<(?:\w+:)?(?:faultstring|faultText|text|Message)[^>]*>([^<]+)<")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))
            .Where(v => v.Length > 0).Distinct().ToList();
        var s = parts.Count > 0 ? string.Join(" | ", parts) : (body ?? "");
        return s.Length > 600 ? s[..600] : s;
    }

    /// <summary>Deletes the empty (rate 0) duplicate of <paramref name="conditionType"/> on every
    /// item of a just-created Sales Order, but only on items that also carry a non-zero row of the
    /// same condition type — so an item whose only ZPR0 is empty is never touched. Returns a short
    /// note for the post message, or "" when there was nothing to clean up.</summary>
    private async Task<string> RemoveEmptyDuplicatePriceConditionsAsync(
        string serviceRoot, string salesOrder, string conditionType, string csrfToken,
        Action<HttpRequestMessage> applyAuth, CancellationToken ct)
    {
        try
        {
            var filter = $"SalesOrder eq '{salesOrder.Replace("'", "''")}' and ConditionType eq '{conditionType.Replace("'", "''")}'";
            var listUrl = $"{serviceRoot}/A_SalesOrderItemPrElement?$filter={Uri.EscapeDataString(filter)}" +
                          "&$select=SalesOrderItem,PricingProcedureStep,PricingProcedureCounter,ConditionRateValue,ConditionAmount" +
                          $"&$format=json&sap-client={config.SapClient}";
            using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
            applyAuth(listReq);
            listReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var listResp = await httpClient.SendAsync(listReq, ct);
            var listText = await listResp.Content.ReadAsStringAsync(ct);
            if (!listResp.IsSuccessStatusCode)
                return $"could not read {conditionType} conditions to remove the empty duplicate ({(int)listResp.StatusCode})";

            using var doc = JsonDocument.Parse(listText);
            var rows = new List<(string Item, string Step, string Counter, decimal Rate, decimal Amount)>();
            if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in results.EnumerateArray())
                {
                    rows.Add((TryGetString(r, "SalesOrderItem") ?? "", TryGetString(r, "PricingProcedureStep") ?? "",
                        TryGetString(r, "PricingProcedureCounter") ?? "", ParseDec(r, "ConditionRateValue"),
                        ParseDec(r, "ConditionAmount")));
                }
            }

            var toDelete = rows
                .GroupBy(r => r.Item)
                .Where(g => g.Count() > 1 && g.Any(r => r.Rate != 0))
                .SelectMany(g => g.Where(r => r.Rate == 0 && r.Amount == 0))
                .ToList();
            if (toDelete.Count == 0) return "";

            var failed = new List<string>();
            foreach (var r in toDelete)
            {
                var key = $"SalesOrder='{salesOrder}',SalesOrderItem='{r.Item}'," +
                          $"PricingProcedureStep='{r.Step}',PricingProcedureCounter='{r.Counter}'";
                using var delReq = new HttpRequestMessage(HttpMethod.Delete,
                    $"{serviceRoot}/A_SalesOrderItemPrElement({key})?sap-client={config.SapClient}");
                applyAuth(delReq);
                delReq.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
                delReq.Headers.TryAddWithoutValidation("If-Match", "*");
                delReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var delResp = await httpClient.SendAsync(delReq, ct);
                if (!delResp.IsSuccessStatusCode)
                {
                    var t = await delResp.Content.ReadAsStringAsync(ct);
                    failed.Add($"item {r.Item}: {(int)delResp.StatusCode} {t[..Math.Min(200, t.Length)]}");
                }
            }
            return failed.Count == 0
                ? $"removed {toDelete.Count} empty duplicate {conditionType} row(s)"
                : $"could not remove empty duplicate {conditionType} row(s): {string.Join("; ", failed)}";
        }
        catch (Exception e)
        {
            return $"could not remove empty duplicate {conditionType} row(s): {e.Message}";
        }
    }

    // OData V2 sends Edm.Decimal as a string ("450.000000000"); accept a JSON number too.
    private static decimal ParseDec(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        return v.ValueKind == JsonValueKind.String
               && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                   System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
