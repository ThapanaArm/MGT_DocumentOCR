/* =====================================================================
   26 — Single active device per user, PER SYSTEM (central table)
   MGT_Datawarehouse.dbo.Ms_UserSession: one row per (user, system) = the ONE browser/device
   currently allowed to use that system.
   - Rule for every system: 1 device per user per system; signing in on a new device kicks the old
     one ("newest device wins"). Different systems don't kick each other (OCR + Dashboard can be
     open together on one PC).
   - OCR writes AppName = 'OCR'. Other systems can adopt the same table with their own AppName
     (Dashboard currently still uses Ms_User.TokenVersion).
   - SessionId: random id the browser keeps in localStorage → every tab of that browser shares it.
   - Kept OUT of Ms_User on purpose (shared by other systems; no schema change there).
   Run against the SQL Server that hosts MGT_Datawarehouse (same server as MGT_Document_OCR).
   Idempotent.
   ===================================================================== */
IF OBJECT_ID('MGT_Datawarehouse.dbo.Ms_UserSession','U') IS NULL
CREATE TABLE MGT_Datawarehouse.dbo.Ms_UserSession(
  UserID     int           NOT NULL,              -- Ms_User.UserID
  AppName    nvarchar(30)  NOT NULL,              -- 'OCR', later 'DASHBOARD', ...
  SessionId  nvarchar(64)  NOT NULL,
  UserAgent  nvarchar(400) NULL,
  IpAddress  nvarchar(64)  NULL,
  ClaimedAt  datetime2(0)  NOT NULL CONSTRAINT DF_Ms_UserSession_Claimed DEFAULT SYSDATETIME(),
  CONSTRAINT PK_Ms_UserSession PRIMARY KEY (UserID, AppName)
);
GO
