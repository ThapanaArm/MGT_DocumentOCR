-- รองรับ Split เอกสาร PO 1 ใบ เป็นหลาย Sales Order — เก็บว่า Sales Order ที่แยกออกมา มาจากเอกสารต้นฉบับใบไหน
-- เอกสารต้นฉบับ (Status='SPLIT') ยังอยู่ครบเป็นเอกสารอ้างอิง ไม่ถูกลบ/แก้ไขรายการ
IF COL_LENGTH('ocr.SalesOrder', 'SourceDocId') IS NULL
    ALTER TABLE ocr.SalesOrder ADD SourceDocId int NULL;
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_SalesOrder_SourceDocId')
    CREATE INDEX IX_SalesOrder_SourceDocId ON ocr.SalesOrder(SourceDocId);
GO
