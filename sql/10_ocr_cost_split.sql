-- แยกค่าใช้จ่ายโดยประมาณเป็นส่วน input/output แยกกัน (OcrCost เดิมยังเก็บผลรวมไว้เหมือนเดิม)
IF COL_LENGTH('ocr.Document', 'OcrInputCost') IS NULL
    ALTER TABLE ocr.Document ADD OcrInputCost decimal(10,4) NULL;
IF COL_LENGTH('ocr.Document', 'OcrOutputCost') IS NULL
    ALTER TABLE ocr.Document ADD OcrOutputCost decimal(10,4) NULL;
