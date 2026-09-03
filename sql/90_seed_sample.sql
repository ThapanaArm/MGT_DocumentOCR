-- =====================================================================
--  ข้อมูลตัวอย่าง (Master demo) — idempotent รันซ้ำได้
--  แปลงมาจาก app/tools/seed.py + seed_uom.py + seed_sapcode.py
--  (แทนที่ dependency กับ Python — init_db.bat เรียกผ่าน sqlcmd แทน)
--  ต้องรันหลัง 01_schema / 02_uom / 03_sapcode (มีคอลัมน์ Sap* ครบแล้ว)
-- =====================================================================
SET NOCOUNT ON;

/* ---------- Customer ---------- */
IF NOT EXISTS(SELECT 1 FROM ocr.Customer WHERE CustomerCode='0010001')
  INSERT ocr.Customer(CustomerCode,NameTh,NameEn,TaxId,Branch,SalesOrg,DistChannel,Division,Currency,PaymentTerms)
  VALUES('0010001',N'บริษัท สยาม เคมิคอล อินดัสทรี จำกัด',N'Siam Chemical Industry Co., Ltd.','0105533012345','00000','1000','10','00','THB','N030');
IF NOT EXISTS(SELECT 1 FROM ocr.Customer WHERE CustomerCode='0010002')
  INSERT ocr.Customer(CustomerCode,NameTh,NameEn,TaxId,Branch,SalesOrg,DistChannel,Division,Currency,PaymentTerms)
  VALUES('0010002',N'บริษัท ไทย โพลีเมอร์ กรุ๊ป จำกัด (มหาชน)',N'Thai Polymer Group PCL.','0107536000123','00000','1000','10','00','THB','N060');
IF NOT EXISTS(SELECT 1 FROM ocr.Customer WHERE CustomerCode='0010003')
  INSERT ocr.Customer(CustomerCode,NameTh,NameEn,TaxId,Branch,SalesOrg,DistChannel,Division,Currency,PaymentTerms)
  VALUES('0010003',N'บริษัท เอเชีย โคทติ้ง แอนด์ พลาสติก จำกัด',N'Asia Coating & Plastic Co., Ltd.','0125548009876','00000','1000','10','00','THB','N030');

/* ---------- Material ---------- */
IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode='FG-100021')
  INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES('FG-100021',N'Titanium Dioxide R-902 (25 KG/BAG)','KG','1000','PIG01');
IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode='FG-100045')
  INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES('FG-100045',N'Calcium Carbonate CC-800 (25 KG/BAG)','KG','1000','FIL01');
IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode='FG-100078')
  INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES('FG-100078',N'Epoxy Resin EP-828 (200 KG/DRUM)','KG','1000','RES01');
IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode='RM-200011')
  INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES('RM-200011',N'Methyl Ethyl Ketone (MEK) 99.5%','L','1000','SOL01');
IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode='RM-200034')
  INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES('RM-200034',N'Toluene Industrial Grade','L','1000','SOL01');
IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode='RM-200099')
  INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES('RM-200099',N'Polypropylene Homopolymer PP-1100N','KG','1000','PLA01');
IF NOT EXISTS(SELECT 1 FROM ocr.Material WHERE MaterialCode='SV-900001')
  INSERT ocr.Material(MaterialCode,Description,Uom,Plant,MatGroup) VALUES('SV-900001',N'ค่าขนส่งสินค้า / Freight Charge','AU','1000','SRV01');

/* ---------- Ship-to ---------- */
IF NOT EXISTS(SELECT 1 FROM ocr.ShipTo WHERE ShipToCode='0010001-01')
  INSERT ocr.ShipTo(ShipToCode,CustomerCode,ShipToName,Address) VALUES('0010001-01','0010001',N'คลังสินค้า บางปู',N'นิคมอุตสาหกรรมบางปู ซ.7 ต.แพรกษา อ.เมือง สมุทรปราการ 10280');
IF NOT EXISTS(SELECT 1 FROM ocr.ShipTo WHERE ShipToCode='0010001-02')
  INSERT ocr.ShipTo(ShipToCode,CustomerCode,ShipToName,Address) VALUES('0010001-02','0010001',N'โรงงาน ระยอง',N'นิคมอุตสาหกรรมมาบตาพุด ต.มาบตาพุด อ.เมือง ระยอง 21150');
IF NOT EXISTS(SELECT 1 FROM ocr.ShipTo WHERE ShipToCode='0010002-01')
  INSERT ocr.ShipTo(ShipToCode,CustomerCode,ShipToName,Address) VALUES('0010002-01','0010002',N'โรงงานอยุธยา (โรจนะ)',N'สวนอุตสาหกรรมโรจนะ ต.คานหาม อ.อุทัย พระนครศรีอยุธยา 13210');
IF NOT EXISTS(SELECT 1 FROM ocr.ShipTo WHERE ShipToCode='0010003-01')
  INSERT ocr.ShipTo(ShipToCode,CustomerCode,ShipToName,Address) VALUES('0010003-01','0010003',N'สำนักงานใหญ่ / คลังบางนา',N'กม.19 ถ.บางนา-ตราด ต.บางโฉลง อ.บางพลี สมุทรปราการ 10540');

