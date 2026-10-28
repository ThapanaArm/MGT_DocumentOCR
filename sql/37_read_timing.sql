/* 37_read_timing.sql
   Read time + read-to-post time on AP / II / PODP documents (Megachem, 28 Oct 2026).
   - OcrStartedAt      : when the document's FIRST read began (set at upload; a re-read keeps it).
                         Written on this SQL server's clock, the same clock PostedAt uses.
   - OcrSeconds        : computed — how long the read took, in seconds (OcrDurationMs / 1000).
                         A re-read overwrites OcrDurationMs, so this is the LATEST read.
                         A copied upload (OcrProvider 'copy', sql/36) shows the copy time.
   - ReadToPostMinutes : computed — minutes from OcrStartedAt to PostedAt. Filled the moment the
                         document is fully POSTED (Status PARTIAL has no PostedAt yet -> NULL).
                         Wall-clock time: includes waiting (e.g. overnight), not only work time.
   Older documents get an estimate: CreatedAt minus the read duration (CreatedAt is stamped right
   after the read).
   Sales Orders (ocr.SalesOrder) are not covered here — they have JobStartedAt / JobTaskTime
   (sql/34_job_tasktime.sql) on the develop branch.
   Safe to re-run. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF COL_LENGTH('ocr.Document', 'OcrStartedAt') IS NULL
    ALTER TABLE ocr.Document ADD OcrStartedAt datetime2(0) NULL;
GO
IF COL_LENGTH('ocr.Document', 'OcrSeconds') IS NULL
    ALTER TABLE ocr.Document ADD OcrSeconds AS (CAST(OcrDurationMs / 1000.0 AS decimal(10,1)));
IF COL_LENGTH('ocr.Document', 'ReadToPostMinutes') IS NULL
    ALTER TABLE ocr.Document ADD ReadToPostMinutes AS
        (CASE WHEN OcrStartedAt IS NOT NULL AND PostedAt >= OcrStartedAt
              THEN CAST(DATEDIFF(second, OcrStartedAt, PostedAt) / 60.0 AS decimal(10,2)) END);
GO
-- Estimate for documents uploaded before this script.
UPDATE ocr.Document
   SET OcrStartedAt = DATEADD(MILLISECOND, -ISNULL(OcrDurationMs, 0), CreatedAt)
 WHERE OcrStartedAt IS NULL AND CreatedAt IS NOT NULL;
GO

/* ---------- Checking (run by hand) ----------

-- Per document
SELECT TOP 50 DocId, Module, ApDocCategory, PartnerName, Status, OcrProvider,
       OcrStartedAt, OcrSeconds, PostedAt, ReadToPostMinutes
FROM ocr.Document
ORDER BY DocId DESC;

-- Averages per document type (posted documents only)
SELECT ApDocCategory, COUNT(*) AS Posted,
       AVG(OcrSeconds) AS AvgReadSec,
       AVG(ReadToPostMinutes) AS AvgReadToPostMin,
       MAX(ReadToPostMinutes) AS MaxReadToPostMin
FROM ocr.Document
WHERE ReadToPostMinutes IS NOT NULL
GROUP BY ApDocCategory
ORDER BY Posted DESC;
*/
