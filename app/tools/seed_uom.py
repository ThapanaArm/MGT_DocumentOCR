"""ใส่กฎการแปลงหน่วยตัวอย่าง (idempotent)"""
import sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from app import db

# (MaterialCode | None = กฎกลาง, ExtUom, SapUom, Factor, Note)
RULES = [
    # --- กฎกลาง: ชื่อหน่วยที่เขียนต่างกันแต่ความหมายเดียวกัน ---
    (None, "กก.",   "KG", 1,     "กิโลกรัม (ไทย) = KG"),
    (None, "กิโลกรัม", "KG", 1,  "กิโลกรัม (ไทย) = KG"),
    (None, "ตัน",   "KG", 1000,  "1 ตัน = 1,000 KG"),
    (None, "TON",   "KG", 1000,  "1 TON = 1,000 KG"),
    (None, "MT",    "KG", 1000,  "1 Metric Ton = 1,000 KG"),
    (None, "ลิตร",  "L",  1,     "ลิตร (ไทย) = L"),
    (None, "LTR",   "L",  1,     "LTR = L"),
    (None, "ชิ้น",  "EA", 1,     "ชิ้น (ไทย) = EA"),
    (None, "PCS",   "EA", 1,     "PCS = EA"),
    (None, "PC",    "EA", 1,     "PC = EA"),
    (None, "งาน",   "AU", 1,     "งาน/ครั้ง = Activity Unit"),
    # --- กฎเฉพาะสินค้า: บรรจุภัณฑ์ ---
    ("FG-100021", "BAG",  "KG", 25,  "TiO2 R-902 บรรจุ 25 กก./ถุง"),
    ("FG-100021", "ถุง",  "KG", 25,  "TiO2 R-902 บรรจุ 25 กก./ถุง"),
    ("FG-100021", "PALLET", "KG", 1000, "1 พาเลท = 40 ถุง = 1,000 กก."),
    ("FG-100045", "BAG",  "KG", 25,  "CaCO3 CC-800 บรรจุ 25 กก./ถุง"),
    ("FG-100045", "ถุง",  "KG", 25,  "CaCO3 CC-800 บรรจุ 25 กก./ถุง"),
    ("FG-100078", "DRUM", "KG", 200, "Epoxy EP-828 บรรจุ 200 กก./ถัง"),
    ("FG-100078", "ถัง",  "KG", 200, "Epoxy EP-828 บรรจุ 200 กก./ถัง"),
    ("RM-200011", "DRUM", "L",  200, "MEK บรรจุ 200 ลิตร/ถัง"),
    ("RM-200034", "DRUM", "L",  200, "Toluene บรรจุ 200 ลิตร/ถัง"),
    ("RM-200099", "BAG",  "KG", 25,  "PP-1100N บรรจุ 25 กก./ถุง"),
]


def main():
    with db.conn() as c:
        cur = c.cursor()
        for mat, ext, sapu, factor, note in RULES:
            if mat is None:
                cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.UomConversion
                                 WHERE MaterialCode IS NULL AND ExtUom=?)
                    INSERT ocr.UomConversion(MaterialCode,ExtUom,SapUom,Factor,Note)
                    VALUES(NULL,?,?,?,?)""", ext, ext, sapu, factor, note)
            else:
                cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.UomConversion
                                 WHERE MaterialCode=? AND ExtUom=?)
                    INSERT ocr.UomConversion(MaterialCode,ExtUom,SapUom,Factor,Note)
                    VALUES(?,?,?,?,?)""", mat, ext, mat, ext, sapu, factor, note)
    n = db.query_one("SELECT COUNT(*) AS n FROM ocr.UomConversion")["n"]
    print(f"  ocr.UomConversion {n} rows")


if __name__ == "__main__":
    main()
    print("seed uom done")
