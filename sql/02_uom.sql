/* =====================================================================
   ตารางการแปลงหน่วย (UoM Conversion) + คอลัมน์หน่วย/จำนวนฝั่ง SAP
   ===================================================================== */
IF OBJECT_ID('ocr.UomConversion') IS NULL
CREATE TABLE ocr.UomConversion(
  Id            int IDENTITY(1,1) PRIMARY KEY,
  MaterialCode  nvarchar(30)  NULL,          -- NULL = ใช้ได้กับทุก Material (กฎกลาง)
  ExtUom        nvarchar(20)  NOT NULL,      -- หน่วยตามเอกสารของคู่ค้า
  SapUom        nvarchar(10)  NOT NULL,      -- หน่วยใน SAP
  Factor        decimal(18,6) NOT NULL,      -- 1 ExtUom = Factor x SapUom
  Note          nvarchar(200) NULL,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  UpdatedAt     datetime2(0)  NULL,
  CONSTRAINT FK_Uom_Material FOREIGN KEY(MaterialCode) REFERENCES ocr.Material(MaterialCode)
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Uom_Lookup')
  CREATE INDEX IX_Uom_Lookup ON ocr.UomConversion(ExtUom, MaterialCode);
GO

IF COL_LENGTH('ocr.DocumentLine','SapQty') IS NULL
  ALTER TABLE ocr.DocumentLine ADD SapQty decimal(18,3) NULL, SapUom nvarchar(10) NULL,
                                   UomFactor decimal(18,6) NULL;
GO
