"""
MGT Document OCR -> SAP S/4HANA
FastAPI backend + static frontend
"""
from __future__ import annotations

import base64
import json
import re
import shutil
import unicodedata
from datetime import datetime, date
from decimal import Decimal
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException, UploadFile, File, Form, Body
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles

from . import config, db, ocr_engine, sap
from .mapping import num, run_mapping

app = FastAPI(title="MGT Document OCR → SAP S/4HANA", version="1.0.0")

# ประเภทเอกสาร AP Invoice — ผู้ใช้เลือกเองในหน้าเอกสาร ไม่ได้เดาจาก OCR (เก็บไว้แสดง/กรองเท่านั้น ยังไม่ผูกกับ SAP)
AP_DOC_CATEGORIES = [
    {"id": "TRADE", "label": "Trade"},
    {"id": "NONTRADE_PO_SERVICE", "label": "Non-Trade มี PO (Service)"},
    {"id": "NONTRADE_PO_ITEM", "label": "Non-Trade มี PO (Item)"},
    {"id": "NONTRADE_NOPO", "label": "Non-Trade ไม่มี PO"},
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
                    ap_doc_category: str = "") -> int:
    h, d = ext["header"], denorm(module, ext["header"])
    doc_id = db.insert_returning_id("""
        INSERT ocr.Document(Module,FileName,StoredPath,FileSize,OcrProvider,OcrConfidence,OcrConfidenceNote,
              OcrTokensIn,OcrTokensOut,ApDocCategory,Status,
              DocNo,DocDate,PostingDate,PartnerName,PartnerTaxId,Currency,SubTotal,VatRate,VatAmount,
              WhtAmount,TotalAmount,HeaderJson,RawText,CreatedBy)
        VALUES(?,?,?,?,?,?,?,?,?,?, 'NEW', ?,?,?,?,?,?,?,?,?,?,?,?,?,?);
        SELECT SCOPE_IDENTITY();""",
        (module, file_name, stored, size, ext.get("provider"), ext.get("confidence"), ext.get("confidenceNote"),
         ext.get("tokensIn"), ext.get("tokensOut"), ap_doc_category or None,
         d["DocNo"], d["DocDate"], d["PostingDate"], d["PartnerName"], d["PartnerTaxId"], d["Currency"],
         d["SubTotal"], d["VatRate"], d["VatAmount"], d["WhtAmount"], d["TotalAmount"],
         json.dumps(h, ensure_ascii=False), (ext.get("rawText") or "")[:20000], user))
    save_lines(doc_id, ext["lines"])
    return doc_id


