using System.Globalization;
using System.Text.Json;
using Dapper;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
using MgtOcr.Core.Json;
using MgtOcr.Data;
using MgtOcr.Sap;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// PO / GR / supplier-invoice reconciliation, written to ocr.GoodsReconciliation and
// ocr.ShippingReconciliation (sql: Create_GoodsReconciliation.sql / Create_ShippingReconciliation.sql).
//
// Everything is read from SAP — the purchase order (items, conditions, dates), the goods receipts
// (API_MATERIAL_DOCUMENT_SRV) and every invoice line already booked against the PO
// (A_SuplrInvcItemPurOrdRef) — so a row says what SAP holds, not what we meant to send.
// Each run INSERTs a fresh set of rows stamped CheckedAt; the *Latest views keep the newest.
//
//   POST /api/reconcile/run                      every PO named on an AP document (optionally ?days=30)
//   POST /api/reconcile/purchase-order/{po}      one PO
[ApiController]
[Route("api/reconcile")]
public class ReconciliationController(Db db, SapPurchaseOrderClient sapPo, SapClient sap, AppConfig config,
    ICurrentUserAccessor currentUser, ILogger<ReconciliationController> logger, DocumentRepository repo) : ControllerBase
{
    // The condition types configured for GLC (KUT-MM-PU-203 p.34), as the PO Reference tab names them.
    private static readonly Dictionary<string, string> ConditionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZFRE"] = "Freight", ["ZLH1"] = "Local Handling", ["ZED1"] = "Excise Duty", ["ZFE2"] = "Fee",
        ["ZINS"] = "Insurance", ["ZIM1"] = "Import Tax", ["ZOVS"] = "Oversea Charge", ["ZOTH"] = "Other Charges",
    };

    /// <summary>What the OCR document itself says about the shipment: the MatDoc number on the
    /// bundle, the shipping agent (PROMPT / PILOT / CHEETAH), and which form lines settle each
    /// condition. These were left NULL — the SAP side has none of them.</summary>
    private sealed record DocSide(string? MatDocNo, string? Agent, Dictionary<string, string> CostLinesByCond);

    private async Task<DocSide> DocSideAsync(int? docId)
    {
        var empty = new DocSide(null, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (docId is not int id) return empty;
        try
        {
            var doc = await repo.GetDocumentAsync(id);
            var header = doc.GetValueOrDefault("header") as Dictionary<string, object?> ?? new();
            string H(string k) => header.GetValueOrDefault(k)?.ToString()?.Trim() ?? "";
            var fileName = doc.GetValueOrDefault("fileName")?.ToString() ?? "";
            var matDoc = H("refMatDoc");
            if (matDoc.Length == 0)
            {
                var m = System.Text.RegularExpressions.Regex.Match(fileName, @"MatDoc_(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) matDoc = m.Groups[1].Value;
            }
            // "PROMPT LOGISTICS CO.,LTD." → PROMPT: the agent as the team names it.
            var vendorName = H("vendorName");
            var agent = vendorName.Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();

            var byCond = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (doc.GetValueOrDefault("lines") is System.Collections.IEnumerable lines)
                foreach (var raw in lines)
                {
                    if (raw is not Dictionary<string, object?> l) continue;
                    var extra = l.GetValueOrDefault("extra") as Dictionary<string, object?>;
                    var cond = (extra?.GetValueOrDefault("deliveryCondType") ?? extra?.GetValueOrDefault("condType"))?.ToString()?.Trim() ?? "";
                    var desc = l.GetValueOrDefault("desc")?.ToString()?.Trim() ?? "";
                    if (cond.Length == 0 || desc.Length == 0) continue;
                    if (!byCond.TryGetValue(cond, out var list)) byCond[cond] = list = [];
                    if (!list.Contains(desc)) list.Add(desc);
                }
            return new DocSide(matDoc.Length == 0 ? null : Cut(matDoc, 20), agent is null ? null : Cut(agent, 20),
                byCond.ToDictionary(kv => kv.Key, kv => Cut(string.Join(", ", kv.Value), 400), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RECON] could not read OCR document {DocId}", id);
            return empty;
        }
    }

    private static string Cut(string v, int max) => v.Length <= max ? v : v[..max];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // Conditions that are taxes or the overseas supplier's own charge: kept in the shipping table,
    // left out of landed cost, the same rule as TOTAL on FORM SHIPPING EXPENSE.
    private static readonly HashSet<string> NotLandedCost = new(StringComparer.OrdinalIgnoreCase) { "ZED1", "ZIM1", "ZOVS" };

    [HttpPost("run")]
    public async Task<IActionResult> RunAll([FromQuery] int? days = null)
    {
        var user = (await currentUser.RequireAsync()).AuditName;

        // Every AP document that names a purchase order — the PO lives in HeaderJson
        // (PurchasingOrder on the PO Reference tab, else the poRef the read found).
        var sql = "SELECT DocId, HeaderJson FROM ocr.Document WHERE Module IN ('AP','II') AND Status IN ('POSTED','PARTIAL')"
                  + (days is > 0 ? " AND CreatedAt >= DATEADD(day, -@days, SYSDATETIME())" : "");
        var docs = await db.QueryAsync(sql, new { days });
        var byPo = new Dictionary<string, List<int>>();
        foreach (var d in docs)
        {
            var po = PoOf((string?)d.HeaderJson);
            if (po.Length == 0) continue;
            if (!byPo.TryGetValue(po, out var list)) byPo[po] = list = [];
            list.Add((int)d.DocId);
        }

        var results = new List<object>();
        foreach (var (po, docIds) in byPo.OrderBy(kv => kv.Key))
        {
            try
            {
                var r = await ReconcilePoAsync(po, docIds.Max(), user);
                results.Add(new { purchaseOrder = po, docIds, r.GoodsRows, r.ShippingRows, r.GoodsNotMatching, r.ShippingNotMatching, r.Warnings });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[RECON] {Po} failed", po);
                results.Add(new { purchaseOrder = po, docIds, error = ex.Message });
            }
        }
        return Ok(new { purchaseOrders = byPo.Count, results });
    }

    [HttpPost("purchase-order/{purchaseOrder}")]
    public async Task<IActionResult> RunOne(string purchaseOrder, [FromQuery] int? docId = null)
    {
        var user = (await currentUser.RequireAsync()).AuditName;
        var po = (purchaseOrder ?? "").Trim();
        if (po.Length == 0) return BadRequest(new { detail = "Provide a purchase order number" });
        try
        {
            return Ok(await ReconcilePoAsync(po, docId, user));
        }
        catch (Exception ex)
        {
            throw new HttpApiException(502, $"Could not reconcile purchase order {po}: {ex.Message}");
        }
    }

    public record ReconResult(string PurchaseOrder, int GoodsRows, int ShippingRows,
        int GoodsNotMatching, int ShippingNotMatching, List<string> Warnings);

    private async Task<ReconResult> ReconcilePoAsync(string po, int? docId, string user)
    {
        if (!sapPo.Configured) throw new Exception("Sap:PurchaseOrder is not configured");
        var warnings = new List<string>();
        var env = config.SapSupplierInvoiceEnvironment;

        // ---------- PO ----------
        var items = await sapPo.ItemsAsync(po);
        if (items.Count == 0) throw new Exception($"SAP returned no items for PO {po}");
        var rate = (double)SapPurchaseOrderClient.RateOf(items);
        var head = (await sapPo.RawAsync($"A_PurchaseOrder('{po}')")).FirstOrDefault();
        var rawItems = await sapPo.RawAsync($"A_PurchaseOrderItem?$filter={Uri.EscapeDataString($"PurchaseOrder eq '{po}'")}");
        List<JsonElement> schedule = [];
        try { schedule = await sapPo.RawAsync($"A_PurchaseOrderScheduleLine?$filter={Uri.EscapeDataString($"PurchaseOrder eq '{po}'")}"); }
        catch (Exception ex) { warnings.Add("schedule lines: " + ex.Message); }

        var companyCode = Str(head, "CompanyCode") ?? "2000";
        var supplier = Str(head, "Supplier");
        var purchaseDate = Date(head, "PurchaseOrderDate");

        // ---------- GR ----------
        List<JsonElement> grItems = [];
        try { grItems = await sapPo.GoodsReceiptItemsAsync(po); }
        catch (Exception ex) { warnings.Add("goods receipts: " + ex.Message); }
        if (string.IsNullOrWhiteSpace(config.SapMaterialDocumentBaseUrl))
            warnings.Add("Sap:MaterialDocument is not configured — GR columns stay empty");

        // ---------- Invoice lines already in SAP ----------
        var invLines = new List<JsonElement>();
        var (invOk, invBody) = await sap.GetInvoiceLinesForPoAsync(po);
        if (invOk) invLines = Results(invBody);
        else warnings.Add("invoice lines: " + Trunc(invBody, 300));
        // Header of each invoice: status (A parked / 5 posted), invoicing party, reference.
        var invHeads = new Dictionary<string, (string? Status, string? Party, string? Reference)>();
        foreach (var key in invLines.Select(l => (Str(l, "SupplierInvoice") ?? "", Str(l, "FiscalYear") ?? "")).Distinct())
        {
            if (key.Item1.Length == 0) continue;
            var (ok, body) = await sap.GetSupplierInvoiceAsync(key.Item1, key.Item2);
            if (!ok) { warnings.Add($"invoice {key.Item1}: {Trunc(body, 200)}"); continue; }
            var h = Results(body).FirstOrDefault();
            invHeads[$"{key.Item1}|{key.Item2}"] = (Str(h, "SupplierInvoiceStatus"), Str(h, "InvoicingParty"), Str(h, "SupplierInvoiceIDByInvcgParty"));
        }
        (string? Status, string? Party, string? Reference) HeadOf(JsonElement l) =>
            invHeads.TryGetValue($"{Str(l, "SupplierInvoice")}|{Str(l, "FiscalYear")}", out var h) ? h : (null, null, null);

        bool SameItem(JsonElement el, string item) => ItemNo(Str(el, "PurchaseOrderItem")) == ItemNo(item);
        // Split valuation is a property of the material: once one receipt carries a valuation
        // type, every receipt of that material needs one.
        var splitMaterials = grItems.Where(g => !string.IsNullOrWhiteSpace(Str(g, "InventoryValuationType")))
            .Select(g => Str(g, "Material") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var docSide = await DocSideAsync(docId);
        var goodsRows = new List<object>();
        var shippingRows = new List<object>();
        var landedByItem = new Dictionary<int, double>();

        foreach (var it in items)
        {
            var raw = rawItems.FirstOrDefault(r => SameItem(r, it.Item));
            var gr = grItems.Where(g => SameItem(g, it.Item)).ToList();
            var receipts = gr.Where(g => !IsReversal(g)).ToList();
            var lastReceipt = receipts.OrderByDescending(g => Str(g, "MaterialDocumentYear") + Str(g, "MaterialDocument")).FirstOrDefault();
            double? grQty = gr.Count == 0 ? null : gr.Sum(g => Sign(g) * Num(g, "QuantityInEntryUnit"));
            double? grAmt = gr.Count == 0 ? null : gr.Sum(g => Sign(g) * Num(g, "TotalGoodsMvtAmtInCCCrcy"));
            var valuation = receipts.Select(g => Str(g, "InventoryValuationType")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            bool? split = receipts.Count == 0 ? null : splitMaterials.Contains(it.Material);
            var grDoc = lastReceipt.ValueKind == JsonValueKind.Object ? Str(lastReceipt, "MaterialDocument") : null;
            var grYear = lastReceipt.ValueKind == JsonValueKind.Object ? Str(lastReceipt, "MaterialDocumentYear") : null;
            var grPosting = lastReceipt.ValueKind == JsonValueKind.Object ? Date(lastReceipt, "PostingDate") : null;
            var delivery = schedule.Where(s => SameItem(s, it.Item)).Select(s => Date(s, "ScheduleLineDeliveryDate")).FirstOrDefault(d => d != null);

            var lines = invLines.Where(l => SameItem(l, it.Item)).ToList();
            var goodsLines = lines.Where(l => string.IsNullOrWhiteSpace(Str(l, "SuplrInvcDeliveryCostCndnType"))).ToList();
            var firstGoods = goodsLines.FirstOrDefault();
            var goodsHead = goodsLines.Count > 0 ? HeadOf(firstGoods) : (null, null, null);

            // ---------- Shipping: one row per delivery-cost condition on this item ----------
            foreach (var c in it.Conditions)
            {
                var plannedThb = string.Equals(c.AmountCurrency, "THB", StringComparison.OrdinalIgnoreCase) ? (double)c.Amount
                    : rate > 0 ? Math.Round((double)c.Amount * rate, 2)
                    : string.Equals(c.RateCurrency, "THB", StringComparison.OrdinalIgnoreCase) ? (double)c.RateValue
                    : (double?)null;
                var cLines = lines.Where(l => string.Equals(Str(l, "SuplrInvcDeliveryCostCndnType"), c.ConditionType, StringComparison.OrdinalIgnoreCase)).ToList();
                double? invoiced = cLines.Count == 0 ? null : Math.Round(cLines.Sum(l => Num(l, "SupplierInvoiceItemAmount")), 2);
                var first = cLines.FirstOrDefault();
                var h = cLines.Count > 0 ? HeadOf(first) : (null, null, null);
                // A condition settled by more than one invoice (split by tax code: PI-2608011 and
                // PI-2608011-2) — every reference and every SAP number, not only the first.
                var refs = cLines.Select(l => HeadOf(l).Reference).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
                var invNos = cLines.Select(l => Str(l, "SupplierInvoice")).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                if (!NotLandedCost.Contains(c.ConditionType))
                    landedByItem[ItemNo(it.Item)] = landedByItem.GetValueOrDefault(ItemNo(it.Item)) + (invoiced ?? plannedThb ?? 0);

                shippingRows.Add(new
                {
                    CheckedBy = user, SapEnvironment = env, CompanyCode = companyCode, DocId = docId,
                    MatDocNo = docSide.MatDocNo, Agent = docSide.Agent,
                    AgentInvoiceNo = refs.Count == 0 ? null : Cut(string.Join(", ", refs), 50),
                    PONumber = po, POItem = it.Item, MaterialCodeS4 = it.Material, MaterialName = it.Text,
                    POCurrency = it.Currency, POExchangeRate = rate > 0 ? (decimal?)rate : null,
                    ConditionType = c.ConditionType,
                    ConditionName = ConditionNames.TryGetValue(c.ConditionType, out var cn) ? cn : null,
                    ConditionVendor = (string?)null,
                    PlannedAmountPO = (decimal?)c.Amount, PlannedAmountTHB = (decimal?)plannedThb,
                    GRMaterialDocument = grDoc, GRYear = grYear, GRPostingDate = grPosting,
                    IsSplitValuation = split, ValuationType = valuation,
                    AccruedAmountTHB = (decimal?)null, AccrualGLAccount = (string?)null,
                    InvoicingParty = h.Party, InvoicedAmountTHB = (decimal?)invoiced,
                    CostLines = docSide.CostLinesByCond.TryGetValue(c.ConditionType, out var cl) ? cl : null,
                    SupplierInvoice = cLines.Count > 0 ? Str(first, "SupplierInvoice") : null,
                    SupplierInvoiceYear = cLines.Count > 0 ? Str(first, "FiscalYear") : null,
                    SupplierInvoiceStatus = h.Status, SapInvoiceLineAmount = (decimal?)invoiced,
                    Remark = invNos.Count > 1 ? Cut($"{cLines.Count} invoice lines: {string.Join(", ", invNos)}", 500)
                        : cLines.Count > 1 ? $"{cLines.Count} invoice lines" : null,
                });
            }

            goodsRows.Add(new
            {
                CheckedBy = user, SapEnvironment = env, DocId = docId, CompanyCode = companyCode,
                PONumber = po, POItem = it.Item, PurchaseDate = purchaseDate, DeliveryDate = delivery,
                SupplierCode = (string?)null, SupplierCodeS4 = supplier, SupplierName = (string?)null,
                MaterialCode = it.Material, MaterialCodeS4 = it.Material, MaterialName = it.Text,
                MaterialGroup = Str(raw, "MaterialGroup"), CurrencyCode = it.Currency,
                ExchangeRate = rate > 0 ? (decimal?)rate : null,
                POQty = (decimal?)it.Quantity, UnitOfMeasure = it.Unit, POPricePerUnit = (decimal?)it.NetPrice,
                POTotalOrderPrice = (decimal?)Math.Round(it.Quantity * it.NetPrice, 2),
                GRMaterialDocument = grDoc, GRYear = grYear, GRItem = lastReceipt.ValueKind == JsonValueKind.Object ? Str(lastReceipt, "MaterialDocumentItem") : null,
                GRPostingDate = grPosting, GRMovementType = lastReceipt.ValueKind == JsonValueKind.Object ? Str(lastReceipt, "GoodsMovementType") : null,
                GRQty = (decimal?)grQty, GRAmountTHB = grAmt is null ? null : (decimal?)Math.Round(grAmt.Value, 2),
                IsSplitValuation = split, ValuationType = valuation,
                InvoiceQty = goodsLines.Count == 0 ? null : (decimal?)goodsLines.Sum(l => Num(l, "QuantityInPurchaseOrderUnit")),
                InvoiceAmount = goodsLines.Count == 0 ? null : (decimal?)Math.Round(goodsLines.Sum(l => Num(l, "SupplierInvoiceItemAmount")), 2),
                InvoiceCurrency = goodsLines.Count == 0 ? null : Str(firstGoods, "DocumentCurrency"),
                SupplierInvoice = goodsLines.Count == 0 ? null : Str(firstGoods, "SupplierInvoice"),
                SupplierInvoiceStatus = goodsHead.Status,
                LandedCostAmountTHB = landedByItem.TryGetValue(ItemNo(it.Item), out var lc) ? (decimal?)Math.Round(lc, 2) : null,
                Remark = goodsLines.Count > 1 ? $"{goodsLines.Count} invoice lines" : null,
            });
        }

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            INSERT ocr.GoodsReconciliation
              (CheckedBy,SapEnvironment,DocId,CompanyCode,PONumber,POItem,PurchaseDate,DeliveryDate,
               SupplierCode,SupplierCodeS4,SupplierName,MaterialCode,MaterialCodeS4,MaterialName,MaterialGroup,
               CurrencyCode,ExchangeRate,POQty,UnitOfMeasure,POPricePerUnit,POTotalOrderPrice,
               GRMaterialDocument,GRYear,GRItem,GRPostingDate,GRMovementType,GRQty,GRAmountTHB,IsSplitValuation,ValuationType,
               InvoiceQty,InvoiceAmount,InvoiceCurrency,SupplierInvoice,SupplierInvoiceStatus,LandedCostAmountTHB,Remark)
            VALUES
              (@CheckedBy,@SapEnvironment,@DocId,@CompanyCode,@PONumber,@POItem,@PurchaseDate,@DeliveryDate,
               @SupplierCode,@SupplierCodeS4,@SupplierName,@MaterialCode,@MaterialCodeS4,@MaterialName,@MaterialGroup,
               @CurrencyCode,@ExchangeRate,@POQty,@UnitOfMeasure,@POPricePerUnit,@POTotalOrderPrice,
               @GRMaterialDocument,@GRYear,@GRItem,@GRPostingDate,@GRMovementType,@GRQty,@GRAmountTHB,@IsSplitValuation,@ValuationType,
               @InvoiceQty,@InvoiceAmount,@InvoiceCurrency,@SupplierInvoice,@SupplierInvoiceStatus,@LandedCostAmountTHB,@Remark)
            """, goodsRows, tx);
        if (shippingRows.Count > 0)
            await conn.ExecuteAsync("""
                INSERT ocr.ShippingReconciliation
                  (CheckedBy,SapEnvironment,CompanyCode,DocId,MatDocNo,Agent,AgentInvoiceNo,PONumber,POItem,
                   MaterialCodeS4,MaterialName,POCurrency,POExchangeRate,ConditionType,ConditionName,ConditionVendor,
                   PlannedAmountPO,PlannedAmountTHB,GRMaterialDocument,GRYear,GRPostingDate,IsSplitValuation,ValuationType,
                   AccruedAmountTHB,AccrualGLAccount,InvoicingParty,InvoicedAmountTHB,CostLines,
                   SupplierInvoice,SupplierInvoiceYear,SupplierInvoiceStatus,SapInvoiceLineAmount,Remark)
                VALUES
                  (@CheckedBy,@SapEnvironment,@CompanyCode,@DocId,@MatDocNo,@Agent,@AgentInvoiceNo,@PONumber,@POItem,
                   @MaterialCodeS4,@MaterialName,@POCurrency,@POExchangeRate,@ConditionType,@ConditionName,@ConditionVendor,
                   @PlannedAmountPO,@PlannedAmountTHB,@GRMaterialDocument,@GRYear,@GRPostingDate,@IsSplitValuation,@ValuationType,
                   @AccruedAmountTHB,@AccrualGLAccount,@InvoicingParty,@InvoicedAmountTHB,@CostLines,
                   @SupplierInvoice,@SupplierInvoiceYear,@SupplierInvoiceStatus,@SapInvoiceLineAmount,@Remark)
                """, shippingRows, tx);
        await tx.CommitAsync();

        // How many of the rows just written do not match, read back from the computed column.
        var goodsBad = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ocr.vw_GoodsReconciliationLatest WHERE PONumber=@po AND SapEnvironment=@env AND MatchStatus<>'MATCH'", new { po, env });
        var shipBad = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ocr.vw_ShippingReconciliationLatest WHERE PONumber=@po AND SapEnvironment=@env AND MatchStatus<>'MATCH'", new { po, env });
        return new ReconResult(po, goodsRows.Count, shippingRows.Count, goodsBad, shipBad, warnings);
    }

    // ---------- helpers ----------
    private static string PoOf(string? headerJson)
    {
        try
        {
            var h = JsonBodyHelpers.Unwrap(JsonSerializer.Deserialize<Dictionary<string, object?>>(
                string.IsNullOrWhiteSpace(headerJson) ? "{}" : headerJson) ?? new());
            var po = Export.SupplierInvoicePayload.PoNumber(h).Trim();
            // A purchase-order number is ten digits; what the read found on a form is sometimes not one.
            return po.Length == 10 && po.All(char.IsDigit) ? po : "";
        }
        catch (JsonException) { return ""; }
    }

    private static List<JsonElement> Results(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var d = doc.RootElement.TryGetProperty("d", out var dd) ? dd : doc.RootElement;
            if (d.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Select(e => e.Clone()).ToList();
            return d.ValueKind == JsonValueKind.Object ? [d.Clone()] : [];
        }
        catch (JsonException) { return []; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind is JsonValueKind.Number ? p.ToString() : null
            : null;

    private static double Num(JsonElement el, string name) =>
        double.TryParse(Str(el, name), NumberStyles.Any, Inv, out var d) ? d : 0;

    // OData V2 dates: "/Date(1785801600000)/".
    private static DateTime? Date(JsonElement el, string name)
    {
        var s = Str(el, name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"-?\d+");
        if (s.StartsWith("/Date(") && m.Success && long.TryParse(m.Value, out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.Date;
        return DateTime.TryParse(s, Inv, DateTimeStyles.AssumeUniversal, out var d) ? d.Date : null;
    }

    private static int ItemNo(string? s) => int.TryParse((s ?? "").Trim(), out var n) ? n : -1;

    // A reversal (102, 122 …) is posted on the credit side; it takes the receipt back.
    private static bool IsReversal(JsonElement g) =>
        string.Equals(Str(g, "DebitCreditCode"), "H", StringComparison.OrdinalIgnoreCase)
        || Str(g, "GoodsMovementType") is "102" or "122" or "162";
    private static double Sign(JsonElement g) => IsReversal(g) ? -1 : 1;

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
