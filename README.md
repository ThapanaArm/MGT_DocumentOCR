# MGT Document OCR → SAP S/4HANA

ระบบ web-based สำหรับอ่านเอกสาร (OCR / Text extraction) แล้ว Mapping กับข้อมูลหลัก
ก่อนสร้างเอกสารใน SAP S/4HANA รองรับ 2 Process

| Module | เอกสารขาเข้า | เอกสารปลายทางใน SAP |
|---|---|---|
| **AP** — ตั้งหนี้เจ้าหนี้ | ใบแจ้งหนี้ / ใบกำกับภาษีจากผู้ขาย | Supplier Invoice (`A_SupplierInvoice`) |
| **SO** — Sales Order | ใบสั่งซื้อจากลูกค้า | Sales Order (`A_SalesOrder`) |

---

## 1. เริ่มใช้งาน

```bat
run.bat
```

เปิดเบราว์เซอร์ที่ **http://localhost:8090**
(ใช้พอร์ต 8090 เพราะพอร์ต 8080 บนเครื่องนี้มีบริการอื่นใช้อยู่แล้ว — เปลี่ยนได้ที่ `.env` → `APP_PORT`)

ครั้งแรกถ้ายังไม่มีตาราง ให้รัน `init_db.bat` หนึ่งครั้ง (สร้าง schema `ocr` + ใส่ข้อมูลตัวอย่าง)

---

## 2. ฐานข้อมูล

`1P69044\SQLEXPRESS` → **MGT_Document_OCR** → schema **`ocr`** (ไม่แตะตาราง `dbo.*` เดิม)

| ตาราง | หน้าที่ |
|---|---|
| `ocr.Customer` | ลูกค้า — จับคู่จากเลขทะเบียนนิติบุคคล / ชื่อ + **SapCustomerCode** |
| `ocr.ShipTo` | สถานที่ส่งของของลูกค้าแต่ละราย + **SapShipToCode** |
| `ocr.CustomerMaterial` | รหัส/ชื่อสินค้าฝั่งลูกค้า → Material ของ SAP |
| `ocr.Vendor` | ผู้ขาย — จับคู่จากเลขทะเบียนนิติบุคคล / ชื่อ + **SapVendorCode** |
| `ocr.VendorMaterial` | รหัส/ชื่อสินค้าฝั่งผู้ขาย → Material ของ SAP |
| `ocr.Material` | Material master + **SapMaterialCode** (ควร replicate จาก S/4HANA) |
| `ocr.UomConversion` | กฎแปลงหน่วยตามเอกสาร → หน่วยของ SAP (เช่น 1 BAG = 25 KG) + **SapUomIso** |
| `ocr.Document` | หัวเอกสารที่อ่านได้ + สถานะ + ผล Mapping + เลขที่เอกสาร SAP |
| `ocr.DocumentLine` | รายการสินค้าแต่ละบรรทัด + Material ที่จับคู่ได้ |
| `ocr.PostLog` | ประวัติการส่งเข้า SAP พร้อม payload เต็ม |

> **หมายเหตุการเชื่อมต่อ:** SQLEXPRESS เครื่องนี้เปิดเฉพาะ **Shared Memory** (TCP/IP ปิด, SQL Browser หยุด)
> ระบบจึงต่อผ่าน **ODBC Driver 17** ด้วย `pyodbc` ซึ่งใช้ Shared Memory ได้โดยไม่ต้องแก้ค่า SQL Server
> ถ้าจะย้าย backend ไปรันคนละเครื่องกับ SQL Server ต้องเปิด TCP/IP และกำหนดพอร์ตคงที่ก่อน

สถานะเอกสาร: `NEW` → `MAPPED` (ผ่าน) / `INCOMPLETE` (ไม่ผ่าน) → `POSTED`

---

## 3. ขั้นตอนการทำงานบนหน้าจอ

