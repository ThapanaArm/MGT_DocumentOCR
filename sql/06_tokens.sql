-- เก็บจำนวน Token ที่ใช้ตอนอ่านเอกสาร (เฉพาะ provider ที่เป็น AI/LLM เช่น Claude, Gemini — คิดค่าใช้จ่ายตาม token)
IF COL_LENGTH('ocr.Document', 'OcrTokensIn') IS NULL
    ALTER TABLE ocr.Document ADD OcrTokensIn int NULL;
IF COL_LENGTH('ocr.Document', 'OcrTokensOut') IS NULL
    ALTER TABLE ocr.Document ADD OcrTokensOut int NULL;
