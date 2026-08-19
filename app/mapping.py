"""
เครื่องมือจับคู่ (Mapping Engine)
  SO : Customer -> เลขทะเบียนนิติบุคคล / ชื่อ, Ship-to -> ชื่อ+ที่อยู่, Material -> รหัส/ชื่อสินค้าของลูกค้า
  AP : Vendor   -> เลขทะเบียนนิติบุคคล / ชื่อ, Material -> รหัส/ชื่อสินค้าของผู้ขาย
"""
import re
from typing import Any

TH_AUTO       = 0.82   # เกณฑ์จับคู่ชื่อคู่ค้าอัตโนมัติ
TH_MAT_SCOPE  = 0.85   # เกณฑ์จับคู่ชื่อสินค้าภายใต้คู่ค้ารายเดียวกัน
TH_MAT_MASTER = 0.93   # เกณฑ์จับคู่กับ Material master ตรง ๆ (เข้มกว่า กันจับผิดตัว)
TH_SHIPTO     = 0.70
TH_SUGGEST    = 0.45

_STRIP = re.compile(r"บริษัท|จำกัด|มหาชน|หจก\.|ห้างหุ้นส่วนจำกัด|co\.,?\s*ltd\.?|company|limited|public|pcl\.?|corp\.?|inc\.?")
_KEEP  = re.compile(r"[^a-z0-9฀-๿]")


def digits(s: Any) -> str:
    return re.sub(r"\D", "", str(s or ""))


def norm(s: Any) -> str:
    return _KEEP.sub("", _STRIP.sub("", str(s or "").lower()))


def sim(a: Any, b: Any) -> float:
    """Dice coefficient บน bigram - ทนต่อการสะกด/เว้นวรรคต่างกัน"""
    a, b = norm(a), norm(b)
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0
    if a in b or b in a:
        return 0.92
    A = [a[i:i + 2] for i in range(len(a) - 1)]
    B = [b[i:i + 2] for i in range(len(b) - 1)]
    if not A or not B:
        return 0.0
    pool, hit = list(B), 0
    for x in A:
        if x in pool:
            pool.remove(x)
            hit += 1
    return 2 * hit / (len(A) + len(B))


def num(v: Any) -> float:
    try:
        return float(re.sub(r"[, ]", "", str(v if v is not None else "0")) or 0)
    except ValueError:
        return 0.0


def _r(status, code="", text="", method="", cands=None):
    return {"status": status, "code": code or "", "text": text or "", "sapCode": "",
            "method": method or "", "cands": cands or [], "doc": [], "sap": []}


# ISO code ของหน่วยที่ใช้บ่อย (ใช้เมื่อกฎแปลงหน่วยยังไม่ได้ระบุ SapUomIso)
UOM_ISO = {"KG": "KGM", "G": "GRM", "TON": "TNE", "L": "LTR", "ML": "MLT", "M": "MTR",
           "EA": "PCE", "PC": "PCE", "PCS": "PCE", "BOX": "BX", "BAG": "BG", "DRUM": "DR", "AU": "ACT"}


def _sap_key(rec: dict | None, field: str) -> str:
    return str((rec or {}).get(field) or "").strip()


def _fld(label, value, match=None):
    """หนึ่งบรรทัดข้อมูลสำหรับการ์ดเทียบ 2 ฝั่ง  match=True/False/None"""
    return {"label": label, "value": "" if value is None else str(value), "match": match}


def _same(a, b) -> bool:
    return bool(a) and bool(b) and norm(a) == norm(b)


def _like(a, b):
    """เทียบชื่อแบบยืดหยุ่น: คล้ายมาก = ติ๊กถูก, ไม่คล้าย = ไม่ต้องขึ้นสัญลักษณ์
    (ชื่อสินค้า/ชื่อบริษัทต่างกันได้เป็นปกติ เพราะจับคู่ด้วยรหัสหรือ master mapping)"""
    return True if (a and b and sim(a, b) >= 0.85) else None


