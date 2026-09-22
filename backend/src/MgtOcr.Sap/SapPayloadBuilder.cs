using System.Globalization;
using MgtOcr.Core;
using MgtOcr.Core.Config;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Sap;

// Ported from app/sap.py's build_payload() (lines 39-113) — bug-for-bug per the approved migration
// plan. In particular: only TOP-LEVEL "_"-prefixed keys are stripped before the live SAP POST
// (see SapClient.Post), so nested "_internalMaterial"/"_isoUnit"/"_docQuantity"/"_uomFactor" inside
// line-item arrays DO leak into the real request body, while "_wht" (top-level) does NOT — meaning
// withholding tax is computed/logged/simulated but never actually sent to SAP today. Preserved
// deliberately, not fixed.
public static class SapPayloadBuilder
{
    public const string SoEndpoint = "API_SALES_ORDER_SRV/A_SalesOrder";
    public const string ApEndpoint = "API_SUPPLIERINVOICE_PROCESS_SRV/A_SupplierInvoice";

    private static string Iso(Dictionary<string, object?>? mapline) =>
        (mapline.Get("uom") as Dictionary<string, object?>).GetStr("iso");

    // Falls back to the internal code if no SAP code was resolved — normally mapping won't let a
    // document pass without one, so this fallback should be unreachable in practice.
    private static string Key(Dictionary<string, object?>? mapline)
    {
        var sapCode = mapline.GetStr("sapCode");
        return !string.IsNullOrEmpty(sapCode) ? sapCode : mapline.GetStr("code");
    }

    private static (double Qty, string Uom, double Factor) QtyUom(Dictionary<string, object?>? mapline, Dictionary<string, object?> line)
    {
        var u = mapline.Get("uom") as Dictionary<string, object?>;
        var status = u.GetStr("status");
        if ((status == "ok" || status == "convert") && !string.IsNullOrEmpty(u.GetStr("sapUom")))
        {
            var factor = Num(u.Get("factor"));
            return (Num(u.Get("sapQty")), u.GetStr("sapUom"), factor != 0 ? factor : 1.0);
        }
        return (Num(line.Get("qty")), line.GetStr("uom"), 1.0);
    }

    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // SAP OData v2 serializes Edm.DateTime properties (CustomerPurchaseOrderDate,
    // RequestedDeliveryDate, DocumentDate, PostingDate, ...) as "/Date(<ms since Unix epoch,
    // UTC>)/". Sending the raw "yyyy-MM-dd" strings we hold in the header triggers
    // CX_SY_CONVERSION_NO_DATE_TIME, so every date field goes through here. Returns null for a
    // blank or unparseable value so the property is omitted and SAP can apply its own default
    // rather than erroring.
    private static string? ODataDate(object? value)
    {
        DateTime dt;
        if (value is DateTime d)
        {
            dt = d.Kind == DateTimeKind.Utc ? d : d.ToUniversalTime();
        }
        else
        {
            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            {
                return null;
            }
        }
        var ms = (long)(dt - UnixEpoch).TotalMilliseconds;
        return $"/Date({ms})/";
    }

