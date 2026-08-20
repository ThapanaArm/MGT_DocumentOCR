/* =====================================================================
   ประวัติแชทสั่งแก้ไขข้อมูล (AI) — เก็บถาวรต่อเอกสาร เพื่อให้เห็นย้อนหลังได้แม้ปิดหน้าเว็บ/รีเฟรช
   และส่งกลับไปเป็นบริบทให้ Claude ตอบแบบถามตอบต่อเนื่องได้ (ไม่ใช่แค่คำสั่งเดี่ยว ๆ ทีละครั้ง)
   ===================================================================== */
IF OBJECT_ID('ocr.DocumentChat') IS NULL
CREATE TABLE ocr.DocumentChat(
  ChatId      int IDENTITY(1,1) PRIMARY KEY,
  DocId       int           NOT NULL,
  Role        nvarchar(10)  NOT NULL,          -- user | assistant
  MessageText nvarchar(max) NULL,
  ImagePath   nvarchar(400) NULL,              -- path ไฟล์ภาพที่แนบ (ถ้ามี) เก็บบนดิสก์ ไม่เก็บ base64 ใน DB
  CreatedAt   datetime2(0)  NOT NULL DEFAULT SYSDATETIME(),
  CreatedBy   nvarchar(100) NULL,
  CONSTRAINT FK_Chat_Document FOREIGN KEY(DocId) REFERENCES ocr.Document(DocId) ON DELETE CASCADE
);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Chat_Doc')
  CREATE INDEX IX_Chat_Doc ON ocr.DocumentChat(DocId, ChatId);
GO