def _same_tax(a, b) -> bool:
    return bool(digits(a)) and digits(a) == digits(b)


def match_partner(rows, tax_id, name, code_key, name_keys):
    """จับคู่คู่ค้า: เลขทะเบียนนิติบุคคลก่อน ถ้าไม่พบจึงเทียบชื่อ"""
    t = digits(tax_id)
    if len(t) >= 10:
        for r in rows:
            if digits(r.get("TaxId")) == t:
                return r, "เลขทะเบียนนิติบุคคล (Tax ID)", 1.0, []
    best, bs = None, 0.0
    for r in rows:
        sc = max(sim(name, r.get(k)) for k in name_keys)
        if sc > bs:
            best, bs = r, sc
    if best and bs >= TH_AUTO:
        return best, "ชื่อ (%d%%)" % round(bs * 100), bs, []
    scored = sorted(((max(sim(name, r.get(k)) for k in name_keys), r) for r in rows),
                    key=lambda x: -x[0])
    cands = [r[code_key] for s, r in scored if s >= TH_SUGGEST][:3]
    return None, "", bs, cands


def match_material(partner_code, ext_code, ext_desc, map_rows, key_field, materials):
    scope = [m for m in map_rows if m[key_field] == partner_code]
    ec = str(ext_code or "").strip().upper()
    if ec:
        for m in scope:
            if str(m.get("ExtCode") or "").strip().upper() == ec:
                return m["MaterialCode"], "รหัสสินค้าของคู่ค้า", []
    best, bs = None, 0.0
    for m in scope:
        sc = sim(ext_desc, m.get("ExtDesc"))
        if sc > bs:
            best, bs = m, sc
    if best and bs >= TH_MAT_SCOPE:
        return best["MaterialCode"], "ชื่อสินค้าของคู่ค้า (%d%%)" % round(bs * 100), []
    b2, s2 = None, 0.0
    for m in materials:
        sc = max(sim(ext_desc, m.get("Description")), sim(ext_code, m.get("MaterialCode")))
        if sc > s2:
            b2, s2 = m, sc
    if b2 and s2 >= TH_MAT_MASTER:
        return b2["MaterialCode"], "Material master (%d%%)" % round(s2 * 100), []
    cands = [m["MaterialCode"] for m in scope if sim(ext_desc, m.get("ExtDesc")) >= TH_SUGGEST]
    for m in materials:
        if sim(ext_desc, m.get("Description")) >= TH_SUGGEST and m["MaterialCode"] not in cands:
            cands.append(m["MaterialCode"])
    return "", "", cands[:3]


def convert_uom(material_code: str, doc_uom, qty, materials: list, uom_rules: list) -> dict:
    """แปลงหน่วยตามเอกสาร -> หน่วยของ Material ใน SAP
    ลำดับ: หน่วยตรงกันอยู่แล้ว -> กฎเฉพาะสินค้า -> กฎกลาง"""
    mat = next((m for m in materials if m["MaterialCode"] == material_code), None)
    base = str((mat or {}).get("Uom") or "").strip()
    du = str(doc_uom or "").strip()
    q = num(qty)

    if not du:
        return {"status": "ok", "sapUom": base, "factor": 1.0, "sapQty": q,
                "method": "ไม่ระบุหน่วยในเอกสาร ใช้หน่วยของ Material"}
    if base and du.upper() == base.upper():
        return {"status": "ok", "sapUom": base, "factor": 1.0, "sapQty": q, "method": "หน่วยตรงกับ Material"}

    rule = next((x for x in uom_rules
                 if x.get("MaterialCode") == material_code and str(x["ExtUom"]).upper() == du.upper()), None)
    scope = "กฎเฉพาะสินค้า"
    if not rule:
        rule = next((x for x in uom_rules
                     if not x.get("MaterialCode") and str(x["ExtUom"]).upper() == du.upper()), None)
        scope = "กฎกลาง"

    if rule:
        f = float(rule["Factor"] or 0)
        sap_uom = str(rule["SapUom"])
        if base and sap_uom.upper() != base.upper():
            return {"status": "fail", "sapUom": base, "factor": 0, "sapQty": 0,
                    "method": "", "detail": "กฎแปลงเป็น %s แต่ Material ใช้หน่วย %s" % (sap_uom, base)}
        if f <= 0:
            return {"status": "fail", "sapUom": base, "factor": 0, "sapQty": 0,
                    "method": "", "detail": "ตัวคูณต้องมากกว่า 0"}
        return {"status": "convert", "sapUom": sap_uom, "factor": f, "sapQty": round(q * f, 3),
                "iso": str(rule.get("SapUomIso") or "").strip(),
                "method": "%s: 1 %s = %g %s" % (scope, du, f, sap_uom)}

    return {"status": "fail", "sapUom": base, "factor": 0, "sapQty": 0, "method": "",
            "detail": "ยังไม่มีกฎแปลงหน่วย"}


