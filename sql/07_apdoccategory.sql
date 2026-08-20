-- ประเภทเอกสาร AP Invoice: Trade / Non-Trade มี PO (Service) / Non-Trade มี PO (Item) / Non-Trade ไม่มี PO
-- ผู้ใช้เลือกเองในหน้าเอกสาร ไม่ได้เดาจาก OCR — เก็บไว้แสดง/กรองในทะเบียนเอกสารเท่านั้น (ยังไม่ผูกกับการส่ง SAP)
IF COL_LENGTH('ocr.Document', 'ApDocCategory') IS NULL
    ALTER TABLE ocr.Document ADD ApDocCategory nvarchar(30) NULL;