    public static Dictionary<string, object?> BuildPayload(AppConfig config, string module, Dictionary<string, object?> header,
        List<Dictionary<string, object?>> lines, Dictionary<string, object?> mapres, Dictionary<string, object?>? partnerMaster,
        Dictionary<string, object?>? source = null)
    {
        source ??= new();
        var resHeader = (Dictionary<string, object?>)mapres["header"]!;
        var resLines = (List<Dictionary<string, object?>>)mapres["lines"]!;

        if (module == "SO")
        {
            var c = partnerMaster ?? new();
            var customer = resHeader.Get("customer") as Dictionary<string, object?>;
            var shipTo = resHeader.Get("shipTo") as Dictionary<string, object?>;
            var salesOrg = string.IsNullOrEmpty(c.GetStr("SalesOrg")) ? "1000" : c.GetStr("SalesOrg");
            var currency = string.IsNullOrEmpty(header.GetStr("currency")) ? "THB" : header.GetStr("currency");
            // Per the "Sales Order Processing" training manual (GLC section): GLC pricing is not
            // fully derived from condition records the way MGT's is, so GLC lines need a manual
            // Gross Price condition (VA01 Conditions tab) sent explicitly. Condition type comes
            // from config (Sap:SalesOrder:PriceConditionType, default "ZPR0") rather than being
            // hardcoded — confirmed correct against this tenant's own working Excel/Zoho SAP
            // integration (SalesOrderImportJob.BuildCreateBody uses the same "to_PricingElement"
            // shape), which also keeps it as a setting rather than a literal for the same reason.
            // MGT keeps relying on its existing condition records (nothing added for it here).
            // Compared by SalesOrganization against config.Companies rather than hardcoding
            // "2000", so this still works if that code ever changes in appsettings.json.
            var isGlc = config.CompanyForSalesOrg(salesOrg)?.Name == "GLC";

            // First non-empty of the three (header override -> master -> default), used for the
            // sales-area fields the GLC Customer card can override (Channel/Division).
            static string Pick(string? a, string? b, string def) =>
                !string.IsNullOrEmpty(a) ? a! : (!string.IsNullOrEmpty(b) ? b! : def);

            var items = new List<object>();
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var mapline = resLines[i];
                var (qty, uom, factor) = QtyUom(mapline, l);
                var item = new Dictionary<string, object?>
                {
                    ["SalesOrderItem"] = ((i + 1) * 10).ToString(),
                    ["Material"] = Key(mapline),
                    ["RequestedQuantity"] = F3(qty),
                    ["RequestedQuantityUnit"] = uom,
                    ["NetAmount"] = F2(Num(l.Get("amount"))),
                    ["MaterialByCustomer"] = l.GetStr("extCode"),
                    ["_internalMaterial"] = mapline.GetStr("code"),
                    ["_isoUnit"] = Iso(mapline), // written twice in Python (harmless dict-literal duplicate) — once here is equivalent
                };
                if (factor != 1)
                {
                    item["_docQuantity"] = $"{FormatGNum(Num(l.Get("qty")))} {l.GetStr("uom")}";
                    item["_uomFactor"] = factor;
                }
                // Item-level SD Sales Employee custom field (YY1_SDSalesEmployeeI_SDI). Per the user,
                // each line records which sales person handled/verified THAT line's mapping. Person ID
                // (SAP custom value, e.g. "9980000002") is stored per line in the line's `extra` bag
                // (extra.salesEmployee) so it survives the DB round-trip — GetDocumentAsync only keeps
                // typed columns + ExtraJson, so a bare top-level line field would be lost by send time.
                // It's pre-filled from the Sales Order history of this customer+material and picked/
                // edited by the CS. Priority: per-line extra -> a fresh (not-yet-saved) top-level line
                // value -> the document-level fallback (header.salesEmployee). Sent only when set, so a
                // blank never overwrites anything in SAP.
                var lineExtra = l.Get("extra") as Dictionary<string, object?>;
                var lineSalesEmp = Pick(lineExtra?.GetStr("salesEmployee"),
                    Pick(l.GetStr("salesEmployee"), header.GetStr("salesEmployee"), ""), "");
                if (!string.IsNullOrWhiteSpace(lineSalesEmp))
                    item["YY1_SDSalesEmployeeI_SDI"] = lineSalesEmp;
                // Item Note 1 (SD item long text). OCR-prefilled from the PO into
                // extra.itemNote1 (editable by the CS), sent via the to_Item -> to_Text
                // navigation as text ID ZI01 (Sap:SalesOrder:ItemNoteTextId). Emitted once
                // per configured language (Sap:SalesOrder:ItemNoteLanguages, default
                // "TH,EN") so the note shows whatever the SAP logon language. Sent only when
                // a note exists, so a blank line never posts an empty text. NOTE: the exact
                // Language key format this service accepts (ISO "TH"/"EN" vs SAP internal
                // "2"/"E") is NOT yet verified against this tenant's $metadata -- same
                // caveat as to_PricingElement below; adjust the config if SAP rejects it.
                var itemNote1 = lineExtra?.GetStr("itemNote1");
                if (!string.IsNullOrWhiteSpace(itemNote1) && !string.IsNullOrWhiteSpace(config.SapSalesOrderItemNoteTextId))
                {
                    var texts = new List<object>();
                    foreach (var lang in config.SapSalesOrderItemNoteLanguages
                                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        texts.Add(new Dictionary<string, object?>
                        {
                            ["Language"] = lang,
                            ["LongTextID"] = config.SapSalesOrderItemNoteTextId,
                            ["LongText"] = itemNote1,
                        });
                    }
                    if (texts.Count > 0) item["to_Text"] = texts;
                }
                // Manual Price Gross (ZPR0) — GLC only, and only when a unit price was actually
                // read/entered for the line. NOTE: entity/nav-property name
                // (A_SalesOrderItemPrElement via "to_PricingElement") not yet verified against
                // this tenant's real $metadata — same caveat as SapBusinessPartnerClient; adjust
                // if SAP rejects this shape. ZDC0/ZCD1 (item discount) are not wired yet — no
                // discount field exists on the line today; add one here if/when the UI gets one.
                var unitPrice = Num(l.Get("price"));
                if (isGlc && unitPrice > 0 && !string.IsNullOrWhiteSpace(config.SapSalesOrderPriceConditionType))
                {
                    item["to_PricingElement"] = new List<object>
                    {
                        new Dictionary<string, object?>
                        {
                            ["ConditionType"] = config.SapSalesOrderPriceConditionType,
                            ["ConditionRateValue"] = F2(unitPrice),
                            ["ConditionCurrency"] = currency,
                        },
                    };
                }
                items.Add(item);
            }

            // Minimal payload matching the tenant's proven working SO integration
            // (SapUomSyncService.BuildDeepInsert). IncotermsClassification and CustomerPaymentTerms
            // are intentionally NOT sent — SAP derives both from the customer master, and sending
            // the raw document incoterms text ("Delivered Duty Paid CHONBURI") overflowed the
            // 3-char field (/IWCOR/CX_DS_EDM_FACET_ERROR). CustomerPurchaseOrderDate is kept (worth
            // recording).
            var soPayload = new Dictionary<string, object?>
            {
                ["_target"] = SoEndpoint,
                ["SalesOrderType"] = "OR",
                ["SalesOrganization"] = salesOrg,
                // Distribution Channel / Division: prefer the sales area the person picked on the GLC
                // Customer card (persisted on the header as distChannel/division), then the local
                // customer master, then the tenant defaults. This is how a customer with more than one
                // SAP sales area gets the RIGHT channel/division onto the order.
                ["DistributionChannel"] = Pick(header.GetStr("distChannel"), c.GetStr("DistChannel"), "10"),
                ["OrganizationDivision"] = Pick(header.GetStr("division"), c.GetStr("Division"), "00"),
                ["SoldToParty"] = Key(customer),
                // "-" when the document has no PO number, rather than sending blank/null to SAP.
                ["PurchaseOrderByCustomer"] = string.IsNullOrWhiteSpace(header.GetStr("poNo")) ? "-" : header.GetStr("poNo"),
                ["CustomerPurchaseOrderDate"] = ODataDate(header.Get("poDate")),
                ["RequestedDeliveryDate"] = ODataDate(header.Get("deliveryDate")),
                ["TransactionCurrency"] = currency,
                ["to_Item"] = items,
                ["_source"] = source,
            };
            // The SH (ship-to) partner is sent ONLY when a ship-to was actually resolved. Per the
            // user, many GLC orders have no separate ship-to and don't need one sent — and posting a
            // partner with an empty Customer makes SAP reject the order. Omitting to_Partner lets SAP
            // default the ship-to to the Sold-to party, which is exactly what's wanted here. When OCR
            // did resolve a ship-to that differs from the sold-to, it's sent as before.
            var shipToKey = Key(shipTo);
            if (!string.IsNullOrWhiteSpace(shipToKey))
                soPayload["to_Partner"] = new List<object> { new Dictionary<string, object?> { ["PartnerFunction"] = "SH", ["Customer"] = shipToKey } };
            // Sales Group: sent only when the person picked a sales area on the GLC Customer card
            // (persisted on the header as salesGroup). A_SalesOrder.SalesGroup is a standard field;
            // omitted when blank so SAP derives it from the customer master as before.
            var salesGroup = header.GetStr("salesGroup");
            if (!string.IsNullOrWhiteSpace(salesGroup))
                soPayload["SalesGroup"] = salesGroup;
            // SD Sales Employee custom field, HEADER extension (YY1_SDSalesEmployee_SDH). The per-line
            // item field YY1_SDSalesEmployeeI_SDI above is the primary record; this header field is
            // sent only when the CS set a single document-level sales employee (header.salesEmployee)
            // — e.g. the whole order handled by one person. Omitted otherwise so a blank never
            // overwrites anything in SAP, and SAP still derives it from the customer master as before.
            var salesEmployee = header.GetStr("salesEmployee");
            if (!string.IsNullOrWhiteSpace(salesEmployee))
                soPayload["YY1_SDSalesEmployee_SDH"] = salesEmployee;
            return soPayload;
        }