def save_lines(doc_id: int, lines: list[dict]) -> None:
    with db.conn() as c:
        cur = c.cursor()
        cur.execute("DELETE FROM ocr.DocumentLine WHERE DocId=?", doc_id)
        for i, l in enumerate(lines):
            cur.execute("""INSERT ocr.DocumentLine(DocId,ItemNo,ExtCode,ExtDesc,Qty,Uom,UnitPrice,Amount,
                              MaterialCode,MapStatus,MapMethod,SapQty,SapUom,UomFactor,ExtraJson)
                           VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                        doc_id, (i + 1) * 10, l.get("extCode"), l.get("desc"), num(l.get("qty")),
                        l.get("uom"), num(l.get("price")), num(l.get("amount")),
                        l.get("materialCode") or None, l.get("mapStatus"), l.get("mapMethod"),
                        l.get("sapQty"), l.get("sapUom"), l.get("uomFactor"),
                        json.dumps(l.get("extra") or {}, ensure_ascii=False) if l.get("extra") else None)


def get_document(doc_id: int) -> dict:
    d = db.query_one("SELECT * FROM ocr.Document WHERE DocId=?", (doc_id,))
    if not d:
        raise HTTPException(404, "ไม่พบเอกสาร")
    d = {k: clean(v) for k, v in d.items()}
    lines = rows(db.query("SELECT * FROM ocr.DocumentLine WHERE DocId=? ORDER BY ItemNo", (doc_id,)))
    return {
        "docId": d["DocId"], "module": d["Module"], "fileName": d["FileName"], "status": d["Status"],
        "provider": d["OcrProvider"], "confidence": d["OcrConfidence"],
        "confidenceNote": d.get("OcrConfidenceNote") or "",
        "tokensIn": d.get("OcrTokensIn"), "tokensOut": d.get("OcrTokensOut"),
        "apDocCategory": d.get("ApDocCategory") or "", "createdAt": d["CreatedAt"],
        "sapDocNo": d["SapDocNo"], "postedAt": d["PostedAt"], "mapStatus": d["MapStatus"],
        "partnerCode": d["PartnerCode"], "shipToCode": d["ShipToCode"],
        "header": json.loads(d["HeaderJson"] or "{}"),
        "lines": [{"itemNo": l["ItemNo"], "extCode": l["ExtCode"] or "", "desc": l["ExtDesc"] or "",
                   "qty": l["Qty"], "uom": l["Uom"] or "", "price": l["UnitPrice"], "amount": l["Amount"],
                   "materialCode": l["MaterialCode"] or "", "mapStatus": l["MapStatus"] or "",
                   "mapMethod": l["MapMethod"] or "", "sapQty": l.get("SapQty"),
                   "sapUom": l.get("SapUom") or "", "uomFactor": l.get("UomFactor"),
                   "extra": json.loads(l.get("ExtraJson") or "{}")} for l in lines],
    }


# ---------------------------------------------------------------- แชทสั่งแก้ไขข้อมูล (AI)
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
    return db.insert_returning_id(
        """INSERT ocr.DocumentChat(DocId,Role,MessageText,ImagePath,CreatedBy) VALUES(?,?,?,?,?);
           SELECT SCOPE_IDENTITY();""",
        (doc_id, role, text, image_path, user))


def get_chat_history(doc_id: int) -> list[dict]:
    """ประวัติแชทของเอกสารนี้ เรียงเก่า->ใหม่ — ใช้ทั้งแสดงผลหน้าเว็บ และเป็นบริบทส่งให้ Claude ตอบต่อเนื่อง"""
    r = rows(db.query("SELECT ChatId, Role, MessageText, ImagePath, CreatedAt FROM ocr.DocumentChat "
                      "WHERE DocId=? ORDER BY ChatId", (doc_id,)))
    return [{"chatId": x["ChatId"], "role": x["Role"], "text": x["MessageText"] or "",
            "hasImage": bool(x["ImagePath"]), "createdAt": x["CreatedAt"]} for x in r]


def update_header(doc_id: int, module: str, header: dict) -> None:
    d = denorm(module, header)
    db.execute("""UPDATE ocr.Document SET HeaderJson=?, DocNo=?, DocDate=?, PostingDate=?, PartnerName=?,
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
                                        (SELECT COUNT(*) FROM ocr.Document) AS documents""")
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
    if module not in ("AP", "SO"):
        raise HTTPException(400, "module ต้องเป็น AP หรือ SO")
    ap_cat = (body.get("apDocCategory") or "").strip().upper()
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
    if module not in ("AP", "SO"):
        raise HTTPException(400, "module ต้องเป็น AP หรือ SO")
    ap_cat = (apDocCategory or "").strip().upper()
    if ap_cat and ap_cat not in {c["id"] for c in AP_DOC_CATEGORIES}:
        raise HTTPException(400, "ประเภทเอกสารไม่ถูกต้อง")
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    fname = safe_name(file.filename or "document")
    stored = config.UPLOAD_DIR / f"{stamp}_{fname}"
    with stored.open("wb") as f:
        shutil.copyfileobj(file.file, f)
    size = stored.stat().st_size
    ext = ocr_engine.extract(stored, module, ocr)
    doc_id = create_document(module, ext, fname, str(stored), size, user, ap_cat)
    out = get_document(doc_id)
    out["ocrNote"] = ext.get("_note") or ""
    return out


@app.get("/api/ap-doc-categories")
def ap_doc_categories():
    return AP_DOC_CATEGORIES


@app.get("/api/documents")
def list_documents(module: str = "", status: str = "", apDocCategory: str = "", limit: int = 100):
    w, p = [], []
    if module:
        w.append("Module=?"); p.append(module.upper())
    if status:
        w.append("Status=?"); p.append(status.upper())
    if apDocCategory:
        w.append("ApDocCategory=?"); p.append(apDocCategory.upper())
    sql = ("SELECT TOP (?) DocId,Module,FileName,Status,DocNo,DocDate,PartnerName,PartnerCode,"
           "TotalAmount,Currency,SapDocNo,PostedAt,CreatedAt,OcrProvider,OcrConfidence,OcrConfidenceNote,"
           "OcrTokensIn,OcrTokensOut,ApDocCategory "
           "FROM ocr.Document")
    if w:
        sql += " WHERE " + " AND ".join(w)
    sql += " ORDER BY DocId DESC"
    return rows(db.query(sql, tuple([limit] + p)))


@app.get("/api/documents/{doc_id}")
def read_document(doc_id: int):
    return get_document(doc_id)


@app.put("/api/documents/{doc_id}")
def save_document(doc_id: int, body: dict = Body(...)):
    doc = get_document(doc_id)
    if doc["status"] == "POSTED":
        raise HTTPException(400, "เอกสารถูกส่งเข้า SAP แล้ว แก้ไขไม่ได้")
    update_header(doc_id, doc["module"], body.get("header") or doc["header"])
    if body.get("lines") is not None:
        save_lines(doc_id, body["lines"])
    db.execute("UPDATE ocr.Document SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END,"
               " MapStatus=NULL, MapMessage=NULL WHERE DocId=?", (doc_id,))
    return get_document(doc_id)


@app.post("/api/documents/{doc_id}/category")
def set_doc_category(doc_id: int, body: dict = Body(...)):
    """ตั้งค่าประเภทเอกสาร AP Invoice (Trade/Non-Trade) — ผู้ใช้เลือกเอง ไม่เกี่ยวกับ OCR/Mapping"""
    cat = (body.get("apDocCategory") or "").strip().upper()
    if cat and cat not in {c["id"] for c in AP_DOC_CATEGORIES}:
        raise HTTPException(400, "ประเภทเอกสารไม่ถูกต้อง")
    db.execute("UPDATE ocr.Document SET ApDocCategory=? WHERE DocId=?", (cat or None, doc_id))
    return get_document(doc_id)


@app.delete("/api/documents/{doc_id}")
def delete_document(doc_id: int):
    db.execute("DELETE FROM ocr.Document WHERE DocId=?", (doc_id,))
    return {"ok": True}


@app.post("/api/documents/{doc_id}/reocr")
def reocr_document(doc_id: int, body: dict = Body(default={})):
    """อ่านไฟล์ต้นฉบับใหม่อีกครั้ง — ระบุ body {"ocr": "tesseract"} เพื่อเลือก engine ได้ (ค่าเริ่มต้น auto)"""
    d = db.query_one("SELECT Module, StoredPath, FileName FROM ocr.Document WHERE DocId=?", (doc_id,))
    if not d:
        raise HTTPException(404, "ไม่พบเอกสาร")
    doc = get_document(doc_id)
    if doc["status"] == "POSTED":
        raise HTTPException(400, "เอกสารถูกส่งเข้า SAP แล้ว อ่านใหม่ไม่ได้")
    if not d["StoredPath"] or not Path(d["StoredPath"]).exists():
        raise HTTPException(400, "ไม่พบไฟล์ต้นฉบับ (เอกสารนี้อาจสร้างจากชุดตัวอย่าง)")

    ext = ocr_engine.extract(Path(d["StoredPath"]), d["Module"], body.get("ocr") or "auto")
    dn = denorm(d["Module"], ext["header"])
    db.execute("""UPDATE ocr.Document SET OcrProvider=?, OcrConfidence=?, OcrConfidenceNote=?,
                    OcrTokensIn=?, OcrTokensOut=?, HeaderJson=?, RawText=?,
                    DocNo=?, DocDate=?, PostingDate=?, PartnerName=?, PartnerTaxId=?, Currency=?,
                    SubTotal=?, VatRate=?, VatAmount=?, WhtAmount=?, TotalAmount=?,
                    Status='NEW', MapStatus=NULL, MapMessage=NULL, PartnerCode=NULL, ShipToCode=NULL,
                    SapPartnerCode=NULL, SapShipToCode=NULL, UpdatedAt=SYSDATETIME()
                  WHERE DocId=?""",
               (ext.get("provider"), ext.get("confidence"), ext.get("confidenceNote"),
                ext.get("tokensIn"), ext.get("tokensOut"),
                json.dumps(ext["header"], ensure_ascii=False), (ext.get("rawText") or "")[:20000],
                dn["DocNo"], dn["DocDate"], dn["PostingDate"], dn["PartnerName"], dn["PartnerTaxId"],
                dn["Currency"], dn["SubTotal"], dn["VatRate"], dn["VatAmount"], dn["WhtAmount"],
                dn["TotalAmount"], doc_id))
    save_lines(doc_id, ext["lines"])
    out = get_document(doc_id)
    out["ocrNote"] = ext.get("_note") or ("อ่านเอกสารใหม่เรียบร้อย (%s)" % ext.get("provider"))
    return out


@app.get("/api/documents/{doc_id}/chat")
def read_chat_history(doc_id: int):
    return get_chat_history(doc_id)


@app.get("/api/documents/{doc_id}/chat/{chat_id}/image")
def chat_image(doc_id: int, chat_id: int):
    r = db.query_one("SELECT ImagePath FROM ocr.DocumentChat WHERE DocId=? AND ChatId=?", (doc_id, chat_id))
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

    history = get_chat_history(doc_id)                    # ก่อนบันทึกข้อความใหม่ — ใช้เป็นบริบทของเทิร์นนี้
    save_chat_message(doc_id, "user", message, image_bytes, image_ext, user)

    prompt_message = message or "ดูภาพที่แนบมา แล้วแก้ไขข้อมูลในเอกสารให้ถูกต้องตามสิ่งที่เห็นในภาพ"
    result = ocr_engine.chat_fix_document(doc["module"], doc["header"], doc["lines"], history, prompt_message,
                                          image_b64, image_media_type)
    if not result:
        raise HTTPException(400, "เชื่อมต่อ Claude ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า ANTHROPIC_API_KEY ใน .env")

    save_chat_message(doc_id, "assistant", result["reply"], None, ".png", "AI")

    update_header(doc_id, doc["module"], result["header"])
    save_lines(doc_id, result["lines"])
    db.execute("UPDATE ocr.Document SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END,"
               " MapStatus=NULL, MapMessage=NULL WHERE DocId=?", (doc_id,))
    out = get_document(doc_id)
    return {"reply": result["reply"], "document": out}


@app.get("/api/documents/{doc_id}/rawtext")
def raw_text(doc_id: int):
    r = db.query_one("SELECT RawText FROM ocr.Document WHERE DocId=?", (doc_id,))
    if not r:
        raise HTTPException(404, "ไม่พบเอกสาร")
    return {"text": r["RawText"] or ""}


@app.get("/api/documents/{doc_id}/file")
def document_file(doc_id: int):
    d = db.query_one("SELECT StoredPath, FileName FROM ocr.Document WHERE DocId=?", (doc_id,))
    if not d or not d["StoredPath"] or not Path(d["StoredPath"]).exists():
        raise HTTPException(404, "ไม่พบไฟล์ต้นฉบับ")
    return FileResponse(d["StoredPath"], filename=d["FileName"])


# ---------------------------------------------------------------- mapping
@app.post("/api/documents/{doc_id}/map")
def map_document(doc_id: int, body: dict = Body(default={})):
    doc = get_document(doc_id)
    manual = body.get("manual") or {}
    if body.get("header"):
        update_header(doc_id, doc["module"], body["header"])
        doc["header"] = body["header"]
    if body.get("lines") is not None:
        save_lines(doc_id, body["lines"])
        doc["lines"] = get_document(doc_id)["lines"]

    res = run_mapping(doc["module"], doc["header"], doc["lines"], load_masters(), manual)

    with db.conn() as c:
        cur = c.cursor()
        for i, l in enumerate(doc["lines"]):
            r = res["lines"][i]
            u = r.get("uom") or {}
            cur.execute("UPDATE ocr.DocumentLine SET MaterialCode=?, MapStatus=?, MapMethod=?, "
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
        cur.execute("""UPDATE ocr.Document SET SapPartnerCode=?, SapShipToCode=?,
                         PartnerCode=?, ShipToCode=?, MapStatus=?, MapMessage=?,
                         Status=CASE WHEN Status='POSTED' THEN 'POSTED' WHEN ?=1 THEN 'MAPPED' ELSE 'INCOMPLETE' END,
                         UpdatedAt=SYSDATETIME() WHERE DocId=?""",
                    sap_partner, sap_shipto, partner, shipto, "PASS" if res["pass"] else "FAIL",
                    json.dumps({"errors": res["errors"], "warns": res["warns"]}, ensure_ascii=False),
                    1 if res["pass"] else 0, doc_id)
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
    r = sap.post(doc["module"], payload)
    with db.conn() as c:
        cur = c.cursor()
        cur.execute("""INSERT ocr.PostLog(DocId,Module,SapDocNo,Endpoint,PayloadJson,Success,Message,PostedBy)
                       VALUES(?,?,?,?,?,?,?,?)""",
                    doc_id, doc["module"], r.get("sapDocNo"), r.get("endpoint"),
                    json.dumps(payload, ensure_ascii=False), 1 if r["success"] else 0,
                    r.get("message"), user)
        if r["success"]:
            cur.execute("""UPDATE ocr.Document SET Status='POSTED', SapDocNo=?, PostedAt=SYSDATETIME(),
                             PostedBy=?, UpdatedAt=SYSDATETIME() WHERE DocId=?""",
                        r.get("sapDocNo"), user, doc_id)
    return {**r, "document": get_document(doc_id)}


@app.get("/api/logs")
def logs(limit: int = 200):
    return rows(db.query("""SELECT TOP (?) l.LogId,l.DocId,l.Module,l.SapDocNo,l.Endpoint,l.Success,
                                   l.Message,l.PostedAt,l.PostedBy,
                                   d.FileName,d.DocNo,d.PartnerName,d.TotalAmount,d.Currency,
                                   (SELECT COUNT(*) FROM ocr.DocumentLine WHERE DocId=l.DocId) AS Lines
                            FROM ocr.PostLog l LEFT JOIN ocr.Document d ON d.DocId=l.DocId
                            ORDER BY l.LogId DESC""", (limit,)))


@app.get("/api/logs/{log_id}/payload")
def log_payload(log_id: int):
    r = db.query_one("SELECT PayloadJson FROM ocr.PostLog WHERE LogId=?", (log_id,))
    if not r:
        raise HTTPException(404, "ไม่พบ log")
    return json.loads(r["PayloadJson"] or "{}")


@app.get("/api/dashboard")
def dashboard():
    by_status = rows(db.query("""SELECT Module, Status, COUNT(*) AS Cnt, SUM(TotalAmount) AS Amount
                                 FROM ocr.Document GROUP BY Module, Status"""))
    recent = rows(db.query("""SELECT TOP 8 DocId,Module,FileName,DocNo,PartnerName,Status,TotalAmount,
                                     SapDocNo,CreatedAt FROM ocr.Document ORDER BY DocId DESC"""))
    return {"byStatus": by_status, "recent": recent}


# ======================================================================
# static frontend
# ======================================================================
app.mount("/", StaticFiles(directory=str(config.PUBLIC_DIR), html=True), name="static")
