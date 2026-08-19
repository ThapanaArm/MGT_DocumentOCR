/* =====================================================================
   MGT_Document_OCR — schema สำหรับระบบ OCR -> SAP S/4HANA
   ใช้ schema [ocr] แยกจากตารางเดิมของฐานข้อมูล
   ===================================================================== */
IF SCHEMA_ID('ocr') IS NULL EXEC('CREATE SCHEMA ocr');
GO

/* ---------------- MASTER: ลูกค้า ---------------- */
IF OBJECT_ID('ocr.Customer') IS NULL
CREATE TABLE ocr.Customer(
  CustomerCode  nvarchar(20)  NOT NULL PRIMARY KEY,
  NameTh        nvarchar(200) NOT NULL,
  NameEn        nvarchar(200) NULL,
  TaxId         nvarchar(20)  NULL,
  Branch        nvarchar(10)  NULL,
  SalesOrg      nvarchar(10)  NULL,
  DistChannel   nvarchar(10)  NULL,
  Division      nvarchar(10)  NULL,
  Currency      nvarchar(5)   NULL,
  PaymentTerms  nvarchar(20)  NULL,
  IsActive      bit           NOT NULL DEFAULT 1,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  UpdatedAt     datetime2(0)  NULL
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Customer_TaxId')
  CREATE INDEX IX_Customer_TaxId ON ocr.Customer(TaxId);
GO

/* ---------------- MASTER: สถานที่ส่งของ ---------------- */
IF OBJECT_ID('ocr.ShipTo') IS NULL
CREATE TABLE ocr.ShipTo(
  ShipToCode    nvarchar(30)  NOT NULL PRIMARY KEY,
  CustomerCode  nvarchar(20)  NOT NULL,
  ShipToName    nvarchar(200) NOT NULL,
  Address       nvarchar(400) NULL,
  IsActive      bit           NOT NULL DEFAULT 1,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  UpdatedAt     datetime2(0)  NULL,
  CONSTRAINT FK_ShipTo_Customer FOREIGN KEY(CustomerCode) REFERENCES ocr.Customer(CustomerCode)
);
GO

/* ---------------- MASTER: สินค้า (SAP) ---------------- */
IF OBJECT_ID('ocr.Material') IS NULL
CREATE TABLE ocr.Material(
  MaterialCode  nvarchar(30)  NOT NULL PRIMARY KEY,
  Description   nvarchar(300) NOT NULL,
  Uom           nvarchar(10)  NULL,
  Plant         nvarchar(10)  NULL,
  MatGroup      nvarchar(20)  NULL,
  IsActive      bit           NOT NULL DEFAULT 1,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  UpdatedAt     datetime2(0)  NULL
);
GO

/* ---------------- MASTER: สินค้าฝั่งลูกค้า ---------------- */
IF OBJECT_ID('ocr.CustomerMaterial') IS NULL
CREATE TABLE ocr.CustomerMaterial(
  Id            int IDENTITY(1,1) PRIMARY KEY,
  CustomerCode  nvarchar(20)  NOT NULL,
  ExtCode       nvarchar(60)  NULL,
  ExtDesc       nvarchar(300) NULL,
  MaterialCode  nvarchar(30)  NOT NULL,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  UpdatedAt     datetime2(0)  NULL,
  CONSTRAINT FK_CustMat_Customer FOREIGN KEY(CustomerCode) REFERENCES ocr.Customer(CustomerCode),
  CONSTRAINT FK_CustMat_Material FOREIGN KEY(MaterialCode) REFERENCES ocr.Material(MaterialCode)
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_CustMat_Lookup')
  CREATE INDEX IX_CustMat_Lookup ON ocr.CustomerMaterial(CustomerCode, ExtCode);
GO

/* ---------------- MASTER: ผู้ขาย ---------------- */
IF OBJECT_ID('ocr.Vendor') IS NULL
CREATE TABLE ocr.Vendor(
  VendorCode    nvarchar(20)  NOT NULL PRIMARY KEY,
  VendorName    nvarchar(200) NOT NULL,
  TaxId         nvarchar(20)  NULL,
  Branch        nvarchar(10)  NULL,
  Currency      nvarchar(5)   NULL,
  PaymentTerms  nvarchar(20)  NULL,
  ReconAcct     nvarchar(20)  NULL,
  WhtCode       nvarchar(30)  NULL,
  IsActive      bit           NOT NULL DEFAULT 1,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  UpdatedAt     datetime2(0)  NULL
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Vendor_TaxId')
  CREATE INDEX IX_Vendor_TaxId ON ocr.Vendor(TaxId);
GO

/* ---------------- MASTER: สินค้าฝั่งผู้ขาย ---------------- */
IF OBJECT_ID('ocr.VendorMaterial') IS NULL
CREATE TABLE ocr.VendorMaterial(
  Id            int IDENTITY(1,1) PRIMARY KEY,
  VendorCode    nvarchar(20)  NOT NULL,
  ExtCode       nvarchar(60)  NULL,
  ExtDesc       nvarchar(300) NULL,
  MaterialCode  nvarchar(30)  NOT NULL,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  UpdatedAt     datetime2(0)  NULL,
  CONSTRAINT FK_VenMat_Vendor   FOREIGN KEY(VendorCode)   REFERENCES ocr.Vendor(VendorCode),
  CONSTRAINT FK_VenMat_Material FOREIGN KEY(MaterialCode) REFERENCES ocr.Material(MaterialCode)
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_VenMat_Lookup')
  CREATE INDEX IX_VenMat_Lookup ON ocr.VendorMaterial(VendorCode, ExtCode);
GO

/* ---------------- TRANSACTION: เอกสาร ---------------- */
IF OBJECT_ID('ocr.Document') IS NULL
CREATE TABLE ocr.Document(
  DocId         int IDENTITY(1,1) PRIMARY KEY,
  Module        nvarchar(2)   NOT NULL,          -- AP | SO
  FileName      nvarchar(260) NULL,
  StoredPath    nvarchar(400) NULL,
  FileSize      int           NULL,
  OcrProvider   nvarchar(30)  NULL,
  OcrConfidence decimal(5,4)  NULL,
  Status        nvarchar(20)  NOT NULL DEFAULT 'NEW',   -- NEW | MAPPED | INCOMPLETE | POSTED
  DocNo         nvarchar(50)  NULL,
  DocDate       date          NULL,
  PostingDate   date          NULL,
  PartnerName   nvarchar(200) NULL,
  PartnerTaxId  nvarchar(20)  NULL,
  PartnerCode   nvarchar(20)  NULL,
  ShipToCode    nvarchar(30)  NULL,
  Currency      nvarchar(5)   NULL,
  SubTotal      decimal(18,2) NULL,
  VatRate       decimal(5,2)  NULL,
  VatAmount     decimal(18,2) NULL,
  WhtAmount     decimal(18,2) NULL,
  TotalAmount   decimal(18,2) NULL,
  HeaderJson    nvarchar(max) NULL,
  RawText       nvarchar(max) NULL,
  MapStatus     nvarchar(20)  NULL,               -- PASS | FAIL
  MapMessage    nvarchar(max) NULL,
  SapDocNo      nvarchar(30)  NULL,
  PostedAt      datetime2(0)  NULL,
  PostedBy      nvarchar(100) NULL,
  CreatedAt     datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  CreatedBy     nvarchar(100) NULL,
  UpdatedAt     datetime2(0)  NULL
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Document_Module_Status')
  CREATE INDEX IX_Document_Module_Status ON ocr.Document(Module, Status, CreatedAt DESC);
GO

/* ---------------- TRANSACTION: รายการสินค้า ---------------- */
IF OBJECT_ID('ocr.DocumentLine') IS NULL
CREATE TABLE ocr.DocumentLine(
  LineId        int IDENTITY(1,1) PRIMARY KEY,
  DocId         int           NOT NULL,
  ItemNo        int           NOT NULL,
  ExtCode       nvarchar(60)  NULL,
  ExtDesc       nvarchar(300) NULL,
  Qty           decimal(18,3) NULL,
  Uom           nvarchar(10)  NULL,
  UnitPrice     decimal(18,4) NULL,
  Amount        decimal(18,2) NULL,
  MaterialCode  nvarchar(30)  NULL,
  MapStatus     nvarchar(10)  NULL,               -- ok | manual | fail
  MapMethod     nvarchar(100) NULL,
  CONSTRAINT FK_Line_Document FOREIGN KEY(DocId) REFERENCES ocr.Document(DocId) ON DELETE CASCADE,
  CONSTRAINT UQ_Line UNIQUE(DocId, ItemNo)
);
GO

/* ---------------- LOG: ส่งเข้า SAP ---------------- */
IF OBJECT_ID('ocr.PostLog') IS NULL
CREATE TABLE ocr.PostLog(
  LogId       int IDENTITY(1,1) PRIMARY KEY,
  DocId       int           NULL,
  Module      nvarchar(2)   NULL,
  SapDocNo    nvarchar(30)  NULL,
  Endpoint    nvarchar(200) NULL,
  PayloadJson nvarchar(max) NULL,
  Success     bit           NOT NULL DEFAULT 1,
  Message     nvarchar(max) NULL,
  PostedAt    datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  PostedBy    nvarchar(100) NULL
);
GO
