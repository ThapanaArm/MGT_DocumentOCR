"""ใส่ข้อมูล Master ตัวอย่าง (idempotent — รันซ้ำได้)"""
import sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from app import db

CUSTOMERS = [
 ("0010001","บริษัท สยาม เคมิคอล อินดัสทรี จำกัด","Siam Chemical Industry Co., Ltd.","0105533012345","00000","1000","10","00","THB","N030"),
 ("0010002","บริษัท ไทย โพลีเมอร์ กรุ๊ป จำกัด (มหาชน)","Thai Polymer Group PCL.","0107536000123","00000","1000","10","00","THB","N060"),
 ("0010003","บริษัท เอเชีย โคทติ้ง แอนด์ พลาสติก จำกัด","Asia Coating & Plastic Co., Ltd.","0125548009876","00000","1000","10","00","THB","N030"),
]
SHIPTOS = [
 ("0010001-01","0010001","คลังสินค้า บางปู","นิคมอุตสาหกรรมบางปู ซ.7 ต.แพรกษา อ.เมือง สมุทรปราการ 10280"),
 ("0010001-02","0010001","โรงงาน ระยอง","นิคมอุตสาหกรรมมาบตาพุด ต.มาบตาพุด อ.เมือง ระยอง 21150"),
 ("0010002-01","0010002","โรงงานอยุธยา (โรจนะ)","สวนอุตสาหกรรมโรจนะ ต.คานหาม อ.อุทัย พระนครศรีอยุธยา 13210"),
 ("0010003-01","0010003","สำนักงานใหญ่ / คลังบางนา","กม.19 ถ.บางนา-ตราด ต.บางโฉลง อ.บางพลี สมุทรปราการ 10540"),
]
MATERIALS = [
 ("FG-100021","Titanium Dioxide R-902 (25 KG/BAG)","KG","1000","PIG01"),
 ("FG-100045","Calcium Carbonate CC-800 (25 KG/BAG)","KG","1000","FIL01"),
 ("FG-100078","Epoxy Resin EP-828 (200 KG/DRUM)","KG","1000","RES01"),
 ("RM-200011","Methyl Ethyl Ketone (MEK) 99.5%","L","1000","SOL01"),
 ("RM-200034","Toluene Industrial Grade","L","1000","SOL01"),
 ("RM-200099","Polypropylene Homopolymer PP-1100N","KG","1000","PLA01"),
 ("SV-900001","ค่าขนส่งสินค้า / Freight Charge","AU","1000","SRV01"),
]
CUSTMAT = [
 ("0010001","SCI-TIO2-902","TIO2 R902 ถุง 25 กก.","FG-100021"),
 ("0010001","SCI-CACO3-800","แคลเซียมคาร์บอเนต CC800","FG-100045"),
 ("0010002","TPG-PP1100","PP HOMO 1100N","RM-200099"),
 ("0010002","TPG-MEK","MEK 99.5%","RM-200011"),
 ("0010003","ACP-EP828","อีพ็อกซี่เรซิน EP-828","FG-100078"),
]
VENDORS = [
 ("V-500012","บริษัท ยูนิเวอร์แซล เคมีคอล ซัพพลาย จำกัด","0105546007788","00000","THB","N030","2110100","-"),
 ("V-500034","บริษัท เอ็น.ซี. โลจิสติกส์ เซอร์วิส จำกัด","0115551002233","00000","THB","N015","2110100","53 (3%)"),
 ("V-500051","บริษัท พีทีจี เพโทรเคมิคอล จำกัด (มหาชน)","0107545000456","00001","THB","N045","2110100","-"),
]
VENMAT = [
 ("V-500012","UC-TL-100","TOLUENE INDUSTRIAL","RM-200034"),
 ("V-500012","UC-MEK-995","MEK 99.5 PCT","RM-200011"),
 ("V-500034","NC-FREIGHT","ค่าขนส่ง","SV-900001"),
 ("V-500051","PTG-PP-1100N","PP HOMOPOLYMER 1100N","RM-200099"),
]

def main():
    with db.conn() as c:
        cur = c.cursor()
        for r in CUSTOMERS:
            cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.Customer WHERE CustomerCode=?)
                INSERT ocr.Customer(CustomerCode,NameTh,NameEn,TaxId,Branch,SalesOrg,DistChannel,Division,Currency,PaymentTerms)
                VALUES(?,?,?,?,?,?,?,?,?,?)""", r[0], *r)
        for r in MATERIALS:
            cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode=?)
                INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES(?,?,?,?,?)""", r[0], *r)
        for r in SHIPTOS:
            cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.ShipTo WHERE ShipToCode=?)
                INSERT ocr.ShipTo(ShipToCode,CustomerCode,ShipToName,Address) VALUES(?,?,?,?)""", r[0], *r)
        for r in VENDORS:
            cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.Vendor WHERE VendorCode=?)
                INSERT ocr.Vendor(VendorCode,VendorName,TaxId,Branch,Currency,PaymentTerms,ReconAcct,WhtCode)
                VALUES(?,?,?,?,?,?,?,?)""", r[0], *r)
        for r in CUSTMAT:
            cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode=? AND ExtCode=?)
                INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES(?,?,?,?)""", r[0], r[1], *r)
        for r in VENMAT:
            cur.execute("""IF NOT EXISTS(SELECT 1 FROM ocr.VendorMaterial WHERE VendorCode=? AND ExtCode=?)
                INSERT ocr.VendorMaterial(VendorCode,ExtCode,ExtDesc,MaterialCode) VALUES(?,?,?,?)""", r[0], r[1], *r)
    for t in ("Customer","ShipTo","Material","CustomerMaterial","Vendor","VendorMaterial"):
        n = db.query_one(f"SELECT COUNT(*) AS n FROM ocr.{t}")["n"]
        print(f"  ocr.{t:<18} {n} rows")

if __name__ == "__main__":
    main()
    print("seed done")
