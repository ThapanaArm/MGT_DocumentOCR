/* =====================================================================
   25 — ย้ายรูปแนบในแชท AI จากไฟล์บนดิสก์ มาเก็บใน DB
   - เดิม: ImagePath nvarchar(400) ชี้ไปไฟล์ใน uploads\chat\{DocId}\ (มีแค่บนเครื่องที่บันทึก
     เครื่องอื่นที่ใช้ DB เดียวกันเปิดรูปไม่ได้)
   - ใหม่: ImageData varbinary(max) + ImageMime nvarchar(50) เก็บตัวรูปในแถวแชทเลย
   - ไม่ย้ายรูปเก่า (เป็นรูปทดสอบ) — ข้อความแชทเดิมยังอยู่ แค่รูปเก่าจะไม่แสดง
   (supersedes sql/22_chat_imagepath.sql from 0cd227c, which re-added ImagePath)
   ทำกับทั้ง 2 ตารางแชท: ocr.DocumentChat (AP/II/PODP) และ ocr.SalesOrderChat (SO)
   รันซ้ำได้ (idempotent) — ต้อง deploy backend เวอร์ชันใหม่พร้อมกัน
   ===================================================================== */
IF COL_LENGTH('ocr.DocumentChat','ImageData') IS NULL
  ALTER TABLE ocr.DocumentChat ADD ImageData varbinary(max) NULL, ImageMime nvarchar(50) NULL;
GO
IF COL_LENGTH('ocr.SalesOrderChat','ImageData') IS NULL
  ALTER TABLE ocr.SalesOrderChat ADD ImageData varbinary(max) NULL, ImageMime nvarchar(50) NULL;
GO
-- Databases created by the older (Python) version already HAVE ImageData/ImageMime, possibly as
-- NOT NULL — text-only chat rows insert NULL there, so make both nullable.
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('ocr.DocumentChat') AND name='ImageData' AND is_nullable=0)
  ALTER TABLE ocr.DocumentChat ALTER COLUMN ImageData varbinary(max) NULL;
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('ocr.DocumentChat') AND name='ImageMime' AND is_nullable=0)
  ALTER TABLE ocr.DocumentChat ALTER COLUMN ImageMime nvarchar(100) NULL;
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('ocr.SalesOrderChat') AND name='ImageData' AND is_nullable=0)
  ALTER TABLE ocr.SalesOrderChat ALTER COLUMN ImageData varbinary(max) NULL;
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('ocr.SalesOrderChat') AND name='ImageMime' AND is_nullable=0)
  ALTER TABLE ocr.SalesOrderChat ALTER COLUMN ImageMime nvarchar(100) NULL;
GO
IF COL_LENGTH('ocr.DocumentChat','ImagePath') IS NOT NULL
  ALTER TABLE ocr.DocumentChat DROP COLUMN ImagePath;
GO
IF COL_LENGTH('ocr.SalesOrderChat','ImagePath') IS NOT NULL
  ALTER TABLE ocr.SalesOrderChat DROP COLUMN ImagePath;
GO