def run_mapping(module: str, header: dict, lines: list, masters: dict, manual: dict | None = None) -> dict:
    manual = manual or {}
    m_head = manual.get("header") or {}
    m_line = {str(k): v for k, v in (manual.get("lines") or {}).items()}
    mat_desc = {m["MaterialCode"]: m["Description"] for m in masters["materials"]}
    res: dict = {"header": {}, "lines": [], "errors": [], "warns": []}

    if module == "SO":
        # ---------- Customer ----------
        if m_head.get("customer"):
            c = next((x for x in masters["customers"] if x["CustomerCode"] == m_head["customer"]), None)
            res["header"]["customer"] = _r("manual", c["CustomerCode"], c["NameTh"], "เลือกด้วยตนเอง") if c else _r("fail")
        else:
            hit, method, _s, cands = match_partner(
                masters["customers"], header.get("customerTaxId"), header.get("customerName"),
                "CustomerCode", ("NameTh", "NameEn"))
            if hit:
                res["header"]["customer"] = _r("ok", hit["CustomerCode"], hit["NameTh"], method)
            else:
                res["header"]["customer"] = _r("fail", cands=cands)
                res["errors"].append({
                    "field": "Customer",
                    "msg": 'ไม่พบลูกค้าที่ตรงกับเลขทะเบียน %s หรือชื่อ "%s"' % (
                        header.get("customerTaxId") or "-", header.get("customerName") or "-"),
                    "fix": "สร้าง/แก้ไขที่ Master Mapping → ลูกค้า (Customer)"})
        cust = res["header"]["customer"]["code"]

        # ---------- Ship-to ----------
        if m_head.get("shipTo"):
            s = next((x for x in masters["shiptos"] if x["ShipToCode"] == m_head["shipTo"]), None)
            res["header"]["shipTo"] = _r("manual", s["ShipToCode"], s["ShipToName"], "เลือกด้วยตนเอง") if s else _r("fail")
        elif not cust:
            res["header"]["shipTo"] = _r("fail")
            res["errors"].append({"field": "Ship-to",
                                  "msg": "ยังระบุ Ship-to ไม่ได้ เนื่องจากยังไม่ทราบลูกค้า",
                                  "fix": "ระบุลูกค้าให้ถูกต้องก่อน"})
        else:
            scope = [x for x in masters["shiptos"] if x["CustomerCode"] == cust]
            best, bs = None, 0.0
            for x in scope:
                sc = max(sim(header.get("shipToName"), x.get("ShipToName")),
                         sim(header.get("shipToAddress"), x.get("Address")))
                if sc > bs:
                    best, bs = x, sc
            if best and bs >= TH_SHIPTO:
                res["header"]["shipTo"] = _r("ok", best["ShipToCode"], best["ShipToName"],
                                             "ชื่อ/ที่อยู่ (%d%%)" % round(bs * 100))
            else:
                res["header"]["shipTo"] = _r("fail", cands=[x["ShipToCode"] for x in scope][:3])
                res["errors"].append({
                    "field": "Ship-to",
                    "msg": 'ไม่พบสถานที่ส่งของ "%s" ของลูกค้ารายนี้' % (header.get("shipToName") or "-"),
                    "fix": "เพิ่มที่ Master Mapping → Ship-to"})
        partner, map_rows, key_field = cust, masters["custmaterials"], "CustomerCode"
        partner_label = "ลูกค้า"
    else:
        # ---------- Vendor ----------
        if m_head.get("vendor"):
            v = next((x for x in masters["vendors"] if x["VendorCode"] == m_head["vendor"]), None)
            res["header"]["vendor"] = _r("manual", v["VendorCode"], v["VendorName"], "เลือกด้วยตนเอง") if v else _r("fail")
        else:
            hit, method, _s, cands = match_partner(
                masters["vendors"], header.get("vendorTaxId"), header.get("vendorName"),
                "VendorCode", ("VendorName",))
            if hit:
                res["header"]["vendor"] = _r("ok", hit["VendorCode"], hit["VendorName"], method)
            else:
                res["header"]["vendor"] = _r("fail", cands=cands)
                res["errors"].append({
                    "field": "Vendor / Supplier",
                    "msg": 'ไม่พบผู้ขายที่ตรงกับเลขทะเบียน %s หรือชื่อ "%s"' % (
                        header.get("vendorTaxId") or "-", header.get("vendorName") or "-"),
                    "fix": "สร้าง/แก้ไขที่ Master Mapping → ผู้ขาย (Vendor)"})
        partner = res["header"]["vendor"]["code"]
        map_rows, key_field = masters["venmaterials"], "VendorCode"
        partner_label = "ผู้ขาย"

        # ตรวจภาษีมูลค่าเพิ่ม
        calc = round(num(header.get("subTotal")) * num(header.get("vatRate")) / 100, 2)
        if abs(calc - num(header.get("vatAmount"))) > 1:
            res["warns"].append(
                "VAT ที่อ่านได้ {:,.2f} ไม่ตรงกับที่คำนวณ {:,.2f} (ฐาน {:,.2f} x {:g}%)".format(
                    num(header.get("vatAmount")), calc, num(header.get("subTotal")), num(header.get("vatRate"))))

    # ---------- Material + การแปลงหน่วย รายบรรทัด ----------
    uom_rules = masters.get("uoms") or []
    for i, ln in enumerate(lines):
        mv = m_line.get(str(i))
        if mv:
            row = _r("manual", mv, mat_desc.get(mv, mv), "เลือกด้วยตนเอง")
        elif not partner:
            row = _r("fail")
            if i == 0:
                res["errors"].append({
                    "field": "Material (ทุกบรรทัด)",
                    "msg": "ยังจับคู่สินค้าไม่ได้ เนื่องจากยังระบุ%sไม่สำเร็จ" % partner_label,
                    "fix": "ระบุ%sให้ถูกต้องก่อน แล้วกด Mapping อีกครั้ง" % partner_label})
        else:
            code, method, cands = match_material(partner, ln.get("extCode"), ln.get("desc"),
                                                 map_rows, key_field, masters["materials"])
            if code:
                row = _r("ok", code, mat_desc.get(code, code), method)
            else:
                row = _r("fail", cands=cands)
                res["errors"].append({
                    "field": "Material บรรทัดที่ %d" % (i + 1),
                    "msg": 'ไม่พบสินค้า %s / "%s" ในรายการสินค้าของ%s' % (
                        ln.get("extCode") or "-", ln.get("desc") or "-", partner_label),
                    "fix": "เพิ่มที่ Master Mapping → สินค้าฝั่ง%s" % partner_label})

        # ---- แปลงหน่วย (ทำเมื่อรู้ Material แล้วเท่านั้น) ----
        if row["code"]:
            u = convert_uom(row["code"], ln.get("uom"), ln.get("qty"), masters["materials"], uom_rules)
            row["uom"] = u
            if u["status"] == "fail":
                mat = next((m for m in masters["materials"] if m["MaterialCode"] == row["code"]), {})
                res["errors"].append({
                    "field": "หน่วย บรรทัดที่ %d" % (i + 1),
                    "msg": 'ไม่พบการแปลงหน่วย "%s" → "%s" ของสินค้า %s (%s)' % (
                        ln.get("uom") or "-", mat.get("Uom") or "-", row["code"], u.get("detail", "")),
                    "fix": "เพิ่มกฎที่ Master Mapping → 4. Material → การแปลงหน่วย (UoM)"})
            elif u["status"] == "convert":
                res["warns"].append("บรรทัดที่ %d แปลงหน่วย %s %s → %s %s (%s)" % (
                    i + 1, "{:,.3f}".format(num(ln.get("qty"))).rstrip("0").rstrip("."), ln.get("uom"),
                    "{:,.3f}".format(u["sapQty"]).rstrip("0").rstrip("."), u["sapUom"], u["method"]))
        else:
            row["uom"] = {"status": "idle", "sapUom": "", "factor": 0, "sapQty": 0, "method": ""}
        res["lines"].append(row)

    # ---------- ตรวจจำนวน / ยอดรวม ----------
    for i, ln in enumerate(lines):
        if num(ln.get("qty")) <= 0:
            res["errors"].append({"field": "จำนวน บรรทัดที่ %d" % (i + 1),
                                  "msg": "จำนวนต้องมากกว่า 0", "fix": "แก้ไขค่าในตาราง Detail"})
        if num(ln.get("price")) <= 0:
            res["warns"].append("บรรทัดที่ %d ราคาต่อหน่วยเป็น 0" % (i + 1))
    total = sum(num(l.get("amount")) for l in lines)
    base = num(header.get("subTotal")) or num(header.get("totalAmount"))
    if abs(total - base) > 1:
        res["warns"].append("ผลรวมรายการ {:,.2f} ไม่ตรงกับยอดในหัวเอกสาร {:,.2f}".format(total, base))

    _attach_sap_keys(module, masters, res)
    _attach_compare(module, header, lines, masters, res)
    res["pass"] = len(res["errors"]) == 0
    return res