/* ---------- Customer material ---------- */
IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode='0010001' AND ExtCode='SCI-TIO2-902')
  INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES('0010001','SCI-TIO2-902',N'TIO2 R902 ถุง 25 กก.','FG-100021');
IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode='0010001' AND ExtCode='SCI-CACO3-800')
  INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES('0010001','SCI-CACO3-800',N'แคลเซียมคาร์บอเนต CC800','FG-100045');
IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode='0010002' AND ExtCode='TPG-PP1100')
  INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES('0010002','TPG-PP1100',N'PP HOMO 1100N','RM-200099');
IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode='0010002' AND ExtCode='TPG-MEK')
  INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES('0010002','TPG-MEK',N'MEK 99.5%','RM-200011');
IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode='0010003' AND ExtCode='ACP-EP828')
  INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES('0010003','ACP-EP828',N'อีพ็อกซี่เรซิน EP-828','FG-100078');

/* ---------- Vendor ---------- */
IF NOT EXISTS(SELECT 1 FROM ocr.Vendor WHERE VendorCode='V-500012')
  INSERT ocr.Vendor(VendorCode,VendorName,TaxId,Branch,Currency,PaymentTerms,ReconAcct,WhtCode)
  VALUES('V-500012',N'บริษัท ยูนิเวอร์แซล เคมีคอล ซัพพลาย จำกัด','0105546007788','00000','THB','N030','2110100','-');
IF NOT EXISTS(SELECT 1 FROM ocr.Vendor WHERE VendorCode='V-500034')
  INSERT ocr.Vendor(VendorCode,VendorName,TaxId,Branch,Currency,PaymentTerms,ReconAcct,WhtCode)
  VALUES('V-500034',N'บริษัท เอ็น.ซี. โลจิสติกส์ เซอร์วิส จำกัด','0115551002233','00000','THB','N015','2110100',N'53 (3%)');
IF NOT EXISTS(SELECT 1 FROM ocr.Vendor WHERE VendorCode='V-500051')
  INSERT ocr.Vendor(VendorCode,VendorName,TaxId,Branch,Currency,PaymentTerms,ReconAcct,WhtCode)
  VALUES('V-500051',N'บริษัท พีทีจี เพโทรเคมิคอล จำกัด (มหาชน)','0107545000456','00001','THB','N045','2110100','-');

/* ---------- Vendor material ---------- */
IF NOT EXISTS(SELECT 1 FROM ocr.VendorMaterial WHERE VendorCode='V-500012' AND ExtCode='UC-TL-100')
  INSERT ocr.VendorMaterial(VendorCode,ExtCode,ExtDesc,MaterialCode) VALUES('V-500012','UC-TL-100',N'TOLUENE INDUSTRIAL','RM-200034');
IF NOT EXISTS(SELECT 1 FROM ocr.VendorMaterial WHERE VendorCode='V-500012' AND ExtCode='UC-MEK-995')
  INSERT ocr.VendorMaterial(VendorCode,ExtCode,ExtDesc,MaterialCode) VALUES('V-500012','UC-MEK-995',N'MEK 99.5 PCT','RM-200011');
IF NOT EXISTS(SELECT 1 FROM ocr.VendorMaterial WHERE VendorCode='V-500034' AND ExtCode='NC-FREIGHT')
  INSERT ocr.VendorMaterial(VendorCode,ExtCode,ExtDesc,MaterialCode) VALUES('V-500034','NC-FREIGHT',N'ค่าขนส่ง','SV-900001');
IF NOT EXISTS(SELECT 1 FROM ocr.VendorMaterial WHERE VendorCode='V-500051' AND ExtCode='PTG-PP-1100N')
  INSERT ocr.VendorMaterial(VendorCode,ExtCode,ExtDesc,MaterialCode) VALUES('V-500051','PTG-PP-1100N',N'PP HOMOPOLYMER 1100N','RM-200099');

/* ---------- UoM conversion rules ---------- */
-- กฎกลาง (MaterialCode = NULL)
MERGE ocr.UomConversion AS t
USING (VALUES
  (N'กก.','KG',1),(N'กิโลกรัม','KG',1),(N'ตัน','KG',1000),('TON','KG',1000),('MT','KG',1000),
  (N'ลิตร','L',1),('LTR','L',1),(N'ชิ้น','EA',1),('PCS','EA',1),('PC','EA',1),(N'งาน','AU',1)
) AS s(ExtUom,SapUom,Factor)
ON (t.MaterialCode IS NULL AND t.ExtUom = s.ExtUom)
WHEN NOT MATCHED THEN
  INSERT(MaterialCode,ExtUom,SapUom,Factor,Note) VALUES(NULL,s.ExtUom,s.SapUom,s.Factor,N'กฎกลาง');

