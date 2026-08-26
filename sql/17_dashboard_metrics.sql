-- เก็บเวลาที่ใช้อ่านเอกสารจริง (มิลลิวินาที) — ใช้คำนวณ "เฉลี่ยต่อเอกสาร" ในการ์ดประสิทธิภาพ OCR หน้า Overview
IF COL_LENGTH('ocr.Document', 'OcrDurationMs') IS NULL
    ALTER TABLE ocr.Document ADD OcrDurationMs int NULL;
IF COL_LENGTH('ocr.SalesOrder', 'OcrDurationMs') IS NULL
    ALTER TABLE ocr.SalesOrder ADD OcrDurationMs int NULL;
