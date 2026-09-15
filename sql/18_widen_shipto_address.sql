/* Ship-to addresses regularly exceed the original short column size. */
IF OBJECT_ID('ocr.ShipTo', 'U') IS NOT NULL
   AND COL_LENGTH('ocr.ShipTo', 'ShipToAddress') IS NOT NULL
BEGIN
  ALTER TABLE ocr.ShipTo ALTER COLUMN ShipToAddress nvarchar(1000) NULL;
END;
GO

