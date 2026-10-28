/* 38_read_hints.sql
   Measuring where the read time goes + TEST position hints (Megachem, 28 Oct 2026). AP / II only.
   - ocr.Document.OcrThinkingTokens : tokens Gemini spent "thinking" before answering, summed over
                                      every request of the read. Billed as output and part of the
                                      wait, but NOT included in OcrTokensOut.
   - ocr.Document.OcrHint           : 'positions' = the read was given an earlier document's positions
                                      (Ocr:PositionHints:Enabled = true); NULL = normal read.
   - ocr.FieldPositionMemory        : where each header value / the line table was found on a read,
                                      as reported by Gemini when position hints are switched on.
                                      The newest row of the same document type is given to the next read.
   The application keeps working before this script is run (it logs and skips). Safe to re-run. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF COL_LENGTH('ocr.Document', 'OcrThinkingTokens') IS NULL
    ALTER TABLE ocr.Document ADD OcrThinkingTokens int NULL;
IF COL_LENGTH('ocr.Document', 'OcrHint') IS NULL
    ALTER TABLE ocr.Document ADD OcrHint varchar(30) NULL;
GO
IF OBJECT_ID('ocr.FieldPositionMemory', 'U') IS NULL
CREATE TABLE ocr.FieldPositionMemory (
    Id            bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldPositionMemory PRIMARY KEY,
    DocId         int            NOT NULL,
    ApDocCategory varchar(40)    NULL,
    VendorTaxId   varchar(20)    NULL,
    PositionsJson nvarchar(max)  NOT NULL,   -- {"invoiceNo":{"image":1,"box":[ymin,xmin,ymax,xmax]}, ...} 0-1000
    CreatedAt     datetime2(0)   NOT NULL CONSTRAINT DF_FieldPositionMemory_Created DEFAULT SYSDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FieldPositionMemory_Category' AND object_id = OBJECT_ID('ocr.FieldPositionMemory'))
    CREATE INDEX IX_FieldPositionMemory_Category ON ocr.FieldPositionMemory(ApDocCategory, Id DESC);
GO

/* ---------- Checking (run by hand) ----------

-- 1. Where the time goes, per document: answer tokens vs thinking tokens vs seconds
SELECT TOP 30 DocId, ApDocCategory, PartnerName, OcrHint, OcrSeconds,
       OcrTokensIn, OcrTokensOut AS AnswerTokens, OcrThinkingTokens AS ThinkingTokens,
       CAST(100.0 * OcrThinkingTokens / NULLIF(OcrTokensOut + OcrThinkingTokens, 0) AS decimal(5,1)) AS ThinkingPctOfOutput
FROM ocr.Document
WHERE Module IN ('AP','II') AND OcrProvider = 'gemini' AND OcrThinkingTokens IS NOT NULL
ORDER BY DocId DESC;

-- 2. With vs without position hints (same document type, measured reads only)
SELECT ApDocCategory, ISNULL(OcrHint, 'none') AS Hint, COUNT(*) AS Docs,
       AVG(OcrSeconds) AS AvgSec, AVG(OcrThinkingTokens) AS AvgThinking, AVG(OcrTokensOut) AS AvgAnswer
FROM ocr.Document
WHERE Module IN ('AP','II') AND OcrProvider = 'gemini' AND OcrThinkingTokens IS NOT NULL
GROUP BY ApDocCategory, ISNULL(OcrHint, 'none')
ORDER BY ApDocCategory, Hint;

-- 3. Positions recorded
SELECT TOP 20 Id, DocId, ApDocCategory, VendorTaxId, LEFT(PositionsJson, 200) AS Positions, CreatedAt
FROM ocr.FieldPositionMemory ORDER BY Id DESC;
*/
