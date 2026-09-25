# เช็คลิสงาน MGT Document OCR — Supplier Invoice Import Flow

อ้างอิง: `Supplier_Invoice_Import_Flow1.docx`, template `2000_Journal Voucher`, `2000_Input Vat`,
คู่มือ SAP `KUT-FI-AP-201` (อ้างอิง PO / MIRO) และ `KUT-FI-AP-204` (ไม่อ้างอิง PO / FB60)

สถานะ: `[x]` ทำแล้ว · `[ ]` ยังต้องทำ · **?** รอคำตอบจากฝั่ง Finance

---

## 01 · จำแนกประเภทเอกสาร

- [x] dropdown ประเภทเอกสารตอนอัปโหลด (`ApDocCategories` ใน `DocumentsController.cs`)
- [x] Expense บังคับเป็น Incoming Invoice (FB60) เสมอ ไม่สนใจว่ามีเลข PO หรือไม่
- [ ] ปรับรายการให้ตรงกับ flow และคู่มือ: Inventory / Expense-Service / Fixed Asset (คุมงบ) / Fixed Asset (ไม่คุมงบ) / Sub Contract
- [ ] คู่มือ AP-201 ระบุคู่บัญชีของแต่ละประเภทไว้แล้ว ใช้เป็นฐานกำหนด G/L ตั้งต้นได้:
  - Inventory: Dr. สินค้า / Cr. GR-IR
  - Expense: Dr. ค่าใช้จ่าย
  - Fixed Asset คุมงบ: Dr. Suspense Purchase FA / Cr. GR-IR
  - Fixed Asset ไม่คุมงบ: Dr. Fixed Asset (Cost)
  - Sub Contract: Dr. เบิกวัตถุดิบไปผลิต / Cr. วัตถุดิบ, Dr. ค่าจ้างผลิต / Cr. GR-IR
  - ทุกประเภทปิดท้ายด้วย Dr. ภาษีซื้อ / Cr. เจ้าหนี้

## 02 · Document Type (Reference Document Category 1 / 3)

- [x] ช่อง `refDocType` ในแท็บ PO Reference และให้ OCR อ่านค่าให้แล้ว
- [ ] แก้กติกาให้ตรง flow: **นำเข้าจากต่างประเทศ → 3** (ค่าสินค้า + Landed Cost: Shipping / Thai Customs / Insurance), **ในประเทศ → 1** (เฉพาะค่าสินค้า)
- [ ] หาวิธีรู้ว่าเป็นสินค้านำเข้า: เจอใบขนสินค้า/ใบเสร็จกรมศุลกากรในชุดเอกสาร, สกุลเงินต่างประเทศ, หรือดึงจาก PO ใน SAP
- **?** ถ้าเอกสารชุดเดียวมีทั้งค่าสินค้าและ Landed Cost ต้องแยกเป็นหลายเอกสารใน SAP หรือบันทึกใบเดียว

## 03 · ตรวจสอบเอกสาร Shipping ทีละรายการ (Flag Y/N)

- [ ] เพิ่ม flag รายบรรทัด: ใบเสร็จรับเงิน / ใบกำกับภาษี → **Y**, ใบแจ้งหนี้ → **N**
- [ ] ให้ OCR อ่านประเภทของเอกสารแนบแต่ละใบในชุดแล้วเติม flag
- [ ] เพิ่มคอลัมน์ในตาราง Line Items ให้แก้ไขได้
- [ ] เก็บลง `ExtraJson` ของ line (มีอยู่แล้ว ไม่ต้องเพิ่มคอลัมน์ในตาราง)
- **?** flag นี้เอาไปใช้ตัดสินอะไร — ใช้แยกว่าใบไหนขึ้นรายงานภาษีซื้อได้ใช่หรือไม่

## 04 · บันทึก Basic Data + Currency จาก DO

- [x] ฟอร์ม Basic Data ครบ (Supplier, Invoice Date, Reference, Posting Date, Document Type, Amount, Currency, Tax Amount)
- [x] Posting Date = วันที่อัปโหลด/OCR, Tax Reporting / Tax Fulfill / Tax Date = Invoice Date
- [ ] ดึง Currency จาก DO ใน SAP มาตรวจสอบ — ยังไม่มี client สำหรับ Delivery Order (มีเฉพาะ Billing / BusinessPartner / Product / SalesOrder)
- [ ] เตือนเมื่อสกุลเงินในเอกสารไม่ตรงกับใน SAP
- [ ] Document Type ควรผูก master จากคู่มือ: KR Vendor Invoice, RE Invoice-Gross, KG Vendor Credit Note, KA, KN, KP

