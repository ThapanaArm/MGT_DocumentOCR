-- ค่าใช้จ่ายโดยประมาณต่อเอกสาร (คำนวณจาก OcrTokensIn/OcrTokensOut x ราคาต่อ token ของแต่ละ provider) และสกุลเงิน
IF COL_LENGTH('ocr.Document', 'OcrCost') IS NULL
    ALTER TABLE ocr.Document ADD OcrCost decimal(10,4) NULL;
IF COL_LENGTH('ocr.Document', 'OcrCostCurrency') IS NULL
    ALTER TABLE ocr.Document ADD OcrCostCurrency nvarchar(10) NULL;
