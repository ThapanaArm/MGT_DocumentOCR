"""
สร้าง payload และส่งเข้า SAP S/4HANA
  SO -> API_SALES_ORDER_SRV / A_SalesOrder
  AP -> API_SUPPLIERINVOICE_PROCESS_SRV / A_SupplierInvoice
ถ้ายังไม่ตั้งค่า SAP_BASE_URL จะทำงานในโหมดจำลอง (บันทึก payload + log ครบ แต่ไม่ยิงออกจริง)
"""
from __future__ import annotations

import base64
import json
import random
import urllib.request
from datetime import datetime

from . import config
from .mapping import num


def _iso(mapline: dict) -> str:
    return ((mapline or {}).get("uom") or {}).get("iso") or ""


def _key(mapline: dict) -> str:
    """รหัสที่ SAP รู้จัก ถ้ายังไม่ได้ระบุจะ fallback เป็นรหัสภายใน (ปกติ mapping จะไม่ปล่อยผ่าน)"""
    return (mapline or {}).get("sapCode") or (mapline or {}).get("code") or ""


def _qty_uom(mapline: dict, line: dict):
    """ใช้จำนวน/หน่วยที่แปลงเป็นหน่วยของ SAP แล้ว ถ้ามี"""
    u = (mapline or {}).get("uom") or {}
    if u.get("status") in ("ok", "convert") and u.get("sapUom"):
        return num(u.get("sapQty")), u.get("sapUom"), float(u.get("factor") or 1)
    return num(line.get("qty")), line.get("uom"), 1.0

SO_ENDPOINT = "API_SALES_ORDER_SRV/A_SalesOrder"
AP_ENDPOINT = "API_SUPPLIERINVOICE_PROCESS_SRV/A_SupplierInvoice"


def build_payload(module: str, header: dict, lines: list, mapres: dict, partner_master: dict,
                  source: dict | None = None) -> dict:
    source = source or {}
    if module == "SO":
        c = partner_master or {}
        return {
            "_target": SO_ENDPOINT,
            "SalesOrderType": "OR",
            "SalesOrganization": c.get("SalesOrg") or "1000",
            "DistributionChannel": c.get("DistChannel") or "10",
            "OrganizationDivision": c.get("Division") or "00",
            "SoldToParty": _key(mapres["header"]["customer"]),
            "PurchaseOrderByCustomer": header.get("poNo"),
            "CustomerPurchaseOrderDate": header.get("poDate"),
            "RequestedDeliveryDate": header.get("deliveryDate"),
            "TransactionCurrency": header.get("currency") or "THB",
            "CustomerPaymentTerms": c.get("PaymentTerms") or "",
            "IncotermsClassification": header.get("incoterms") or "",
            "to_Partner": [{"PartnerFunction": "SH", "Customer": _key(mapres["header"]["shipTo"])}],
            "to_Item": [dict({
                "SalesOrderItem": str((i + 1) * 10),
                "Material": _key(mapres["lines"][i]),
                "RequestedQuantity": "%.3f" % _qty_uom(mapres["lines"][i], l)[0],
                "RequestedQuantityUnit": _qty_uom(mapres["lines"][i], l)[1],
                "NetAmount": "%.2f" % num(l.get("amount")),
                "MaterialByCustomer": l.get("extCode") or "",
                "_internalMaterial": mapres["lines"][i]["code"],
            "_isoUnit": _iso(mapres["lines"][i]),
                "_isoUnit": _iso(mapres["lines"][i]),
            }, **({"_docQuantity": "%g %s" % (num(l.get("qty")), l.get("uom") or ""),
                   "_uomFactor": _qty_uom(mapres["lines"][i], l)[2]}
                  if _qty_uom(mapres["lines"][i], l)[2] != 1 else {}))
                for i, l in enumerate(lines)],
            "_source": source,
        }

    v = partner_master or {}
    payload = {
        "_target": AP_ENDPOINT,
        "CompanyCode": config.SAP_COMPANY_CODE,
        "DocumentDate": header.get("invoiceDate"),
        "PostingDate": header.get("postingDate") or header.get("invoiceDate"),
        "InvoicingParty": _key(mapres["header"]["vendor"]),
        "SupplierInvoiceIDByInvcgParty": header.get("invoiceNo"),
        "DocumentCurrency": header.get("currency") or "THB",
        "InvoiceGrossAmount": "%.2f" % num(header.get("totalAmount")),
        "PaymentTerms": v.get("PaymentTerms") or "",
        "TaxIsCalculatedAutomatically": False,
        "to_SuplrInvcItemPurOrdRef": [{
            "SupplierInvoiceItem": str(i + 1),
            "PurchaseOrder": header.get("poRef") or "",
            "PurchaseOrderItem": str((i + 1) * 10) if header.get("poRef") else "",
            "Material": _key(mapres["lines"][i]),
            "Plant": config.SAP_DEFAULT_PLANT,
            "QuantityInPurchaseOrderUnit": "%.3f" % _qty_uom(mapres["lines"][i], l)[0],
            "PurchaseOrderQuantityUnit": _qty_uom(mapres["lines"][i], l)[1],
            "SupplierInvoiceItemAmount": "%.2f" % num(l.get("amount")),
            "TaxCode": "V7",
            "_internalMaterial": mapres["lines"][i]["code"],
            "_isoUnit": _iso(mapres["lines"][i]),
            **({"_docQuantity": "%g %s" % (num(l.get("qty")), l.get("uom") or ""),
                "_uomFactor": _qty_uom(mapres["lines"][i], l)[2]}
               if _qty_uom(mapres["lines"][i], l)[2] != 1 else {}),
        } for i, l in enumerate(lines)],
        "to_SuplrInvcTax": [{
            "TaxCode": "V7",
            "TaxBaseAmount": "%.2f" % num(header.get("subTotal")),
            "TaxAmount": "%.2f" % num(header.get("vatAmount")),
        }],
        "_source": source,
    }
    if num(header.get("whtAmount")) > 0:
        payload["_wht"] = {"WithholdingTaxType": v.get("WhtCode") or "53",
                           "WithholdingTaxAmount": "%.2f" % num(header.get("whtAmount"))}
    return payload