-- กฎเฉพาะสินค้า (บรรจุภัณฑ์)
MERGE ocr.UomConversion AS t
USING (VALUES
  ('FG-100021','BAG','KG',25),('FG-100021',N'ถุง','KG',25),('FG-100021','PALLET','KG',1000),
  ('FG-100045','BAG','KG',25),('FG-100045',N'ถุง','KG',25),
  ('FG-100078','DRUM','KG',200),('FG-100078',N'ถัง','KG',200),
  ('RM-200011','DRUM','L',200),('RM-200034','DRUM','L',200),('RM-200099','BAG','KG',25)
) AS s(MaterialCode,ExtUom,SapUom,Factor)
ON (t.MaterialCode = s.MaterialCode AND t.ExtUom = s.ExtUom)
WHEN NOT MATCHED THEN
  INSERT(MaterialCode,ExtUom,SapUom,Factor,Note) VALUES(s.MaterialCode,s.ExtUom,s.SapUom,s.Factor,N'บรรจุภัณฑ์');

/* ---------- SAP codes (เติมเฉพาะแถวที่ยังว่าง) ---------- */
UPDATE ocr.Customer SET SapCustomerCode='0000100023' WHERE CustomerCode='0010001' AND (SapCustomerCode IS NULL OR SapCustomerCode='');
UPDATE ocr.Customer SET SapCustomerCode='0000100047' WHERE CustomerCode='0010002' AND (SapCustomerCode IS NULL OR SapCustomerCode='');
UPDATE ocr.Customer SET SapCustomerCode='0000100112' WHERE CustomerCode='0010003' AND (SapCustomerCode IS NULL OR SapCustomerCode='');

UPDATE ocr.ShipTo SET SapShipToCode='0000100024' WHERE ShipToCode='0010001-01' AND (SapShipToCode IS NULL OR SapShipToCode='');
UPDATE ocr.ShipTo SET SapShipToCode='0000100025' WHERE ShipToCode='0010001-02' AND (SapShipToCode IS NULL OR SapShipToCode='');
UPDATE ocr.ShipTo SET SapShipToCode='0000100048' WHERE ShipToCode='0010002-01' AND (SapShipToCode IS NULL OR SapShipToCode='');
UPDATE ocr.ShipTo SET SapShipToCode='0000100112' WHERE ShipToCode='0010003-01' AND (SapShipToCode IS NULL OR SapShipToCode='');

UPDATE ocr.Vendor SET SapVendorCode='0000200015' WHERE VendorCode='V-500012' AND (SapVendorCode IS NULL OR SapVendorCode='');
UPDATE ocr.Vendor SET SapVendorCode='0000200037' WHERE VendorCode='V-500034' AND (SapVendorCode IS NULL OR SapVendorCode='');
UPDATE ocr.Vendor SET SapVendorCode='0000200054' WHERE VendorCode='V-500051' AND (SapVendorCode IS NULL OR SapVendorCode='');

-- Material: เลข SAP 18 หลัก (เติมศูนย์หน้าจากตัวเลขในรหัส)
UPDATE ocr.Material SET SapMaterialCode='000000000000100021' WHERE MaterialCode='FG-100021' AND (SapMaterialCode IS NULL OR SapMaterialCode='');
UPDATE ocr.Material SET SapMaterialCode='000000000000100045' WHERE MaterialCode='FG-100045' AND (SapMaterialCode IS NULL OR SapMaterialCode='');
UPDATE ocr.Material SET SapMaterialCode='000000000000100078' WHERE MaterialCode='FG-100078' AND (SapMaterialCode IS NULL OR SapMaterialCode='');
UPDATE ocr.Material SET SapMaterialCode='000000000000200011' WHERE MaterialCode='RM-200011' AND (SapMaterialCode IS NULL OR SapMaterialCode='');
UPDATE ocr.Material SET SapMaterialCode='000000000000200034' WHERE MaterialCode='RM-200034' AND (SapMaterialCode IS NULL OR SapMaterialCode='');
UPDATE ocr.Material SET SapMaterialCode='000000000000200099' WHERE MaterialCode='RM-200099' AND (SapMaterialCode IS NULL OR SapMaterialCode='');
UPDATE ocr.Material SET SapMaterialCode='000000000000900001' WHERE MaterialCode='SV-900001' AND (SapMaterialCode IS NULL OR SapMaterialCode='');

-- UoM ISO code (จาก SapUom)
UPDATE ocr.UomConversion SET SapUomIso='KGM' WHERE (SapUomIso IS NULL OR SapUomIso='') AND UPPER(SapUom)='KG';
UPDATE ocr.UomConversion SET SapUomIso='LTR' WHERE (SapUomIso IS NULL OR SapUomIso='') AND UPPER(SapUom)='L';
UPDATE ocr.UomConversion SET SapUomIso='PCE' WHERE (SapUomIso IS NULL OR SapUomIso='') AND UPPER(SapUom)='EA';
UPDATE ocr.UomConversion SET SapUomIso='ACT' WHERE (SapUomIso IS NULL OR SapUomIso='') AND UPPER(SapUom)='AU';

PRINT 'seed sample data done';
