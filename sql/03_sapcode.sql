/* =====================================================================
   เพิ่มรหัสของระบบ SAP (SAP key) ในทุก master
   รหัสภายในเดิม = คีย์ของระบบ OCR / ใช้อ้างอิงภายใน
   รหัส SAP      = ค่าที่ส่งจริงใน OData payload ไปยัง S/4HANA
   ===================================================================== */
IF COL_LENGTH('ocr.Customer','SapCustomerCode') IS NULL
  ALTER TABLE ocr.Customer ADD SapCustomerCode nvarchar(20) NULL;   -- Sold-to / BP number
GO
IF COL_LENGTH('ocr.ShipTo','SapShipToCode') IS NULL
  ALTER TABLE ocr.ShipTo ADD SapShipToCode nvarchar(20) NULL;       -- Ship-to partner number
GO
IF COL_LENGTH('ocr.Vendor','SapVendorCode') IS NULL
  ALTER TABLE ocr.Vendor ADD SapVendorCode nvarchar(20) NULL;       -- Supplier / BP number
GO
IF COL_LENGTH('ocr.Material','SapMaterialCode') IS NULL
  ALTER TABLE ocr.Material ADD SapMaterialCode nvarchar(40) NULL;   -- Material number ใน SAP
GO
IF COL_LENGTH('ocr.UomConversion','SapUomIso') IS NULL
  ALTER TABLE ocr.UomConversion ADD SapUomIso nvarchar(3) NULL;     -- ISO code เช่น KGM / LTR / PCE
GO
/* เก็บรหัส SAP ที่ส่งจริงลงเอกสาร เพื่อการตรวจสอบย้อนหลัง */
IF COL_LENGTH('ocr.Document','SapPartnerCode') IS NULL
  ALTER TABLE ocr.Document ADD SapPartnerCode nvarchar(20) NULL, SapShipToCode nvarchar(20) NULL;
GO
IF COL_LENGTH('ocr.DocumentLine','SapMaterialCode') IS NULL
  ALTER TABLE ocr.DocumentLine ADD SapMaterialCode nvarchar(40) NULL, SapUomIso nvarchar(3) NULL;
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Material_SapCode')
  CREATE INDEX IX_Material_SapCode ON ocr.Material(SapMaterialCode);
GO