1. **เลือกโมดูล** — AP หรือ SO
2. **นำเข้าเอกสาร** — ลากไฟล์ PDF/JPG/PNG/TIFF มาวาง ระบบอ่านแล้วแยกเป็น **Header** และ **Detail** (แก้ไขได้ทุกช่อง)
3. **กดปุ่ม Mapping** — ระบบแสดง **การ์ดเทียบข้อมูลทีละจุด** โดยแยกเป็น 2 ฝั่งเสมอ

   | ฝั่งซ้าย 📄 ข้อมูลจากเอกสาร | ฝั่งขวา 🏦 ข้อมูลจาก SAP |
   |---|---|
   | ค่าที่อ่านได้จากเอกสารจริง | ข้อมูล master ที่จับคู่ได้ พร้อม ✓ ตรงกัน / ≠ ไม่ตรง รายฟิลด์ |

   การ์ดที่แสดง — **AP:** ① Vendor ② Material (รายบรรทัด) ③ Relate Unit ·
   **SO:** ① Customer ② Ship-to ③ Material (รายบรรทัด) ④ Relate Unit

   | จุด | ฝั่งเอกสาร | ฝั่ง SAP |
   |---|---|---|
   | Vendor | ชื่อผู้ขาย, เลขทะเบียน, สาขา | รหัสผู้ขาย, ชื่อ, เลขทะเบียน, สาขา, Payment Terms, Recon. Account, WHT |
   | Customer | ชื่อลูกค้า, เลขทะเบียน | รหัสลูกค้า, ชื่อ, เลขทะเบียน, Sales Org/Channel/Div, Payment Terms, สกุลเงิน |
   | Ship-to | สถานที่ส่งของ, ที่อยู่จัดส่ง | รหัส Ship-to, ชื่อ, ที่อยู่, อยู่ใต้ลูกค้ารายใด |
   | Material | รหัส/ชื่อสินค้าของคู่ค้า, จำนวน, หน่วย, ราคา, จำนวนเงิน | รหัส Material, รายละเอียด, หน่วยฐาน, จำนวนที่ส่งเข้า SAP, Plant, Material Group |
   | Relate Unit | จำนวน + หน่วยตามเอกสาร | จำนวน + หน่วยใน SAP, ตัวคูณ, ที่มาของกฎ |

   ทุกการ์ดมี dropdown ให้เลือกค่าที่ถูกต้องเองได้ และการ์ดที่ไม่ผ่านจะขึ้นกรอบแดง
   ส่วนการ์ด Relate Unit ที่ไม่ผ่านจะมีปุ่ม **+ เพิ่มกฎแปลงหน่วย** ที่เติมค่าให้อัตโนมัติ
4. **ส่งเข้า SAP** — ปุ่มเปิดใช้เมื่อ Mapping ผ่านเท่านั้น มีหน้ายืนยัน + ดู payload ได้ก่อนส่ง

### เกณฑ์การจับคู่ — แยกเป็น 4 กลุ่ม

หน้า **Master Mapping** จัดกลุ่มตามจุดที่ต้องจับคู่ (แท็บบนสุด)

**1. Vendor / Supplier** — ใช้กับโมดูล AP
ตรวจจาก **เลขทะเบียนนิติบุคคล 13 หลัก** ก่อน (ตัดอักขระที่ไม่ใช่ตัวเลขออกแล้วเทียบตรงตัว)
ถ้าไม่พบจึงเทียบ **ชื่อผู้ขาย** ด้วย Dice similarity ≥ 82% (ตัดคำว่า บริษัท/จำกัด/มหาชน/Co.,Ltd ออกก่อนเทียบ)

**2. Customer** — ใช้กับโมดูล SO
ตรรกะเดียวกับ Vendor แต่เทียบชื่อได้ทั้ง **ภาษาไทยและภาษาอังกฤษ**

**3. Ship-to** — ใช้กับโมดูล SO
เทียบ **ชื่อสถานที่ + ที่อยู่จัดส่ง** เฉพาะรายการที่อยู่ใต้ลูกค้าที่จับคู่ได้แล้ว (≥ 70%)

**4. Material** — ใช้กับทั้ง AP และ SO ประกอบด้วย 4 ตาราง

