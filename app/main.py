"""
MGT Document OCR -> SAP S/4HANA
FastAPI backend + static frontend
"""
from __future__ import annotations

import base64
import json
import re
import shutil
import time
import unicodedata
from datetime import datetime, date, timedelta
from decimal import Decimal
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException, UploadFile, File, Form, Body
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles

from . import config, db, ocr_engine, sap
from .mapping import num, run_mapping

app = FastAPI(title="MGT Document OCR → SAP S/4HANA", version="1.0.0")

# ประเภทเอกสาร Supplier Invoice — ผู้ใช้เลือกเองในหน้าเอกสาร ไม่ได้เดาจาก OCR (เก็บไว้แสดง/กรองเท่านั้น ยังไม่ผูกกับ SAP)
AP_DOC_CATEGORIES = [
    {"id": "INVENTORY", "label": "การบันทึกรายการตั้งหนี้เจ้า - Inventory"},
    {"id": "EXPENSE", "label": "การบันทึกรายการตั้งหนี้เจ้า - Expense"},
    {"id": "FIXED_ASSET_BUDGET", "label": "การบันทึกรายการตั้งหนี้เจ้า - Fixed Asset กรณีคุมงบประมาณ"},
    {"id": "FIXED_ASSET_NO_BUDGET", "label": "การบันทึกรายการตั้งหนี้เจ้า - Fixed Asset กรณีไม่คุมงบประมาณ"},
    {"id": "SUB_CONTRACT", "label": "การบันทึกรายการตั้งหนี้เจ้า - Sub Contract"},
]

# ======================================================================
# helpers
# ======================================================================
def clean(v: Any) -> Any:
    if isinstance(v, Decimal):
        return float(v)
    if isinstance(v, (datetime, date)):
        return v.isoformat(sep=" ") if isinstance(v, datetime) else v.isoformat()
    return v


def rows(rs: list[dict]) -> list[dict]:
    return [{k: clean(v) for k, v in r.items()} for r in rs]


def _d(s: Any):
    """แปลงข้อความเป็น date (รับ YYYY-MM-DD) — ว่าง = None"""
    s = str(s or "").strip()[:10]
    if not re.match(r"^\d{4}-\d{2}-\d{2}$", s):
        return None
    try:
        return datetime.strptime(s, "%Y-%m-%d").date()
    except ValueError:
        return None


def safe_name(name: str) -> str:
    name = unicodedata.normalize("NFC", name or "file")
    name = re.sub(r"[^\w฀-๿.\- ]", "_", name).strip() or "file"
    return name[:120]


# ======================================================================
# master data
# ======================================================================
MASTERS: dict[str, dict] = {
    "customers":      {"table": "ocr.Customer",         "key": "CustomerCode", "identity": False,
                       "cols": ["CustomerCode", "SapCustomerCode", "NameTh", "NameEn", "TaxId", "Branch",
                                "SalesOrg", "DistChannel", "Division", "Currency", "PaymentTerms"]},
    "shiptos":        {"table": "ocr.ShipTo",           "key": "ShipToCode", "identity": False,
                       "cols": ["ShipToCode", "SapShipToCode", "CustomerCode", "ShipToName", "Address"]},
    "materials":      {"table": "ocr.Material",         "key": "MaterialCode", "identity": False,
                       "cols": ["MaterialCode", "SapMaterialCode", "Description", "Uom", "Plant", "MatGroup"]},
    "custmaterials":  {"table": "ocr.CustomerMaterial", "key": "Id", "identity": True,
                       "cols": ["CustomerCode", "ExtCode", "ExtDesc", "MaterialCode"]},
    "vendors":        {"table": "ocr.Vendor",           "key": "VendorCode", "identity": False,
                       "cols": ["VendorCode", "SapVendorCode", "VendorName", "TaxId", "Branch", "Currency",
                                "PaymentTerms", "ReconAcct", "WhtCode"]},
    "venmaterials":   {"table": "ocr.VendorMaterial",   "key": "Id", "identity": True,
                       "cols": ["VendorCode", "ExtCode", "ExtDesc", "MaterialCode"]},
    "uoms":           {"table": "ocr.UomConversion",    "key": "Id", "identity": True,
                       "cols": ["MaterialCode", "ExtUom", "SapUom", "SapUomIso", "Factor", "Note"]},
}
ORDER_BY = {"customers": "CustomerCode", "shiptos": "CustomerCode, ShipToCode", "materials": "MaterialCode",
            "custmaterials": "CustomerCode, ExtCode", "vendors": "VendorCode", "venmaterials": "VendorCode, ExtCode",
            "uoms": "CASE WHEN MaterialCode IS NULL THEN 0 ELSE 1 END, MaterialCode, ExtUom"}


def load_masters() -> dict:
    return {
        "customers":     rows(db.query("SELECT * FROM ocr.Customer WHERE IsActive=1 ORDER BY CustomerCode")),
        "shiptos":       rows(db.query("SELECT * FROM ocr.ShipTo WHERE IsActive=1 ORDER BY CustomerCode, ShipToCode")),
        "materials":     rows(db.query("SELECT * FROM ocr.Material WHERE IsActive=1 ORDER BY MaterialCode")),
        "custmaterials": rows(db.query("SELECT * FROM ocr.CustomerMaterial ORDER BY CustomerCode, ExtCode")),
        "vendors":       rows(db.query("SELECT * FROM ocr.Vendor WHERE IsActive=1 ORDER BY VendorCode")),
        "venmaterials":  rows(db.query("SELECT * FROM ocr.VendorMaterial ORDER BY VendorCode, ExtCode")),
        "uoms":          rows(db.query("SELECT * FROM ocr.UomConversion ORDER BY "
                                       "CASE WHEN MaterialCode IS NULL THEN 0 ELSE 1 END, MaterialCode, ExtUom")),
    }


# ======================================================================
# document repository
# ======================================================================
# ฟิลด์ header ที่ "คงที่ต่อคู่ค้า" ข้ามเอกสาร (ชื่อ/เงื่อนไข/สกุลเงิน) — ใช้จำไว้เดาในเอกสารถัดไปจากคู่ค้าเดิม
# ไม่รวมฟิลด์ที่เปลี่ยนทุกเอกสาร (เลขที่เอกสาร วันที่ ยอดเงิน อ้างอิง PO) หรือรายการสินค้า (Detail)
_VENDOR_TAXID_FIELD = {"AP": "vendorTaxId", "SO": "customerTaxId", "II": "vendorTaxId", "PODP": "vendorTaxId"}
_MEMORABLE_FIELDS = {
    "AP": ["vendorName", "branch", "paymentTerms", "currency", "taxCode", "calculateTax", "paymentMethod"],
    "SO": ["customerName", "shipToName", "shipToAddress", "currency", "paymentTerms", "incoterms"],
    "II": ["vendorName", "paymentTerms", "currency", "taxCode", "calculateTax", "paymentMethod",
           "businessPlace", "language", "bankCountry", "bankKey", "bankAccountNo"],
    "PODP": ["vendorName", "paymentTerms", "currency"],
}


# Sales Order แยกตารางกายภาพของตัวเอง (ocr.SalesOrder/SalesOrderLine/SalesOrderChat) ต่างจาก AP/II/PODP
# ที่ยังใช้ ocr.Document/DocumentLine/DocumentChat ร่วมกัน (คั่นด้วยคอลัมน์ Module) — คอลัมน์เหมือนกันทุกอย่าง
# ต่างแค่ DocId ของ Sales Order เริ่มนับที่ 100,000,001 เพื่อไม่ให้เลขที่เอกสารชนกับอีกฝั่ง
SO_ID_BASE = 100_000_000


def _tables(module: str) -> tuple[str, str, str]:
    """(ตาราง header, ตารางรายการ, ตารางแชท) ของ module นี้"""
    if module == "SO":
        return "ocr.SalesOrder", "ocr.SalesOrderLine", "ocr.SalesOrderChat"
    return "ocr.Document", "ocr.DocumentLine", "ocr.DocumentChat"


def _tables_for_id(doc_id: int) -> tuple[str, str, str]:
    """เดาว่า doc_id นี้อยู่ตารางไหนจากช่วงเลขที่ — ใช้ตอนยังไม่รู้ module ล่วงหน้า (มีแค่ doc_id จาก path param)"""
    return _tables("SO") if doc_id >= SO_ID_BASE else _tables("")


