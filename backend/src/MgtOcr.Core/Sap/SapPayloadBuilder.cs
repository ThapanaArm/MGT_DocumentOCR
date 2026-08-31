using System.Globalization;
using MgtOcr.Core.Config;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Core.Sap;

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
                items.Add(item);
            }

            return new Dictionary<string, object?>
            {
                ["_target"] = SoEndpoint,
                ["SalesOrderType"] = "OR",
                ["SalesOrganization"] = string.IsNullOrEmpty(c.GetStr("SalesOrg")) ? "1000" : c.GetStr("SalesOrg"),
                ["DistributionChannel"] = string.IsNullOrEmpty(c.GetStr("DistChannel")) ? "10" : c.GetStr("DistChannel"),
                ["OrganizationDivision"] = string.IsNullOrEmpty(c.GetStr("Division")) ? "00" : c.GetStr("Division"),
                ["SoldToParty"] = Key(customer),
                ["PurchaseOrderByCustomer"] = header.Get("poNo"),
                ["CustomerPurchaseOrderDate"] = header.Get("poDate"),
                ["RequestedDeliveryDate"] = header.Get("deliveryDate"),
                ["TransactionCurrency"] = string.IsNullOrEmpty(header.GetStr("currency")) ? "THB" : header.GetStr("currency"),
                ["CustomerPaymentTerms"] = c.GetStr("PaymentTerms"),
                ["IncotermsClassification"] = header.GetStr("incoterms"),
                ["to_Partner"] = new List<object> { new Dictionary<string, object?> { ["PartnerFunction"] = "SH", ["Customer"] = Key(shipTo) } },
                ["to_Item"] = items,
                ["_source"] = source,
            };
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
            ["DocumentDate"] = header.Get("invoiceDate"),
            ["PostingDate"] = header.Get("postingDate") ?? header.Get("invoiceDate"),
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