| ตาราง | หน้าที่ |
|---|---|
| สินค้า/บริการ (Material) | Material master ของ SAP + หน่วยฐาน |
| สินค้าฝั่งลูกค้า | รหัส/ชื่อสินค้าที่ลูกค้าใช้ → Material |
| สินค้าฝั่งผู้ขาย | รหัส/ชื่อสินค้าที่ผู้ขายใช้ → Material |
| **การแปลงหน่วย (UoM)** | หน่วยตามเอกสาร → หน่วยของ SAP พร้อมตัวคูณ |

ลำดับการจับคู่สินค้า: **รหัสสินค้าของคู่ค้าตรงตัว** → **ชื่อสินค้าของคู่ค้า ≥ 85%** → **Material master ≥ 93%**

**4.1 การแปลงหน่วย (Unit Conversion)**

หลังจับคู่ Material ได้แล้ว ระบบจะเทียบหน่วยในเอกสารกับหน่วยฐานของ Material

| กรณี | ผลลัพธ์ |
|---|---|
| หน่วยตรงกันอยู่แล้ว | ผ่าน ตัวคูณ = 1 |
| มี **กฎเฉพาะสินค้า** (ระบุ Material) | ใช้กฎนั้นก่อนเสมอ เช่น *FG-100021: 1 BAG = 25 KG* |
| ไม่มีกฎเฉพาะ แต่มี **กฎกลาง** (เว้นช่อง Material ว่าง) | ใช้กฎกลาง เช่น *1 ตัน = 1,000 KG*, *กก. = KG*, *PCS = EA* |
| ไม่มีกฎเลย | **Mapping ไม่ผ่าน** — แจ้ง "ไม่พบการแปลงหน่วย X → Y" พร้อมปุ่ม **+ เพิ่มกฎ** ที่เติมค่าให้อัตโนมัติ |
| กฎแปลงไปเป็นหน่วยที่ไม่ตรงกับหน่วยฐานของ Material | ไม่ผ่าน (กันตั้งค่าผิด) |

จำนวนที่ส่งเข้า SAP จะเป็น **จำนวนที่แปลงหน่วยแล้ว** และแนบจำนวนเดิมตามเอกสารไว้ใน payload
(`_docQuantity`, `_uomFactor`) เพื่อการตรวจสอบย้อนกลับ พร้อมบันทึกลง `ocr.DocumentLine`
(`SapQty`, `SapUom`, `UomFactor`)

### รหัสของระบบ SAP (SAP key) — บังคับต้องมี

ทุก master เก็บ **2 รหัส** คู่กันเสมอ

| ฝั่ง | ใช้ทำอะไร | ตัวอย่าง |
|---|---|---|
| รหัสภายใน | คีย์ของระบบ OCR ใช้อ้างอิง/จับคู่ภายใน | `0010001`, `V-500012`, `FG-100021` |
| **รหัสใน SAP** | ค่าที่ส่งจริงใน OData payload ไป S/4HANA | `0000100023`, `0000200015`, `000000000000100021` |

| Master | คอลัมน์รหัส SAP | ไปอยู่ที่ field ไหนใน payload |
|---|---|---|
| Customer | `SapCustomerCode` | `SoldToParty` |
| Ship-to | `SapShipToCode` | `to_Partner[PartnerFunction=SH].Customer` |
| Vendor | `SapVendorCode` | `InvoicingParty` |
| Material | `SapMaterialCode` | `to_Item.Material` / `to_SuplrInvcItemPurOrdRef.Material` |
| หน่วย | `SapUom` + `SapUomIso` | `RequestedQuantityUnit` / `PurchaseOrderQuantityUnit` (ISO อยู่ใน `_isoUnit`) |

**ถ้าช่องรหัส SAP ว่าง → Mapping ไม่ผ่านทันที** พร้อมข้อความ เช่น
*ผู้ขาย "บริษัท ยูนิเวอร์แซล เคมีคอล ซัพพลาย จำกัด" ยังไม่ได้ระบุรหัสของ SAP จึงส่งเข้า SAP ไม่ได้*
และในตาราง Master คอลัมน์นั้นจะขึ้น **✗ ยังไม่ระบุ** สีแดง