def post(module: str, payload: dict) -> dict:
    """ส่งเข้า SAP จริงถ้าตั้งค่า SAP_BASE_URL ไว้ มิฉะนั้นคืนผลจำลอง"""
    endpoint = payload.get("_target", "")
    body = {k: v for k, v in payload.items() if not k.startswith("_")}

    if not config.SAP_BASE_URL:
        doc_no = ("00" if module == "SO" else "51") + str(random.randint(100000, 999999))
        return {"success": True, "simulated": True, "sapDocNo": doc_no, "endpoint": endpoint,
                "message": "โหมดจำลอง: ยังไม่ได้ตั้งค่า SAP_BASE_URL ใน .env (บันทึก payload และ log ไว้แล้ว)"}

    url = "%s/sap/opu/odata/sap/%s?sap-client=%s" % (
        config.SAP_BASE_URL.rstrip("/"), endpoint, config.SAP_CLIENT)
    auth = base64.b64encode(f"{config.SAP_USER}:{config.SAP_PASSWORD}".encode()).decode()
    req = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"), method="POST", headers={
        "Authorization": "Basic " + auth, "Content-Type": "application/json", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=90) as r:
            data = json.loads(r.read().decode("utf-8", "ignore") or "{}")
        d = data.get("d", data)
        doc_no = d.get("SalesOrder") or d.get("SupplierInvoice") or ""
        return {"success": True, "simulated": False, "sapDocNo": doc_no, "endpoint": endpoint,
                "message": "สร้างเอกสารใน SAP สำเร็จ", "raw": d}
    except Exception as e:                                    # noqa: BLE001
        detail = getattr(e, "read", lambda: b"")()
        return {"success": False, "simulated": False, "sapDocNo": "", "endpoint": endpoint,
                "message": "ส่งเข้า SAP ไม่สำเร็จ: %s %s" % (e, detail[:500].decode("utf-8", "ignore"))}


def now_str() -> str:
    return datetime.now().strftime("%d/%m/%Y %H:%M:%S")
