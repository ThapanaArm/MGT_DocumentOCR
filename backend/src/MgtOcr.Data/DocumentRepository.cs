using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MgtOcr.Core;
using MgtOcr.Core.Json;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Data;

// Ported from the "document repository" section of app/main.py (lines 107-327): denorm(),
// create_document(), save_lines(), get_document(), update_header(), vendor-memory helpers,
// log_audit(), and chat helpers. Header/lines are plain Dictionary<string,object?> trees
// throughout, matching Python's untyped dict handling exactly (see DynamicRow/JsonBodyHelpers).
public partial class DocumentRepository(Db db, string uploadDir)
{
    private static readonly Dictionary<string, string> VendorTaxIdField = new()
    {
        ["AP"] = "vendorTaxId", ["SO"] = "customerTaxId", ["II"] = "vendorTaxId", ["PODP"] = "vendorTaxId",
    };

    private static readonly Dictionary<string, string[]> MemorableFields = new()
    {
        ["AP"] = ["vendorName", "branch", "paymentTerms", "currency", "taxCode", "calculateTax", "paymentMethod"],
        ["SO"] = ["customerName", "shipToName", "shipToAddress", "currency", "paymentTerms", "incoterms"],
        ["II"] = ["vendorName", "paymentTerms", "currency", "taxCode", "calculateTax", "paymentMethod",
                  "businessPlace", "language", "bankCountry", "bankKey", "bankAccountNo"],
        ["PODP"] = ["vendorName", "paymentTerms", "currency"],
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateRegex();

    private static DateTime? ParseDate(object? v)
    {
        var s = (v?.ToString() ?? "").Trim();
        if (s.Length > 10) s = s[..10];
        if (!DateRegex().IsMatch(s)) return null;
        return DateTime.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    // Posting Date is the date the document entered the system (upload / OCR), not the date printed
    // on the document: SAP posts into the period the document is recorded in, and the OCR header
    // otherwise falls back to invoiceDate. Stamped once at creation and preserved on re-read; the
    // user can still overwrite it in the header form. SO has no posting date (see Denorm).
    public static void StampPostingDate(string module, Dictionary<string, object?> header, DateTime when)
    {
        if (module == "SO") return;
        header["postingDate"] = when.ToString("yyyy-MM-dd");
    }

    // Tax tab (MIRO / FB60): Tax Reporting Date, Tax Fulfill Date and Tax Date default to the
    // invoice date. Only blanks are filled, so a value OCR read or the user typed is never overwritten.
    public static void DefaultTaxDates(string module, Dictionary<string, object?> header)
    {
        if (module == "SO") return;
        var invoiceDate = header.GetStr("invoiceDate").Trim();
        if (invoiceDate.Length == 0) return;
        foreach (var key in new[] { "taxReportingDate", "taxFulfillDate", "taxDate" })
            if (string.IsNullOrWhiteSpace(header.GetStr(key))) header[key] = invoiceDate;
    }

    public static Dictionary<string, object?> Denorm(string module, Dictionary<string, object?> h)
    {
        if (module == "SO")
        {
            var total = Num(h.Get("totalAmount"));
            return new()
            {
                ["DocNo"] = h.Get("poNo"), ["DocDate"] = ParseDate(h.Get("poDate")), ["PostingDate"] = null,
                ["PartnerName"] = h.Get("customerName"), ["PartnerTaxId"] = h.Get("customerTaxId"),
                ["Currency"] = string.IsNullOrEmpty(h.GetStr("currency")) ? "THB" : h.GetStr("currency"),
                ["SubTotal"] = total, ["VatRate"] = 0.0, ["VatAmount"] = 0.0, ["WhtAmount"] = 0.0, ["TotalAmount"] = total,
            };
        }
        return new()
        {
            ["DocNo"] = h.Get("invoiceNo"), ["DocDate"] = ParseDate(h.Get("invoiceDate")),
            ["PostingDate"] = ParseDate(h.Get("postingDate") ?? h.Get("invoiceDate")),
            ["PartnerName"] = h.Get("vendorName"), ["PartnerTaxId"] = h.Get("vendorTaxId"),
            ["Currency"] = string.IsNullOrEmpty(h.GetStr("currency")) ? "THB" : h.GetStr("currency"),
            ["SubTotal"] = Num(h.Get("subTotal")),
            // decimal(5,2) in the DB (max 999.99) — clamp guards against an OCR misread blowing up the INSERT.
            ["VatRate"] = Math.Min(Math.Max(Num(h.Get("vatRate")), 0), 100),
            ["VatAmount"] = Num(h.Get("vatAmount")),
            ["WhtAmount"] = Num(h.Get("whtAmount")), ["TotalAmount"] = Num(h.Get("totalAmount")),
        };
    }

    public async Task<Dictionary<string, object?>> GetVendorMemoryAsync(string module, string taxId)
    {
        if (string.IsNullOrEmpty(taxId)) return new();
        var r = await db.QueryOneAsync("SELECT MemoryJson FROM ocr.VendorMemory WHERE TaxId=@taxId AND Module=@module", new { taxId, module });
        if (r == null) return new();
        return JsonBodyHelpers.Unwrap(JsonSerializer.Deserialize<Dictionary<string, object?>>((string)r.MemoryJson) ?? new());
    }

    // Fills only OCR-blank fields from the same partner's previously-confirmed values; never
    // overwrites a value OCR actually read. Returns the field names filled, for a transparency note.
    public async Task<List<string>> ApplyVendorMemoryAsync(string module, Dictionary<string, object?> header)
    {
        var taxId = (header.Get(VendorTaxIdField.GetValueOrDefault(module, "")) ?? "").ToString()?.Trim() ?? "";
        var mem = await GetVendorMemoryAsync(module, taxId);
        if (mem.Count == 0) return [];
        var filled = new List<string>();
        foreach (var f in MemorableFields.GetValueOrDefault(module, []))
        {
            var cur = (header.Get(f) ?? "").ToString()?.Trim() ?? "";
            var memVal = (mem.Get(f) ?? "").ToString()?.Trim() ?? "";
            if (cur.Length == 0 && memVal.Length > 0)
            {
                header[f] = mem[f];
                filled.Add(f);
            }
        }
        return filled;
    }

    public async Task SaveVendorMemoryAsync(string module, Dictionary<string, object?> header)
    {
        var taxId = (header.Get(VendorTaxIdField.GetValueOrDefault(module, "")) ?? "").ToString()?.Trim() ?? "";
        if (taxId.Length == 0) return;
        var mem = MemorableFields.GetValueOrDefault(module, [])
            .Where(f => !string.IsNullOrWhiteSpace(header.Get(f)?.ToString()))
            .ToDictionary(f => f, f => header.Get(f));
        if (mem.Count == 0) return;
        var json = JsonSerializer.Serialize(mem, PyJson.Options);
        await db.ExecuteAsync("""
            MERGE ocr.VendorMemory AS t USING (SELECT @taxId AS TaxId, @module AS Module) AS s
              ON t.TaxId=s.TaxId AND t.Module=s.Module
              WHEN MATCHED THEN UPDATE SET MemoryJson=@json, UpdatedAt=SYSDATETIME()
              WHEN NOT MATCHED THEN INSERT(TaxId,Module,MemoryJson) VALUES(@taxId,@module,@json);
            """, new { taxId, module, json });
    }

    public async Task LogAuditAsync(int? docId, string module, string action, string user, string detail = "",
        string docNo = "", string fileName = "", string ocrProvider = "")
    {
        await db.ExecuteAsync("""
            INSERT ocr.AuditLog(DocId,Module,Action,DocNo,FileName,Detail,PerformedBy,OcrProvider)
            VALUES(@docId,@module,@action,@docNo,@fileName,@detail,@user,@ocrProvider)
            """, new
        {
            docId, module, action,
            docNo = docNo.Length > 0 ? docNo : null, fileName = fileName.Length > 0 ? fileName : null,
            detail = detail.Length > 0 ? detail : null, user = user.Length > 0 ? user : "system",
            ocrProvider = ocrProvider.Length > 0 ? ocrProvider : null,
        });
    }

    // ext: {header, lines, provider, confidence, confidenceNote, tokensIn, tokensOut, cost, costIn,
    // costOut, costCurrency, rawText} — the same shape app/ocr_engine.py's extract() returns.
    public async Task<int> CreateDocumentAsync(string module, Dictionary<string, object?> ext, string fileName,
        string stored, int size, string user, string apDocCategory, int? durationMs = null)
    {
        var header = (Dictionary<string, object?>)ext["header"]!;
        var filled = await ApplyVendorMemoryAsync(module, header);
        if (filled.Count > 0)
        {
            var note = "Filled from a previously confirmed partner (not read directly from this document): " + string.Join(", ", filled);
            var existing = ext.GetStr("confidenceNote");
            ext["confidenceNote"] = existing.Length > 0 ? $"{existing} / {note}" : note;
        }
        StampPostingDate(module, header, DateTime.Today);
        DefaultTaxDates(module, header);
        var d = Denorm(module, header);
        var docT = DocumentTables.For(module).Doc;
        var rawText = ext.GetStr("rawText");
        if (rawText.Length > 20000) rawText = rawText[..20000];

        var docId = await db.InsertReturningIdAsync($"""
            INSERT {docT}(Module,FileName,StoredPath,FileSize,OcrProvider,OcrConfidence,OcrConfidenceNote,
                  OcrTokensIn,OcrTokensOut,OcrCost,OcrInputCost,OcrOutputCost,OcrCostCurrency,OcrDurationMs,
                  ApDocCategory,Status,
                  DocNo,DocDate,PostingDate,PartnerName,PartnerTaxId,Currency,SubTotal,VatRate,VatAmount,
                  WhtAmount,TotalAmount,HeaderJson,RawText,CreatedBy)
            VALUES(@module,@fileName,@stored,@size,@provider,@confidence,@confidenceNote,
                  @tokensIn,@tokensOut,@cost,@costIn,@costOut,@costCurrency,@durationMs,
                  @apDocCategory,'NEW', @docNo,@docDate,@postingDate,@partnerName,@partnerTaxId,@currency,
                  @subTotal,@vatRate,@vatAmount,@whtAmount,@totalAmount,@headerJson,@rawText,@user);
            SELECT SCOPE_IDENTITY();
            """, new
        {
            module, fileName, stored, size,
            provider = ext.Get("provider"), confidence = ext.Get("confidence"), confidenceNote = ext.Get("confidenceNote"),
            tokensIn = ext.Get("tokensIn"), tokensOut = ext.Get("tokensOut"), cost = ext.Get("cost"),
            costIn = ext.Get("costIn"), costOut = ext.Get("costOut"), costCurrency = ext.Get("costCurrency"),
            durationMs,
            apDocCategory = apDocCategory.Length > 0 ? apDocCategory : null,
            docNo = d.Get("DocNo"), docDate = d.Get("DocDate"), postingDate = d.Get("PostingDate"),
            partnerName = d.Get("PartnerName"), partnerTaxId = d.Get("PartnerTaxId"), currency = d.Get("Currency"),
            subTotal = d.Get("SubTotal"), vatRate = d.Get("VatRate"), vatAmount = d.Get("VatAmount"),
            whtAmount = d.Get("WhtAmount"), totalAmount = d.Get("TotalAmount"),
            headerJson = JsonSerializer.Serialize(header, PyJson.Options), rawText, user,
        });

        await SaveLinesAsync(module, docId, (List<Dictionary<string, object?>>)ext["lines"]!);
        await LogAuditAsync(docId, module, "CREATE", user, detail: "Imported new document",
            docNo: d.GetStr("DocNo"), fileName: fileName, ocrProvider: ext.GetStr("provider"));
        return docId;
    }

    // Always full delete+reinsert (matches Python) — ItemNo is always recomputed as (i+1)*10.
    public async Task SaveLinesAsync(string module, int docId, List<Dictionary<string, object?>> lines)
    {
        var lineT = DocumentTables.For(module).Line;
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync($"DELETE FROM {lineT} WHERE DocId=@docId", new { docId }, tx);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var extra = l.Get("extra") as Dictionary<string, object?>;
            await conn.ExecuteAsync($"""
                INSERT {lineT}(DocId,ItemNo,ExtCode,ExtDesc,Qty,Uom,UnitPrice,Amount,
                      MaterialCode,MapStatus,MapMethod,SapQty,SapUom,UomFactor,ExtraJson)
                VALUES(@docId,@itemNo,@extCode,@desc,@qty,@uom,@price,@amount,
                      @materialCode,@mapStatus,@mapMethod,@sapQty,@sapUom,@uomFactor,@extraJson)
                """, new
            {
                docId, itemNo = (i + 1) * 10, extCode = l.Get("extCode"), desc = l.Get("desc"),
                qty = Num(l.Get("qty")), uom = l.Get("uom"), price = Num(l.Get("price")), amount = Num(l.Get("amount")),
                materialCode = string.IsNullOrEmpty(l.GetStr("materialCode")) ? null : l.GetStr("materialCode"),
                mapStatus = l.Get("mapStatus"), mapMethod = l.Get("mapMethod"),
                sapQty = l.Get("sapQty"), sapUom = l.Get("sapUom"), uomFactor = l.Get("uomFactor"),
                extraJson = extra is { Count: > 0 } ? JsonSerializer.Serialize(extra, PyJson.Options) : null,
            }, tx);
        }
        await tx.CommitAsync();
    }

    // Records an external-system post (Zoho CRM Sales Order) exactly the way
    // DocumentsController.PostDocument records a SAP post: always one ocr.PostLog row, and on
    // success flip the document to POSTED with the external doc number + who/when. Lives here (not
    // in the Zoho controller) so it reuses the repo's own Db/transaction, and so
    // DocumentsController.PostDocument / sap.PostAsync stay untouched (standing rule). extDocNo is
    // stored in the generic SapDocNo ("external document number") column — SalesOrder has no
    // separate Zoho column and adding one would need a migration for no functional gain.
    //
    // companyCode (added 2026-09-22, SharePoint archiving phase-1 foundation): the canonical
    // "MGT"/"GLC" label (AppConfig.CompanyProfile.Name), stamped once here at the moment of a
    // successful post -- never recomputed later from whoever is viewing the document, since a user
    // can belong to more than one company. This endpoint only ever posts to Zoho, which only ever
    // means MGT, so callers pass "MGT" literally here; nullable purely so a failed post (no
    // company decided yet, since nothing was actually posted) leaves it unset. See
    // sql/22_document_company_code.sql for the column itself and the fuller rationale.
    public async Task RecordExternalPostAsync(string module, int docId, string? extDocNo, string? endpoint,
        string payloadJson, bool success, string? message, string user, string? companyCode = null)
    {
        var docT = DocumentTables.For(module).Doc;
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            INSERT ocr.PostLog(DocId,Module,SapDocNo,Endpoint,PayloadJson,Success,Message,PostedBy)
            VALUES(@docId,@module,@extDocNo,@endpoint,@payloadJson,@success,@message,@user)
            """, new { docId, module, extDocNo, endpoint, payloadJson, success = success ? 1 : 0, message, user }, tx);
        if (success)
            await conn.ExecuteAsync($"UPDATE {docT} SET Status='POSTED', SapDocNo=@extDocNo, CompanyCode=@companyCode, PostedAt=SYSDATETIME(), PostedBy=@user, UpdatedAt=SYSDATETIME() WHERE DocId=@docId",
                new { extDocNo, companyCode, user, docId }, tx);
        await tx.CommitAsync();
    }

    public async Task<Dictionary<string, object?>> GetDocumentAsync(int docId)
    {
        var t = DocumentTables.ForId(docId);
        var row = await db.QueryOneAsync($"SELECT * FROM {t.Doc} WHERE DocId=@docId", new { docId });
        if (row == null) throw new HttpApiException(404, "Document not found");
        // Explicit type (not var): `row` is dynamic, and passing a dynamic argument makes the whole
        // call-site expression type `dynamic` too unless the target is annotated — that broke the
        // `is { Length: > 0 }` pattern match further down, which needs a real static string type.
        Dictionary<string, object?> d = DynamicRow.ToDict(row);

        var lineRows = DynamicRow.ToDictList(await db.QueryAsync($"SELECT * FROM {t.Line} WHERE DocId=@docId ORDER BY ItemNo", new { docId }));

        int? sourceDocId = t.Doc == "ocr.SalesOrder" ? (d.Get("SourceDocId") as int?) : null;
        IEnumerable<dynamic> splitChildren = [];
        if (t.Doc == "ocr.SalesOrder" && sourceDocId == null)
        {
            splitChildren = await db.QueryAsync(
                "SELECT DocId, DocNo, Status, TotalAmount FROM ocr.SalesOrder WHERE SourceDocId=@docId ORDER BY DocId", new { docId });
        }

        return new Dictionary<string, object?>
        {
            ["docId"] = d.Get("DocId"), ["module"] = d.Get("Module"), ["fileName"] = d.Get("FileName"), ["status"] = d.Get("Status"),
            ["provider"] = d.Get("OcrProvider"), ["confidence"] = d.Get("OcrConfidence"),
            ["confidenceNote"] = d.GetStr("OcrConfidenceNote"),
            ["tokensIn"] = d.Get("OcrTokensIn"), ["tokensOut"] = d.Get("OcrTokensOut"),
            ["cost"] = d.Get("OcrCost"), ["costIn"] = d.Get("OcrInputCost"), ["costOut"] = d.Get("OcrOutputCost"),
            ["costCurrency"] = d.GetStr("OcrCostCurrency"),
            ["apDocCategory"] = d.GetStr("ApDocCategory"), ["createdAt"] = d.Get("CreatedAt"),
            ["sapDocNo"] = d.Get("SapDocNo"), ["postedAt"] = d.Get("PostedAt"), ["mapStatus"] = d.Get("MapStatus"),
            ["partnerCode"] = d.Get("PartnerCode"), ["shipToCode"] = d.Get("ShipToCode"),
            ["sourceDocId"] = sourceDocId, ["splitChildren"] = splitChildren,
            ["header"] = JsonBodyHelpers.Unwrap(JsonSerializer.Deserialize<Dictionary<string, object?>>(d.GetStr("HeaderJson") is { Length: > 0 } hj ? hj : "{}") ?? new()),
            ["lines"] = lineRows.Select(l => new Dictionary<string, object?>
            {
                ["itemNo"] = l.Get("ItemNo"), ["extCode"] = l.GetStr("ExtCode"), ["desc"] = l.GetStr("ExtDesc"),
                ["qty"] = l.Get("Qty"), ["uom"] = l.GetStr("Uom"), ["price"] = l.Get("UnitPrice"), ["amount"] = l.Get("Amount"),
                ["materialCode"] = l.GetStr("MaterialCode"), ["mapStatus"] = l.GetStr("MapStatus"), ["mapMethod"] = l.GetStr("MapMethod"),
                ["sapQty"] = l.Get("SapQty"), ["sapUom"] = l.GetStr("SapUom"), ["uomFactor"] = l.Get("UomFactor"),
                ["extra"] = JsonBodyHelpers.Unwrap(JsonSerializer.Deserialize<Dictionary<string, object?>>(l.GetStr("ExtraJson") is { Length: > 0 } ej ? ej : "{}") ?? new()),
            }).ToList(),
        };
    }

    public async Task UpdateHeaderAsync(int docId, string module, Dictionary<string, object?> header)
    {
        var docT = DocumentTables.For(module).Doc;
        var d = Denorm(module, header);
        await db.ExecuteAsync($"""
            UPDATE {docT} SET HeaderJson=@headerJson, DocNo=@docNo, DocDate=@docDate, PostingDate=@postingDate,
                PartnerName=@partnerName, PartnerTaxId=@partnerTaxId, Currency=@currency, SubTotal=@subTotal,
                VatRate=@vatRate, VatAmount=@vatAmount, WhtAmount=@whtAmount, TotalAmount=@totalAmount,
                UpdatedAt=SYSDATETIME() WHERE DocId=@docId
            """, new
        {
            headerJson = JsonSerializer.Serialize(header, PyJson.Options),
            docNo = d.Get("DocNo"), docDate = d.Get("DocDate"), postingDate = d.Get("PostingDate"),
            partnerName = d.Get("PartnerName"), partnerTaxId = d.Get("PartnerTaxId"), currency = d.Get("Currency"),
            subTotal = d.Get("SubTotal"), vatRate = d.Get("VatRate"), vatAmount = d.Get("VatAmount"),
            whtAmount = d.Get("WhtAmount"), totalAmount = d.Get("TotalAmount"), docId,
        });
    }

    private const string DocListCols =
        "DocId,Module,FileName,Status,DocNo,DocDate,PartnerName,PartnerCode," +
        "TotalAmount,Currency,SapDocNo,PostedAt,CreatedAt,OcrProvider,OcrConfidence,OcrConfidenceNote," +
        "OcrTokensIn,OcrTokensOut,OcrCost,OcrInputCost,OcrOutputCost,OcrCostCurrency,ApDocCategory";

    // Ported from list_documents() (main.py:466-491): SO queries ocr.SalesOrder alone; any other
    // specific module queries ocr.Document filtered by Module; no module unions both tables.
    public async Task<IEnumerable<dynamic>> ListDocumentsAsync(string module, string status, string apDocCategory, int limit,
        IReadOnlyCollection<string>? allowedModules = null)
    {
        var mod = module.ToUpperInvariant();
        var where = new List<string>();
        var p = new DynamicParameters();
        if (status.Length > 0) { where.Add("Status=@status"); p.Add("status", status.ToUpperInvariant()); }
        if (apDocCategory.Length > 0) { where.Add("ApDocCategory=@apDocCategory"); p.Add("apDocCategory", apDocCategory.ToUpperInvariant()); }

        if (mod.Length > 0)
        {
            var table = mod == "SO" ? "ocr.SalesOrder" : "ocr.Document";
            var w = new List<string>(where);
            // AP = liability-recording umbrella: Supplier Invoice (with PO) + Incoming Invoice
            // (without PO), both stored in ocr.Document.
            if (mod == "AP") { w.Insert(0, "Module IN ('AP','II')"); }
            else if (mod != "SO") { w.Insert(0, "Module=@module"); p.Add("module", mod); }
            var whereSql = w.Count > 0 ? " WHERE " + string.Join(" AND ", w) : "";
            p.Add("limit", limit);
            return await db.QueryAsync($"SELECT TOP (@limit) {DocListCols} FROM {table}{whereSql} ORDER BY DocId DESC", p);
        }

        // No specific tab: union across the tables, restricted to the modules the caller may
        // see. allowedModules == null means "no restriction" (for callers that do not pass it).
        IReadOnlyCollection<string> allow = allowedModules ?? ["AP", "II", "PODP", "SO"];
        var docMods = new[] { "AP", "II", "PODP" }.Where(m => allow.Contains(m)).ToArray();
        var includeSo = allow.Contains("SO");
        if (docMods.Length == 0 && !includeSo) return [];

        var whereSql1 = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var parts = new List<string>();
        if (docMods.Length > 0)
        {
            var w = new List<string>(where) { "Module IN @docMods" };
            p.Add("docMods", docMods);
            parts.Add($"SELECT {DocListCols} FROM ocr.Document WHERE {string.Join(" AND ", w)}");
        }
        if (includeSo)
            parts.Add($"SELECT {DocListCols} FROM ocr.SalesOrder{whereSql1}");
        p.Add("limit", limit);
        return await db.QueryAsync(
            $"SELECT TOP (@limit) * FROM ({string.Join(" UNION ALL ", parts)}) x ORDER BY DocId DESC", p);
    }

    public record DocumentsPage(IReadOnlyList<dynamic> Rows, int Total, int? CountAll, int? CountAP, int? CountII);

    // Real server-side paging for the Document Register (InboxPage). Search (DocNo/PartnerName),
    // date range (DocDate) and the module/category/status filters are ALL applied in SQL; one page
    // is returned via OFFSET/FETCH plus the true total COUNT, so the client never pulls the whole
    // table and slices it. For the merged invoice tab (module "AP" = AP+II) the per-tab counts
    // (all/AP/II) are computed server-side too, respecting search+date but ignoring the selected
    // tab. Common filter columns (Status, ApDocCategory, DocNo, PartnerName, DocDate) exist on both
    // ocr.Document and ocr.SalesOrder, so the same predicates work across the union. Built with
    // plain string concatenation (not $"" interpolation) to keep the nested SQL quotes unambiguous.
    public async Task<DocumentsPage> ListDocumentsPagedAsync(
        string module, string status, string apDocCategory, string search,
        string dateFrom, string dateTo, string invModule, int page, int pageSize,
        IReadOnlyCollection<string>? allowedModules = null)
    {
        var mod = module.ToUpperInvariant();
        var p = new DynamicParameters();
        var common = new List<string>();
        if (status.Length > 0) { common.Add("Status=@status"); p.Add("status", status.ToUpperInvariant()); }
        if (apDocCategory.Length > 0) { common.Add("ApDocCategory=@apDocCategory"); p.Add("apDocCategory", apDocCategory.ToUpperInvariant()); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            common.Add("(DocNo LIKE @q OR PartnerName LIKE @q)");
            var esc = search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
            p.Add("q", "%" + esc + "%");
        }
        if (dateFrom.Length > 0) { common.Add("DocDate >= @dateFrom"); p.Add("dateFrom", dateFrom); }
        if (dateTo.Length > 0) { common.Add("DocDate <= @dateTo"); p.Add("dateTo", dateTo); }

        string Where(IEnumerable<string> extra)
        {
            var all = extra.Concat(common).ToList();
            return all.Count > 0 ? " WHERE " + string.Join(" AND ", all) : "";
        }

        string srcSql;
        int? cAll = null, cAp = null, cIi = null;

        if (mod == "AP")
        {
            var extra = new List<string> { "Module IN ('AP','II')" };
            var inv = invModule.ToUpperInvariant();
            if (inv == "AP" || inv == "II") { extra.Add("Module=@invModule"); p.Add("invModule", inv); }
            srcSql = "SELECT " + DocListCols + " FROM ocr.Document" + Where(extra);
            var countWhere = Where(new List<string> { "Module IN ('AP','II')" });
            var grp = await db.QueryAsync("SELECT Module, COUNT(*) AS Cnt FROM ocr.Document" + countWhere + " GROUP BY Module", p);
            cAp = 0; cIi = 0;
            foreach (var r in grp)
            {
                var m = (string)r.Module; var c = (int)r.Cnt;
                if (m == "AP") cAp = c; else if (m == "II") cIi = c;
            }
            cAll = (cAp ?? 0) + (cIi ?? 0);
        }
        else if (mod == "SO")
        {
            srcSql = "SELECT " + DocListCols + " FROM ocr.SalesOrder" + Where(Array.Empty<string>());
        }
        else if (mod.Length > 0)
        {
            p.Add("module", mod);
            srcSql = "SELECT " + DocListCols + " FROM ocr.Document" + Where(new[] { "Module=@module" });
        }
        else
        {
            IReadOnlyCollection<string> allow = allowedModules ?? new[] { "AP", "II", "PODP", "SO" };
            var docMods = new[] { "AP", "II", "PODP" }.Where(m => allow.Contains(m)).ToArray();
            var includeSo = allow.Contains("SO");
            if (docMods.Length == 0 && !includeSo) return new DocumentsPage(Array.Empty<dynamic>(), 0, null, null, null);
            var parts = new List<string>();
            if (docMods.Length > 0)
            {
                p.Add("docMods", docMods);
                parts.Add("SELECT " + DocListCols + " FROM ocr.Document" + Where(new[] { "Module IN @docMods" }));
            }
            if (includeSo)
                parts.Add("SELECT " + DocListCols + " FROM ocr.SalesOrder" + Where(Array.Empty<string>()));
            srcSql = string.Join(" UNION ALL ", parts);
        }

        p.Add("off", Math.Max(0, (page - 1) * pageSize));
        p.Add("ps", pageSize);
        var totalRow = await db.QueryOneAsync("SELECT COUNT(*) AS Cnt FROM (" + srcSql + ") x", p);
        var total = totalRow == null ? 0 : (int)totalRow.Cnt;
        var rows = (await db.QueryAsync(
            "SELECT * FROM (" + srcSql + ") x ORDER BY DocId DESC OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY", p)).ToList();
        return new DocumentsPage(rows, total, cAll, cAp, cIi);
    }

    // ---------------------------------------------------------------- chat (AI correction history)
    private string ChatDir => Path.Combine(uploadDir, "chat");

    public async Task<int> SaveChatMessageAsync(int docId, string role, string text, byte[]? imageBytes, string imageExt, string user)
    {
        string? imagePath = null;
        if (imageBytes != null)
        {
            var dir = Path.Combine(ChatDir, docId.ToString());
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_ffffff}{imageExt}");
            await File.WriteAllBytesAsync(p, imageBytes);
            imagePath = p;
        }
        var chatT = DocumentTables.ForId(docId).Chat;
        return await db.InsertReturningIdAsync($"""
            INSERT {chatT}(DocId,Role,MessageText,ImagePath,CreatedBy) VALUES(@docId,@role,@text,@imagePath,@user);
            SELECT SCOPE_IDENTITY();
            """, new { docId, role, text, imagePath, user });
    }

    public async Task<List<Dictionary<string, object?>>> GetChatHistoryAsync(int docId)
    {
        var chatT = DocumentTables.ForId(docId).Chat;
        var rows = DynamicRow.ToDictList(await db.QueryAsync(
            $"SELECT ChatId, Role, MessageText, ImagePath, CreatedAt FROM {chatT} WHERE DocId=@docId ORDER BY ChatId", new { docId }));
        return rows.Select(x => new Dictionary<string, object?>
        {
            ["chatId"] = x.Get("ChatId"), ["role"] = x.Get("Role"), ["text"] = x.GetStr("MessageText"),
            ["hasImage"] = !string.IsNullOrEmpty(x.GetStr("ImagePath")), ["createdAt"] = x.Get("CreatedAt"),
        }).ToList();
    }
}