def get_vendor_memory(module: str, tax_id: str) -> dict:
    if not tax_id:
        return {}
    r = db.query_one("SELECT MemoryJson FROM ocr.VendorMemory WHERE TaxId=? AND Module=?", (tax_id, module))
    return json.loads(r["MemoryJson"]) if r else {}


def apply_vendor_memory(module: str, header: dict) -> list[str]:
    """เติมเฉพาะฟิลด์ที่ OCR อ่านไม่ได้ (ว่างเปล่า) จากข้อมูลที่เคยยืนยันไว้ของคู่ค้าเดิม — ไม่ทับค่าที่ OCR อ่านมาแล้ว
    คืนรายชื่อฟิลด์ที่เติมให้ เพื่อเอาไปแจ้งผู้ใช้อย่างโปร่งใสว่าค่าไหนเป็นการเดาจากประวัติ ไม่ใช่จากเอกสารนี้จริง"""
    tax_id = (header.get(_VENDOR_TAXID_FIELD.get(module, "")) or "").strip()
    mem = get_vendor_memory(module, tax_id)
    if not mem:
        return []
    filled = []
    for f in _MEMORABLE_FIELDS.get(module, []):
        if not str(header.get(f) or "").strip() and str(mem.get(f) or "").strip():
            header[f] = mem[f]
            filled.append(f)
    return filled


def save_vendor_memory(module: str, header: dict) -> None:
    tax_id = (header.get(_VENDOR_TAXID_FIELD.get(module, "")) or "").strip()
    if not tax_id:
        return
    mem = {f: header.get(f) for f in _MEMORABLE_FIELDS.get(module, []) if str(header.get(f) or "").strip()}
    if not mem:
        return
    db.execute("""MERGE ocr.VendorMemory AS t USING (SELECT ? AS TaxId, ? AS Module) AS s
                  ON t.TaxId=s.TaxId AND t.Module=s.Module
                  WHEN MATCHED THEN UPDATE SET MemoryJson=?, UpdatedAt=SYSDATETIME()
                  WHEN NOT MATCHED THEN INSERT(TaxId,Module,MemoryJson) VALUES(?,?,?);""",
               (tax_id, module, json.dumps(mem, ensure_ascii=False), tax_id, module, json.dumps(mem, ensure_ascii=False)))


def log_audit(doc_id: int | None, module: str, action: str, user: str, detail: str = "",
              doc_no: str = "", file_name: str = "", ocr_provider: str = "") -> None:
    """บันทึกประวัติการทำงานต่อเอกสาร (เพิ่ม/แก้ไข/ลบ/อ่าน OCR ใหม่) — ไม่ผูก FK กับ Document
    เพื่อให้ยังดูประวัติได้แม้เอกสารถูกลบไปแล้ว
    ocr_provider: ระบุเฉพาะ action ที่เป็นการอ่านเอกสาร (CREATE/REOCR) — โมเดล/วิธีที่ใช้อ่าน (Gemini/Claude/OCR ฯลฯ)
    เพื่อแสดงเป็นคอลัมน์ Model แยกในหน้า Log กิจกรรม"""
    db.execute("""INSERT ocr.AuditLog(DocId,Module,Action,DocNo,FileName,Detail,PerformedBy,OcrProvider)
                 VALUES(?,?,?,?,?,?,?,?)""",
              (doc_id, module, action, doc_no or None, file_name or None, detail or None, user or "system",
               ocr_provider or None))


def denorm(module: str, h: dict) -> dict:
    if module == "SO":
        return {"DocNo": h.get("poNo"), "DocDate": _d(h.get("poDate")), "PostingDate": None,
                "PartnerName": h.get("customerName"), "PartnerTaxId": h.get("customerTaxId"),
                "Currency": h.get("currency") or "THB", "SubTotal": num(h.get("totalAmount")),
                "VatRate": 0, "VatAmount": 0, "WhtAmount": 0, "TotalAmount": num(h.get("totalAmount"))}
    return {"DocNo": h.get("invoiceNo"), "DocDate": _d(h.get("invoiceDate")),
            "PostingDate": _d(h.get("postingDate") or h.get("invoiceDate")),
            "PartnerName": h.get("vendorName"), "PartnerTaxId": h.get("vendorTaxId"),
            "Currency": h.get("currency") or "THB", "SubTotal": num(h.get("subTotal")),
            # VatRate เป็น decimal(5,2) ในฐานข้อมูล (สูงสุด 999.99) — กันค่าผิดปกติจาก OCR ทำให้ insert พัง
            "VatRate": min(max(num(h.get("vatRate")), 0), 100),
            "VatAmount": num(h.get("vatAmount")),
            "WhtAmount": num(h.get("whtAmount")), "TotalAmount": num(h.get("totalAmount"))}


def create_document(module: str, ext: dict, file_name: str, stored: str, size: int, user: str,
                    ap_doc_category: str = "", duration_ms: int | None = None) -> int:
    filled = apply_vendor_memory(module, ext["header"])
    if filled:
        note = "เติมข้อมูลจากคู่ค้าเดิมที่เคยยืนยันไว้ (ไม่ได้อ่านจากเอกสารนี้โดยตรง): " + ", ".join(filled)
        ext["confidenceNote"] = (ext.get("confidenceNote") + " / " + note) if ext.get("confidenceNote") else note
    h, d = ext["header"], denorm(module, ext["header"])
    doc_t, _, _ = _tables(module)
    doc_id = db.insert_returning_id(f"""
        INSERT {doc_t}(Module,FileName,StoredPath,FileSize,OcrProvider,OcrConfidence,OcrConfidenceNote,
              OcrTokensIn,OcrTokensOut,OcrCost,OcrInputCost,OcrOutputCost,OcrCostCurrency,OcrDurationMs,
              ApDocCategory,Status,
              DocNo,DocDate,PostingDate,PartnerName,PartnerTaxId,Currency,SubTotal,VatRate,VatAmount,
              WhtAmount,TotalAmount,HeaderJson,RawText,CreatedBy)
        VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,
              ?,'NEW', ?,?,?,?,?,?,?,?,?,?,?,?,?,?);
        SELECT SCOPE_IDENTITY();""",
        (module, file_name, stored, size, ext.get("provider"), ext.get("confidence"), ext.get("confidenceNote"),
         ext.get("tokensIn"), ext.get("tokensOut"), ext.get("cost"), ext.get("costIn"), ext.get("costOut"),
         ext.get("costCurrency"), duration_ms,
         ap_doc_category or None,
         d["DocNo"], d["DocDate"], d["PostingDate"], d["PartnerName"], d["PartnerTaxId"], d["Currency"],
         d["SubTotal"], d["VatRate"], d["VatAmount"], d["WhtAmount"], d["TotalAmount"],
         json.dumps(h, ensure_ascii=False), (ext.get("rawText") or "")[:20000], user))
    save_lines(module, doc_id, ext["lines"])
    log_audit(doc_id, module, "CREATE", user, detail="นำเข้าเอกสารใหม่",
             doc_no=d["DocNo"], file_name=file_name, ocr_provider=ext.get("provider") or "")
    return doc_id