def _attach_sap_keys(module: str, masters: dict, res: dict) -> None:
    """เติม 'รหัสของ SAP' ที่จะใช้ยิง OData และตรวจว่าครบทุกจุด"""
    def need(row, rec, field, label, noun, fix):
        if not row or not row.get("code"):
            return
        key = _sap_key(rec, field)
        row["sapCode"] = key
        if not key:
            res["errors"].append({
                "field": label,
                "msg": "%s \"%s\" ยังไม่ได้ระบุรหัสของ SAP จึงส่งเข้า SAP ไม่ได้" % (noun, row.get("text") or row["code"]),
                "fix": fix})

    if module == "SO":
        c = next((x for x in masters["customers"]
                  if x["CustomerCode"] == (res["header"].get("customer") or {}).get("code")), None)
        need(res["header"].get("customer"), c, "SapCustomerCode", "รหัส SAP ของลูกค้า", "ลูกค้า",
             "กรอกช่อง 'รหัสใน SAP (Sold-to)' ที่ Master Mapping → 2. Customer")
        st = next((x for x in masters["shiptos"]
                   if x["ShipToCode"] == (res["header"].get("shipTo") or {}).get("code")), None)
        need(res["header"].get("shipTo"), st, "SapShipToCode", "รหัส SAP ของ Ship-to", "สถานที่ส่งของ",
             "กรอกช่อง 'รหัสใน SAP (Ship-to)' ที่ Master Mapping → 3. Ship-to")
    else:
        v = next((x for x in masters["vendors"]
                  if x["VendorCode"] == (res["header"].get("vendor") or {}).get("code")), None)
        need(res["header"].get("vendor"), v, "SapVendorCode", "รหัส SAP ของผู้ขาย", "ผู้ขาย",
             "กรอกช่อง 'รหัสใน SAP (Supplier)' ที่ Master Mapping → 1. Vendor / Supplier")

    for i, row in enumerate(res["lines"]):
        m = next((x for x in masters["materials"] if x["MaterialCode"] == row.get("code")), None)
        need(row, m, "SapMaterialCode", "รหัส SAP ของสินค้า บรรทัดที่ %d" % (i + 1),
             "สินค้า %s" % row.get("code"),
             "กรอกช่อง 'รหัสใน SAP (Material)' ที่ Master Mapping → 4. Material")
        u = row.get("uom") or {}
        if u.get("status") in ("ok", "convert"):
            iso = u.get("iso") or UOM_ISO.get(str(u.get("sapUom") or "").upper(), "")
            u["iso"] = iso

