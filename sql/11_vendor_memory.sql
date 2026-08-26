-- จำข้อมูล header ที่เคยยืนยัน/แก้ไขไว้ต่อคู่ค้าแต่ละราย (คีย์ด้วยเลขผู้เสียภาษี + module) เพื่อเดา
-- ฟิลด์ที่คงที่ต่อคู่ค้า (ชื่อ, เงื่อนไขชำระเงิน, สกุลเงิน ฯลฯ) ให้อัตโนมัติในเอกสารถัดไปจากคู่ค้าเดิม
-- ไม่รวมฟิลด์ที่เปลี่ยนทุกเอกสาร เช่น เลขที่ใบแจ้งหนี้ วันที่ ยอดเงิน หรือรายการสินค้า
IF OBJECT_ID('ocr.VendorMemory') IS NULL
BEGIN
    CREATE TABLE ocr.VendorMemory(
        TaxId       nvarchar(50)  NOT NULL,
        Module      nvarchar(10)  NOT NULL,
        MemoryJson  nvarchar(max) NOT NULL,
        UpdatedAt   datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
        CONSTRAINT PK_VendorMemory PRIMARY KEY (TaxId, Module)
    );
END
