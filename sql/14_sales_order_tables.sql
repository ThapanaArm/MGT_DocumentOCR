/* =====================================================================
   แยกตารางเก็บข้อมูล Sales Order ออกจาก ocr.Document/DocumentLine/DocumentChat
   ที่ใช้ร่วมกันทุกโมดูล (คั่นด้วยคอลัมน์ Module) — เป็นตารางกายภาพของตัวเองล้วน ๆ
   คอลัมน์เหมือน ocr.Document/DocumentLine/DocumentChat ทุกอย่าง (เพื่อให้โค้ดฝั่ง backend
   ใช้ query แบบเดียวกันได้ แค่สลับชื่อตาราง) ยกเว้น DocId เริ่มนับที่ 100,000,001 แทนที่จะเริ่มที่ 1
   เพื่อไม่ให้เลขที่เอกสารชนกับ ocr.Document ที่มีอยู่แล้ว (แยกฐานข้อมูลกันจริง แต่ยังสืบเลขที่ไม่ซ้ำกัน)
   ===================================================================== */
IF OBJECT_ID('ocr.SalesOrder') IS NULL
CREATE TABLE ocr.SalesOrder(
  DocId             int IDENTITY(100000001,1) PRIMARY KEY,
  Module            nvarchar(10)  NOT NULL DEFAULT 'SO',
  FileName          nvarchar(260) NULL,
  StoredPath        nvarchar(400) NULL,
  FileSize          int           NULL,
  OcrProvider       nvarchar(30)  NULL,
  OcrConfidence     decimal(5,4)  NULL,
  OcrConfidenceNote nvarchar(500) NULL,
  OcrTokensIn       int           NULL,
  OcrTokensOut      int           NULL,
  OcrCost           decimal(10,4) NULL,
  OcrInputCost      decimal(10,4) NULL,
  OcrOutputCost     decimal(10,4) NULL,
  OcrCostCurrency   nvarchar(10)  NULL,
  ApDocCategory     nvarchar(30)  NULL,
  Status            nvarchar(20)  NOT NULL DEFAULT 'NEW',   -- NEW | MAPPED | INCOMPLETE | POSTED
  DocNo             nvarchar(50)  NULL,
  DocDate           date          NULL,
  PostingDate       date          NULL,
  PartnerName       nvarchar(200) NULL,
  PartnerTaxId      nvarchar(20)  NULL,
  PartnerCode       nvarchar(20)  NULL,
  ShipToCode        nvarchar(30)  NULL,
  SapPartnerCode    nvarchar(20)  NULL,
  SapShipToCode     nvarchar(20)  NULL,
  Currency          nvarchar(5)   NULL,
  SubTotal          decimal(18,2) NULL,
  VatRate           decimal(5,2)  NULL,
  VatAmount         decimal(18,2) NULL,
  WhtAmount         decimal(18,2) NULL,
  TotalAmount       decimal(18,2) NULL,
  HeaderJson        nvarchar(max) NULL,
  RawText           nvarchar(max) NULL,
  MapStatus         nvarchar(20)  NULL,               -- PASS | FAIL
  MapMessage        nvarchar(max) NULL,
  SapDocNo          nvarchar(30)  NULL,
  PostedAt          datetime2(0)  NULL,
  PostedBy          nvarchar(100) NULL,
  CreatedAt         datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  CreatedBy         nvarchar(100) NULL,
  UpdatedAt         datetime2(0)  NULL
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_SalesOrder_Status')
  CREATE INDEX IX_SalesOrder_Status ON ocr.SalesOrder(Status, CreatedAt DESC);
GO

IF OBJECT_ID('ocr.SalesOrderLine') IS NULL
CREATE TABLE ocr.SalesOrderLine(
  LineId          int IDENTITY(1,1) PRIMARY KEY,
  DocId           int           NOT NULL,
  ItemNo          int           NOT NULL,
  ExtCode         nvarchar(60)  NULL,
  ExtDesc         nvarchar(300) NULL,
  Qty             decimal(18,3) NULL,
  Uom             nvarchar(10)  NULL,
  UnitPrice       decimal(18,4) NULL,
  Amount          decimal(18,2) NULL,
  MaterialCode    nvarchar(30)  NULL,
  MapStatus       nvarchar(10)  NULL,               -- ok | manual | fail
  MapMethod       nvarchar(100) NULL,
  SapQty          decimal(18,3) NULL,
  SapUom          nvarchar(10)  NULL,
  UomFactor       decimal(18,6) NULL,
  SapMaterialCode nvarchar(40)  NULL,
  SapUomIso       nvarchar(3)   NULL,
  ExtraJson       nvarchar(max) NULL,
  CONSTRAINT FK_SOLine_SalesOrder FOREIGN KEY(DocId) REFERENCES ocr.SalesOrder(DocId) ON DELETE CASCADE,
  CONSTRAINT UQ_SOLine UNIQUE(DocId, ItemNo)
);
GO

IF OBJECT_ID('ocr.SalesOrderChat') IS NULL
CREATE TABLE ocr.SalesOrderChat(
  ChatId      int IDENTITY(1,1) PRIMARY KEY,
  DocId       int           NOT NULL,
  Role        nvarchar(10)  NOT NULL,          -- user | assistant
  MessageText nvarchar(max) NULL,
  ImagePath   nvarchar(400) NULL,
  CreatedAt   datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  CreatedBy   nvarchar(100) NULL,
  CONSTRAINT FK_SOChat_SalesOrder FOREIGN KEY(DocId) REFERENCES ocr.SalesOrder(DocId) ON DELETE CASCADE
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_SOChat_Doc')
  CREATE INDEX IX_SOChat_Doc ON ocr.SalesOrderChat(DocId, ChatId);
GO

-- ocr.PostLog.Module เดิมกำหนดไว้แค่ nvarchar(2) เหมือน Document เดิม (บั๊กแฝงเดียวกับที่เจอใน Document ตอนเพิ่ม PODP)
-- ขยายให้เท่ากันเผื่อโค้ดของยังไม่พังตอนส่ง PODP เข้า SAP จริง
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA='ocr' AND TABLE_NAME='PostLog' AND COLUMN_NAME='Module' AND CHARACTER_MAXIMUM_LENGTH < 10)
    ALTER TABLE ocr.PostLog ALTER COLUMN Module nvarchar(10) NULL;
GO
