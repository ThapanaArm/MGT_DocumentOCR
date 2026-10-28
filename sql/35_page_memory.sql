/* 35_page_memory.sql
   Page memory for AP / II bundles (Megachem, 28 Oct 2026).
   One row per page of a read: what the page is (FORM_SHIPPING_EXPENSE, PURCHASE_ORDER, TAX_INVOICE,
   INVOICE, RECEIPT, CUSTOMS, WHT_CERTIFICATE, DELIVERY_ORDER, PACKING_LIST, BILL_OF_LADING,
   CERTIFICATE, CONTINUATION, BLANK, OTHER, UNCLASSIFIED), how sure, and whether it was read.
   Anchor pages (FORM SHIPPING EXPENSE / PURCHASE ORDER) also keep a small JPEG of the top of the
   page. When the document is POSTED, that picture becomes a reference the page classifier compares
   new bundles against ("this is what the anchor page looks like").
   - Method      : TEXT = found through the PDF text layer (certain); AI = page classifier.
   - AiRole      : the classifier's own answer, kept even when the text layer overrides PageRole —
                   compare the two to see how often the classifier is right.
   - KeepForRead : 0 = the classifier is sure the page carries no amounts (packing list, B/L,
                   certificate, blank) and mode "on" may leave it out.
   - UsedForRead : the page was actually sent to the extraction read.
   - Mode        : shadow (recorded only) / on (used to plan the read) / backfill (old document).
                   See Ocr:PageMemory:Mode.
   Backfill (Ocr:PageMemory:Backfill=true) also fills this table for documents read before it existed;
   a document it could record nothing for gets one placeholder row with PageNo 0 and PageRole
   NO_FILE / NO_ANCHOR / FAILED (Method NONE) so it is not looked at again — the queries below skip
   PageNo 0. Rows are replaced on every re-read of the document. No foreign key on purpose: deleting a document
   must never be blocked by this table (orphan rows are harmless; the reference query joins).
   Safe to re-run. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;   -- filtered index below needs it (sqlcmd defaults it OFF)
GO
IF OBJECT_ID('ocr.DocPageMap', 'U') IS NULL
CREATE TABLE ocr.DocPageMap (
    Id           bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DocPageMap PRIMARY KEY,
    DocId        int            NOT NULL,
    PageNo       int            NOT NULL,          -- 1-based page of the file
    PageRole     varchar(40)    NOT NULL,
    Confidence   decimal(4,3)   NULL,
    Method       varchar(10)    NOT NULL,          -- TEXT / AI
    AiRole       varchar(40)    NULL,              -- classifier's answer (NULL = classifier did not run)
    AiConfidence decimal(4,3)   NULL,
    KeepForRead  bit            NOT NULL CONSTRAINT DF_DocPageMap_Keep DEFAULT 1,
    UsedForRead  bit            NOT NULL CONSTRAINT DF_DocPageMap_Used DEFAULT 1,
    HeaderJpeg   varbinary(max) NULL,              -- anchor pages only (~10-30 KB)
    Mode         varchar(10)    NOT NULL,          -- shadow / on / backfill
    CreatedAt    datetime2(0)   NOT NULL CONSTRAINT DF_DocPageMap_Created DEFAULT SYSDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DocPageMap_Doc' AND object_id = OBJECT_ID('ocr.DocPageMap'))
    CREATE INDEX IX_DocPageMap_Doc ON ocr.DocPageMap(DocId, PageNo);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DocPageMap_Reference' AND object_id = OBJECT_ID('ocr.DocPageMap'))
    CREATE INDEX IX_DocPageMap_Reference ON ocr.DocPageMap(PageRole, DocId) INCLUDE (Method, Mode, UsedForRead)
        WHERE HeaderJpeg IS NOT NULL;
GO

/* ---------- Checking the results (run by hand; nothing below changes data) ----------

-- 1. Page map of one document
SELECT m.PageNo, m.PageRole, m.Confidence, m.Method, m.KeepForRead, m.UsedForRead,
       CASE WHEN m.HeaderJpeg IS NULL THEN '' ELSE 'picture' END AS Ref, m.Mode
FROM ocr.DocPageMap m WHERE m.DocId = 823 ORDER BY m.PageNo;

-- 2. Per document: pages, anchor found?, pages "on" mode would leave out, read time and tokens
SELECT d.DocId, d.ApDocCategory, d.PartnerName, d.Status,
       COUNT(*)                                                         AS Pages,
       SUM(CASE WHEN m.PageRole IN ('FORM_SHIPPING_EXPENSE','PURCHASE_ORDER') THEN 1 ELSE 0 END) AS AnchorPages,
       MAX(CASE WHEN m.Method = 'TEXT' THEN 1 ELSE 0 END)              AS AnchorByTextLayer,
       SUM(CASE WHEN m.KeepForRead = 0 THEN 1 ELSE 0 END)              AS PagesWithoutAmounts,
       d.OcrTokensIn, d.OcrDurationMs / 1000 AS OcrSec, MAX(m.Mode) AS Mode
FROM ocr.DocPageMap m JOIN ocr.Document d ON d.DocId = m.DocId
WHERE m.PageNo > 0
GROUP BY d.DocId, d.ApDocCategory, d.PartnerName, d.Status, d.OcrTokensIn, d.OcrDurationMs
ORDER BY d.DocId DESC;

-- 3. Classifier accuracy on anchor pages: where the text layer KNOWS the anchor page (Method TEXT),
--    what did the classifier say about that same page? (needs PDFs with a text layer)
SELECT m.PageRole AS TextLayerSays, ISNULL(m.AiRole, '(not classified)') AS ClassifierSaid, COUNT(*) AS Pages
FROM ocr.DocPageMap m
WHERE m.Method = 'TEXT'
GROUP BY m.PageRole, ISNULL(m.AiRole, '(not classified)')
ORDER BY Pages DESC;

-- 3b. Pages the classifier called an anchor but the text layer did not (wrong pick, or a scan)
SELECT m.DocId, m.PageNo, m.AiRole, m.AiConfidence
FROM ocr.DocPageMap m
WHERE m.Method = 'AI' AND m.AiRole IN ('FORM_SHIPPING_EXPENSE','PURCHASE_ORDER')
ORDER BY m.DocId DESC, m.PageNo;

-- 4. Reference pictures page memory will use next (newest first, 2 per role are sent):
--    text-layer anchors at once; AI picks only after a post in mode "on"
SELECT m.PageRole, m.DocId, d.PartnerName, d.Status, m.Method, m.Mode, DATALENGTH(m.HeaderJpeg) AS Bytes
FROM ocr.DocPageMap m JOIN ocr.Document d ON d.DocId = m.DocId
WHERE m.HeaderJpeg IS NOT NULL
  AND (m.Method = 'TEXT' OR (d.Status = 'POSTED' AND m.Mode = 'on' AND m.UsedForRead = 1))
ORDER BY m.PageRole, m.Id DESC;

-- 5. Backfill progress: documents per outcome
WITH perDoc AS (
    SELECT m.DocId,
           MAX(CASE WHEN m.PageNo = 0 THEN m.PageRole END)       AS Marker,
           MAX(CASE WHEN m.Method = 'TEXT' THEN 1 ELSE 0 END)    AS HasTextAnchor
    FROM ocr.DocPageMap m
    WHERE m.Mode = 'backfill'
    GROUP BY m.DocId
)
SELECT COALESCE(Marker, CASE WHEN HasTextAnchor = 1 THEN 'ANCHOR_FROM_TEXT' ELSE 'AI_ONLY' END) AS Outcome,
       COUNT(*) AS Docs
FROM perDoc
GROUP BY COALESCE(Marker, CASE WHEN HasTextAnchor = 1 THEN 'ANCHOR_FROM_TEXT' ELSE 'AI_ONLY' END);
*/