## 05 · PO Reference

- [x] มีช่อง PO Reference และส่งเข้า payload (`PurchaseOrder`, `PurchaseOrderItem`)
- [ ] ตรวจสอบเลข PO กับ SAP จริง (ตอนนี้เป็นช่องพิมพ์อิสระ)
- [ ] ดึง PO item จาก SAP มาจับคู่กับรายการในเอกสาร แทนการรันเลข item 10, 20, 30 เอง

## 06 · GL Account Item

- [ ] **Inventory ไม่ต้องบันทึก GL Account Item** แต่ระบบแสดงตารางนี้เสมอ (`showGlItems = AP || II`) ต้องซ่อนเมื่อประเภทเป็น Inventory
- [ ] กันไม่ให้ส่ง GL items เข้า payload สำหรับ Inventory
- [x] Incoming Invoice (Expense) สร้างแถว G/L จากรายการที่ OCR อ่านได้ให้อัตโนมัติ (S-Debit) และแถวหัก ณ ที่จ่ายเป็น H-Credit
- [ ] ยังไม่มีเลขบัญชี G/L — ดูหัวข้อ Master Data ด้านล่าง

## 07 · Payment

- [ ] Business Place ยังว่างทั้งที่เป็น required — คู่มือระบุ Head Office = **0000** สำหรับทั้ง company code 1000 (MGT) และ 2000 (GLC) ใช้เป็นค่าตั้งต้นได้
- [ ] Baseline Date: คู่มือบอกว่าอิงตาม Payment Terms (บางรหัสนับจาก Posting Date บางรหัสนับจาก Document Date) ต้องทำตารางคำนวณ
- [ ] Payment Terms ควรเป็น dropdown จาก master ในคู่มือ (~50 รหัส: 0001, 1001, 2101, 4003, 5004, 6003, 6101 ฯลฯ)
- [ ] House Bank / Account ID มี master ในคู่มือแยกตาม company code พร้อมเลข G/L ของแต่ละบัญชี

## 08 · Withholding Tax

- [x] อ่านยอดหัก ณ ที่จ่ายจากทุกใบในชุด และใช้ผลรวมเป็น `whtAmount`
- [ ] **W/Tax Type ต้องใช้รหัสจริง** ตอนนี้หน้าจอเก็บเป็นข้อความ "WHT Type for Payment Posting" ควรเป็นรหัส:
  - Posting at Invoice: `TI` / `TJ` / `TK`
  - Posting at Payment: `OA` / `OB` / `OC`
- [ ] **W/Tax Code** ยังว่าง ต้องทำ master ตามคู่มือ (01 การขนส่ง, 02 ดอกเบี้ย, 03 ประกันภัย, 04 โฆษณา, 05 จ้างทำของ, 06 ซอฟต์แวร์, 07 ค่าเช่า, 08 ค่านายหน้า, 09 บริการ, 10 ใบอนุญาต — ตัวเลขอัตราในไฟล์ PDF อ่านไม่ชัด ขอยืนยันอีกครั้ง)
- [ ] **Recipient Type** ยังไม่มีในระบบเลย คู่มือระบุ 02 / 03 / 53 / 54 (ภ.ง.ด.) และเป็นข้อมูลบังคับ
- [ ] W/Tax Base: คู่มือระบุว่าเป็นยอดก่อน VAT — ปัจจุบันใช้ `subTotal` ของทั้งเอกสาร ควรเป็นฐานของใบที่ถูกหักจริง
- **?** หลักเกณฑ์การเลือก W/Tax Code จากคำอธิบายรายการ ให้ระบบเดาให้หรือให้ user เลือกเอง

## 09 · ตรวจสอบความครบถ้วนก่อน Export

- [ ] หน้าสรุปตรวจสอบ Basic Data / PO Reference / Payment / Withholding Tax ว่าครบ
- [ ] บล็อกปุ่ม Export ถ้ายังขาด พร้อมแสดงรายการช่องที่ขาดเป็นข้อ ๆ
- [ ] ตรวจว่า Debit = Credit ก่อนออกไฟล์ JV

## 10 · Export File

### 10.1 Journal Voucher (`2000_Journal Voucher Template.xlsx`)