payload ยังแนบรหัสภายในไว้ที่ `_internalMaterial` และหน่วย ISO ที่ `_isoUnit` เพื่อสอบย้อนกลับ
(field ที่ขึ้นต้นด้วย `_` จะถูกตัดออกก่อนส่งจริง — ถ้าเกตเวย์ของ SAP ต้องการหน่วยแบบ ISO
ให้สลับไปใช้ `_isoUnit` ได้ที่ `app/sap.py`)

รหัสของ SAP ที่ใช้จริงจะถูกบันทึกลงเอกสารด้วย (`ocr.Document.SapPartnerCode`, `SapShipToCode`,
`ocr.DocumentLine.SapMaterialCode`, `SapUomIso`)

**การตรวจเพิ่มเติม:** จำนวน > 0, VAT ที่อ่านได้เทียบกับที่คำนวณ, ผลรวมรายการเทียบยอดหัวเอกสาร
(สองข้อหลังเป็นคำเตือน ไม่บล็อกการส่ง)

ปุ่ม **+ Master** ที่ท้ายบรรทัดจะบันทึกคู่ "รหัสสินค้าคู่ค้า → Material" ลงฐานข้อมูล ครั้งต่อไปจับคู่อัตโนมัติ

---

## 4. การอ่านเอกสาร (OCR)

ตั้งที่ `.env` → `OCR_PROVIDER` (ค่าเริ่มต้น `auto`)

| ลำดับ | เงื่อนไข | ผลลัพธ์ |
|---|---|---|
| 1 | PDF ที่มีชั้นข้อความ | ดึงข้อความด้วย `pdfplumber` แล้ว parse (แม่นที่สุด ไม่ต้อง OCR) |
| 2 | ไฟล์รูป และติดตั้ง Tesseract แล้ว | OCR ด้วย `pytesseract` (`lang=tha+eng`) |
| 3 | ตั้งค่า `AZURE_DI_ENDPOINT` / `AZURE_DI_KEY` | Azure Document Intelligence รุ่น `prebuilt-invoice` |
| 4 | อ่านไม่ได้ทั้งหมด | ใช้ชุดข้อมูลตัวอย่าง + แจ้งเตือนบนหน้าจอ เพื่อให้ทดสอบขั้นตอนถัดไปได้ |

ตัวอ่านข้อความรองรับ: เลขประจำตัวผู้เสียภาษี 13 หลัก, วันที่ พ.ศ./ค.ศ. และเดือนภาษาไทย,
ยอดก่อนภาษี / VAT (แยกอัตรา % ออกจากยอดเงิน) / ภาษีหัก ณ ที่จ่าย / ยอดสุทธิ,
และแถวรายการสินค้าแบบ token-based (รหัส – ชื่อ – จำนวน – หน่วย – ราคา – จำนวนเงิน)

**ติดตั้ง Tesseract เพิ่ม (ถ้าต้องการอ่านไฟล์สแกน)**

```bat
winget install UB-Mannheim.TesseractOCR
pip install pytesseract pillow
```
แล้วใส่ path ลง `.env` → `TESSERACT_CMD=C:\Program Files\Tesseract-OCR\tesseract.exe`

---

## 5. การเชื่อมต่อ SAP

ค่าเริ่มต้นเป็น **โหมดจำลอง** (ยังไม่ยิงออก SAP จริง แต่บันทึก payload และ log ครบทุกครั้ง)
เปิดใช้งานจริงโดยกรอก `.env`

```
SAP_BASE_URL=https://s4hana.megachem.co.th:44300
SAP_USER=RFC_OCR
SAP_PASSWORD=********
SAP_CLIENT=100
SAP_COMPANY_CODE=1000
SAP_DEFAULT_PLANT=1000
```

ระบบจะ POST ไปที่ `"{SAP_BASE_URL}/sap/opu/odata/sap/{service}"` ด้วย Basic Auth
และเก็บเลขที่เอกสารที่ SAP ตอบกลับ (`SalesOrder` / `SupplierInvoice`) ลง `ocr.Document.SapDocNo`

