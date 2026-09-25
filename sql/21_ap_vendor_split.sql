-- รองรับ Split เอกสาร AP/II (ชุดเอกสาร Shipping 1 ไฟล์) ออกเป็นหลายใบตามรหัส Vendor ในฟอร์ม
-- โครงสร้างเดียวกับ 15_so_split.sql ของ Sales Order — เอกสารต้นฉบับ (Status='SPLIT') ยังอยู่เป็นเอกสารอ้างอิง
IF COL_LENGTH('ocr.Document', 'SourceDocId') IS NULL
    ALTER TABLE ocr.Document ADD SourceDocId int NULL;
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Document_SourceDocId')
    CREATE INDEX IX_Document_SourceDocId ON ocr.Document(SourceDocId);
GO
