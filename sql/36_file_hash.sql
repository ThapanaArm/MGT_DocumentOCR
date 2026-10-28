/* 36_file_hash.sql
   Same file uploaded again (Megachem, 28 Oct 2026) - AP / II documents.
   - FileHash         : SHA-256 (hex) of the file as uploaded, stamped on every new AP/II document.
   - DuplicateOfDocId : the earlier document with the same file - the one copied from, or the newest
                        one when the upload was read again (NULL = first time this file was seen).
   On upload, a file whose hash matches an earlier AP/II document of the same company:
   - is COPIED from the newest earlier document that was checked (MAPPED / POSTED / PARTIAL), of the
     same document type and not a split part - header, lines and line mapping, with every correction
     made there - and is not sent to Gemini (OcrProvider = 'copy');
   - otherwise is read as usual, with "Same file as document #..." in the confidence note;
   - and in both cases gets a warning when the earlier one was already posted (or split), so the
     same liability is not recorded twice.
   Documents uploaded before this script have no hash; when a new upload has the same size as one of
   them, that older file is hashed on the spot (files compressed since then no longer match).
   Safe to re-run. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;   -- filtered index below needs it (sqlcmd defaults it OFF)
GO
IF COL_LENGTH('ocr.Document', 'FileHash') IS NULL
    ALTER TABLE ocr.Document ADD FileHash char(64) NULL;
IF COL_LENGTH('ocr.Document', 'DuplicateOfDocId') IS NULL
    ALTER TABLE ocr.Document ADD DuplicateOfDocId int NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Document_FileHash' AND object_id = OBJECT_ID('ocr.Document'))
    CREATE INDEX IX_Document_FileHash ON ocr.Document(FileHash)
        INCLUDE (Module, Status, ApDocCategory, SalesOrg, SourceDocId)
        WHERE FileHash IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Document_FileSize_NoHash' AND object_id = OBJECT_ID('ocr.Document'))
    CREATE INDEX IX_Document_FileSize_NoHash ON ocr.Document(FileSize)
        WHERE FileHash IS NULL;
GO

/* ---------- Checking (run by hand) ----------

-- Uploads recognised as an earlier file: copied (provider 'copy') or read again
SELECT d.DocId, d.DuplicateOfDocId, d.OcrProvider, d.Status, d.ApDocCategory, d.PartnerName,
       d.OcrDurationMs / 1000.0 AS Sec, d.CreatedAt, d.OcrConfidenceNote
FROM ocr.Document d
WHERE d.DuplicateOfDocId IS NOT NULL
ORDER BY d.DocId DESC;

-- Files uploaded more than once
SELECT FileHash, COUNT(*) AS Uploads, MIN(DocId) AS FirstDoc, MAX(DocId) AS LastDoc,
       SUM(CASE WHEN Status IN ('POSTED','PARTIAL') THEN 1 ELSE 0 END) AS Posted
FROM ocr.Document
WHERE FileHash IS NOT NULL
GROUP BY FileHash HAVING COUNT(*) > 1
ORDER BY Uploads DESC;
*/
