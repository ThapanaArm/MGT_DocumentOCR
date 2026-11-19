using System.Text.Json;
using System.Text.RegularExpressions;
using MgtOcr.Core;
using MgtOcr.Core.Config;
using MgtOcr.Data;
using MgtOcr.Ocr;

namespace MgtOcr.Api.Services;

/// <summary>Sales Order "Reading rules" per customer (sql/39_customer_read_hints.sql, Megachem 17 Nov 2026).
///
/// Before the read: the customer is found in the PDF's text layer (13-digit tax ID, else a customer
/// name that already has rules) — no AI needed — and that customer's rules are added to the same
/// single Gemini request. A scanned PDF without a text layer is read as before (no second request:
/// Megachem does not want reads that cost more).
///
/// After the read: the positions the AI reports are kept per document (ocr.DocReadPosition). When the
/// document is POSTED to Zoho they become AUTO rules for that customer. A person's rules (Source = that
/// person's role, e.g. Admin / Leader / Manager / user -- anything but AUTO) always win and are never
/// overwritten by AUTO; a DISABLED field is neither sent nor learned.
///
/// Every database call is best effort: before sql/39 is run the read goes on as before.</summary>
public sealed partial class ReadHintService(Db db, AppConfig config, ILogger<ReadHintService> log)
{
    public const string AutoSource = "AUTO";

    /// <summary>Source of a rule a person set: their real role from Ms_User.UserRole (Admin, Leader,
    /// Manager, user), not a fixed "CS" (Megachem, 17 Nov 2026: "use the real roles -- I'm not CS").
    /// Fits Source varchar(10); never "AUTO". Rows written before this keep "CS" and still count as a person's.</summary>
    public static string PersonSource(string? role)
    {
        var r = (role ?? "").Trim();
        if (r.Length == 0 || r.Equals(AutoSource, StringComparison.OrdinalIgnoreCase)) r = "USER";
        return r.Length > 10 ? r[..10] : r;
    }

    /// <summary>True for a rule a person set (any Source except AUTO).</summary>
    public static bool IsPerson(string? source) => !string.Equals(source, AutoSource, StringComparison.OrdinalIgnoreCase);

    // ---------------- customer key ----------------

    /// <summary>Megachem's / Green Leaf's own tax IDs (App:OwnTaxId, comma separated) — never a customer.</summary>
    private HashSet<string> OwnTaxIds() =>
        (config.OwnTaxId ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Digits).Where(d => d.Length == 13).ToHashSet();

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>A customer's key: its 13-digit tax ID, else "N:" + normalised name; null when neither is usable.</summary>
    public string? KeyFor(string? taxId, string? name)
    {
        var t = Digits(taxId);
        if (t.Length == 13 && !OwnTaxIds().Contains(t)) return t;
        var n = NormName(name);
        return n.Length >= 5 ? "N:" + n : null;
    }

    /// <summary>Lower case, company-form words and punctuation/spaces removed, so "ABC Co., Ltd." and
    /// "บริษัท เอบีซี จำกัด" style differences in spacing/punctuation do not matter.</summary>
    public static string NormName(string? s)
    {
        var x = (s ?? "").ToLowerInvariant();
        x = CompanyWords().Replace(x, " ");
        return new string(x.Where(char.IsLetterOrDigit).ToArray());
    }

    [GeneratedRegex(@"บริษัท|บจก\.?|จำกัด|\(?มหาชน\)?|company\s+limited|co\.?\s*,?\s*ltd\.?|limited|\bltd\b|public|\bpcl\b|\binc\b|corporation|\bcorp\b|\(thailand\)|thailand")]
    private static partial Regex CompanyWords();

    // 13 digits, possibly written 0-1055-14005-33-9 or with spaces.
    [GeneratedRegex(@"(?<!\d)\d(?:[\s\-]?\d){12}(?!\d)")]
    private static partial Regex TaxIdLike();

    // ---------------- before the read ----------------