> ระบบจริงควรเพิ่ม CSRF token (`X-CSRF-Token`) ตามการตั้งค่าเกตเวย์ของ SAP —
> แก้ได้ที่ `app/sap.py` ฟังก์ชัน `post()`

---

## 6. โครงสร้างโปรเจกต์

```
app/
  main.py         FastAPI + REST API ทั้งหมด
  db.py           เชื่อมต่อ SQL Server (pyodbc)
  mapping.py      เครื่องมือจับคู่ (Dice similarity + กติกาแต่ละ process)
  ocr_engine.py   อ่านเอกสาร + parser + ชุดข้อมูลตัวอย่าง
  sap.py          สร้าง payload + ส่งเข้า SAP
  config.py       อ่านค่าจาก .env
  tools/seed.py   ใส่ข้อมูล Master ตัวอย่าง
  tools/seed_uom.py  ใส่กฎการแปลงหน่วยตัวอย่าง
  tools/seed_sapcode.py  เติมรหัสของ SAP ให้ข้อมูลตัวอย่าง
frontend/         หน้าเว็บ (index.html / app.js / style.css) — ธีมตาม design system ของ bitkub.com
                  แยกโฟลเดอร์ออกจาก backend/ เพื่อรันเดี่ยวๆ ผ่าน dev server คนละ port ได้ (ดู .vscode/tasks.json)
sql/01_schema.sql สคริปต์สร้างตาราง
sql/02_uom.sql    ตารางการแปลงหน่วย + คอลัมน์หน่วยฝั่ง SAP
sql/03_sapcode.sql คอลัมน์รหัสของระบบ SAP ในทุก master
uploads/          ไฟล์เอกสารต้นฉบับที่อัปโหลด
legacy/           ต้นแบบเวอร์ชันไฟล์เดียว (localStorage) ก่อนต่อฐานข้อมูล
```

### REST API หลัก

| Method | Path | หน้าที่ |
|---|---|---|
| GET | `/api/health` | สถานะระบบ + จำนวนข้อมูล |
| GET/POST/PUT/DELETE | `/api/masters/{kind}` | Master data (`vendors`, `customers`, `shiptos`, `materials`, `custmaterials`, `venmaterials`, `uoms`) |
| POST | `/api/documents/upload` | อัปโหลดไฟล์ + อ่านเอกสาร |
| POST | `/api/documents/sample` | สร้างเอกสารจากชุดตัวอย่าง |
| GET/PUT/DELETE | `/api/documents/{id}` | อ่าน / แก้ไข / ลบเอกสาร |
| POST | `/api/documents/{id}/map` | รัน Mapping (รับ manual override ได้) |
| POST | `/api/documents/{id}/learn` | บันทึกคู่สินค้าลง Master |
| GET | `/api/documents/{id}/payload` | ดู payload ที่จะส่ง |
| POST | `/api/documents/{id}/post` | ส่งเข้า SAP |
| GET | `/api/logs` | ประวัติการส่ง |

เอกสาร API อัตโนมัติ: **http://localhost:8090/docs**

---

## 7. งานที่ควรทำต่อก่อนขึ้น Production

- [ ] ระบบล็อกอิน / สิทธิ์ผู้ใช้ (ตอนนี้ยังเป็น user คงที่ `it-digital@megachem.co.th`)
- [ ] ย้ายรหัสผ่านใน `.env` ไปเก็บใน Windows Credential Manager หรือใช้ Windows Authentication
- [ ] Replicate Customer / Vendor / Material จาก S/4HANA มาลง schema `ocr` อัตโนมัติ (job รายวัน)
- [ ] เปิด CSRF token + retry/queue เวลาส่ง SAP ไม่สำเร็จ
- [ ] ติดตั้ง Tesseract หรือเปิดใช้ Azure Document Intelligence สำหรับเอกสารสแกน

ล้างเอกสารทดสอบทั้งหมด (ข้อมูล Master ยังอยู่ครบ)

```sql
DELETE FROM ocr.PostLog;
DELETE FROM ocr.Document;   -- DocumentLine ถูกลบตาม (ON DELETE CASCADE)
```