def _attach_compare(module: str, header: dict, lines: list, masters: dict, res: dict) -> None:
    """เติมข้อมูล 2 ฝั่งของทุกจุดที่จับคู่: ฝั่งเอกสาร (OCR) และฝั่ง SAP (master ที่จับได้)"""
    # ---------------- คู่ค้า ----------------
    if module == "SO":
        r = res["header"].get("customer") or {}
        dn, dt_ = header.get("customerName"), header.get("customerTaxId")
        r["doc"] = [_fld("ชื่อลูกค้า", dn), _fld("เลขทะเบียนนิติบุคคล", dt_)]
        c = next((x for x in masters["customers"] if x["CustomerCode"] == r.get("code")), None)
        r["sap"] = ([_fld("รหัสใน SAP (Sold-to)", c.get("SapCustomerCode") or "— ยังไม่ระบุ —",
                          True if c.get("SapCustomerCode") else False),
                     _fld("รหัสลูกค้า (ภายใน)", c["CustomerCode"]),
                     _fld("ชื่อใน SAP", c["NameTh"], _like(dn, c["NameTh"]) or _like(dn, c.get("NameEn"))),
                     _fld("เลขทะเบียนนิติบุคคล", c.get("TaxId"), _same_tax(dt_, c.get("TaxId"))),
                     _fld("Sales Org / Channel / Div", "%s / %s / %s" % (
                         c.get("SalesOrg") or "-", c.get("DistChannel") or "-", c.get("Division") or "-")),
                     _fld("Payment Terms", c.get("PaymentTerms")),
                     _fld("สกุลเงิน", c.get("Currency"))] if c else [])

        r = res["header"].get("shipTo") or {}
        sn, sa = header.get("shipToName"), header.get("shipToAddress")
        r["doc"] = [_fld("สถานที่ส่งของ", sn), _fld("ที่อยู่จัดส่ง", sa)]
        st = next((x for x in masters["shiptos"] if x["ShipToCode"] == r.get("code")), None)
        r["sap"] = ([_fld("รหัสใน SAP (Ship-to)", st.get("SapShipToCode") or "— ยังไม่ระบุ —",
                          True if st.get("SapShipToCode") else False),
                     _fld("รหัส Ship-to (ภายใน)", st["ShipToCode"]),
                     _fld("ชื่อสถานที่", st["ShipToName"], _like(sn, st["ShipToName"])),
                     _fld("ที่อยู่", st.get("Address"), _like(sa, st.get("Address"))),
                     _fld("อยู่ใต้ลูกค้า", st.get("CustomerCode"))] if st else [])
    else:
        r = res["header"].get("vendor") or {}
        dn, dt_ = header.get("vendorName"), header.get("vendorTaxId")
        r["doc"] = [_fld("ชื่อผู้ขาย", dn), _fld("เลขทะเบียนนิติบุคคล", dt_),
                    _fld("สาขา", header.get("branch"))]
        v = next((x for x in masters["vendors"] if x["VendorCode"] == r.get("code")), None)
        r["sap"] = ([_fld("รหัสใน SAP (Supplier)", v.get("SapVendorCode") or "— ยังไม่ระบุ —",
                          True if v.get("SapVendorCode") else False),
                     _fld("รหัสผู้ขาย (ภายใน)", v["VendorCode"]),
                     _fld("ชื่อใน SAP", v["VendorName"], _like(dn, v["VendorName"])),
                     _fld("เลขทะเบียนนิติบุคคล", v.get("TaxId"), _same_tax(dt_, v.get("TaxId"))),
                     _fld("สาขา", v.get("Branch")),
                     _fld("Payment Terms", v.get("PaymentTerms")),
                     _fld("Recon. Account", v.get("ReconAcct")),
                     _fld("ภาษีหัก ณ ที่จ่าย", v.get("WhtCode"))] if v else [])

    # ---------------- สินค้า + หน่วย ----------------
    for i, ln in enumerate(lines):
        r = res["lines"][i]
        dq, du = num(ln.get("qty")), ln.get("uom") or ""
        r["doc"] = [_fld("รหัสสินค้าของคู่ค้า", ln.get("extCode")),
                    _fld("ชื่อสินค้าตามเอกสาร", ln.get("desc")),
                    _fld("จำนวน", "{:,.3f}".format(dq).rstrip("0").rstrip(".")),
                    _fld("หน่วยตามเอกสาร", du),
                    _fld("ราคา/หน่วย", "{:,.2f}".format(num(ln.get("price")))),
                    _fld("จำนวนเงิน", "{:,.2f}".format(num(ln.get("amount"))))]
        m = next((x for x in masters["materials"] if x["MaterialCode"] == r.get("code")), None)
        u = r.get("uom") or {}
        r["sap"] = ([_fld("รหัสใน SAP (Material)", m.get("SapMaterialCode") or "— ยังไม่ระบุ —",
                          True if m.get("SapMaterialCode") else False),
                     _fld("รหัส Material (ภายใน)", m["MaterialCode"]),
                     _fld("รายละเอียด", m.get("Description"), _like(ln.get("desc"), m.get("Description"))),
                     _fld("หน่วยฐานใน SAP", m.get("Uom"), True if _same(du, m.get("Uom")) else (False if du else None)),
                     _fld("จำนวนที่ส่งเข้า SAP",
                          "{:,.3f}".format(num(u.get("sapQty"))).rstrip("0").rstrip(".") + " " + (u.get("sapUom") or ""),
                          u.get("status") in ("ok", "convert")),
                     _fld("Plant", m.get("Plant")),
                     _fld("Material Group", m.get("MatGroup"))] if m else [])
        # การ์ดย่อย: การแปลงหน่วย
        r["unit"] = {
            "status": u.get("status", "idle"),
            "doc": [_fld("จำนวนตามเอกสาร", "{:,.3f}".format(dq).rstrip("0").rstrip(".")),
                    _fld("หน่วยตามเอกสาร", du or "-")],
            "sap": [_fld("จำนวนใน SAP", "{:,.3f}".format(num(u.get("sapQty"))).rstrip("0").rstrip(".")
                         if u.get("status") in ("ok", "convert") else "-"),
                    _fld("หน่วยใน SAP", u.get("sapUom") or "-"),
                    _fld("ตัวคูณ", ("x %g" % u["factor"]) if u.get("factor") else "-"),
                    _fld("ISO code", u.get("iso") or "-"),
                    _fld("ที่มาของกฎ", u.get("method") or u.get("detail") or "-")],
        }