    private sealed record RuleRow(string CustomerKey, string? CustomerTaxId, string? CustomerName, string Field,
        int? PageNo, string? Box, string? Note, string Source);

    /// <summary>What to add to the read: always the "_positions" request; plus this customer's rules when
    /// the customer is found in the text layer. Null when switched off or not a Sales Order.
    /// knownTaxId / knownName: the customer an earlier read of the SAME document found (re-read). Used when
    /// the text layer names no customer with rules -- a scanned / photographed PO has no text layer, so on
    /// re-read its rules were never sent (Megachem, 17 Nov 2026: hand-written PO of แอคมี เคม เทค, "it doesn't
    /// read at all"). Free: no extra AI request. A first upload of a scanned PO still has nothing to go on.</summary>
    public async Task<ReadHintRequest?> PrepareAsync(string module, string path, string? salesOrg,
        string? knownTaxId = null, string? knownName = null)
    {
        if (!config.ReadHintsEnabled || module != "SO") return null;
        try
        {
            var org = (salesOrg ?? "").Trim();
            var rows = (await db.QueryAsync("""
                SELECT CustomerKey, CustomerTaxId, CustomerName, Field, PageNo, Box, Note, Source
                FROM ocr.CustomerReadHint WHERE Module=@module AND SalesOrg=@org AND Status='ACTIVE'
                """, new { module, org }))
                .Select(r => new RuleRow((string)r.CustomerKey, (string?)r.CustomerTaxId, (string?)r.CustomerName, (string)r.Field,
                    (int?)r.PageNo, (string?)r.Box, (string?)r.Note, (string)r.Source))
                .ToList();
            if (rows.Count == 0) return Plain();

            var text = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? PdfExtraction.PdfText(path) : "";

            var byKey = rows.GroupBy(r => r.CustomerKey).ToDictionary(g => g.Key, g => g.ToList());
            var own = OwnTaxIds();
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                candidates = TaxIdLike().Matches(text).Select(m => Digits(m.Value))
                    .Where(d => !own.Contains(d) && byKey.ContainsKey(d)).Distinct().ToList();
                if (candidates.Count == 0)
                {
                    var normText = NormName(text);
                    candidates = byKey.Keys.Where(k => k.StartsWith("N:") && k.Length > 6 && normText.Contains(k[2..])).ToList();
                }
            }
            if (candidates.Count == 0)
            {
                // Scanned (no text layer) or the text names nobody with rules: fall back to the customer an
                // earlier read of this document found -- by tax ID first, then by name.
                var t = Digits(knownTaxId);
                if (t.Length == 13 && !own.Contains(t) && byKey.ContainsKey(t)) candidates.Add(t);
                else if (NormName(knownName) is { Length: >= 5 } n && byKey.ContainsKey("N:" + n)) candidates.Add("N:" + n);
            }
            if (candidates.Count == 0) return Plain();   // read as before, positions still recorded

            // More than one customer on the page (rare): the one a person has set the most rules for, then the most rules.
            var key = candidates
                .OrderByDescending(k => byKey[k].Count(r => IsPerson(r.Source)))
                .ThenByDescending(k => byKey[k].Count)
                .First();
            var mine = byKey[key].OrderBy(r => Array.IndexOf(ReadHints.Fields, r.Field)).ToList();
            var rules = mine.Select(r => new ReadHintRule(r.Field, r.PageNo, ParseBox(r.Box), r.Note, r.Source)).ToList();
            var first = mine[0];
            var label = string.Join(" / ", new[] { first.CustomerName, first.CustomerTaxId }.Where(s => !string.IsNullOrWhiteSpace(s)));

            try
            {
                await db.ExecuteAsync("""
                    UPDATE ocr.CustomerReadHint SET UseCount = UseCount + 1
                    WHERE Module=@module AND SalesOrg=@org AND CustomerKey=@key AND Status='ACTIVE'
                    """, new { module, org, key });
            }
            catch (Exception ex) { log.LogDebug(ex, "[ReadHints] use count not updated"); }

            return new ReadHintRequest
            {
                PromptText = ReadHints.BuildPrompt(rules, label),
                FieldsSent = rules.Select(r => r.Field).ToList(),
                CustomerKey = key,
            };
        }
        catch (Exception ex)
        {
            log.LogWarning("[ReadHints] rules not loaded (run sql/39_customer_read_hints.sql?): {Message}", ex.Message);
            return Plain();
        }

