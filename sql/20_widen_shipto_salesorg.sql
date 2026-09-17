/* ocr.ShipTo.SalesOrg was created narrower than the other master tables' SalesOrg
   (ocr.Customer / ocr.CustomerMaterial both use nvarchar(10)). A 4-char SalesOrg
   such as "2000" (GLC) was therefore truncated on save
   ("String or binary data would be truncated ... column 'SalesOrg'").
   Add it if missing, otherwise widen it to match the other master tables. */
IF OBJECT_ID('ocr.ShipTo', 'U') IS NOT NULL
BEGIN
  IF COL_LENGTH('ocr.ShipTo', 'SalesOrg') IS NULL
    ALTER TABLE ocr.ShipTo ADD SalesOrg nvarchar(10) NULL;
  ELSE
    ALTER TABLE ocr.ShipTo ALTER COLUMN SalesOrg nvarchar(10) NULL;
END;
GO