โครงสร้าง: sheet `Control Sheet` (RUNNING / SHEET / SAP DOC. NO. / STATUS / Start Date / Finish Date)
และ sheet `1` = 1 ใบสำคัญ หัวมี Posting Date, Reference ตารางคือ
LINE ITEM / G/L ACCOUNT / TAX CODE / DEBIT / CREDIT / ASSIGMENT / TEXT / COST CENTER

- [ ] เขียนตัวสร้างไฟล์จาก template
- [ ] เติม Posting Date, Reference จาก header
- [ ] เติมรายการจากตาราง Line Items (Debit) + แถวหัก ณ ที่จ่าย (Credit)
- [ ] **เพิ่มบรรทัดเจ้าหนี้ฝั่ง Credit** ให้เอกสารบาลานซ์ — ปัจจุบันระบบมีแต่ฝั่งค่าใช้จ่าย
- [ ] ตัวนับเลข running ของใบสำคัญสำหรับ Control Sheet
- **?** 1 ไฟล์ = 1 เอกสาร หรือรวมหลายเอกสารโดยเพิ่ม sheet `2`, `3`
- **?** Status / Start Date / Finish Date ให้เว้นว่างให้คนกรอกตอน post ใช่หรือไม่

### 10.2 Input VAT (`2000_Input Vat Template 1.xlsx`)

โครงสร้าง sheet `Vat Report` หัวตารางแถว 4:
Header Text / Document No. / Doc. Date / Posting Date / Tax ID No. / Branch / Invoice No /
Item text / Base amount / Input tax / Line 1 / Tax Code / Line 2 / Status / Start Date / Finish Date

- [ ] **เปลี่ยนวิธีเก็บ VAT** จากบรรทัดเดียวที่มีแต่ยอด ให้เป็นข้อมูลมีโครงสร้างรายใบ: ผู้ออกเอกสาร / เลขผู้เสียภาษี / สาขา / เลขที่ใบกำกับ / วันที่ / ฐานภาษี / ยอดภาษี
- [x] ตัดกรอง VAT ของกรมศุลกากรและกงสุลออกแล้ว (ทั้งใน prompt และในโค้ด)
- [ ] เขียนตัวสร้างไฟล์จาก template
- **?** Branch ใน template เป็น `00000` (5 หลัก) แต่ Business Place ใน SAP เป็น `0000` (4 หลัก) ใช้ค่าไหน
- **?** คอลัมน์ Line 1 / Line 2 หมายถึงอะไร
- **?** export ทีละเอกสาร หรือรวมทั้งงวดแล้ว export ครั้งเดียว

---

## Master Data ที่ต้องเพิ่มเข้าระบบ (ได้จากคู่มือแล้ว พร้อมทำ)

- [ ] Document Type: KA, KG, KN, KP, KR, KZ, RE, RV, ZP, ZS, ZV พร้อมช่วงเลขเอกสาร
- [ ] Payment Terms ~50 รหัส พร้อมว่า baseline date นับจาก Posting Date หรือ Document Date
- [ ] Business Place: 0000 Head Office (company code 1000 และ 2000)
- [ ] House Bank / Account ID / G/L ของบัญชีธนาคาร แยกตาม company code
- [ ] Withholding Tax Type: TI, TJ, TK, OA, OB, OC
- [ ] Withholding Tax Code: 01–10
- [ ] Recipient Type: 02, 03, 53, 54

## ยังไม่มีข้อมูล

- [ ] ผังบัญชี G/L สำหรับค่าใช้จ่ายแต่ละประเภท (เช่น STORAGE CHARGE, TRUCKING, CUSTOMS FEE → เลขบัญชีอะไร)
- [ ] Cost Center ใช้จากไหน (ผูกกับ G/L หรือกับแผนกผู้ขอซื้อ)
- [ ] Tax Code ของ VAT มีกี่รหัส (ระบบใช้ V1 อย่างเดียวอยู่ตอนนี้)

## งานคงค้างจากที่แก้ไปแล้ว

- [ ] Rebuild backend แล้ว **restart process** ทุกครั้ง มิฉะนั้นยังใช้ dll ตัวเก่า (เคยทำให้เข้าใจผิดว่าโค้ดไม่ทำงานมาแล้ว)
- [ ] เอกสารที่อ่านไว้ก่อนแก้ ต้องกด Re-read ถึงจะได้ข้อมูลตามกติกาใหม่
- [ ] ตาราง DETAIL ของ MIRO ยังแสดงแถว WHT / VAT ปนกับรายการสินค้าและรอ mapping material — ควรซ่อนเหมือนฝั่ง FB60
