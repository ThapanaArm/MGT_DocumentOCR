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
            if (byTax != null) return (byTax, "เลขทะเบียนนิติบุคคล (Tax ID)", 1.0, new List<string>());
        }
        Dictionary<string, object?>? best = null; var bs = 0.0;
        foreach (var r in rows)
        {
            var sc = nameKeys.Max(k => Sim(name, r.Get(k)));
            if (sc > bs) { best = r; bs = sc; }
        }
        if (best != null && bs >= ThAuto)
            return (best, $"ชื่อ ({(int)Math.Round(bs * 100)}%)", bs, new List<string>());
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
            if (hit != null) return (hit.GetStr("MaterialCode"), "รหัสสินค้าของคู่ค้า", new List<string>());
        }
        Dictionary<string, object?>? best = null; var bs = 0.0;
        foreach (var m in scope)
        {
            var sc = Sim(extDesc, m.Get("ExtDesc"));
            if (sc > bs) { best = m; bs = sc; }
        }
        if (best != null && bs >= ThMatScope)
            return (best.GetStr("MaterialCode"), $"ชื่อสินค้าของคู่ค้า ({(int)Math.Round(bs * 100)}%)", new List<string>());

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

        if (du.Length == 0)
            return new() { ["status"] = "ok", ["sapUom"] = baseUom, ["factor"] = 1.0, ["sapQty"] = q, ["method"] = "ไม่ระบุหน่วยในเอกสาร ใช้หน่วยของ Material" };
        if (baseUom.Length > 0 && du.Equals(baseUom, StringComparison.OrdinalIgnoreCase))
            return new() { ["status"] = "ok", ["sapUom"] = baseUom, ["factor"] = 1.0, ["sapQty"] = q, ["method"] = "หน่วยตรงกับ Material" };

        var rule = uomRules.FirstOrDefault(x => x.GetStr("MaterialCode") == materialCode && x.GetStr("ExtUom").Equals(du, StringComparison.OrdinalIgnoreCase));
        var scope = "กฎเฉพาะสินค้า";
        if (rule == null)
        {
            rule = uomRules.FirstOrDefault(x => string.IsNullOrEmpty(x.GetStr("MaterialCode")) && x.GetStr("ExtUom").Equals(du, StringComparison.OrdinalIgnoreCase));
            scope = "กฎกลาง";
        }

        if (rule != null)
        {
            var f = Num(rule.Get("Factor"));
            var sapUom = rule.GetStr("SapUom");
            if (baseUom.Length > 0 && !sapUom.Equals(baseUom, StringComparison.OrdinalIgnoreCase))
                return new() { ["status"] = "fail", ["sapUom"] = baseUom, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = $"กฎแปลงเป็น {sapUom} แต่ Material ใช้หน่วย {baseUom}" };
            if (f <= 0)
                return new() { ["status"] = "fail", ["sapUom"] = baseUom, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = "ตัวคูณต้องมากกว่า 0" };
            return new()
            {
                ["status"] = "convert", ["sapUom"] = sapUom, ["factor"] = f, ["sapQty"] = Math.Round(q * f, 3),
                ["iso"] = rule.GetStr("SapUomIso").Trim(),
                ["method"] = $"{scope}: 1 {du} = {FormatG(f)} {sapUom}",
            };
        }

        return new() { ["status"] = "fail", ["sapUom"] = baseUom, ["factor"] = 0, ["sapQty"] = 0, ["method"] = "", ["detail"] = "ยังไม่มีกฎแปลงหน่วย" };
    }

    public static Dictionary<string, object?> RunMapping(string module, Dictionary<string, object?> header,
        List<Dictionary<string, object?>> lines, MasterData masters, Dictionary<string, object?>? manual)
    {
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
                resHeader["customer"] = c != null ? R("manual", c.GetStr("CustomerCode"), c.GetStr("NameTh"), "เลือกด้วยตนเอง") : R("fail");
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
                        ["msg"] = $"ไม่พบลูกค้าที่ตรงกับเลขทะเบียน {Dash(header.GetStr("customerTaxId"))} หรือชื่อ \"{Dash(header.GetStr("customerName"))}\"",
                        ["fix"] = "สร้าง/แก้ไขที่ Master Mapping → ลูกค้า (Customer)",
                    });
                }
            }
            var cust = ((Dictionary<string, object?>)resHeader["customer"]!).GetStr("code");

            var manualShipTo = mHead.Get("shipTo")?.ToString();
            if (!string.IsNullOrEmpty(manualShipTo))
            {
                var s = masters.ShipTos.FirstOrDefault(x => x.GetStr("ShipToCode") == manualShipTo);
                resHeader["shipTo"] = s != null ? R("manual", s.GetStr("ShipToCode"), s.GetStr("ShipToName"), "เลือกด้วยตนเอง") : R("fail");
            }
            else if (string.IsNullOrEmpty(cust))
            {
                resHeader["shipTo"] = R("fail");
                errors.Add(new() { ["field"] = "Ship-to", ["msg"] = "ยังระบุ Ship-to ไม่ได้ เนื่องจากยังไม่ทราบลูกค้า", ["fix"] = "ระบุลูกค้าให้ถูกต้องก่อน" });
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
                    resHeader["shipTo"] = R("ok", best.GetStr("ShipToCode"), best.GetStr("ShipToName"), $"ชื่อ/ที่อยู่ ({(int)Math.Round(bs * 100)}%)");
                else
                {
                    resHeader["shipTo"] = R("fail", cands: scope.Select(x => x.GetStr("ShipToCode")).Take(3).ToList());
                    errors.Add(new()
                    {
                        ["field"] = "Ship-to",
                        ["msg"] = $"ไม่พบสถานที่ส่งของ \"{Dash(header.GetStr("shipToName"))}\" ของลูกค้ารายนี้",
                        ["fix"] = "เพิ่มที่ Master Mapping → Ship-to",
                    });
                }
            }
            partner = cust; mapRows = masters.CustomerMaterials; keyField = "CustomerCode"; partnerLabel = "ลูกค้า";
        }
        else
        {
            var manualVendor = mHead.Get("vendor")?.ToString();
            if (!string.IsNullOrEmpty(manualVendor))
            {
                var v = masters.Vendors.FirstOrDefault(x => x.GetStr("VendorCode") == manualVendor);
                resHeader["vendor"] = v != null ? R("manual", v.GetStr("VendorCode"), v.GetStr("VendorName"), "เลือกด้วยตนเอง") : R("fail");
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
                        ["msg"] = $"ไม่พบผู้ขายที่ตรงกับเลขทะเบียน {Dash(header.GetStr("vendorTaxId"))} หรือชื่อ \"{Dash(header.GetStr("vendorName"))}\"",
                        ["fix"] = "สร้าง/แก้ไขที่ Master Mapping → ผู้ขาย (Vendor)",
                    });
                }
            }
            partner = ((Dictionary<string, object?>)resHeader["vendor"]!).GetStr("code");
            mapRows = masters.VendorMaterials; keyField = "VendorCode"; partnerLabel = "ผู้ขาย";

            var calc = Math.Round(Num(header.Get("subTotal")) * Num(header.Get("vatRate")) / 100, 2);
            if (Math.Abs(calc - Num(header.Get("vatAmount"))) > 1)
                warns.Add($"VAT ที่อ่านได้ {Money(Num(header.Get("vatAmount")))} ไม่ตรงกับที่คำนวณ {Money(calc)} (ฐาน {Money(Num(header.Get("subTotal")))} x {FormatG(Num(header.Get("vatRate")))}%)");
        }

        var uomRules = masters.Uoms;
        for (var i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            var mv = mLineRaw.Get(i.ToString())?.ToString();
            Dictionary<string, object?> row;
            if (!string.IsNullOrEmpty(mv))
            {
                row = R("manual", mv, matDesc.TryGetValue(mv, out var d) ? d?.ToString() ?? mv : mv, "เลือกด้วยตนเอง");
            }
            else if (string.IsNullOrEmpty(partner))
            {
                row = R("fail");
                if (i == 0)
                    errors.Add(new()
                    {
                        ["field"] = "Material (ทุกบรรทัด)",
                        ["msg"] = $"ยังจับคู่สินค้าไม่ได้ เนื่องจากยังระบุ{partnerLabel}ไม่สำเร็จ",
                        ["fix"] = $"ระบุ{partnerLabel}ให้ถูกต้องก่อน แล้วกด Mapping อีกครั้ง",
                    });
            }
            else
            {
                var (code, method, cands) = MatchMaterial(partner, ln.Get("extCode"), ln.Get("desc"), mapRows, keyField, masters.Materials);
                if (!string.IsNullOrEmpty(code))
                    row = R("ok", code, matDesc.TryGetValue(code, out var d2) ? d2?.ToString() ?? code : code, method);
                else
                {
                    row = R("fail", cands: cands);
                    errors.Add(new()
                    {
                        ["field"] = $"Material บรรทัดที่ {i + 1}",
                        ["msg"] = $"ไม่พบสินค้า {Dash(ln.GetStr("extCode"))} / \"{Dash(ln.GetStr("desc"))}\" ในรายการสินค้าของ{partnerLabel}",
                        ["fix"] = $"เพิ่มที่ Master Mapping → สินค้าฝั่ง{partnerLabel}",
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
                        ["field"] = $"หน่วย บรรทัดที่ {i + 1}",
                        ["msg"] = $"ไม่พบการแปลงหน่วย \"{Dash(ln.GetStr("uom"))}\" → \"{Dash(mat.GetStr("Uom"))}\" ของสินค้า {row.GetStr("code")} ({u.GetStr("detail")})",
                        ["fix"] = "เพิ่มกฎที่ Master Mapping → 4. Material → การแปลงหน่วย (UoM)",
                    });
                }
                else if (u.GetStr("status") == "convert")
                {
                    warns.Add($"บรรทัดที่ {i + 1} แปลงหน่วย {Qty3(Num(ln.Get("qty")))} {ln.GetStr("uom")} → {Qty3(Convert.ToDouble(u.Get("sapQty")))} {u.GetStr("sapUom")} ({u.GetStr("method")})");
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
                errors.Add(new() { ["field"] = $"จำนวน บรรทัดที่ {i + 1}", ["msg"] = "จำนวนต้องมากกว่า 0", ["fix"] = "แก้ไขค่าในตาราง Detail" });
            if (Num(ln.Get("price")) <= 0)
                warns.Add($"บรรทัดที่ {i + 1} ราคาต่อหน่วยเป็น 0");
        }
        var total = lines.Sum(l => Num(l.Get("amount")));
        var baseAmt = Num(header.Get("subTotal"));
        if (baseAmt == 0) baseAmt = Num(header.Get("totalAmount"));
        if (Math.Abs(total - baseAmt) > 1)
            warns.Add($"ผลรวมรายการ {Money(total)} ไม่ตรงกับยอดในหัวเอกสาร {Money(baseAmt)}");

        AttachSapKeys(module, masters, res);
        AttachCompare(module, header, lines, masters, res);
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
                    ["msg"] = $"{noun} \"{(string.IsNullOrEmpty(row.GetStr("text")) ? row.GetStr("code") : row.GetStr("text"))}\" ยังไม่ได้ระบุรหัสของ SAP จึงส่งเข้า SAP ไม่ได้",
                    ["fix"] = fix,
                });
        }

        if (module == "SO")
        {
            var custRow = resHeader.Get("customer") as Dictionary<string, object?>;
            var c = masters.Customers.FirstOrDefault(x => x.GetStr("CustomerCode") == custRow.GetStr("code"));
            Need(custRow, c, "SapCustomerCode", "รหัส SAP ของลูกค้า", "ลูกค้า", "กรอกช่อง 'รหัสใน SAP (Sold-to)' ที่ Master Mapping → 2. Customer");
            var stRow = resHeader.Get("shipTo") as Dictionary<string, object?>;
            var st = masters.ShipTos.FirstOrDefault(x => x.GetStr("ShipToCode") == stRow.GetStr("code"));
            Need(stRow, st, "SapShipToCode", "รหัส SAP ของ Ship-to", "สถานที่ส่งของ", "กรอกช่อง 'รหัสใน SAP (Ship-to)' ที่ Master Mapping → 3. Ship-to");
        }
        else
        {
            var venRow = resHeader.Get("vendor") as Dictionary<string, object?>;
            var v = masters.Vendors.FirstOrDefault(x => x.GetStr("VendorCode") == venRow.GetStr("code"));
            Need(venRow, v, "SapVendorCode", "รหัส SAP ของผู้ขาย", "ผู้ขาย", "กรอกช่อง 'รหัสใน SAP (Supplier)' ที่ Master Mapping → 1. Vendor / Supplier");
        }

        for (var i = 0; i < resLines.Count; i++)
        {
            var row = resLines[i];
            var m = masters.Materials.FirstOrDefault(x => x.GetStr("MaterialCode") == row.GetStr("code"));
            Need(row, m, "SapMaterialCode", $"รหัส SAP ของสินค้า บรรทัดที่ {i + 1}", $"สินค้า {row.GetStr("code")}", "กรอกช่อง 'รหัสใน SAP (Material)' ที่ Master Mapping → 4. Material");
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
            r["doc"] = new List<object> { Fld("ชื่อลูกค้า", dn), Fld("เลขทะเบียนนิติบุคคล", dt) };
            var c = masters.Customers.FirstOrDefault(x => x.GetStr("CustomerCode") == r.GetStr("code"));
            r["sap"] = c == null ? new List<object>() : new List<object>
            {
                Fld("รหัสใน SAP (Sold-to)", string.IsNullOrEmpty(c.GetStr("SapCustomerCode")) ? "— ยังไม่ระบุ —" : c.GetStr("SapCustomerCode"), !string.IsNullOrEmpty(c.GetStr("SapCustomerCode"))),
                Fld("รหัสลูกค้า (ภายใน)", c.GetStr("CustomerCode")),
                Fld("ชื่อใน SAP", c.GetStr("NameTh"), Like(dn, c.GetStr("NameTh")) ?? Like(dn, c.Get("NameEn"))),
                Fld("เลขทะเบียนนิติบุคคล", c.Get("TaxId"), SameTax(dt, c.Get("TaxId"))),
                Fld("Sales Org / Channel / Div", $"{Dash(c.GetStr("SalesOrg"))} / {Dash(c.GetStr("DistChannel"))} / {Dash(c.GetStr("Division"))}"),
                Fld("Payment Terms", c.Get("PaymentTerms")),
                Fld("สกุลเงิน", c.Get("Currency")),
            };

            r = (Dictionary<string, object?>)resHeader["shipTo"]!;
            var sn = header.Get("shipToName"); var sa = header.Get("shipToAddress");
            r["doc"] = new List<object> { Fld("สถานที่ส่งของ", sn), Fld("ที่อยู่จัดส่ง", sa) };
            var st = masters.ShipTos.FirstOrDefault(x => x.GetStr("ShipToCode") == r.GetStr("code"));
            r["sap"] = st == null ? new List<object>() : new List<object>
            {
                Fld("รหัสใน SAP (Ship-to)", string.IsNullOrEmpty(st.GetStr("SapShipToCode")) ? "— ยังไม่ระบุ —" : st.GetStr("SapShipToCode"), !string.IsNullOrEmpty(st.GetStr("SapShipToCode"))),
                Fld("รหัส Ship-to (ภายใน)", st.GetStr("ShipToCode")),
                Fld("ชื่อสถานที่", st.GetStr("ShipToName"), Like(sn, st.GetStr("ShipToName"))),
                Fld("ที่อยู่", st.Get("Address"), Like(sa, st.Get("Address"))),
                Fld("อยู่ใต้ลูกค้า", st.Get("CustomerCode")),
            };
        }
        else
        {
            var r = (Dictionary<string, object?>)resHeader["vendor"]!;
            var dn = header.Get("vendorName"); var dt = header.Get("vendorTaxId");
            r["doc"] = new List<object> { Fld("ชื่อผู้ขาย", dn), Fld("เลขทะเบียนนิติบุคคล", dt), Fld("สาขา", header.Get("branch")) };
            var v = masters.Vendors.FirstOrDefault(x => x.GetStr("VendorCode") == r.GetStr("code"));
            r["sap"] = v == null ? new List<object>() : new List<object>
            {
                Fld("รหัสใน SAP (Supplier)", string.IsNullOrEmpty(v.GetStr("SapVendorCode")) ? "— ยังไม่ระบุ —" : v.GetStr("SapVendorCode"), !string.IsNullOrEmpty(v.GetStr("SapVendorCode"))),
                Fld("รหัสผู้ขาย (ภายใน)", v.GetStr("VendorCode")),
                Fld("ชื่อใน SAP", v.GetStr("VendorName"), Like(dn, v.GetStr("VendorName"))),
                Fld("เลขทะเบียนนิติบุคคล", v.Get("TaxId"), SameTax(dt, v.Get("TaxId"))),
                Fld("สาขา", v.Get("Branch")),
                Fld("Payment Terms", v.Get("PaymentTerms")),
                Fld("Recon. Account", v.Get("ReconAcct")),
                Fld("ภาษีหัก ณ ที่จ่าย", v.Get("WhtCode")),
            };
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            var r = resLines[i];
            var dq = Num(ln.Get("qty")); var du = ln.GetStr("uom");
            r["doc"] = new List<object>
            {
                Fld("รหัสสินค้าของคู่ค้า", ln.Get("extCode")),
                Fld("ชื่อสินค้าตามเอกสาร", ln.Get("desc")),
                Fld("จำนวน", Qty3(dq)),
                Fld("หน่วยตามเอกสาร", du),
                Fld("ราคา/หน่วย", Money(Num(ln.Get("price")))),
                Fld("จำนวนเงิน", Money(Num(ln.Get("amount")))),
            };
            var m = masters.Materials.FirstOrDefault(x => x.GetStr("MaterialCode") == r.GetStr("code"));
            var u = r.Get("uom") as Dictionary<string, object?> ?? new();
            r["sap"] = m == null ? new List<object>() : new List<object>
            {
                Fld("รหัสใน SAP (Material)", string.IsNullOrEmpty(m.GetStr("SapMaterialCode")) ? "— ยังไม่ระบุ —" : m.GetStr("SapMaterialCode"), !string.IsNullOrEmpty(m.GetStr("SapMaterialCode"))),
                Fld("รหัส Material (ภายใน)", m.GetStr("MaterialCode")),
                Fld("รายละเอียด", m.Get("Description"), Like(ln.Get("desc"), m.Get("Description"))),
                Fld("หน่วยฐานใน SAP", m.Get("Uom"), Same(du, m.GetStr("Uom")) ? true : (string.IsNullOrEmpty(du) ? (bool?)null : false)),
                Fld("จำนวนที่ส่งเข้า SAP", $"{Qty3(Num(u.Get("sapQty")))} {u.GetStr("sapUom")}", u.GetStr("status") is "ok" or "convert"),
                Fld("Plant", m.Get("Plant")),
                Fld("Material Group", m.Get("MatGroup")),
            };
            r["unit"] = new Dictionary<string, object?>
            {
                ["status"] = u.GetStr("status").Length > 0 ? u.GetStr("status") : "idle",
                ["doc"] = new List<object> { Fld("จำนวนตามเอกสาร", Qty3(dq)), Fld("หน่วยตามเอกสาร", string.IsNullOrEmpty(du) ? "-" : du) },
                ["sap"] = new List<object>
                {
                    Fld("จำนวนใน SAP", u.GetStr("status") is "ok" or "convert" ? Qty3(Num(u.Get("sapQty"))) : "-"),
                    Fld("หน่วยใน SAP", string.IsNullOrEmpty(u.GetStr("sapUom")) ? "-" : u.GetStr("sapUom")),
                    Fld("ตัวคูณ", Num(u.Get("factor")) != 0 ? $"x {FormatG(Num(u.Get("factor")))}" : "-"),
                    Fld("ISO code", string.IsNullOrEmpty(u.GetStr("iso")) ? "-" : u.GetStr("iso")),
                    Fld("ที่มาของกฎ", !string.IsNullOrEmpty(u.GetStr("method")) ? u.GetStr("method") : (!string.IsNullOrEmpty(u.GetStr("detail")) ? u.GetStr("detail") : "-")),
                },
            };
        }
    }
}