        static ReadHintRequest Plain() => new() { PromptText = ReadHints.BuildPrompt([], null) };
    }

    /// <summary>A note for the document's confidence note when rules were used; empty otherwise.</summary>
    public static string NoteFor(ReadHintRequest? req) =>
        req is { FieldsSent.Count: > 0 } ? $"Read with this customer's reading rules ({req.FieldsSent.Count} field(s))" : "";

    // ---------------- after the read ----------------

    /// <summary>Keeps where the AI found each field on this read, and which rules it was given (upload and re-read).</summary>
    /// <summary>After the read, no AI (Megachem, 17 Nov 2026, แอคมี เคม เทค: Sold-to and Ship-to rules on the same
    /// address box, Ship-to came back empty -- the base prompt says never repeat Sold-to in Ship-to, and a
    /// photographed PO's first read has no rules at all). For each ACTIVE rule of this customer whose field
    /// came back EMPTY: if the rule's box sits on the same place (overlap >= 0.6) as a field of the same
    /// kind that has a value -- that field's rule box, or where the AI says it read it this time -- copy
    /// that value. Kinds: address (Sold-to / Ship-to), name (Customer / Ship-to name). Customer = the one
    /// the rules were sent for, else the customer this read found (so it also works on a first upload of
    /// a scanned PO). Returns the fields filled; best effort.</summary>
    public async Task<List<string>> ApplyRuleBoxesAsync(string module, string? salesOrg, Dictionary<string, object?> header,
        string? positionsJson, ReadHintRequest? sent)
    {
        var filled = new List<string>();
        if (!config.ReadHintsEnabled || module != "SO") return filled;
        try
        {
            var key = sent?.CustomerKey ?? KeyFor(header.GetStr("customerTaxId"), header.GetStr("customerName"));
            if (key == null) return filled;
            var org = (salesOrg ?? "").Trim();
            var rules = (await db.QueryAsync("""
                SELECT Field, PageNo, Box FROM ocr.CustomerReadHint
                WHERE Module=@module AND SalesOrg=@org AND CustomerKey=@key AND Status='ACTIVE' AND Box IS NOT NULL
                """, new { module, org, key }))
                .Select(r => (Field: (string)r.Field, Page: (int?)r.PageNo ?? 1, Box: ParseBox((string?)r.Box)))
                .Where(r => r.Box != null).ToList();
            if (rules.Count == 0) return filled;

            // Where the AI says it read each field this time.
            var found = new Dictionary<string, (int Page, int[] Box)>();
            if (!string.IsNullOrWhiteSpace(positionsJson))
            {
                using var pj = JsonDocument.Parse(positionsJson);
                foreach (var p in pj.RootElement.EnumerateObject())
                {
                    var page = p.Value.TryGetProperty("image", out var im) && im.TryGetInt32(out var iv) ? iv : 1;
                    var box = p.Value.TryGetProperty("box", out var bx) && bx.ValueKind == JsonValueKind.Array
                        ? bx.EnumerateArray().Select(v => v.TryGetInt32(out var n) ? n : -1).ToArray() : null;
                    if (ReadHints.ValidBox(box)) found[p.Name] = (page, box!);
                }
            }

            foreach (var r in rules)
            {
                if (header.GetStr(r.Field).Trim().Length > 0) continue;
                var kind = ReadHints.SameKind.FirstOrDefault(k => k.Contains(r.Field));
                if (kind == null) continue;
                foreach (var other in kind.Where(f => f != r.Field))
                {
                    var value = header.GetStr(other).Trim();
                    if (value.Length == 0) continue;
                    var sameAsRule = rules.Any(o => o.Field == other && o.Page == r.Page && ReadHints.Overlap(o.Box!, r.Box!) >= 0.6);
                    var sameAsRead = found.TryGetValue(other, out var seen) && seen.Page == r.Page && ReadHints.Overlap(seen.Box, r.Box!) >= 0.6;
                    if (!sameAsRule && !sameAsRead) continue;
                    header[r.Field] = value;
                    filled.Add(r.Field);
                    break;
                }
            }
        }
        catch (Exception ex) { log.LogWarning("[ReadHints] rule boxes not applied: {Message}", ex.Message); }
        return filled;
    }

    public static string NoteForFilled(List<string> filled) =>
        filled.Count == 0 ? "" : "Copied from the same place on the PO (reading rules): " + string.Join(", ", filled);

    public async Task SaveDocAsync(int docId, string module, ReadHintRequest? req, string? positionsJson)
    {
        if (req == null) return;
        try
        {
            await db.ExecuteAsync("""
                MERGE ocr.DocReadPosition AS t USING (SELECT @docId AS DocId) AS s ON t.DocId = s.DocId
                WHEN MATCHED THEN UPDATE SET Module=@module, CustomerKey=@key, PositionsJson=@positionsJson,
                     HintsUsed=@hints, LearnedAt=NULL, UpdatedAt=SYSDATETIME()
                WHEN NOT MATCHED THEN INSERT (DocId, Module, CustomerKey, PositionsJson, HintsUsed)
                     VALUES (@docId, @module, @key, @positionsJson, @hints);
                """, new
            {
                docId, module, key = req.CustomerKey, positionsJson,
                hints = req.FieldsSent.Count == 0 ? null : string.Join(",", req.FieldsSent),
            });
        }
        catch (Exception ex) { log.LogWarning("[ReadHints] positions of doc {DocId} not saved: {Message}", docId, ex.Message); }
    }

    // REMARK (17 Nov 2026): only MGT calls this today (ZohoSalesOrderController). GLC must call it in
    // DocumentsController post-so when newStatus == "POSTED" — see the checklist in sql/39_customer_read_hints.sql.
    /// <summary>Called when a Sales Order becomes POSTED: the positions of every field that has a value
    /// on the posted (person-checked) document become this customer's AUTO rules.</summary>
    public async Task LearnFromPostedAsync(int docId, DocumentRepository repo)
    {
        if (!config.ReadHintsEnabled) return;
        try
        {
            var pos = await db.QueryOneAsync("SELECT Module, PositionsJson FROM ocr.DocReadPosition WHERE DocId=@docId", new { docId });
            string? json = pos?.PositionsJson;
            if (string.IsNullOrWhiteSpace(json)) return;
            string module = pos!.Module;

            var doc = await repo.GetDocumentAsync(docId);
            var header = doc["header"] as Dictionary<string, object?> ?? new();
            var lineCount = (doc["lines"] as System.Collections.ICollection)?.Count ?? 0;
            var key = KeyFor(header.GetStr("customerTaxId"), header.GetStr("customerName"));
            if (key == null) return;
            var org = (await repo.GetCompanyAsync(docId) ?? "").Trim();
            var taxId = Digits(header.GetStr("customerTaxId")) is { Length: 13 } t ? t : null;
            var name = header.GetStr("customerName").Trim();

            using var parsed = JsonDocument.Parse(json);
            var learned = 0;
            foreach (var p in parsed.RootElement.EnumerateObject())
            {
                var field = p.Name;
                if (!ReadHints.Fields.Contains(field)) continue;
                var confirmed = field == "linesTable" ? lineCount > 0 : header.GetStr(field).Trim().Length > 0;
                if (!confirmed) continue;
                var page = p.Value.TryGetProperty("image", out var im) && im.TryGetInt32(out var iv) ? iv : 1;
                var box = p.Value.TryGetProperty("box", out var bx) && bx.ValueKind == JsonValueKind.Array
                    ? bx.EnumerateArray().Select(v => v.TryGetInt32(out var n) ? n : -1).ToArray() : null;
                if (!ReadHints.ValidBox(box)) continue;
                await db.ExecuteAsync("""
                    MERGE ocr.CustomerReadHint AS t
                    USING (SELECT @org AS SalesOrg, @module AS Module, @key AS CustomerKey, @field AS Field) AS s
                      ON t.SalesOrg=s.SalesOrg AND t.Module=s.Module AND t.CustomerKey=s.CustomerKey AND t.Field=s.Field
                    WHEN MATCHED AND t.Status='ACTIVE' AND t.Source='AUTO' THEN UPDATE SET
                         PageNo=@page, Box=@box, ConfirmCount=t.ConfirmCount+1, LastDocId=@docId,
                         CustomerTaxId=ISNULL(@taxId, t.CustomerTaxId), CustomerName=ISNULL(NULLIF(@name,''), t.CustomerName),
                         UpdatedBy='system', UpdatedAt=SYSDATETIME()
                    WHEN MATCHED AND t.Status='ACTIVE' THEN UPDATE SET ConfirmCount=t.ConfirmCount+1, LastDocId=@docId
                    WHEN NOT MATCHED THEN INSERT (SalesOrg, Module, CustomerKey, CustomerTaxId, CustomerName, Field, PageNo, Box,
                         Source, Status, ConfirmCount, LastDocId, UpdatedBy)
                         VALUES (@org, @module, @key, @taxId, NULLIF(@name,''), @field, @page, @box, 'AUTO', 'ACTIVE', 1, @docId, 'system');
                    """, new { org, module, key, field, page, box = BoxText(box!), docId, taxId, name });
                learned++;
            }
            await db.ExecuteAsync("UPDATE ocr.DocReadPosition SET LearnedAt=SYSDATETIME() WHERE DocId=@docId", new { docId });
            log.LogInformation("[ReadHints] doc {DocId}: {Count} position(s) learned for customer {Key}", docId, learned, key);
        }
        catch (Exception ex) { log.LogWarning("[ReadHints] could not learn from doc {DocId}: {Message}", docId, ex.Message); }
    }

    // ---------------- the "Reading rules" screen ----------------

    public sealed record RuleDto(string Field, int? PageNo, int[]? Box, string? Note, string Source, string Status,
        int ConfirmCount, int UseCount, string? UpdatedBy, DateTime UpdatedAt);

    /// <summary>The customer this document's rules are kept under (from the document's own customer fields).</summary>
    public async Task<(string? Key, string? TaxId, string Name, string Org)> CustomerOfAsync(int docId, DocumentRepository repo)
    {
        var doc = await repo.GetDocumentAsync(docId);
        var header = doc["header"] as Dictionary<string, object?> ?? new();
        var taxId = Digits(header.GetStr("customerTaxId"));
        var name = header.GetStr("customerName").Trim();
        return (KeyFor(taxId, name), taxId.Length == 13 ? taxId : null, name, (await repo.GetCompanyAsync(docId) ?? "").Trim());
    }

    public async Task<List<RuleDto>> RulesAsync(string org, string module, string key)
    {
        var rows = await db.QueryAsync("""
            SELECT Field, PageNo, Box, Note, Source, Status, ConfirmCount, UseCount, UpdatedBy, UpdatedAt
            FROM ocr.CustomerReadHint WHERE SalesOrg=@org AND Module=@module AND CustomerKey=@key
            """, new { org, module, key });
        return rows.Select(r => new RuleDto((string)r.Field, (int?)r.PageNo, ParseBox((string?)r.Box), (string?)r.Note,
                (string)r.Source, (string)r.Status, (int)r.ConfirmCount, (int)r.UseCount, (string?)r.UpdatedBy, (DateTime)r.UpdatedAt))
            .OrderBy(r => Array.IndexOf(ReadHints.Fields, r.Field)).ToList();
    }

    public async Task<(string? Positions, string? HintsUsed, string? TextKey)> DocPositionsAsync(int docId)
    {
        var r = await db.QueryOneAsync("SELECT PositionsJson, HintsUsed, CustomerKey FROM ocr.DocReadPosition WHERE DocId=@docId", new { docId });
        return r == null ? (null, null, null) : ((string?)r.PositionsJson, (string?)r.HintsUsed, (string?)r.CustomerKey);
    }

    /// <summary>A person sets / corrects a field's rule (Source = their role, ACTIVE). Box null = note-only.</summary>
    public Task SaveCsAsync(string org, string module, string key, string? taxId, string name, string field,
        int? page, int[]? box, string? note, string user, string source) =>
        db.ExecuteAsync("""
            MERGE ocr.CustomerReadHint AS t
            USING (SELECT @org AS SalesOrg, @module AS Module, @key AS CustomerKey, @field AS Field) AS s
              ON t.SalesOrg=s.SalesOrg AND t.Module=s.Module AND t.CustomerKey=s.CustomerKey AND t.Field=s.Field
            WHEN MATCHED THEN UPDATE SET PageNo=@page, Box=@box, Note=@note, Source=@source, Status='ACTIVE',
                 CustomerTaxId=ISNULL(@taxId, t.CustomerTaxId), CustomerName=ISNULL(NULLIF(@name,''), t.CustomerName),
                 UpdatedBy=@user, UpdatedAt=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (SalesOrg, Module, CustomerKey, CustomerTaxId, CustomerName, Field, PageNo, Box, Note,
                 Source, Status, UpdatedBy)
                 VALUES (@org, @module, @key, @taxId, NULLIF(@name,''), @field, @page, @box, @note, @source, 'ACTIVE', @user);
            """, new
        {
            org, module, key, taxId, name, field,
            page = box == null ? null : page, box = box == null ? null : BoxText(box),
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)],
            user, source,
        });

    /// <summary>Turn a field off (not sent, not learned again) or back on. A missing row is created
    /// DISABLED so AUTO cannot bring it back.</summary>
    public Task SetStatusAsync(string org, string module, string key, string? taxId, string name, string field, bool active, string user, string source) =>
        db.ExecuteAsync("""
            MERGE ocr.CustomerReadHint AS t
            USING (SELECT @org AS SalesOrg, @module AS Module, @key AS CustomerKey, @field AS Field) AS s
              ON t.SalesOrg=s.SalesOrg AND t.Module=s.Module AND t.CustomerKey=s.CustomerKey AND t.Field=s.Field
            WHEN MATCHED THEN UPDATE SET Status=@status, UpdatedBy=@user, UpdatedAt=SYSDATETIME()
            WHEN NOT MATCHED AND @status='DISABLED' THEN INSERT (SalesOrg, Module, CustomerKey, CustomerTaxId, CustomerName, Field,
                 Source, Status, UpdatedBy)
                 VALUES (@org, @module, @key, @taxId, NULLIF(@name,''), @field, @source, 'DISABLED', @user);
            """, new { org, module, key, taxId, name, field, status = active ? "ACTIVE" : "DISABLED", user, source });

    /// <summary>Forget a field's rule completely (AUTO may learn it again from the next posted document).</summary>
    public Task DeleteAsync(string org, string module, string key, string field) =>
        db.ExecuteAsync("DELETE FROM ocr.CustomerReadHint WHERE SalesOrg=@org AND Module=@module AND CustomerKey=@key AND Field=@field",
            new { org, module, key, field });

    // ---------------- helpers ----------------

    private static string BoxText(int[] b) => string.Join(",", b);

    public static int[]? ParseBox(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;
        var b = parts.Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();
        return ReadHints.ValidBox(b) ? b : null;
    }
}
