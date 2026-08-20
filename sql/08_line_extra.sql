-- ข้อมูลระดับรายการสำหรับ Non-Trade มี PO (Account Assignment, Item Category, G/L Account, Cost Center,
-- Internal Order, WBS Element, Asset Number, Tax Code, Delivery Date, GR/IR Indicator, GR-Based IV)
-- เก็บเป็น JSON ต่อรายการ (ยืดหยุ่นเหมือน HeaderJson) แทนการเพิ่มคอลัมน์ตายตัวจำนวนมาก
IF COL_LENGTH('ocr.DocumentLine', 'ExtraJson') IS NULL
    ALTER TABLE ocr.DocumentLine ADD ExtraJson nvarchar(max) NULL;
