using System.Globalization;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Core.Mapping;

// Ported function-for-function from app/mapping.py (438 lines) per the approved migration plan —
// same thresholds, same match order, same output shape. Header/lines/manual are plain
// Dictionary<string,object?> trees (as produced by JsonBodyHelpers.Unwrap or loaded from stored
// HeaderJson/ExtraJson), mirroring Python's untyped dict handling exactly.
public static class MappingEngine
{
    public const double ThAuto = 0.82;
    public const double ThMatScope = 0.85;
    public const double ThMatMaster = 0.93;
    public const double ThShipTo = 0.70;
    public const double ThSuggest = 0.45;

    private static readonly Dictionary<string, string> UomIso = new()
    {
        ["KG"] = "KGM", ["G"] = "GRM", ["TON"] = "TNE", ["L"] = "LTR", ["ML"] = "MLT", ["M"] = "MTR",
        ["EA"] = "PCE", ["PC"] = "PCE", ["PCS"] = "PCE", ["BOX"] = "BX", ["BAG"] = "BG", ["DRUM"] = "DR", ["AU"] = "ACT",
    };

    private static string Dash(string? s) => string.IsNullOrEmpty(s) ? "-" : s;
    private static string Money(double v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Qty3(double v) => TrimTrailingZeros(v.ToString("#,##0.000", CultureInfo.InvariantCulture));

    private static string TrimTrailingZeros(string s) => s.TrimEnd('0').TrimEnd('.');

    private static string FormatG(double d)
    {
        if (d == 0) return "0";
        return d.ToString("G6", CultureInfo.InvariantCulture).Replace("E", "e");
    }

    private static Dictionary<string, object?> R(string status, string code = "", string text = "", string method = "", List<string>? cands = null) =>
        new()
        {
            ["status"] = status, ["code"] = code, ["text"] = text ?? "", ["sapCode"] = "",
            ["method"] = method ?? "", ["cands"] = cands ?? new List<string>(),
            ["doc"] = new List<object>(), ["sap"] = new List<object>(),
        };

    private static Dictionary<string, object?> Fld(string label, object? value, bool? match = null) =>
        new() { ["label"] = label, ["value"] = value?.ToString() ?? "", ["match"] = match };

    // Every confirmed address sub-field on a matched Customer/ShipTo master row, as individual
    // Fld() rows -- added 2026-09-22 so the persisted "main table" (SideList) shows the same
    // level of detail as the live SAP/Zoho search panels, instead of one collapsed "Address"
    // string (Ship-to) or nothing at all (Customer/Sold-to, which never showed an address here).
    // Field order/labels mirror the frontend's AddressFieldRows(). Empty sub-fields are skipped
    // entirely (not shown as blank rows) -- same behavior as AddressFieldRows on the frontend.
    private static List<object> AddressFlds(Dictionary<string, object?>? row)
    {
        if (row == null) return new List<object>();
        var pairs = new (string Label, string Col)[]
        {
            ("House Number", "HouseNumber"), ("Street", "Street"), ("Street 2", "Street2"),
            ("Street 3", "Street3"), ("Street 4", "Street4"), ("Street 5", "Street5"),
            ("District", "District"), ("City", "City"), ("Difference City", "DifferenceCity"),
            ("Post Code", "PostCode"), ("Country / Reg", "CountryReg"),
        };
        var result = new List<object>();
        foreach (var (label, col) in pairs)
        {
            var v = row.GetStr(col);
            if (!string.IsNullOrWhiteSpace(v)) result.Add(Fld(label, v));
        }
        return result;
    }

    private static string SapKey(Dictionary<string, object?>? rec, string field) => (rec.Get(field) ?? "").ToString()?.Trim() ?? "";

    private static bool Same(object? a, object? b)
    {
        var sa = a?.ToString(); var sb = b?.ToString();
        return !string.IsNullOrEmpty(sa) && !string.IsNullOrEmpty(sb) && Norm(sa) == Norm(sb);
    }

    // _like(): flexible name compare — very similar => tick, else no symbol at all (None), never a red X.
    private static bool? Like(object? a, object? b)
    {
        var sa = a?.ToString(); var sb = b?.ToString();
        return !string.IsNullOrEmpty(sa) && !string.IsNullOrEmpty(sb) && Sim(sa, sb) >= 0.85 ? true : null;
    }

    private static bool SameTax(object? a, object? b)
    {
        var da = Digits(a);
        return da.Length > 0 && da == Digits(b);
    }

    public static (Dictionary<string, object?>? Hit, string Method, double Score, List<string> Cands) MatchPartner(
        List<Dictionary<string, object?>> rows, object? taxId, object? name, string codeKey, string[] nameKeys)
    {
        var t = Digits(taxId);
        if (t.Length >= 10)
        {
            var byTax = rows.FirstOrDefault(r => Digits(r.Get("TaxId")) == t);
            if (byTax != null) return (byTax, "Tax Registration No (Tax ID)", 1.0, new List<string>());
        }
        Dictionary<string, object?>? best = null; var bs = 0.0;
        foreach (var r in rows)
        {
            var sc = nameKeys.Max(k => Sim(name, r.Get(k)));
            if (sc > bs) { best = r; bs = sc; }
        }
        if (best != null && bs >= ThAuto)
            return (best, $"Name ({(int)Math.Round(bs * 100)}%)", bs, new List<string>());
        var scored = rows.Select(r => (Score: nameKeys.Max(k => Sim(name, r.Get(k))), Row: r))
            .OrderByDescending(x => x.Score).ToList();
        var cands = scored.Where(x => x.Score >= ThSuggest).Take(3).Select(x => x.Row.GetStr(codeKey)).ToList();
        return (null, "", bs, cands);
    }

    public static (string Code, string Method, List<string> Cands) MatchMaterial(
        string? partnerCode, object? extCode, object? extDesc,
        List<Dictionary<string, object?>> mapRows, string keyField, List<Dictionary<string, object?>> materials)
    {
        var scope = mapRows.Where(m => m.GetStr(keyField) == partnerCode).ToList();
        var ec = (extCode?.ToString() ?? "").Trim().ToUpperInvariant();
        if (ec.Length > 0)
        {
            var hit = scope.FirstOrDefault(m => m.GetStr("ExtCode").Trim().ToUpperInvariant() == ec);
            if (hit != null) return (hit.GetStr("MaterialCode"), "Partner Material Code", new List<string>());
        }
        Dictionary<string, object?>? best = null; var bs = 0.0;
        foreach (var m in scope)
        {
            var sc = Sim(extDesc, m.Get("ExtDesc"));
            if (sc > bs) { best = m; bs = sc; }
        }
        if (best != null && bs >= ThMatScope)
            return (best.GetStr("MaterialCode"), $"Partner Material Name ({(int)Math.Round(bs * 100)}%)", new List<string>());

        Dictionary<string, object?>? b2 = null; var s2 = 0.0;
        foreach (var m in materials)
        {
            var sc = Math.Max(Sim(extDesc, m.Get("Description")), Sim(extCode, m.Get("MaterialCode")));
            if (sc > s2) { b2 = m; s2 = sc; }
        }
        if (b2 != null && s2 >= ThMatMaster)
            return (b2.GetStr("MaterialCode"), $"Material master ({(int)Math.Round(s2 * 100)}%)", new List<string>());

        var cands = scope.Where(m => Sim(extDesc, m.Get("ExtDesc")) >= ThSuggest).Select(m => m.GetStr("MaterialCode")).ToList();
        foreach (var m in materials)
        {
            var code = m.GetStr("MaterialCode");
            if (Sim(extDesc, m.Get("Description")) >= ThSuggest && !cands.Contains(code)) cands.Add(code);
        }
        return ("", "", cands.Take(3).ToList());
    }

    public static Dictionary<string, object?> ConvertUom(string materialCode, object? docUom, object? qty,
        List<Dictionary<string, object?>> materials, List<Dictionary<string, object?>> uomRules)
    {
        var mat = materials.FirstOrDefault(m => m.GetStr("MaterialCode") == materialCode);
        var baseUom = mat.GetStr("Uom").Trim();
        var du = (docUom?.ToString() ?? "").Trim();
        var q = Num(qty);

        if (du.Length == 0 && baseUom.Length > 0)
            return new() { ["status"] = "ok", ["sapUom"] = baseUom, ["factor"] = 1.0, ["sapQty"] = q, ["method"] = "No unit in document; using Material's base unit" };
        if (baseUom.Length > 0 && du.Equals(baseUom, StringComparison.OrdinalIgnoreCase))
            return new() { ["status"] = "ok", ["sapUom"] = baseUom, ["factor"] = 1.0, ["sapQty"] = q, ["method"] = "Unit matches Material" };

        // uomRules is already limited to this company's rules + all-company rules (SalesOrg blank),
        // by MasterSchema.ForSalesOrg. So a row with a non-empty SalesOrg here IS this company's.
        // Resolve most-specific first: company+material > any+material > company+general > any+general.
        bool ext(Dictionary<string, object?> x) => x.GetStr("ExtUom").Equals(du, StringComparison.OrdinalIgnoreCase);
        bool forMat(Dictionary<string, object?> x) => x.GetStr("MaterialCode") == materialCode;
        bool general(Dictionary<string, object?> x) => string.IsNullOrEmpty(x.GetStr("MaterialCode"));
        bool forCompany(Dictionary<string, object?> x) => !string.IsNullOrEmpty(x.GetStr("SalesOrg"));
        var rule = uomRules.FirstOrDefault(x => ext(x) && forMat(x) && forCompany(x))
                ?? uomRules.FirstOrDefault(x => ext(x) && forMat(x))
                ?? uomRules.FirstOrDefault(x => ext(x) && general(x) && forCompany(x))
                ?? uomRules.FirstOrDefault(x => ext(x) && general(x));
        var scope = rule == null ? ""
            : (general(rule) ? "general rule" : "product-specific rule") + (forCompany(rule) ? " (this company)" : "");

        if (rule != null)
        {
            var f = Num(rule.Get("Factor"));
            var sapUom = rule.GetStr("SapUom");
            if (baseUom.Length > 0 && !sapUom.Equals(baseUom, StringComparison.OrdinalIgnoreCase))
                return new() { ["status"] = "fail", ["sapUom"] = baseUom, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = $"Rule converts to {sapUom} but Material uses unit {baseUom}" };
            if (f <= 0 || string.IsNullOrWhiteSpace(sapUom))
                return new() { ["status"] = "fail", ["sapUom"] = baseUom, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = "Factor must be greater than 0" };
            // The resolved unit is what goes onto the SAP/Zoho order line. A composite label like
            // "KG/PC" is exactly what SAP rejects with RequestedQuantityUnit invalid, so stop it here
            // even if an old rule still carries one (the master screen now prevents new ones).
            if (sapUom.Contains(' ') || sapUom.Contains('/') || sapUom.Contains('\\'))
                return new() { ["status"] = "fail", ["sapUom"] = sapUom, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = $"Order unit '{sapUom}' isn't a valid code (no spaces or '/'). Fix it in Master → Unit Conversion" };
            return new()
            {
                ["status"] = "convert", ["sapUom"] = sapUom, ["factor"] = f, ["sapQty"] = Math.Round(q * f, 3),
                ["iso"] = rule.GetStr("SapUomIso").Trim(),
                ["method"] = $"{scope}: 1 {du} = {FormatG(f)} {sapUom}",
            };
        }

        // CustomerMaterial intentionally has no dependency on the legacy Material table. When no UoM
        // rule was configured, forward the document unit as-is — but only if it already looks like a
        // clean code. A composite/garbage label (has a space or '/') is the KG/PC bug, so fail with a
        // clear ask to add a rule rather than posting an invalid unit to SAP.
        if (baseUom.Length == 0 && du.Length > 0)
        {
            if (du.Contains(' ') || du.Contains('/') || du.Contains('\\'))
                return new() { ["status"] = "fail", ["sapUom"] = du, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = $"No unit rule, and the document unit '{du}' isn't a valid code — add a rule in Master → Unit Conversion" };
            return new() { ["status"] = "ok", ["sapUom"] = du, ["iso"] = UomIso.GetValueOrDefault(du.ToUpperInvariant(), ""), ["factor"] = 1.0, ["sapQty"] = q, ["method"] = "Using document unit" };
        }

        return new() { ["status"] = "fail", ["sapUom"] = baseUom, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = "No unit-conversion rule yet" };
    }

    public static Dictionary<string, object?> RunMapping(string module, Dictionary<string, object?> header,
        List<Dictionary<string, object?>> lines, MasterData masters, Dictionary<string, object?>? manual,
        string? companyCode = null, bool shipToOptional = false)
    {
        if (module == "SO") masters = MasterSchema.ForSalesOrg(
            masters, string.IsNullOrEmpty(companyCode) ? header.GetStr("salesOrg") : companyCode);
        manual ??= new();
        var mHead = manual.Get("header") as Dictionary<string, object?> ?? new();
        var mLineRaw = manual.Get("lines") as Dictionary<string, object?> ?? new();

        var matDesc = masters.Materials.ToDictionary(m => m.GetStr("MaterialCode"), m => (object?)m.GetStr("Description"));
        var resHeader = new Dictionary<string, object?>();
        var resLines = new List<Dictionary<string, object?>>();
        var errors = new List<Dictionary<string, object?>>();
        var warns = new List<string>();
        var res = new Dictionary<string, object?> { ["header"] = resHeader, ["lines"] = resLines, ["errors"] = errors, ["warns"] = warns };

        string? partner; List<Dictionary<string, object?>> mapRows; string keyField; string partnerLabel;

        if (module == "SO")
        {
            var manualCust = mHead.Get("customer")?.ToString();
            if (!string.IsNullOrEmpty(manualCust))
            {
                var c = masters.Customers.FirstOrDefault(x => x.GetStr("CustomerCode") == manualCust);
                resHeader["customer"] = c != null ? R("manual", c.GetStr("CustomerCode"), c.GetStr("NameTh"), "manually selected") : R("fail");
            }
            else
            {
                var (hit, method, _, cands) = MatchPartner(masters.Customers, header.Get("customerTaxId"), header.Get("customerName"), "CustomerCode", ["NameTh", "NameEn"]);
                if (hit != null)
                    resHeader["customer"] = R("ok", hit.GetStr("CustomerCode"), hit.GetStr("NameTh"), method);
                else
                {
                    resHeader["customer"] = R("fail", cands: cands);
                    errors.Add(new()
                    {
                        ["field"] = "Customer",
                        ["msg"] = $"No customer matches Tax No {Dash(header.GetStr("customerTaxId"))} or name \"{Dash(header.GetStr("customerName"))}\"",
                        ["fix"] = "Create/edit in Master Mapping → Customer",
                    });
                }
            }
            var cust = ((Dictionary<string, object?>)resHeader["customer"]!).GetStr("code");

            var manualShipTo = mHead.Get("shipTo")?.ToString();
            if (!string.IsNullOrEmpty(manualShipTo))
            {
                var s = masters.ShipTos.FirstOrDefault(x => x.GetStr("CustomerCode") == cust && x.GetStr("SapShipToCode") == manualShipTo);
                resHeader["shipTo"] = s != null ? R("manual", s.GetStr("SapShipToCode"), s.GetStr("ShipToName"), "manually selected") : R("fail");
            }
            else if (string.IsNullOrEmpty(cust))
            {
                resHeader["shipTo"] = R("fail");
                errors.Add(new() { ["field"] = "Ship-to", ["msg"] = "Ship-to cannot be determined yet because the customer is unknown", ["fix"] = "Specify the correct customer first" });
            }
            else
            {
                var scope = masters.ShipTos.Where(x => x.GetStr("CustomerCode") == cust).ToList();
                Dictionary<string, object?>? best = null; var bs = 0.0;
                foreach (var x in scope)
                {
                    var sc = Math.Max(Sim(header.Get("shipToName"), x.Get("ShipToName")), Sim(header.Get("shipToAddress"), x.Get("Address")));
                    if (sc > bs) { best = x; bs = sc; }
                }
                if (best != null && bs >= ThShipTo)
                    resHeader["shipTo"] = R("ok", best.GetStr("SapShipToCode"), best.GetStr("ShipToName"), $"Name/Address ({(int)Math.Round(bs * 100)}%)");
                else if (shipToOptional)
                {
                    // GLC: a ship-to is optional, but the choice must be EXPLICIT (per Megachem). When
                    // no ship-to is matched, the person must actively pick one of two fallbacks via
                    // mHead["shipToFallback"] (or select/search a real ship-to instead). BOTH fallbacks
                    // send the SAME payload -- no SH partner, so SAP fills ship-to = sold-to (a SAP SO
                    // always needs a ship-to; the API just doesn't have to supply it). They differ
                    // only in the label. Until a choice is made this is a BLOCKING "needchoice" (an
                    // error), so no order is ever sent without a deliberate ship-to decision.
                    var fallback = (mHead.Get("shipToFallback")?.ToString() ?? "").Trim().ToLowerInvariant();
                    if (fallback == "soldto")
                    {
                        resHeader["shipTo"] = R("skip", "", "", "ใช้ Sold-to เป็นผู้รับ");
                        warns.Add("Ship-to: user chose to use the sold-to party as the receiver (no SH sent; SAP defaults to sold-to).");
                    }
                    else if (fallback is "omit" or "none")
                    {
                        resHeader["shipTo"] = R("skip", "", "", "ไม่ระบุ Ship-to (ไม่ส่งไป SAP)");
                        warns.Add("Ship-to: user chose to omit the ship-to (no SH sent; SAP defaults to sold-to).");
                    }
                    else
                    {
                        // GLC default (Megachem): no ship-to matched and the user made no explicit
                        // choice, so fall back to the sold-to party automatically instead of blocking
                        // — GLC orders usually ship to the sold-to. Non-blocking: surfaced as a warning,
                        // not an error, so the mapping still passes. No SH is sent; SAP fills ship-to =
                        // sold-to. The user can still override via the buttons (omit / pick a ship-to).
                        var hadShipTo = !string.IsNullOrWhiteSpace(header.GetStr("shipToName"))
                                     || !string.IsNullOrWhiteSpace(header.GetStr("shipToAddress"));
                        resHeader["shipTo"] = R("skip", "", "", "ใช้ Sold-to เป็นผู้รับ (อัตโนมัติ)");
                        warns.Add(hadShipTo
                            ? $"Ship-to: the document's ship-to \"{Dash(header.GetStr("shipToName"))}\" didn't match any master, so the sold-to party is used as the receiver. Add it in Master → Ship-to if it should route elsewhere."
                            : "Ship-to: none on the document — the sold-to party is used as the receiver (no SH sent; SAP defaults to sold-to).");
                    }
                }
                else
                {
                    resHeader["shipTo"] = R("fail", cands: scope.Select(x => x.GetStr("SapShipToCode")).Take(3).ToList());
                    errors.Add(new()
                    {
                        ["field"] = "Ship-to",
                        ["msg"] = $"Ship-to location not found \"{Dash(header.GetStr("shipToName"))}\" for this customer",
                        ["fix"] = "Add in Master Mapping → Ship-to",
                    });
                }
            }
            var selectedCustomer = masters.Customers.FirstOrDefault(x => x.GetStr("CustomerCode") == cust);
            partner = cust;
            mapRows = masters.CustomerMaterials.Where(x => x.GetStr("CustomerCode") == cust
                && x.GetStr("SalesOrg") == selectedCustomer.GetStr("SalesOrg")).ToList();
            keyField = "CustomerCode"; partnerLabel = "Customer";
        }
        else
        {
            var manualVendor = mHead.Get("vendor")?.ToString();
            if (!string.IsNullOrEmpty(manualVendor))
            {
                var v = masters.Vendors.FirstOrDefault(x => x.GetStr("VendorCode") == manualVendor);
                resHeader["vendor"] = v != null ? R("manual", v.GetStr("VendorCode"), v.GetStr("VendorName"), "manually selected") : R("fail");
            }
            else
            {
                var (hit, method, _, cands) = MatchPartner(masters.Vendors, header.Get("vendorTaxId"), header.Get("vendorName"), "VendorCode", ["VendorName"]);
                if (hit != null)
                    resHeader["vendor"] = R("ok", hit.GetStr("VendorCode"), hit.GetStr("VendorName"), method);
                else
                {
                    resHeader["vendor"] = R("fail", cands: cands);
                    errors.Add(new()
                    {
                        ["field"] = "Vendor / Supplier",
                        ["msg"] = $"No vendor matches Tax No {Dash(header.GetStr("vendorTaxId"))} or name \"{Dash(header.GetStr("vendorName"))}\"",
                        ["fix"] = "Create/edit in Master Mapping → Vendor",
                    });
                }
            }
            partner = ((Dictionary<string, object?>)resHeader["vendor"]!).GetStr("code");
            mapRows = masters.VendorMaterials; keyField = "VendorCode"; partnerLabel = "Vendor";

            var calc = Math.Round(Num(header.Get("subTotal")) * Num(header.Get("vatRate")) / 100, 2);
            if (Math.Abs(calc - Num(header.Get("vatAmount"))) > 1)
                warns.Add($"VAT read {Money(Num(header.Get("vatAmount")))} does not match calculated {Money(calc)} (base {Money(Num(header.Get("subTotal")))} x {FormatG(Num(header.Get("vatRate")))}%)");
        }

        var uomRules = masters.Uoms;
        for (var i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            var mv = mLineRaw.Get(i.ToString())?.ToString();
            Dictionary<string, object?> row;
            if (!string.IsNullOrEmpty(mv))
            {
                row = module == "SO" && !mapRows.Any(x => x.GetStr("MaterialCode") == mv)
                    ? R("fail")
                    : R("manual", mv, matDesc.TryGetValue(mv, out var d) ? d?.ToString() ?? mv : mv, "manually selected");
                if (row.GetStr("status") == "fail")
                    errors.Add(new() { ["field"] = $"Material line {i + 1}", ["msg"] = "Material is not active for this customer and SalesOrg", ["fix"] = "Select or add CustomerMaterial for this customer" });
            }
            else if (string.IsNullOrEmpty(partner))
            {
                row = R("fail");
                if (i == 0)
                    errors.Add(new()
                    {
                        ["field"] = "Material (all lines)",
                        ["msg"] = $"Products cannot be matched yet because the {partnerLabel} has not been set",
                        ["fix"] = $"Set the {partnerLabel} correctly first, then run Mapping again",
                    });
            }
            else
            {
                var (code, method, cands) = MatchMaterial(partner, ln.Get("extCode"), ln.Get("desc"), mapRows, keyField, module == "SO" ? [] : masters.Materials);
                if (!string.IsNullOrEmpty(code))
                    row = R("ok", code, matDesc.TryGetValue(code, out var d2) ? d2?.ToString() ?? code : code, method);
                else
                {
                    row = R("fail", cands: cands);
                    errors.Add(new()
                    {
                        ["field"] = $"Material line {i + 1}",
                        ["msg"] = $"Product not found {Dash(ln.GetStr("extCode"))} / \"{Dash(ln.GetStr("desc"))}\" in the product list of {partnerLabel}",
                        ["fix"] = $"Add in Master Mapping → products for {partnerLabel}",
                    });
                }
            }

            if (!string.IsNullOrEmpty(row.GetStr("code")))
            {
                var u = ConvertUom(row.GetStr("code"), ln.Get("uom"), ln.Get("qty"), masters.Materials, uomRules);
                row["uom"] = u;
                if (u.GetStr("status") == "fail")
                {
                    var mat = masters.Materials.FirstOrDefault(m => m.GetStr("MaterialCode") == row.GetStr("code"));
                    errors.Add(new()
                    {
                        ["field"] = $"Unit line {i + 1}",
                        ["msg"] = $"No unit conversion \"{Dash(ln.GetStr("uom"))}\" → \"{Dash(mat.GetStr("Uom"))}\" for product {row.GetStr("code")} ({u.GetStr("detail")})",
                        ["fix"] = "Add a rule in Master Mapping → 4. Material → Unit Conversion (UoM)",
                    });
                }
                else if (u.GetStr("status") == "convert")
                {
                    warns.Add($"line {i + 1} converts unit {Qty3(Num(ln.Get("qty")))} {ln.GetStr("uom")} → {Qty3(Convert.ToDouble(u.Get("sapQty")))} {u.GetStr("sapUom")} ({u.GetStr("method")})");
                }
            }
            else
            {
                row["uom"] = new Dictionary<string, object?> { ["status"] = "idle", ["sapUom"] = "", ["factor"] = 0, ["sapQty"] = 0, ["method"] = "" };
            }
            resLines.Add(row);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            if (Num(ln.Get("qty")) <= 0)
                errors.Add(new() { ["field"] = $"Quantity line {i + 1}", ["msg"] = "Quantity must be greater than 0", ["fix"] = "Edit the value in the Detail table" });
            if (Num(ln.Get("price")) <= 0)
                warns.Add($"line {i + 1} has a unit price of 0");
        }
        var total = lines.Sum(l => Num(l.Get("amount")));
        var baseAmt = Num(header.Get("subTotal"));
        if (baseAmt == 0) baseAmt = Num(header.Get("totalAmount"));
        if (Math.Abs(total - baseAmt) > 1)
            warns.Add($"Line total {Money(total)} does not match the header amount {Money(baseAmt)}");

        AttachSapKeys(module, masters, res);
        AttachCompare(module, header, lines, masters, res);
        // Incoming Invoice (FB60) and PO Down Payment do not post document lines as Materials.
        // Keep their mapping pass/fail scoped to the relevant header partner; material/UoM errors
        // produced by the shared legacy line mapper must not block these module-specific flows.
        if (module is "II" or "PODP")
            errors.RemoveAll(e =>
            {
                var field = e.GetStr("field");
                return field.StartsWith("Material", StringComparison.OrdinalIgnoreCase)
                    || field.StartsWith("Unit", StringComparison.OrdinalIgnoreCase)
                    || field.StartsWith("Quantity line", StringComparison.OrdinalIgnoreCase);
            });
        res["pass"] = errors.Count == 0;
        return res;
    }

    private static void AttachSapKeys(string module, MasterData masters, Dictionary<string, object?> res)
    {
        var errors = (List<Dictionary<string, object?>>)res["errors"]!;
        var resHeader = (Dictionary<string, object?>)res["header"]!;
        var resLines = (List<Dictionary<string, object?>>)res["lines"]!;

        void Need(Dictionary<string, object?>? row, Dictionary<string, object?>? rec, string field, string label, string noun, string fix)
        {
            if (row == null || string.IsNullOrEmpty(row.GetStr("code"))) return;
            var key = SapKey(rec, field);
            row["sapCode"] = key;
            if (string.IsNullOrEmpty(key))
                errors.Add(new()
                {
                    ["field"] = label,
                    ["msg"] = $"{noun} \"{(string.IsNullOrEmpty(row.GetStr("text")) ? row.GetStr("code") : row.GetStr("text"))}\" has no SAP code yet, so it cannot be posted to SAP",
                    ["fix"] = fix,
                });
        }

        if (module == "SO")
        {
            var custRow = resHeader.Get("customer") as Dictionary<string, object?>;
            var c = masters.Customers.FirstOrDefault(x => x.GetStr("CustomerCode") == custRow.GetStr("code"));
            Need(custRow, c, "SapCustomerCode", "Customer SAP code", "Customer", "Fill 'External Code (Sold-to)' in Master Mapping → 2. Customer");
            var stRow = resHeader.Get("shipTo") as Dictionary<string, object?>;
            var st = masters.ShipTos.FirstOrDefault(x => x.GetStr("CustomerCode") == custRow.GetStr("code") && x.GetStr("SapShipToCode") == stRow.GetStr("code"));
            Need(stRow, st, "SapShipToCode", "Ship-to SAP code", "Ship-to Location", "Fill 'External Code (Ship-to)' in Master Mapping → 3. Ship-to");
        }
        else
        {
            var venRow = resHeader.Get("vendor") as Dictionary<string, object?>;
            var v = masters.Vendors.FirstOrDefault(x => x.GetStr("VendorCode") == venRow.GetStr("code"));
            Need(venRow, v, "SapVendorCode", "Vendor SAP code", "Vendor", "Fill 'SAP Code (Supplier)' in Master Mapping → 1. Vendor / Supplier");
        }

        for (var i = 0; i < resLines.Count; i++)
        {
            var row = resLines[i];
            var m = masters.Materials.FirstOrDefault(x => x.GetStr("MaterialCode") == row.GetStr("code"));
            Need(row, m, "SapMaterialCode", $"Material SAP code line {i + 1}", $"product {row.GetStr("code")}", "Fill 'SAP Code (Material)' in Master Mapping → 4. Material");
            var u = row.Get("uom") as Dictionary<string, object?>;
            if (u != null && (u.GetStr("status") == "ok" || u.GetStr("status") == "convert"))
            {
                var iso = u.GetStr("iso");
                if (string.IsNullOrEmpty(iso)) iso = UomIso.GetValueOrDefault(u.GetStr("sapUom").ToUpperInvariant(), "");
                u["iso"] = iso;
            }
        }
    }

    private static void AttachCompare(string module, Dictionary<string, object?> header, List<Dictionary<string, object?>> lines,
        MasterData masters, Dictionary<string, object?> res)
    {
        var resHeader = (Dictionary<string, object?>)res["header"]!;
        var resLines = (List<Dictionary<string, object?>>)res["lines"]!;

        if (module == "SO")
        {
            var r = (Dictionary<string, object?>)resHeader["customer"]!;
            var dn = header.Get("customerName"); var dt = header.Get("customerTaxId");
            r["doc"] = new List<object> { Fld("Customer Name", dn), Fld("Tax Registration No", dt) };
            var c = masters.Customers.FirstOrDefault(x => x.GetStr("CustomerCode") == r.GetStr("code"));
            // "SAP Code (Sold-to)" and "Customer Code (internal)" dropped from this list per
            // Megachem's request -- both are already redundant with the "SAP: ..." badge in the
            // card header (AttachSapKeys below), and the internal code isn't meaningful to a
            // person visually comparing document vs Zoho/SAP data.
            r["sap"] = c == null ? new List<object>() : new List<object>
            {
                Fld("Name from SAP / Zoho", c.GetStr("CompanyNameSAP"), Like(dn, c.Get("CompanyNameSAP"))),
                Fld("Tax Registration No", c.Get("TaxId"), SameTax(dt, c.Get("TaxId"))),
                Fld("Sales Org / Channel / Div", $"{Dash(c.GetStr("SalesOrg"))} / {Dash(c.GetStr("DistChannel"))} / {Dash(c.GetStr("Division"))}"),
                Fld("Payment Terms", c.Get("PaymentTerms")),
                Fld("Currency", c.Get("Currency")),
            }
            // Sold-to address rows added 2026-09-22 -- this card never showed an address at all
            // before (Zoho's live "Sold-to Address" sub-panel was the only place it appeared, and
            // only transiently/live, never persisted here for either SAP or Zoho). See AddressFlds.
            .Concat(AddressFlds(c))
            .ToList();

            r = (Dictionary<string, object?>)resHeader["shipTo"]!;
            var sn = header.Get("shipToName"); var sa = header.Get("shipToAddress");
            r["doc"] = new List<object> { Fld("Ship-to Location", sn), Fld("Delivery Address", sa) };
            var customerCode = (resHeader.Get("customer") as Dictionary<string, object?>).GetStr("code");
            var st = masters.ShipTos.FirstOrDefault(x => x.GetStr("CustomerCode") == customerCode && x.GetStr("SapShipToCode") == r.GetStr("code"));
            // "SAP Code (Ship-to)" and "Under Customer" dropped for the same reason as the
            // Customer section above.
            r["sap"] = st == null ? new List<object>() : new List<object>
            {
                Fld("ShipToCode", st.GetStr("SapShipToCode")),
                Fld("Location Name", st.GetStr("ShipToName"), Like(sn, st.GetStr("ShipToName"))),
            }
            // Address used to be one collapsed Fld("Address", ...) row here -- replaced
            // 2026-09-22 with the full per-field breakdown (see AddressFlds above). The
            // doc-vs-master similarity match (previously shown as a tick/x on that one row) still
            // runs against the same collapsed st.Get("Address") string; it just isn't rendered as
            // its own row anymore, since there's no single "sap side" address string left to pair
            // it with -- Like(sa, st.Get("Address")) is kept available on the underlying data for
            // any caller that still wants it, but no longer surfaced as a UI row here.
            .Concat(AddressFlds(st))
            .ToList();
        }
        else
        {
            var r = (Dictionary<string, object?>)resHeader["vendor"]!;
            var dn = header.Get("vendorName"); var dt = header.Get("vendorTaxId");
            r["doc"] = new List<object> { Fld("Vendor Name", dn), Fld("Tax Registration No", dt), Fld("Branch", header.Get("branch")) };
            var v = masters.Vendors.FirstOrDefault(x => x.GetStr("VendorCode") == r.GetStr("code"));
            r["sap"] = v == null ? new List<object>() : new List<object>
            {
                Fld("SAP Code (Supplier)", string.IsNullOrEmpty(v.GetStr("SapVendorCode")) ? "— not set —" : v.GetStr("SapVendorCode"), !string.IsNullOrEmpty(v.GetStr("SapVendorCode"))),
                Fld("Vendor Code (internal)", v.GetStr("VendorCode")),
                Fld("Name in SAP", v.GetStr("VendorName"), Like(dn, v.GetStr("VendorName"))),
                Fld("Tax Registration No", v.Get("TaxId"), SameTax(dt, v.Get("TaxId"))),
                Fld("Branch", v.Get("Branch")),
                Fld("Payment Terms", v.Get("PaymentTerms")),
                Fld("Recon. Account", v.Get("ReconAcct")),
                Fld("Withholding Tax", v.Get("WhtCode")),
            };
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            var r = resLines[i];
            var dq = Num(ln.Get("qty")); var du = ln.GetStr("uom");
            r["doc"] = new List<object>
            {
                Fld("Partner Material Code", ln.Get("extCode")),
                Fld("Material Name (from document)", ln.Get("desc")),
                Fld("Quantity", Qty3(dq)),
                Fld("Unit (from document)", du),
                Fld("Price/Unit", Money(Num(ln.Get("price")))),
                Fld("Amount", Money(Num(ln.Get("amount")))),
            };
            var m = masters.Materials.FirstOrDefault(x => x.GetStr("MaterialCode") == r.GetStr("code"));
            var u = r.Get("uom") as Dictionary<string, object?> ?? new();
            r["sap"] = m == null ? new List<object>() : new List<object>
            {
                Fld("SAP Code (Material)", string.IsNullOrEmpty(m.GetStr("SapMaterialCode")) ? "— not set —" : m.GetStr("SapMaterialCode"), !string.IsNullOrEmpty(m.GetStr("SapMaterialCode"))),
                Fld("Material Code (internal)", m.GetStr("MaterialCode")),
                Fld("Description", m.Get("Description"), Like(ln.Get("desc"), m.Get("Description"))),
                Fld("Base Unit in SAP", m.Get("Uom"), Same(du, m.GetStr("Uom")) ? true : (string.IsNullOrEmpty(du) ? (bool?)null : false)),
                Fld("Quantity to SAP", $"{Qty3(Num(u.Get("sapQty")))} {u.GetStr("sapUom")}", u.GetStr("status") is "ok" or "convert"),
                Fld("Plant", m.Get("Plant")),
                Fld("Material Group", m.Get("MatGroup")),
            };
            r["unit"] = new Dictionary<string, object?>
            {
                ["status"] = u.GetStr("status").Length > 0 ? u.GetStr("status") : "idle",
                ["doc"] = new List<object> { Fld("Quantity (from document)", Qty3(dq)), Fld("Unit (from document)", string.IsNullOrEmpty(du) ? "-" : du) },
                ["sap"] = new List<object>
                {
                    Fld("Quantity in SAP", u.GetStr("status") is "ok" or "convert" ? Qty3(Num(u.Get("sapQty"))) : "-"),
                    Fld("Unit in SAP", string.IsNullOrEmpty(u.GetStr("sapUom")) ? "-" : u.GetStr("sapUom")),
                    Fld("Factor", Num(u.Get("factor")) != 0 ? $"x {FormatG(Num(u.Get("factor")))}" : "-"),
                    Fld("ISO code", string.IsNullOrEmpty(u.GetStr("iso")) ? "-" : u.GetStr("iso")),
                    Fld("Rule source", !string.IsNullOrEmpty(u.GetStr("method")) ? u.GetStr("method") : (!string.IsNullOrEmpty(u.GetStr("detail")) ? u.GetStr("detail") : "-")),
                },
            };
        }
    }
}
