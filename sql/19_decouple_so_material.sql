/*
  Sales Order material mapping is owned by ocr.CustomerMaterial.
  ocr.Material remains the legacy/AP material master and must not be required
  before a CustomerMaterial or an SO-specific UoM rule can be saved.
*/

/* Remove CustomerMaterial -> legacy Material foreign keys, regardless of the
   constraint name retained after earlier column renames. */
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'ALTER TABLE ocr.CustomerMaterial DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = OBJECT_ID('ocr.CustomerMaterial')
  AND fk.referenced_object_id = OBJECT_ID('ocr.Material');
IF @sql <> N'' EXEC sp_executesql @sql;
GO

/* Normalize an installation created from the original schema to the current
   CustomerMaterial column contract. sp_rename preserves the existing data. */
IF COL_LENGTH('ocr.CustomerMaterial', 'SalesOrg') IS NULL
  ALTER TABLE ocr.CustomerMaterial ADD SalesOrg nvarchar(10) NULL;
GO
IF COL_LENGTH('ocr.CustomerMaterial', 'MaterialCodeCode') IS NULL
   AND COL_LENGTH('ocr.CustomerMaterial', 'ExtCode') IS NOT NULL
  EXEC sp_rename 'ocr.CustomerMaterial.ExtCode', 'MaterialCodeCode', 'COLUMN';
GO
IF COL_LENGTH('ocr.CustomerMaterial', 'MaterialCodeName') IS NULL
   AND COL_LENGTH('ocr.CustomerMaterial', 'ExtDesc') IS NOT NULL
  EXEC sp_rename 'ocr.CustomerMaterial.ExtDesc', 'MaterialCodeName', 'COLUMN';
GO
IF COL_LENGTH('ocr.CustomerMaterial', 'MaterialCodeSAP') IS NULL
   AND COL_LENGTH('ocr.CustomerMaterial', 'MaterialCode') IS NOT NULL
  EXEC sp_rename 'ocr.CustomerMaterial.MaterialCode', 'MaterialCodeSAP', 'COLUMN';
GO
IF COL_LENGTH('ocr.CustomerMaterial', 'Isactive') IS NULL
BEGIN
  ALTER TABLE ocr.CustomerMaterial ADD Isactive bit NULL;
  UPDATE ocr.CustomerMaterial SET Isactive=1 WHERE Isactive IS NULL;
END;
GO

/* Backfill the NULL SalesOrg values visible in the legacy data from the current
   customer account. Support both the current and the original Customer schema. */
IF COL_LENGTH('ocr.Customer', 'ComcompyCodeSAP') IS NOT NULL
  EXEC(N'
    UPDATE cm SET SalesOrg=c.SalesOrg, UpdatedAt=SYSDATETIME()
    FROM ocr.CustomerMaterial cm
    JOIN ocr.Customer c ON c.ComcompyCodeSAP=cm.CustomerCode
    WHERE cm.SalesOrg IS NULL AND c.SalesOrg IS NOT NULL;');
ELSE IF COL_LENGTH('ocr.Customer', 'CustomerCode') IS NOT NULL
  EXEC(N'
    UPDATE cm SET SalesOrg=c.SalesOrg, UpdatedAt=SYSDATETIME()
    FROM ocr.CustomerMaterial cm
    JOIN ocr.Customer c ON c.CustomerCode=cm.CustomerCode
    WHERE cm.SalesOrg IS NULL AND c.SalesOrg IS NOT NULL;');
GO

/* The old FK caused CustomerMaterial.MaterialCodeSAP to contain Material.MaterialCode
   (an internal code). Convert it to the real SAP code before the Material table is
   removed from the SO lookup path. Keep the internal code only when legacy data has
   no SapMaterialCode yet. */
IF COL_LENGTH('ocr.CustomerMaterial', 'MaterialCodeSAP') IS NOT NULL
BEGIN
  ALTER TABLE ocr.CustomerMaterial ALTER COLUMN MaterialCodeSAP nvarchar(40) NULL;
  UPDATE cm
     SET MaterialCodeSAP = COALESCE(NULLIF(LTRIM(RTRIM(m.SapMaterialCode)), ''), m.MaterialCode),
         UpdatedAt = SYSDATETIME()
  FROM ocr.CustomerMaterial cm
  JOIN ocr.Material m ON m.MaterialCode = cm.MaterialCodeSAP
  WHERE cm.MaterialCodeSAP <> COALESCE(NULLIF(LTRIM(RTRIM(m.SapMaterialCode)), ''), m.MaterialCode);
END;
GO

/* UoM rules use the target material code directly. Keep the established MaterialCode column so
   existing databases and the Master Mapping screen remain backward-compatible. */
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'ALTER TABLE ocr.UomConversion DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = OBJECT_ID('ocr.UomConversion')
  AND fk.referenced_object_id = OBJECT_ID('ocr.Material');
IF @sql <> N'' EXEC sp_executesql @sql;
GO

/* Existing product-specific UoM rules also used Material.MaterialCode. Convert them
   to the same real SAP code used by CustomerMaterial and document posting. */
IF COL_LENGTH('ocr.UomConversion', 'MaterialCode') IS NOT NULL
BEGIN
  ALTER TABLE ocr.UomConversion ALTER COLUMN MaterialCode nvarchar(40) NULL;
  UPDATE u
     SET MaterialCode = COALESCE(NULLIF(LTRIM(RTRIM(m.SapMaterialCode)), ''), m.MaterialCode),
         UpdatedAt = SYSDATETIME()
  FROM ocr.UomConversion u
  JOIN ocr.Material m ON m.MaterialCode = u.MaterialCode
  WHERE u.MaterialCode IS NOT NULL
    AND u.MaterialCode <> COALESCE(NULLIF(LTRIM(RTRIM(m.SapMaterialCode)), ''), m.MaterialCode);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('ocr.CustomerMaterial') AND name='IX_CustomerMaterial_Mapping')
  CREATE INDEX IX_CustomerMaterial_Mapping
    ON ocr.CustomerMaterial(SalesOrg, CustomerCode, MaterialCodeCode)
    INCLUDE(MaterialCodeSAP, MaterialCodeName, Isactive);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('ocr.UomConversion') AND name='IX_Uom_SalesOrg_Material')
  CREATE INDEX IX_Uom_SalesOrg_Material
    ON ocr.UomConversion(MaterialCode, ExtUom);
GO
