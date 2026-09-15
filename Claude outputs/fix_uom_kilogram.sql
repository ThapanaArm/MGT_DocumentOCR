/* ============================================================================
   FIX: RequestedQuantityUnit 'Kilogram' invalid  (/IWCOR/CX_DS_EDM_FACET_ERROR)
   ----------------------------------------------------------------------------
   SAP รับหน่วยเป็น "รหัส 3 ตัว" (KG) ไม่ใช่ชื่อเต็ม (Kilogram)
   ต้นตอ: ocr.Material.Uom เก็บเป็น 'Kilogram' -> ตรงกับหน่วยในเอกสารพอดี
          ระบบเลยส่ง 'Kilogram' ผ่านไปตรงๆ ไม่เข้า UomConversion
   ต้องแก้ 2 จุดคู่กัน:
     (A) ตั้ง base unit ของ Material ให้เป็นรหัส SAP  -> 'KG'
     (B) เพิ่มกฎแปลงหน่วย: เอกสารเขียน 'Kilogram' -> SAP 'KG' (factor 1)
   DB: MGT_Document_OCR   schema: ocr
   ============================================================================ */

/* ---------- STEP 0: ดูค่าปัจจุบันก่อน (ยังไม่แก้อะไร) --------------------- */

-- base unit ปัจจุบันของ material ที่เกี่ยวข้อง
SELECT MaterialCode, SapMaterialCode, Description, Uom
FROM [MGT_Document_OCR].[ocr].[Material]
WHERE MaterialCode IN ('SOCA01-CN-BG-11', 'SOCA01-CN-BG-12');

-- base unit ทั้งหมดในระบบ: หน่วยไหนเป็น "คำเต็ม" (ยาวกว่า 3 ตัว) = ต้องแก้
SELECT Uom, COUNT(*) AS Materials
FROM [MGT_Document_OCR].[ocr].[Material]
GROUP BY Uom
ORDER BY Materials DESC;

-- กฎแปลงหน่วยที่มีอยู่ตอนนี้
SELECT * FROM [MGT_Document_OCR].[ocr].[UomConversion]
WHERE ExtUom = 'Kilogram' OR SapUom = 'Kilogram';


/* ---------- STEP A: base unit ของ Material -> รหัส SAP 'KG' ---------------- */
/* แก้ทุก material ที่เก็บ base unit ผิดเป็น 'Kilogram' (ไม่ใช่แค่ 2 ตัวในเคสนี้) */

UPDATE [MGT_Document_OCR].[ocr].[Material]
SET Uom = 'KG', UpdatedAt = SYSDATETIME()
WHERE LTRIM(RTRIM(Uom)) = 'Kilogram';
-- ^ ถ้าตาราง Material ไม่มีคอลัมน์ UpdatedAt ให้ลบส่วน ", UpdatedAt = SYSDATETIME()" ออก


/* ---------- STEP B: กฎแปลงหน่วย Kilogram -> KG (global, ทุก material) ------ */
/* MaterialCode = NULL หมายถึงใช้ได้กับทุก material (ระบบถือเป็น global rule)
   Factor = 1 เพราะเป็นแค่การเปลี่ยน "คำ" ไม่ใช่แปลงปริมาณจริง
   กัน insert ซ้ำด้วย NOT EXISTS */

INSERT INTO [MGT_Document_OCR].[ocr].[UomConversion]
       (MaterialCode, ExtUom, SapUom, SapUomIso, Factor, Note, CreatedAt)
SELECT  NULL, 'Kilogram', 'KG', 'KGM', 1,
        N'Normalize document word Kilogram -> SAP unit KG', SYSDATETIME()
WHERE NOT EXISTS (
    SELECT 1 FROM [MGT_Document_OCR].[ocr].[UomConversion]
    WHERE ISNULL(MaterialCode, '') = '' AND LTRIM(RTRIM(ExtUom)) = 'Kilogram'
);
-- ^ ถ้าไม่มีคอลัมน์ CreatedAt ให้ลบ ", CreatedAt" และ ", SYSDATETIME()" ออก


/* ---------- STEP C: ตรวจผลหลังแก้ ---------------------------------------- */

SELECT MaterialCode, Uom
FROM [MGT_Document_OCR].[ocr].[Material]
WHERE MaterialCode IN ('SOCA01-CN-BG-11', 'SOCA01-CN-BG-12');   -- ควรเป็น KG

SELECT MaterialCode, ExtUom, SapUom, SapUomIso, Factor
FROM [MGT_Document_OCR].[ocr].[UomConversion]
WHERE ExtUom = 'Kilogram';                                       -- ควรมี NULL/Kilogram/KG/KGM/1


/* ============================================================================
   หน่วยอื่นๆ (ทำเพิ่มเองตามที่เจอในเอกสาร) — เฉพาะกรณี "เปลี่ยนคำ" factor = 1
   รหัส SAP อ้างจาก UomIso ในโค้ด: KG,G,TON,L,ML,M,EA,PC,PCS,BOX,BAG,DRUM
   ----------------------------------------------------------------------------
   INSERT [ocr].[UomConversion] (MaterialCode,ExtUom,SapUom,SapUomIso,Factor,Note)
   VALUES
     (NULL,'Liter','L','LTR',1,N'word->code'),
     (NULL,'Litre','L','LTR',1,N'word->code'),
     (NULL,'Gram','G','GRM',1,N'word->code'),
     (NULL,'Ton','TON','TNE',1,N'word->code'),
     (NULL,'Piece','PC','PCE',1,N'word->code'),
     (NULL,'Pieces','PC','PCE',1,N'word->code');

   *** สำคัญ ***
   - global rule (MaterialCode=NULL) ใช้ได้เฉพาะ "เปลี่ยนคำหน่วยเดียวกัน" (factor 1)
     และ SapUom ต้องตรงกับ base unit ของ material นั้น มิฉะนั้นระบบจะขึ้น
     "Rule converts to X but Material uses unit Y" (fail)
   - ถ้าเป็นการ "แปลงข้ามหน่วยจริง" เช่น เอกสารเป็น Bag แต่ SAP เก็บเป็น KG
     ต้องใส่แถวแบบเจาะจง material พร้อม Factor จริง เช่น
     (N'SOCA01-CN-BG-11','Bag','KG','KGM',25, N'1 bag = 25 kg')
   - อย่าลืมทำ STEP A (base unit = รหัส SAP) ให้ครบทุก material ที่เก็บเป็นคำเต็ม
   ============================================================================ */
