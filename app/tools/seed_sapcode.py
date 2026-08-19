"""
เติมรหัสของระบบ SAP ให้ข้อมูลตัวอย่าง (idempotent — เติมเฉพาะแถวที่ยังว่าง)
รูปแบบที่ใช้เป็นตัวอย่าง
  Customer / Ship-to / Vendor : เลข BP 10 หลัก เติมศูนย์หน้า
  Material                    : เลข 18 หลัก เติมศูนย์หน้า
  หน่วย                        : ISO code เช่น KG -> KGM, L -> LTR
ของจริงให้แก้เป็นรหัสจาก S/4HANA หรือทำ job replicate ทับ
"""
import sys, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from app import db

CUSTOMER_SAP = {"0010001": "0000100023", "0010002": "0000100047", "0010003": "0000100112"}
SHIPTO_SAP   = {"0010001-01": "0000100024", "0010001-02": "0000100025",
                "0010002-01": "0000100048", "0010003-01": "0000100112"}
VENDOR_SAP   = {"V-500012": "0000200015", "V-500034": "0000200037", "V-500051": "0000200054"}

ISO = {"KG": "KGM", "G": "GRM", "TON": "TNE", "L": "LTR", "ML": "MLT", "M": "MTR",
       "EA": "PCE", "PC": "PCE", "PCS": "PCE", "BOX": "BX", "BAG": "BG", "DRUM": "DR", "AU": "ACT"}


def mat_sap(code: str) -> str:
    """FG-100021 -> 000000000000100021 (เลข 18 หลัก)"""
    return re.sub(r"\D", "", code).rjust(18, "0")


def main():
    with db.conn() as c:
        cur = c.cursor()
        for k, v in CUSTOMER_SAP.items():
            cur.execute("UPDATE ocr.Customer SET SapCustomerCode=? WHERE CustomerCode=? "
                        "AND (SapCustomerCode IS NULL OR SapCustomerCode='')", v, k)
        for k, v in SHIPTO_SAP.items():
            cur.execute("UPDATE ocr.ShipTo SET SapShipToCode=? WHERE ShipToCode=? "
                        "AND (SapShipToCode IS NULL OR SapShipToCode='')", v, k)
        for k, v in VENDOR_SAP.items():
            cur.execute("UPDATE ocr.Vendor SET SapVendorCode=? WHERE VendorCode=? "
                        "AND (SapVendorCode IS NULL OR SapVendorCode='')", v, k)
        for m in db.query("SELECT MaterialCode, Uom FROM ocr.Material "
                          "WHERE SapMaterialCode IS NULL OR SapMaterialCode=''"):
            cur.execute("UPDATE ocr.Material SET SapMaterialCode=? WHERE MaterialCode=?",
                        mat_sap(m["MaterialCode"]), m["MaterialCode"])
        for u in db.query("SELECT Id, SapUom FROM ocr.UomConversion WHERE SapUomIso IS NULL OR SapUomIso=''"):
            iso = ISO.get(str(u["SapUom"]).upper(), "")
            if iso:
                cur.execute("UPDATE ocr.UomConversion SET SapUomIso=? WHERE Id=?", iso, u["Id"])

    for sql, label in (
        ("SELECT COUNT(*) AS n FROM ocr.Customer WHERE SapCustomerCode<>''", "Customer มีรหัส SAP"),
        ("SELECT COUNT(*) AS n FROM ocr.ShipTo   WHERE SapShipToCode<>''",   "Ship-to  มีรหัส SAP"),
        ("SELECT COUNT(*) AS n FROM ocr.Vendor   WHERE SapVendorCode<>''",   "Vendor   มีรหัส SAP"),
        ("SELECT COUNT(*) AS n FROM ocr.Material WHERE SapMaterialCode<>''", "Material มีรหัส SAP"),
        ("SELECT COUNT(*) AS n FROM ocr.UomConversion WHERE SapUomIso<>''",  "UoM      มี ISO code"),
    ):
        print("  %-22s %s" % (label, db.query_one(sql)["n"]))


if __name__ == "__main__":
    main()
    print("seed sap code done")