        var v = partnerMaster ?? new();
        var vendor = resHeader.Get("vendor") as Dictionary<string, object?>;
        var poRef = header.GetStr("poRef");

        var poItems = new List<object>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var mapline = resLines[i];
            var (qty, uom, factor) = QtyUom(mapline, l);
            var item = new Dictionary<string, object?>
            {
                ["SupplierInvoiceItem"] = (i + 1).ToString(),
                ["PurchaseOrder"] = poRef,
                ["PurchaseOrderItem"] = !string.IsNullOrEmpty(poRef) ? ((i + 1) * 10).ToString() : "",
                ["Material"] = Key(mapline),
                ["Plant"] = config.SapDefaultPlant,
                ["QuantityInPurchaseOrderUnit"] = F3(qty),
                ["PurchaseOrderQuantityUnit"] = uom,
                ["SupplierInvoiceItemAmount"] = F2(Num(l.Get("amount"))),
                ["TaxCode"] = "V7",
                ["_internalMaterial"] = mapline.GetStr("code"),
                ["_isoUnit"] = Iso(mapline),
            };
            if (factor != 1)
            {
                item["_docQuantity"] = $"{FormatGNum(Num(l.Get("qty")))} {l.GetStr("uom")}";
                item["_uomFactor"] = factor;
            }
            poItems.Add(item);
        }

        var payload = new Dictionary<string, object?>
        {
            ["_target"] = ApEndpoint,
            ["CompanyCode"] = config.SapCompanyCode,
            ["DocumentDate"] = ODataDate(header.Get("invoiceDate")),
            ["PostingDate"] = ODataDate(header.Get("postingDate")) ?? ODataDate(header.Get("invoiceDate")),
            ["InvoicingParty"] = Key(vendor),
            ["SupplierInvoiceIDByInvcgParty"] = header.Get("invoiceNo"),
            ["DocumentCurrency"] = string.IsNullOrEmpty(header.GetStr("currency")) ? "THB" : header.GetStr("currency"),
            ["InvoiceGrossAmount"] = F2(Num(header.Get("totalAmount"))),
            ["PaymentTerms"] = v.GetStr("PaymentTerms"),
            ["TaxIsCalculatedAutomatically"] = false,
            ["to_SuplrInvcItemPurOrdRef"] = poItems,
            ["to_SuplrInvcTax"] = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["TaxCode"] = "V7",
                    ["TaxBaseAmount"] = F2(Num(header.Get("subTotal"))),
                    ["TaxAmount"] = F2(Num(header.Get("vatAmount"))),
                },
            },
            ["_source"] = source,
        };
        if (Num(header.Get("whtAmount")) > 0)
        {
            payload["_wht"] = new Dictionary<string, object?>
            {
                ["WithholdingTaxType"] = string.IsNullOrEmpty(v.GetStr("WhtCode")) ? "53" : v.GetStr("WhtCode"),
                ["WithholdingTaxAmount"] = F2(Num(header.Get("whtAmount"))),
            };
        }
        return payload;
    }

    // "%g" formatting for the human-readable _docQuantity note (e.g. "12 KG") — not sent to SAP.
    private static string FormatGNum(double d)
    {
        if (d == 0) return "0";
        return d.ToString("G6", CultureInfo.InvariantCulture).Replace("E", "e");
    }
}
