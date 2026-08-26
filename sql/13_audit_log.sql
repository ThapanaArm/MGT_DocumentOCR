-- ตารางบันทึกประวัติการทำงานต่อเอกสาร (เพิ่ม/แก้ไข/ลบ/อ่าน OCR ใหม่) แยกดูได้ตาม Module
-- ไม่ผูก FK กับ ocr.Document เพื่อให้ยังเห็นประวัติได้แม้เอกสารนั้นถูกลบไปแล้ว (เก็บ DocNo/FileName สำรองไว้)
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name='AuditLog' AND schema_id=SCHEMA_ID('ocr'))
CREATE TABLE ocr.AuditLog(
    LogId       INT IDENTITY(1,1) PRIMARY KEY,
    DocId       INT NULL,
    Module      NVARCHAR(10) NOT NULL,
    Action      NVARCHAR(20) NOT NULL,      -- CREATE / UPDATE / DELETE / REOCR
    DocNo       NVARCHAR(100) NULL,
    FileName    NVARCHAR(255) NULL,
    Detail      NVARCHAR(500) NULL,
    PerformedBy NVARCHAR(200) NULL,
    CreatedAt   DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);