def save_lines(module: str, doc_id: int, lines: list[dict]) -> None:
    _, line_t, _ = _tables(module)
    with db.conn() as c:
        cur = c.cursor()
        cur.execute(f"DELETE FROM {line_t} WHERE DocId=?", doc_id)
        for i, l in enumerate(lines):
            cur.execute(f"""INSERT {line_t}(DocId,ItemNo,ExtCode,ExtDesc,Qty,Uom,UnitPrice,Amount,
                              MaterialCode,MapStatus,MapMethod,SapQty,SapUom,UomFactor,ExtraJson)
                           VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                        doc_id, (i + 1) * 10, l.get("extCode"), l.get("desc"), num(l.get("qty")),
                        l.get("uom"), num(l.get("price")), num(l.get("amount")),
                        l.get("materialCode") or None, l.get("mapStatus"), l.get("mapMethod"),
                        l.get("sapQty"), l.get("sapUom"), l.get("uomFactor"),
                        json.dumps(l.get("extra") or {}, ensure_ascii=False) if l.get("extra") else None)


def get_document(doc_id: int) -> dict:
    doc_t, line_t, _ = _tables_for_id(doc_id)
    d = db.query_one(f"SELECT * FROM {doc_t} WHERE DocId=?", (doc_id,))
    if not d:
        raise HTTPException(404, "ไม่พบเอกสาร")
    d = {k: clean(v) for k, v in d.items()}
    lines = rows(db.query(f"SELECT * FROM {line_t} WHERE DocId=? ORDER BY ItemNo", (doc_id,)))
    source_doc_id = d.get("SourceDocId") if doc_t == "ocr.SalesOrder" else None
    split_children = []
    if doc_t == "ocr.SalesOrder" and not source_doc_id:
        split_children = rows(db.query(
            "SELECT DocId, DocNo, Status, TotalAmount FROM ocr.SalesOrder WHERE SourceDocId=? ORDER BY DocId",
            (doc_id,)))
    return {
        "docId": d["DocId"], "module": d["Module"], "fileName": d["FileName"], "status": d["Status"],
        "provider": d["OcrProvider"], "confidence": d["OcrConfidence"],
        "confidenceNote": d.get("OcrConfidenceNote") or "",
        "tokensIn": d.get("OcrTokensIn"), "tokensOut": d.get("OcrTokensOut"),
        "cost": d.get("OcrCost"), "costIn": d.get("OcrInputCost"), "costOut": d.get("OcrOutputCost"),
        "costCurrency": d.get("OcrCostCurrency") or "",
        "apDocCategory": d.get("ApDocCategory") or "", "createdAt": d["CreatedAt"],
        "sapDocNo": d["SapDocNo"], "postedAt": d["PostedAt"], "mapStatus": d["MapStatus"],
        "partnerCode": d["PartnerCode"], "shipToCode": d["ShipToCode"],
        "sourceDocId": source_doc_id, "splitChildren": split_children,
        "header": json.loads(d["HeaderJson"] or "{}"),
        "lines": [{"itemNo": l["ItemNo"], "extCode": l["ExtCode"] or "", "desc": l["ExtDesc"] or "",
                   "qty": l["Qty"], "uom": l["Uom"] or "", "price": l["UnitPrice"], "amount": l["Amount"],
                   "materialCode": l["MaterialCode"] or "", "mapStatus": l["MapStatus"] or "",
                   "mapMethod": l["MapMethod"] or "", "sapQty": l.get("SapQty"),
                   "sapUom": l.get("SapUom") or "", "uomFactor": l.get("UomFactor"),
                   "extra": json.loads(l.get("ExtraJson") or "{}")} for l in lines],
    }


# ---------------------------------------------------------------- แชทสั่งแก้ไขข้อมูล (AI)
_CHAT_FIX_PROVIDER_LABEL = {"claude": ("Claude", "ANTHROPIC_API_KEY"), "gemini": ("Gemini", "GEMINI_API_KEY"),
                            "openai": ("ChatGPT", "OPENAI_API_KEY")}
CHAT_DIR = config.UPLOAD_DIR / "chat"
CHAT_DIR.mkdir(exist_ok=True)


def save_chat_message(doc_id: int, role: str, text: str, image_bytes: bytes | None,
                      image_ext: str, user: str) -> int:
    """บันทึกข้อความแชทถาวรลง DB — ภาพเก็บเป็นไฟล์บนดิสก์ (เหมือนไฟล์เอกสารต้นฉบับ) ไม่เก็บ base64 ใน DB
    เพื่อไม่ให้ตารางบวมเกินจำเป็น"""
    image_path = None
    if image_bytes:
        d = CHAT_DIR / str(doc_id)
        d.mkdir(parents=True, exist_ok=True)
        p = d / f"{datetime.now().strftime('%Y%m%d_%H%M%S_%f')}{image_ext}"
        p.write_bytes(image_bytes)
        image_path = str(p)
    _, _, chat_t = _tables_for_id(doc_id)
    return db.insert_returning_id(
        f"""INSERT {chat_t}(DocId,Role,MessageText,ImagePath,CreatedBy) VALUES(?,?,?,?,?);
           SELECT SCOPE_IDENTITY();""",
        (doc_id, role, text, image_path, user))


def get_chat_history(doc_id: int) -> list[dict]:
    """ประวัติแชทของเอกสารนี้ เรียงเก่า->ใหม่ — ใช้ทั้งแสดงผลหน้าเว็บ และเป็นบริบทส่งให้ Claude ตอบต่อเนื่อง"""
    _, _, chat_t = _tables_for_id(doc_id)
    r = rows(db.query(f"SELECT ChatId, Role, MessageText, ImagePath, CreatedAt FROM {chat_t} "
                      "WHERE DocId=? ORDER BY ChatId", (doc_id,)))
    return [{"chatId": x["ChatId"], "role": x["Role"], "text": x["MessageText"] or "",
            "hasImage": bool(x["ImagePath"]), "createdAt": x["CreatedAt"]} for x in r]


def update_header(doc_id: int, module: str, header: dict) -> None:
    doc_t, _, _ = _tables(module)
    d = denorm(module, header)
    db.execute(f"""UPDATE {doc_t} SET HeaderJson=?, DocNo=?, DocDate=?, PostingDate=?, PartnerName=?,
                    PartnerTaxId=?, Currency=?, SubTotal=?, VatRate=?, VatAmount=?, WhtAmount=?,
                    TotalAmount=?, UpdatedAt=SYSDATETIME() WHERE DocId=?""",
               (json.dumps(header, ensure_ascii=False), d["DocNo"], d["DocDate"], d["PostingDate"],
                d["PartnerName"], d["PartnerTaxId"], d["Currency"], d["SubTotal"], d["VatRate"],
                d["VatAmount"], d["WhtAmount"], d["TotalAmount"], doc_id))


# ======================================================================
# API — health / masters
# ======================================================================
@app.get("/api/health")
def health():
    try:
        info = db.ping()
        counts = db.query_one("""SELECT (SELECT COUNT(*) FROM ocr.Customer) AS customers,
                                        (SELECT COUNT(*) FROM ocr.Vendor)   AS vendors,
                                        (SELECT COUNT(*) FROM ocr.Material) AS materials,
                                        (SELECT COUNT(*) FROM ocr.Document) + (SELECT COUNT(*) FROM ocr.SalesOrder) AS documents""")
        return {"ok": True, "db": info, "counts": counts,
                "ocrProvider": config.OCR_PROVIDER,
                "sapMode": "live" if config.SAP_BASE_URL else "simulate"}
    except Exception as e:                                    # noqa: BLE001
        return JSONResponse({"ok": False, "error": str(e)}, status_code=500)


@app.get("/api/masters")
def masters_all():
    return load_masters()


@app.get("/api/masters/{kind}")
def masters_list(kind: str, q: str = ""):
    m = MASTERS.get(kind)
    if not m:
        raise HTTPException(404, "ไม่รู้จักตาราง master นี้")
    sql = f"SELECT * FROM {m['table']}"
    params: tuple = ()
    if q:
        cols = " OR ".join(f"CAST({c} AS nvarchar(400)) LIKE ?" for c in m["cols"])
        sql += f" WHERE ({cols})"
        params = tuple([f"%{q}%"] * len(m["cols"]))
    sql += " ORDER BY " + ORDER_BY[kind]
    return rows(db.query(sql, params))


@app.post("/api/masters/{kind}")
def masters_create(kind: str, body: dict = Body(...)):
    m = MASTERS.get(kind) or _bad_kind()
    cols = [c for c in m["cols"] if c in body]
    if not cols:
        raise HTTPException(400, "ไม่มีข้อมูลที่จะบันทึก")
    sql = (f"INSERT {m['table']}({','.join(cols)}) VALUES({','.join('?' * len(cols))})")
    db.execute(sql, tuple(body.get(c) for c in cols))
    return {"ok": True}


@app.put("/api/masters/{kind}/{key}")
def masters_update(kind: str, key: str, body: dict = Body(...)):
    m = MASTERS.get(kind) or _bad_kind()
    cols = [c for c in m["cols"] if c in body]
    sets = ", ".join(f"{c}=?" for c in cols) + ", UpdatedAt=SYSDATETIME()"
    n = db.execute(f"UPDATE {m['table']} SET {sets} WHERE {m['key']}=?",
                   tuple([body.get(c) for c in cols] + [key]))
    return {"ok": n > 0}


@app.delete("/api/masters/{kind}/{key}")
def masters_delete(kind: str, key: str):
    m = MASTERS.get(kind) or _bad_kind()
    try:
        n = db.execute(f"DELETE FROM {m['table']} WHERE {m['key']}=?", (key,))
    except Exception as e:                                    # noqa: BLE001
        raise HTTPException(400, "ลบไม่ได้ เนื่องจากมีข้อมูลอื่นอ้างอิงอยู่ (%s)" % str(e)[:200])
    return {"ok": n > 0}


def _bad_kind():
    raise HTTPException(404, "ไม่รู้จักตาราง master นี้")


# ======================================================================
# API — documents
# ======================================================================
@app.get("/api/samples/{module}")
def samples(module: str):
    module = module.upper()
    return [{"index": i, "name": s["name"], "label": s["label"], "confidence": s["confidence"]}
            for i, s in enumerate(ocr_engine.DEMO.get(module, []))]


@app.post("/api/documents/sample")
def create_from_sample(body: dict = Body(...)):
    module = (body.get("module") or "").upper()
    if module not in ("AP", "SO", "II", "PODP"):
        raise HTTPException(400, "module ต้องเป็น AP, SO, II หรือ PODP")
    ap_cat = (body.get("apDocCategory") or "").strip().upper() if module == "AP" else ""
    if ap_cat and ap_cat not in {c["id"] for c in AP_DOC_CATEGORIES}:
        raise HTTPException(400, "ประเภทเอกสารไม่ถูกต้อง")
    idx = int(body.get("index") or 0)
    ext = ocr_engine.demo_doc(module, idx)
    doc_id = create_document(module, ext, ext.get("sampleName", "sample.pdf"), "", 0,
                             body.get("user") or "system", ap_cat)
    return get_document(doc_id)


@app.get("/api/ocr/providers")
def ocr_providers():
    return ocr_engine.OCR_PROVIDERS


@app.post("/api/documents/upload")
def upload(module: str = Form(...), user: str = Form("system"), ocr: str = Form("auto"),
          apDocCategory: str = Form(""), file: UploadFile = File(...)):
    module = module.upper()
    if module not in ("AP", "SO", "II", "PODP"):
        raise HTTPException(400, "module ต้องเป็น AP, SO, II หรือ PODP")
    ap_cat = (apDocCategory or "").strip().upper() if module == "AP" else ""
    if ap_cat and ap_cat not in {c["id"] for c in AP_DOC_CATEGORIES}:
        raise HTTPException(400, "ประเภทเอกสารไม่ถูกต้อง")
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    fname = safe_name(file.filename or "document")
    stored = config.UPLOAD_DIR / f"{stamp}_{fname}"
    with stored.open("wb") as f:
        shutil.copyfileobj(file.file, f)
    size = stored.stat().st_size
    t0 = time.monotonic()
    ext = ocr_engine.extract(stored, module, ocr)
    duration_ms = int((time.monotonic() - t0) * 1000)
    doc_id = create_document(module, ext, fname, str(stored), size, user, ap_cat, duration_ms)
    out = get_document(doc_id)
    out["ocrNote"] = ext.get("_note") or ""
    return out


@app.get("/api/ap-doc-categories")
def ap_doc_categories():
    return AP_DOC_CATEGORIES


_DOC_LIST_COLS = ("DocId,Module,FileName,Status,DocNo,DocDate,PartnerName,PartnerCode,"
                 "TotalAmount,Currency,SapDocNo,PostedAt,CreatedAt,OcrProvider,OcrConfidence,OcrConfidenceNote,"
                 "OcrTokensIn,OcrTokensOut,OcrCost,OcrInputCost,OcrOutputCost,OcrCostCurrency,ApDocCategory")


@app.get("/api/documents")
def list_documents(module: str = "", status: str = "", apDocCategory: str = "", limit: int = 100):
    def where(extra_w: list, extra_p: list) -> tuple[str, list]:
        w, p = list(extra_w), list(extra_p)
        if status:
            w.append("Status=?"); p.append(status.upper())
        if apDocCategory:
            w.append("ApDocCategory=?"); p.append(apDocCategory.upper())
        return (" WHERE " + " AND ".join(w)) if w else "", p

    mod = module.upper()
    if mod == "SO" or mod:
        table = "ocr.SalesOrder" if mod == "SO" else "ocr.Document"
        extra = [] if mod == "SO" else ["Module=?"]
        extra_p = [] if mod == "SO" else [mod]
        w, p = where(extra, extra_p)
        sql = f"SELECT TOP (?) {_DOC_LIST_COLS} FROM {table}{w} ORDER BY DocId DESC"
        return rows(db.query(sql, tuple([limit] + p)))

    # ไม่ระบุ module — รวมทุกโมดูล (ocr.Document ที่ใช้ร่วมกัน + ocr.SalesOrder ที่แยกตาราง)
    w1, p1 = where([], [])
    w2, p2 = where([], [])
    sql = (f"SELECT TOP (?) * FROM ("
          f"SELECT {_DOC_LIST_COLS} FROM ocr.Document{w1} "
          f"UNION ALL SELECT {_DOC_LIST_COLS} FROM ocr.SalesOrder{w2}) x ORDER BY DocId DESC")
    return rows(db.query(sql, tuple([limit] + p1 + p2)))


@app.get("/api/documents/{doc_id}")
def read_document(doc_id: int):
    return get_document(doc_id)


@app.put("/api/documents/{doc_id}")
def save_document(doc_id: int, body: dict = Body(...)):
    doc = get_document(doc_id)
    if doc["status"] == "POSTED":
        raise HTTPException(400, "เอกสารถูกส่งเข้า SAP แล้ว แก้ไขไม่ได้")
    doc_t, _, _ = _tables(doc["module"])
    update_header(doc_id, doc["module"], body.get("header") or doc["header"])
    if body.get("lines") is not None:
        save_lines(doc["module"], doc_id, body["lines"])
    db.execute(f"UPDATE {doc_t} SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END,"
               " MapStatus=NULL, MapMessage=NULL WHERE DocId=?", (doc_id,))
    log_audit(doc_id, doc["module"], "UPDATE", body.get("user") or "system",
             detail="แก้ไขข้อมูลเอกสาร", file_name=doc["fileName"])
    return get_document(doc_id)


@app.post("/api/documents/{doc_id}/category")
def set_doc_category(doc_id: int, body: dict = Body(...)):
    """ตั้งค่าประเภทเอกสาร AP Invoice (Trade/Non-Trade) — ผู้ใช้เลือกเอง ไม่เกี่ยวกับ OCR/Mapping"""
    cat = (body.get("apDocCategory") or "").strip().upper()
    if cat and cat not in {c["id"] for c in AP_DOC_CATEGORIES}:
        raise HTTPException(400, "ประเภทเอกสารไม่ถูกต้อง")
    doc_t, _, _ = _tables_for_id(doc_id)
    db.execute(f"UPDATE {doc_t} SET ApDocCategory=? WHERE DocId=?", (cat or None, doc_id))
    doc = get_document(doc_id)
    log_audit(doc_id, doc["module"], "UPDATE", body.get("user") or "system",
             detail="เปลี่ยนประเภทเอกสารเป็น: " + (cat or "-"), file_name=doc["fileName"])
    return doc


@app.delete("/api/documents/{doc_id}")
def delete_document(doc_id: int, user: str = "system"):
    doc = get_document(doc_id)
    doc_t, _, _ = _tables(doc["module"])
    db.execute(f"DELETE FROM {doc_t} WHERE DocId=?", (doc_id,))
    log_audit(doc_id, doc["module"], "DELETE", user, detail="ลบเอกสาร",
             doc_no=doc["header"].get("invoiceNo") or doc["header"].get("poNo") or "", file_name=doc["fileName"])
    return {"ok": True}


@app.post("/api/documents/{doc_id}/reocr")
def reocr_document(doc_id: int, body: dict = Body(default={})):
    """อ่านไฟล์ต้นฉบับใหม่อีกครั้ง — ระบุ body {"ocr": "tesseract"} เพื่อเลือก engine ได้ (ค่าเริ่มต้น auto)"""
    doc_t, _, _ = _tables_for_id(doc_id)
    d = db.query_one(f"SELECT Module, StoredPath, FileName FROM {doc_t} WHERE DocId=?", (doc_id,))
    if not d:
        raise HTTPException(404, "ไม่พบเอกสาร")
    doc = get_document(doc_id)
    if doc["status"] == "POSTED":
        raise HTTPException(400, "เอกสารถูกส่งเข้า SAP แล้ว อ่านใหม่ไม่ได้")
    if doc["status"] == "SPLIT":
        raise HTTPException(400, "เอกสารนี้ถูก Split ไปแล้ว อ่านใหม่ไม่ได้ (เก็บไว้เป็นเอกสารอ้างอิงของเอกสารที่แยกไป)")
    if doc.get("sourceDocId"):
        raise HTTPException(400, "เอกสารนี้เป็นส่วนที่ Split มาจากเอกสารอื่น อ่านใหม่ไม่ได้ (จะทับรายการที่แยกไว้ด้วยข้อมูลเอกสารเต็ม)")
    if not d["StoredPath"] or not Path(d["StoredPath"]).exists():
        raise HTTPException(400, "ไม่พบไฟล์ต้นฉบับ (เอกสารนี้อาจสร้างจากชุดตัวอย่าง)")

    t0 = time.monotonic()
    ext = ocr_engine.extract(Path(d["StoredPath"]), d["Module"], body.get("ocr") or "auto")
    duration_ms = int((time.monotonic() - t0) * 1000)
    filled = apply_vendor_memory(d["Module"], ext["header"])
    if filled:
        note = "เติมข้อมูลจากคู่ค้าเดิมที่เคยยืนยันไว้ (ไม่ได้อ่านจากเอกสารนี้โดยตรง): " + ", ".join(filled)
        ext["confidenceNote"] = (ext.get("confidenceNote") + " / " + note) if ext.get("confidenceNote") else note
    dn = denorm(d["Module"], ext["header"])
    db.execute(f"""UPDATE {doc_t} SET OcrProvider=?, OcrConfidence=?, OcrConfidenceNote=?,
                    OcrTokensIn=?, OcrTokensOut=?, OcrCost=?, OcrInputCost=?, OcrOutputCost=?, OcrCostCurrency=?,
                    OcrDurationMs=?, HeaderJson=?, RawText=?,
                    DocNo=?, DocDate=?, PostingDate=?, PartnerName=?, PartnerTaxId=?, Currency=?,
                    SubTotal=?, VatRate=?, VatAmount=?, WhtAmount=?, TotalAmount=?,
                    Status='NEW', MapStatus=NULL, MapMessage=NULL, PartnerCode=NULL, ShipToCode=NULL,
                    SapPartnerCode=NULL, SapShipToCode=NULL, UpdatedAt=SYSDATETIME()
                  WHERE DocId=?""",
               (ext.get("provider"), ext.get("confidence"), ext.get("confidenceNote"),
                ext.get("tokensIn"), ext.get("tokensOut"), ext.get("cost"), ext.get("costIn"), ext.get("costOut"),
                ext.get("costCurrency"), duration_ms,
                json.dumps(ext["header"], ensure_ascii=False), (ext.get("rawText") or "")[:20000],
                dn["DocNo"], dn["DocDate"], dn["PostingDate"], dn["PartnerName"], dn["PartnerTaxId"],
                dn["Currency"], dn["SubTotal"], dn["VatRate"], dn["VatAmount"], dn["WhtAmount"],
                dn["TotalAmount"], doc_id))
    save_lines(d["Module"], doc_id, ext["lines"])
    log_audit(doc_id, d["Module"], "REOCR", body.get("user") or "system",
             detail="อ่านเอกสารใหม่", file_name=d["FileName"], ocr_provider=ext.get("provider") or "")
    out = get_document(doc_id)
    out["ocrNote"] = ext.get("_note") or ("อ่านเอกสารใหม่เรียบร้อย (%s)" % ext.get("provider"))
    return out


@app.get("/api/documents/{doc_id}/chat")
def read_chat_history(doc_id: int):
    return get_chat_history(doc_id)


@app.get("/api/documents/{doc_id}/chat/{chat_id}/image")
def chat_image(doc_id: int, chat_id: int):
    _, _, chat_t = _tables_for_id(doc_id)
    r = db.query_one(f"SELECT ImagePath FROM {chat_t} WHERE DocId=? AND ChatId=?", (doc_id, chat_id))
    if not r or not r["ImagePath"] or not Path(r["ImagePath"]).exists():
        raise HTTPException(404, "ไม่พบภาพ")
    return FileResponse(r["ImagePath"])


@app.post("/api/documents/{doc_id}/chat-fix")
def chat_fix_document(doc_id: int, body: dict = Body(...)):
    """เมนู "แชทสั่งแก้" — ผู้ใช้พิมพ์บอกจุดที่ OCR อ่านผิดด้วยภาษาธรรมดา หรือถามคำถามเกี่ยวกับเอกสารก็ได้
    (แนบภาพประกอบได้ เช่น capture จุดที่ผิดจาก Review Document) ให้ Claude แก้เฉพาะจุดนั้นในเอกสารนี้
    บันทึกบทสนทนาถาวรลง ocr.DocumentChat เพื่อดูย้อนหลังได้ และส่งกลับไปเป็นบริบทให้ตอบต่อเนื่องได้
    (ไม่บันทึกลง Master Data ถาวร ต่างจาก /learn — ต้องตั้งค่า ANTHROPIC_API_KEY ใน .env ก่อนใช้งานได้)"""
    message = (body.get("message") or "").strip()
    image_data_url = (body.get("image") or "").strip()
    user = body.get("user") or "system"
    if not message and not image_data_url:
        raise HTTPException(400, "กรุณาพิมพ์ข้อความหรือแนบภาพ")
    doc = get_document(doc_id)
    if doc["status"] == "POSTED":
        raise HTTPException(400, "เอกสารถูกส่งเข้า SAP แล้ว แก้ไขไม่ได้")

    image_b64, image_media_type, image_bytes, image_ext = None, "image/png", None, ".png"
    if image_data_url:
        m = re.match(r"^data:(image/([a-zA-Z0-9.+-]+));base64,(.+)$", image_data_url, re.S)
        if not m:
            raise HTTPException(400, "รูปแบบภาพไม่ถูกต้อง")
        image_media_type, subtype, image_b64 = m.group(1), m.group(2), m.group(3)
        image_ext = "." + (subtype if re.fullmatch(r"[a-zA-Z0-9]+", subtype) else "png")
        try:
            image_bytes = base64.b64decode(image_b64)
        except Exception:
            raise HTTPException(400, "ถอดรหัสภาพไม่สำเร็จ")

    provider = body.get("provider") or "claude"
    if provider not in _CHAT_FIX_PROVIDER_LABEL:
        provider = "claude"

    history = get_chat_history(doc_id)                    # ก่อนบันทึกข้อความใหม่ — ใช้เป็นบริบทของเทิร์นนี้
    save_chat_message(doc_id, "user", message, image_bytes, image_ext, user)

    prompt_message = message or "ดูภาพที่แนบมา แล้วแก้ไขข้อมูลในเอกสารให้ถูกต้องตามสิ่งที่เห็นในภาพ"
    result = ocr_engine.chat_fix_document(doc["module"], doc["header"], doc["lines"], history, prompt_message,
                                          image_b64, image_media_type, provider)
    if not result:
        label, env_var = _CHAT_FIX_PROVIDER_LABEL[provider]
        raise HTTPException(400, f"เชื่อมต่อ {label} ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า {env_var} ใน .env")

    save_chat_message(doc_id, "assistant", result["reply"], None, ".png", "AI")

    update_header(doc_id, doc["module"], result["header"])
    save_lines(doc["module"], doc_id, result["lines"])
    doc_t, _, _ = _tables(doc["module"])
    db.execute(f"UPDATE {doc_t} SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END,"
               " MapStatus=NULL, MapMessage=NULL WHERE DocId=?", (doc_id,))
    log_audit(doc_id, doc["module"], "UPDATE", user,
             detail="แก้ไขผ่านแชท AI: " + message[:200], file_name=doc["fileName"])
    out = get_document(doc_id)
    return {"reply": result["reply"], "document": out}


@app.get("/api/documents/{doc_id}/rawtext")
def raw_text(doc_id: int):
    doc_t, _, _ = _tables_for_id(doc_id)
    r = db.query_one(f"SELECT RawText FROM {doc_t} WHERE DocId=?", (doc_id,))
    if not r:
        raise HTTPException(404, "ไม่พบเอกสาร")
    return {"text": r["RawText"] or ""}


@app.get("/api/documents/{doc_id}/file")
def document_file(doc_id: int):
    doc_t, _, _ = _tables_for_id(doc_id)
    d = db.query_one(f"SELECT StoredPath, FileName FROM {doc_t} WHERE DocId=?", (doc_id,))
    if not d or not d["StoredPath"] or not Path(d["StoredPath"]).exists():
        raise HTTPException(404, "ไม่พบไฟล์ต้นฉบับ")
    return FileResponse(d["StoredPath"], filename=d["FileName"])


# ---------------------------------------------------------------- mapping
@app.post("/api/documents/{doc_id}/map")
def map_document(doc_id: int, body: dict = Body(default={})):
    doc = get_document(doc_id)
    manual = body.get("manual") or {}
    # ถือว่าเป็นการ "แก้ไข" จริง (บันทึก log) เมื่อ header/lines ต่างจากที่เก็บไว้ หรือมีการเลือก manual override
    # ไม่ log ตอนเปิดเอกสารแล้วระบบเรียก map แบบเงียบเพื่อรีเฟรชผลเท่านั้น (header/lines/manual เหมือนเดิมทุกอย่าง)
    edited = bool(manual) or (bool(body.get("header")) and body["header"] != doc["header"]) or \
        (body.get("lines") is not None and body["lines"] != doc["lines"])
    doc_t, line_t, _ = _tables(doc["module"])
    if body.get("header"):
        update_header(doc_id, doc["module"], body["header"])
        doc["header"] = body["header"]
    if body.get("lines") is not None:
        save_lines(doc["module"], doc_id, body["lines"])
        doc["lines"] = get_document(doc_id)["lines"]

    res = run_mapping(doc["module"], doc["header"], doc["lines"], load_masters(), manual)

    with db.conn() as c:
        cur = c.cursor()
        for i, l in enumerate(doc["lines"]):
            r = res["lines"][i]
            u = r.get("uom") or {}
            cur.execute(f"UPDATE {line_t} SET MaterialCode=?, MapStatus=?, MapMethod=?, "
                        "SapQty=?, SapUom=?, UomFactor=?, SapMaterialCode=?, SapUomIso=? "
                        "WHERE DocId=? AND ItemNo=?",
                        r["code"] or None, r["status"], r["method"],
                        u.get("sapQty") if u.get("status") in ("ok", "convert") else None,
                        u.get("sapUom") or None,
                        u.get("factor") if u.get("status") in ("ok", "convert") else None,
                        r.get("sapCode") or None, u.get("iso") or None,
                        doc_id, l["itemNo"])
        head = res["header"]
        partner = (head.get("customer") or head.get("vendor") or {}).get("code") or None
        shipto = (head.get("shipTo") or {}).get("code") or None
        sap_partner = (head.get("customer") or head.get("vendor") or {}).get("sapCode") or None
        sap_shipto = (head.get("shipTo") or {}).get("sapCode") or None
        cur.execute(f"""UPDATE {doc_t} SET SapPartnerCode=?, SapShipToCode=?,
                         PartnerCode=?, ShipToCode=?, MapStatus=?, MapMessage=?,
                         Status=CASE WHEN Status='POSTED' THEN 'POSTED' WHEN ?=1 THEN 'MAPPED' ELSE 'INCOMPLETE' END,
                         UpdatedAt=SYSDATETIME() WHERE DocId=?""",
                    sap_partner, sap_shipto, partner, shipto, "PASS" if res["pass"] else "FAIL",
                    json.dumps({"errors": res["errors"], "warns": res["warns"]}, ensure_ascii=False),
                    1 if res["pass"] else 0, doc_id)
    if res["pass"]:
        # Mapping ผ่านแล้ว = ผู้ใช้ตรวจสอบ/แก้ไข header จนถูกต้องแล้ว — จำค่าฟิลด์ที่คงที่ต่อคู่ค้าไว้ใช้เดาเอกสารถัดไป
        save_vendor_memory(doc["module"], doc["header"])
    if edited:
        log_audit(doc_id, doc["module"], "UPDATE", body.get("user") or "system",
                 detail="แก้ไขข้อมูลเอกสาร / Mapping", file_name=doc["fileName"])
    res["document"] = get_document(doc_id)
    return res


@app.post("/api/documents/{doc_id}/learn")
def learn_mapping(doc_id: int, body: dict = Body(...)):
    """บันทึกคู่ 'รหัสสินค้าคู่ค้า -> Material' ลง Master เพื่อให้ครั้งหน้าจับคู่อัตโนมัติ"""
    doc = get_document(doc_id)
    partner = body.get("partnerCode") or doc.get("partnerCode")
    ext_code, ext_desc, mat = body.get("extCode"), body.get("extDesc"), body.get("materialCode")
    if not (partner and mat):
        raise HTTPException(400, "ต้องระบุคู่ค้าและ Material")
    if doc["module"] == "SO":
        db.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode=? AND ExtCode=?)
                      INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES(?,?,?,?)""",
                   (partner, ext_code, partner, ext_code, ext_desc, mat))
    else:
        db.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.VendorMaterial WHERE VendorCode=? AND ExtCode=?)
                      INSERT ocr.VendorMaterial(VendorCode,ExtCode,ExtDesc,MaterialCode) VALUES(?,?,?,?)""",
                   (partner, ext_code, partner, ext_code, ext_desc, mat))
    return {"ok": True}


@app.post("/api/documents/{doc_id}/split")
def split_document(doc_id: int, body: dict = Body(...)):
    """แยกเอกสาร PO (Sales Order) 1 ใบ ออกเป็นหลาย Sales Order — ผู้ใช้เลือกเองว่ารายการไหนไปกลุ่มไหน
    (body.assign: {itemNo(str): กลุ่ม}) เอกสารต้นฉบับยังอยู่ครบเป็นเอกสารอ้างอิง (Status='SPLIT') ไม่ถูกลบ/แก้ไขรายการ"""
    doc = get_document(doc_id)
    if doc["module"] != "SO":
        raise HTTPException(400, "Split ใช้ได้เฉพาะเอกสาร Sales Order")
    if doc["status"] in ("POSTED", "SPLIT"):
        raise HTTPException(400, "เอกสารนี้ส่งเข้า SAP แล้ว หรือถูก Split ไปแล้ว")
    if doc.get("sourceDocId"):
        raise HTTPException(400, "เอกสารที่แยกมาจากเอกสารอื่นแล้ว ไม่สามารถแยกซ้ำได้")

    assign = body.get("assign") or {}
    user = body.get("user") or "system"
    groups: dict[int, list[dict]] = {}
    for l in doc["lines"]:
        try:
            g = int(assign.get(str(l["itemNo"])) or 0)
        except (TypeError, ValueError):
            g = 0
        if g > 0:
            groups.setdefault(g, []).append(l)
    if len(groups) < 2:
        raise HTTPException(400, "ต้องแบ่งอย่างน้อย 2 กลุ่มจึงจะ Split ได้")

    doc_t, _, _ = _tables("SO")
    src = db.query_one(f"SELECT StoredPath, FileSize, RawText FROM {doc_t} WHERE DocId=?", (doc_id,))
    created = []
    for g_no in sorted(groups):
        g_lines = groups[g_no]
        header = dict(doc["header"])
        g_total = sum(num(l.get("amount")) for l in g_lines)
        header["totalAmount"] = g_total
        header["subTotal"] = g_total
        if header.get("poNo"):
            header["poNo"] = f'{header["poNo"]}-{g_no}'
        d = denorm("SO", header)
        new_id = db.insert_returning_id(f"""
            INSERT {doc_t}(Module,FileName,StoredPath,FileSize,OcrProvider,OcrConfidence,OcrConfidenceNote,Status,
                  DocNo,DocDate,PostingDate,PartnerName,PartnerTaxId,Currency,SubTotal,VatRate,VatAmount,
                  WhtAmount,TotalAmount,HeaderJson,RawText,CreatedBy,SourceDocId)
            VALUES('SO',?,?,?,?,?,?,'NEW',?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);
            SELECT SCOPE_IDENTITY();""",
            (doc["fileName"], src["StoredPath"], src["FileSize"], doc["provider"], doc["confidence"],
             doc["confidenceNote"],
             d["DocNo"], d["DocDate"], d["PostingDate"], d["PartnerName"], d["PartnerTaxId"], d["Currency"],
             d["SubTotal"], d["VatRate"], d["VatAmount"], d["WhtAmount"], d["TotalAmount"],
             json.dumps(header, ensure_ascii=False), src["RawText"], user, doc_id))
        save_lines("SO", new_id, [{
            "extCode": l.get("extCode"), "desc": l.get("desc"), "qty": l.get("qty"), "uom": l.get("uom"),
            "price": l.get("price"), "amount": l.get("amount"), "materialCode": l.get("materialCode"),
            "extra": l.get("extra"),
        } for l in g_lines])
        log_audit(new_id, "SO", "CREATE", user, detail="Split จากเอกสาร #%d" % doc_id,
                 doc_no=d["DocNo"], file_name=doc["fileName"])
        created.append(get_document(new_id))

    db.execute(f"UPDATE {doc_t} SET Status='SPLIT', UpdatedAt=SYSDATETIME() WHERE DocId=?", (doc_id,))
    log_audit(doc_id, "SO", "UPDATE", user,
             detail="แยกเอกสารออกเป็น %d Sales Order: %s" % (len(groups), ", ".join(str(c["docId"]) for c in created)),
             file_name=doc["fileName"])
    return {"source": get_document(doc_id), "created": created}


# ---------------------------------------------------------------- SAP
def stored_manual(doc: dict) -> dict:
    """ประกอบค่าที่ 'ยืนยันไว้แล้ว' ในฐานข้อมูล (รวมค่าที่ผู้ใช้เลือกเอง) กลับมาเป็น manual override
    เพื่อให้ payload / การส่ง SAP ตรงกับที่ผู้ใช้เห็นบนหน้าจอตอนกด Mapping"""
    m: dict = {"header": {}, "lines": {}}
    if doc["module"] == "SO":
        if doc.get("partnerCode"):
            m["header"]["customer"] = doc["partnerCode"]
        if doc.get("shipToCode"):
            m["header"]["shipTo"] = doc["shipToCode"]
    elif doc.get("partnerCode"):
        m["header"]["vendor"] = doc["partnerCode"]
    for i, l in enumerate(doc["lines"]):
        if l.get("materialCode"):
            m["lines"][str(i)] = l["materialCode"]
    return m


def _payload_for(doc: dict, manual: dict | None = None) -> tuple[dict, dict]:
    masters = load_masters()
    res = run_mapping(doc["module"], doc["header"], doc["lines"], masters,
                      manual if manual is not None else stored_manual(doc))
    if doc["module"] == "SO":
        pm = next((c for c in masters["customers"]
                   if c["CustomerCode"] == res["header"]["customer"]["code"]), {})
    else:
        pm = next((v for v in masters["vendors"]
                   if v["VendorCode"] == res["header"]["vendor"]["code"]), {})
    payload = sap.build_payload(doc["module"], doc["header"], doc["lines"], res, pm,
                                source={"docId": doc["docId"], "file": doc["fileName"],
                                        "ocrProvider": doc["provider"], "confidence": doc["confidence"]})
    return payload, res


@app.get("/api/documents/{doc_id}/payload")
def preview_payload(doc_id: int):
    doc = get_document(doc_id)
    payload, res = _payload_for(doc)
    return {"payload": payload, "pass": res["pass"], "errors": res["errors"]}


@app.post("/api/documents/{doc_id}/post")
def post_document(doc_id: int, body: dict = Body(default={})):
    doc = get_document(doc_id)
    if doc["status"] == "POSTED":
        raise HTTPException(400, "เอกสารนี้ถูกส่งเข้า SAP แล้ว (%s)" % doc["sapDocNo"])
    if doc["status"] != "MAPPED" or doc["mapStatus"] != "PASS":
        raise HTTPException(400, "ต้องกด Mapping ให้ผ่านก่อนส่งเข้า SAP")
    payload, res = _payload_for(doc)
    if not res["pass"]:
        raise HTTPException(400, "Mapping ยังไม่ผ่าน — ไม่พบข้อมูล %d จุด" % len(res["errors"]))

    user = body.get("user") or "system"
    doc_t, _, _ = _tables(doc["module"])
    r = sap.post(doc["module"], payload)
    with db.conn() as c:
        cur = c.cursor()
        cur.execute("""INSERT ocr.PostLog(DocId,Module,SapDocNo,Endpoint,PayloadJson,Success,Message,PostedBy)
                       VALUES(?,?,?,?,?,?,?,?)""",
                    doc_id, doc["module"], r.get("sapDocNo"), r.get("endpoint"),
                    json.dumps(payload, ensure_ascii=False), 1 if r["success"] else 0,
                    r.get("message"), user)
        if r["success"]:
            cur.execute(f"""UPDATE {doc_t} SET Status='POSTED', SapDocNo=?, PostedAt=SYSDATETIME(),
                             PostedBy=?, UpdatedAt=SYSDATETIME() WHERE DocId=?""",
                        r.get("sapDocNo"), user, doc_id)
    return {**r, "document": get_document(doc_id)}


@app.get("/api/logs")
def logs(limit: int = 200):
    return rows(db.query(f"""SELECT TOP (?) l.LogId,l.DocId,l.Module,l.SapDocNo,l.Endpoint,l.Success,
                                   l.Message,l.PostedAt,l.PostedBy,
                                   d.FileName,d.DocNo,d.PartnerName,d.TotalAmount,d.Currency,d.OcrProvider,
                                   (CASE WHEN l.DocId>={SO_ID_BASE}
                                         THEN (SELECT COUNT(*) FROM ocr.SalesOrderLine WHERE DocId=l.DocId)
                                         ELSE (SELECT COUNT(*) FROM ocr.DocumentLine WHERE DocId=l.DocId) END) AS Lines
                            FROM ocr.PostLog l LEFT JOIN (
                                SELECT DocId,FileName,DocNo,PartnerName,TotalAmount,Currency,OcrProvider FROM ocr.Document
                                UNION ALL
                                SELECT DocId,FileName,DocNo,PartnerName,TotalAmount,Currency,OcrProvider FROM ocr.SalesOrder
                            ) d ON d.DocId=l.DocId
                            ORDER BY l.LogId DESC""", (limit,)))


@app.get("/api/logs/{log_id}/payload")
def log_payload(log_id: int):
    r = db.query_one("SELECT PayloadJson FROM ocr.PostLog WHERE LogId=?", (log_id,))
    if not r:
        raise HTTPException(404, "ไม่พบ log")
    return json.loads(r["PayloadJson"] or "{}")


@app.get("/api/audit-logs")
def audit_logs(module: str = "", docId: int | None = None, limit: int = 200):
    """ประวัติการทำงานต่อเอกสาร (เพิ่ม/แก้ไข/ลบ/อ่าน OCR ใหม่) — ดูแยกตาม Module ได้"""
    w, p = [], []
    if module:
        w.append("Module=?"); p.append(module.upper())
    if docId:
        w.append("DocId=?"); p.append(docId)
    sql = "SELECT TOP (?) LogId,DocId,Module,Action,DocNo,FileName,Detail,PerformedBy,CreatedAt,OcrProvider FROM ocr.AuditLog"
    if w:
        sql += " WHERE " + " AND ".join(w)
    sql += " ORDER BY LogId DESC"
    return rows(db.query(sql, tuple([limit] + p)))


@app.get("/api/dashboard")
def dashboard(days: int = 7):
    """สรุปข้อมูลหน้า Overview — สถานะเอกสาร (+ เทียบช่วง `days` วันล่าสุด), เอกสารตามประเภท,
    ประสิทธิภาพ OCR (ความแม่นยำ/เวลาเฉลี่ย/สัดส่วนที่ผู้ใช้ต้องแก้ไข/Token วันนี้), กราฟรายวัน, และรายการล่าสุด"""
    days = max(1, min(days, 90))
    today = date.today()
    start = today - timedelta(days=days - 1)

    by_status = rows(db.query("""SELECT Module, Status, COUNT(*) AS Cnt, SUM(TotalAmount) AS Amount FROM (
                                    SELECT Module, Status, TotalAmount FROM ocr.Document
                                    UNION ALL
                                    SELECT Module, Status, TotalAmount FROM ocr.SalesOrder
                                  ) x GROUP BY Module, Status"""))

    all_docs = rows(db.query("""SELECT DocId, Module, Status, CreatedAt, OcrConfidence, OcrProvider, OcrDurationMs,
                                       OcrCost, OcrTokensIn, OcrTokensOut, OcrCostCurrency
                                FROM (
                                    SELECT DocId, Module, Status, CreatedAt, OcrConfidence, OcrProvider, OcrDurationMs,
                                           OcrCost, OcrTokensIn, OcrTokensOut, OcrCostCurrency FROM ocr.Document
                                    UNION ALL
                                    SELECT DocId, Module, Status, CreatedAt, OcrConfidence, OcrProvider, OcrDurationMs,
                                           OcrCost, OcrTokensIn, OcrTokensOut, OcrCostCurrency FROM ocr.SalesOrder
                                ) x"""))

    # ---- สถานะ + แนวโน้ม: เทียบเอกสารที่สร้างมาก่อนช่วง `days` วันล่าสุด (baseline) กับจำนวนปัจจุบัน (ทุกสถานะยังนับจากของจริงเสมอ) ----
    statuses = ["NEW", "INCOMPLETE", "MAPPED", "POSTED", "SPLIT"]
    status_counts = {s: 0 for s in statuses}
    status_counts["total"] = 0
    baseline_counts = {s: 0 for s in statuses}
    baseline_counts["total"] = 0
    by_module_recent: dict[str, int] = {}
    cost_by_module: dict[str, dict] = {}
    cost_currency = "USD"
    confidences, durations = [], []
    start_iso = start.isoformat()
    for r in all_docs:
        st = r["Status"]
        created_date = (r["CreatedAt"] or "")[:10]
        if st in status_counts:
            status_counts[st] += 1
        status_counts["total"] += 1
        if created_date and created_date < start_iso:
            if st in baseline_counts:
                baseline_counts[st] += 1
            baseline_counts["total"] += 1
        if created_date and created_date >= start_iso:
            by_module_recent[r["Module"]] = by_module_recent.get(r["Module"], 0) + 1
            cm = cost_by_module.setdefault(r["Module"], {"count": 0, "tokens": 0, "cost": 0.0})
            cm["count"] += 1
            cm["tokens"] += (r.get("OcrTokensIn") or 0) + (r.get("OcrTokensOut") or 0)
            cm["cost"] += float(r.get("OcrCost") or 0)
            if r.get("OcrCostCurrency"):
                cost_currency = r["OcrCostCurrency"]
        if r.get("OcrConfidence") is not None:
            confidences.append(float(r["OcrConfidence"]))
        if r.get("OcrDurationMs"):
            durations.append(r["OcrDurationMs"])

    def pct(cur: int, base: int) -> float:
        if base == 0:
            return 100.0 if cur > 0 else 0.0
        return round((cur - base) / base * 100, 1)

    trend = {k: pct(status_counts.get(k, 0), baseline_counts.get(k, 0)) for k in statuses + ["total"]}
    by_module = [{"module": m, "count": c} for m, c in sorted(by_module_recent.items(), key=lambda x: -x[1])]
    cost_by_module_list = [{"module": m, "count": v["count"], "tokens": v["tokens"], "cost": round(v["cost"], 4),
                            "costCurrency": cost_currency}
                           for m, v in sorted(cost_by_module.items(), key=lambda x: -x[1]["cost"])]

    # ---- ประสิทธิภาพ OCR ----
    all_doc_ids = {r["DocId"] for r in all_docs}
    edited_doc_ids = {r["DocId"] for r in rows(db.query(
        "SELECT DISTINCT DocId FROM ocr.AuditLog WHERE Action='UPDATE'"))}
    tokens_today_row = db.query_one("""SELECT SUM(ISNULL(OcrTokensIn,0)+ISNULL(OcrTokensOut,0)) AS T FROM (
                                          SELECT CreatedAt, OcrTokensIn, OcrTokensOut FROM ocr.Document
                                          UNION ALL
                                          SELECT CreatedAt, OcrTokensIn, OcrTokensOut FROM ocr.SalesOrder
                                        ) x WHERE CAST(CreatedAt AS DATE)=CAST(SYSDATETIME() AS DATE)""")
    ocr_perf = {
        "avgConfidencePct": round(sum(confidences) / len(confidences) * 100, 1) if confidences else None,
        "avgDurationSec": round(sum(durations) / len(durations) / 1000, 1) if durations else None,
        "pctEditedByUser": round(len(edited_doc_ids & all_doc_ids) / len(all_doc_ids) * 100, 1) if all_doc_ids else 0.0,
        "tokensToday": int((tokens_today_row or {}).get("T") or 0),
    }

    # ---- ปริมาณเอกสาร + OCR สำเร็จ รายวัน (สำหรับกราฟเส้น) ----
    daily_raw = rows(db.query("""SELECT CAST(CreatedAt AS DATE) AS Day, COUNT(*) AS DocCount,
                                    SUM(CASE WHEN OcrProvider IS NOT NULL AND OcrProvider<>'failed' THEN 1 ELSE 0 END) AS OkCount
                             FROM (
                               SELECT CreatedAt, OcrProvider FROM ocr.Document WHERE CreatedAt >= ?
                               UNION ALL
                               SELECT CreatedAt, OcrProvider FROM ocr.SalesOrder WHERE CreatedAt >= ?
                             ) x GROUP BY CAST(CreatedAt AS DATE)""", (start, start)))
    by_day = {str(r["Day"])[:10]: r for r in daily_raw}
    ocr_daily = []
    for i in range(days):
        key = (start + timedelta(days=i)).isoformat()
        r = by_day.get(key)
        ocr_daily.append({"date": key, "docCount": int(r["DocCount"]) if r else 0,
                          "okCount": int(r["OkCount"] or 0) if r else 0})

    recent = rows(db.query("""SELECT TOP 8 DocId,Module,FileName,DocNo,PartnerName,Status,TotalAmount,
                                     SapDocNo,CreatedAt,UpdatedAt,CreatedBy,PostedBy FROM (
                                       SELECT DocId,Module,FileName,DocNo,PartnerName,Status,TotalAmount,SapDocNo,
                                              CreatedAt,UpdatedAt,CreatedBy,PostedBy FROM ocr.Document
                                       UNION ALL
                                       SELECT DocId,Module,FileName,DocNo,PartnerName,Status,TotalAmount,SapDocNo,
                                              CreatedAt,UpdatedAt,CreatedBy,PostedBy FROM ocr.SalesOrder
                                     ) x ORDER BY DocId DESC"""))

    return {"byStatus": by_status, "statusCounts": status_counts, "trend": trend, "byModule": by_module,
            "costByModule": cost_by_module_list, "ocrPerf": ocr_perf, "ocrDaily": ocr_daily, "recent": recent}


@app.get("/api/ocr-usage")
def ocr_usage(days: int = 7):
    """สรุปการใช้งาน OCR รายวัน (จำนวนเอกสาร, Token, ค่าใช้จ่าย) — days: 1=วันนี้, 7/15/30=ย้อนหลัง N วัน (รวมวันนี้)"""
    days = max(1, min(days, 90))
    today = date.today()
    start = today - timedelta(days=days - 1)
    raw = rows(db.query("""SELECT CAST(CreatedAt AS DATE) AS Day, COUNT(*) AS DocCount,
                                  SUM(ISNULL(OcrTokensIn,0)) AS TokensIn, SUM(ISNULL(OcrTokensOut,0)) AS TokensOut,
                                  SUM(ISNULL(OcrCost,0)) AS Cost
                           FROM (
                             SELECT CreatedAt, OcrTokensIn, OcrTokensOut, OcrCost FROM ocr.Document WHERE CreatedAt >= ?
                             UNION ALL
                             SELECT CreatedAt, OcrTokensIn, OcrTokensOut, OcrCost FROM ocr.SalesOrder WHERE CreatedAt >= ?
                           ) x GROUP BY CAST(CreatedAt AS DATE)""", (start, start)))
    by_day = {str(r["Day"])[:10]: r for r in raw}
    out = []
    for i in range(days):
        d = start + timedelta(days=i)
        key = d.isoformat()
        r = by_day.get(key)
        out.append({"date": key,
                    "docCount": int(r["DocCount"]) if r else 0,
                    "tokensIn": int(r["TokensIn"] or 0) if r else 0,
                    "tokensOut": int(r["TokensOut"] or 0) if r else 0,
                    "cost": round(float(r["Cost"] or 0), 4) if r else 0.0})
    return out


# ======================================================================
# static frontend
# ======================================================================
app.mount("/", StaticFiles(directory=str(config.PUBLIC_DIR), html=True), name="static")
