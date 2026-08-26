-- เก็บว่า Log ประเภท CREATE/REOCR อ่านเอกสารด้วยโมเดล/วิธีไหน (Gemini, Claude, Tesseract OCR ฯลฯ)
-- เพื่อแสดงเป็นคอลัมน์ "Model" แยกต่างหากในหน้า Log กิจกรรม (Action อื่นเช่น UPDATE/DELETE จะเป็น NULL)
IF COL_LENGTH('ocr.AuditLog', 'OcrProvider') IS NULL
    ALTER TABLE ocr.AuditLog ADD OcrProvider nvarchar(30) NULL;
