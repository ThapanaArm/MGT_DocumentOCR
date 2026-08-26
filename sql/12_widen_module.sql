-- Module เดิมกำหนดไว้แค่ nvarchar(2) (พอสำหรับ AP/SO/II) แต่ไม่พอสำหรับ PODP (Purchase Order Down Payments)
-- ขยายเป็น nvarchar(10) ให้รองรับโค้ด module ที่ยาวขึ้นในอนาคตด้วย
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA='ocr' AND TABLE_NAME='Document' AND COLUMN_NAME='Module' AND CHARACTER_MAXIMUM_LENGTH < 10)
    ALTER TABLE ocr.Document ALTER COLUMN Module nvarchar(10) NOT NULL;
