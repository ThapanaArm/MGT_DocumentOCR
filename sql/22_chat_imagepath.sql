/* =====================================================================
   ซ่อมโครงสร้างตารางประวัติแชท (ocr.DocumentChat / ocr.SalesOrderChat)

   อาการ: SqlException "Invalid column name 'ImagePath'"

   สาเหตุ: ตารางในฐานจริงถูกสร้างไว้ตั้งแต่ระบบรุ่นก่อน (Python) ซึ่งเก็บ
   รูปเป็น binary ในฐานข้อมูล -> มีคอลัมน์ ImageData / ImageMime
   ส่วนโค้ด .NET รุ่นนี้เปลี่ยนไปเก็บไฟล์รูปไว้บนดิสก์แล้วบันทึกแค่ path
   -> ใช้คอลัมน์ ImagePath ซึ่งไม่เคยถูกสร้างในฐานจริง

   และที่รัน init_db.bat ซ้ำแล้วไม่หาย เพราะ 05_chat.sql / 14_sales_order_tables.sql
   ครอบด้วย IF OBJECT_ID(...) IS NULL — ตารางมีอยู่แล้วจึงถูกข้ามทั้งก้อน
   ไม่มีใครไปเติมคอลัมน์ให้

   สคริปต์นี้ทำ 2 อย่าง (รันซ้ำได้ ไม่พัง ไม่ลบข้อมูลเดิม):
     1) เพิ่ม ImagePath (และ CreatedBy / CreatedAt ถ้าขาด)
     2) ปลด NOT NULL ของ ImageData / ImageMime ที่เหลือจากรุ่นเก่า
        เพราะคำสั่ง INSERT ของโค้ดใหม่ไม่ได้ส่งค่าสองคอลัมน์นี้
        ถ้ายังบังคับ NOT NULL อยู่ การบันทึกแชทจะ error ต่อทันที

   หมายเหตุ: รูปของแชทเก่าที่เก็บไว้ใน ImageData จะไม่แสดงในระบบใหม่
   (ข้อความยังอยู่ครบ) ถ้าต้องการกู้รูปเก่าค่อยทำสคริปต์ดึงออกเป็นไฟล์ทีหลัง
   ===================================================================== */

DECLARE @t sysname, @sql nvarchar(max);
DECLARE tabs CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM (VALUES ('ocr.DocumentChat'), ('ocr.SalesOrderChat')) AS x(name);

OPEN tabs;
FETCH NEXT FROM tabs INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(@t) IS NOT NULL
    BEGIN
        IF COL_LENGTH(@t, 'ImagePath') IS NULL
        BEGIN
            SET @sql = N'ALTER TABLE ' + @t + N' ADD ImagePath nvarchar(400) NULL;';
            EXEC sp_executesql @sql;
        END

        IF COL_LENGTH(@t, 'CreatedBy') IS NULL
        BEGIN
            SET @sql = N'ALTER TABLE ' + @t + N' ADD CreatedBy nvarchar(100) NULL;';
            EXEC sp_executesql @sql;
        END

        IF COL_LENGTH(@t, 'CreatedAt') IS NULL
        BEGIN
            SET @sql = N'ALTER TABLE ' + @t + N' ADD CreatedAt datetime2(0) NOT NULL DEFAULT SYSDATETIME();';
            EXEC sp_executesql @sql;
        END

        -- คอลัมน์ตกค้างจากรุ่นที่เก็บรูปในฐานข้อมูล: ต้องยอมให้เป็น NULL ได้
        IF EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(@t) AND name = 'ImageData' AND is_nullable = 0)
        BEGIN
            SET @sql = N'ALTER TABLE ' + @t + N' ALTER COLUMN ImageData varbinary(max) NULL;';
            EXEC sp_executesql @sql;
        END

        IF EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(@t) AND name = 'ImageMime' AND is_nullable = 0)
        BEGIN
            SET @sql = N'ALTER TABLE ' + @t + N' ALTER COLUMN ImageMime nvarchar(100) NULL;';
            EXEC sp_executesql @sql;
        END
    END

    FETCH NEXT FROM tabs INTO @t;
END
CLOSE tabs;
DEALLOCATE tabs;
GO

-- ตรวจผลหลังรัน: ต้องเห็น ImagePath และ ImageData/ImageMime ต้อง IS_NULLABLE = YES
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'ocr' AND TABLE_NAME IN ('DocumentChat', 'SalesOrderChat')
ORDER BY TABLE_NAME, ORDINAL_POSITION;
GO
