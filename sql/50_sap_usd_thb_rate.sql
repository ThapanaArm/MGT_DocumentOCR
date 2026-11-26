-- Run in each OCR database after sql/29_job_log.sql.
IF OBJECT_ID(N'ocr.JobLog', N'U') IS NULL
    THROW 50001, 'Apply the OCR JobLog migration before this script.', 1;
GO

IF OBJECT_ID(N'ocr.SapUsdThbRate', N'U') IS NULL
    CREATE TABLE ocr.SapUsdThbRate (
        RateDate date NOT NULL PRIMARY KEY,
        ThbPerUsd decimal(12,6) NOT NULL CHECK (ThbPerUsd > 0),
        RateType varchar(4) NOT NULL
    );
GO

-- JobLog.StartedAt follows the SQL Server's local date, as in ocr.vJobCostDaily.
CREATE OR ALTER VIEW ocr.vJobOcrCostSap
AS
SELECT j.JobId, j.DocId, j.Module, j.FileName, j.Provider, j.Model, j.Status,
       j.StartedAt, d.UsageDate, j.TokensIn, j.TokensOut, j.TokensTotal,
       j.Cost AS StoredCost, j.CostCurrency, fx.ThbPerUsd, fx.RateType AS SapRateType,
       CASE j.CostCurrency
           WHEN 'USD' THEN CONVERT(decimal(24,12), j.Cost * fx.ThbPerUsd)
           WHEN 'THB' THEN CONVERT(decimal(24,12), j.Cost)
       END AS CostThb
FROM ocr.JobLog j
CROSS APPLY (VALUES (CONVERT(date, j.StartedAt))) d(UsageDate)
LEFT JOIN ocr.SapUsdThbRate fx ON fx.RateDate = d.UsageDate AND j.CostCurrency = 'USD'
WHERE j.JobType = 'OCR';
GO
