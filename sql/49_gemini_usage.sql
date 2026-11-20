-- Run separately in the target OCR database. Requires ocr.JobLog (migration 29).
IF OBJECT_ID(N'ocr.JobLog', N'U') IS NULL
    THROW 50001, 'Apply the OCR JobLog migration before this script.', 1;
GO

IF OBJECT_ID(N'ocr.GeminiRequestLog', N'U') IS NULL
BEGIN
    CREATE TABLE ocr.GeminiRequestLog (
        RequestId uniqueidentifier NOT NULL PRIMARY KEY NONCLUSTERED,
        StartedUtc datetime2(3) NOT NULL,
        FinishedUtc datetime2(3) NOT NULL,
        JobId bigint NULL,
        DocId int NULL,
        Module nvarchar(10) NULL,
        EnvironmentName nvarchar(120) NOT NULL,
        KeyName nvarchar(100) NOT NULL,
        Model nvarchar(120) NOT NULL,
        Operation varchar(50) NOT NULL,
        Attempt int NOT NULL,
        PageNumbers nvarchar(max) NOT NULL CHECK (ISJSON(PageNumbers) = 1),
        Outcome varchar(24) NOT NULL,
        HttpStatus int NULL,
        ResponseId nvarchar(256) NULL,
        InputTokens int NULL,
        OutputTokens int NULL,
        ThinkingTokens int NULL,
        CachedInputTokens int NULL,
        InputRate decimal(12,6) NULL,
        OutputRate decimal(12,6) NULL,
        CachedRate decimal(12,6) NULL,
        CostUsd_Stored decimal(22,12) NULL,
        INDEX IX_GeminiRequestLog_Time CLUSTERED (StartedUtc, RequestId),
        INDEX IX_GeminiRequestLog_Job NONCLUSTERED (JobId)
    );
END;
GO

-- Populate only verified, dated BOT USD reference rates. Unpublished dates remain NULL.
IF OBJECT_ID(N'ocr.BotUsdThbReferenceRate', N'U') IS NULL
    CREATE TABLE ocr.BotUsdThbReferenceRate (
        RateDate date NOT NULL PRIMARY KEY,
        ThbPerUsd decimal(12,6) NOT NULL CHECK (ThbPerUsd > 0)
    );
GO

CREATE OR ALTER VIEW ocr.vGeminiRequestCost
AS
SELECT r.RequestId, r.JobId, COALESCE(j.DocId, r.DocId) AS DocId, COALESCE(j.Module, r.Module) AS Module,
       j.FileName, j.Status AS OcrJobStatus, j.StartedAt AS LegacyJobStartedAt,
       r.EnvironmentName, r.KeyName, r.Model, r.Operation, r.Attempt,
       r.PageNumbers, r.StartedUtc, r.FinishedUtc, r.Outcome, r.HttpStatus, r.ResponseId,
       CONVERT(date, (r.StartedUtc AT TIME ZONE 'UTC') AT TIME ZONE 'Pacific Standard Time') AS GoogleUsageDate,
       tz.ThaiUsageDate, r.InputTokens, r.OutputTokens, r.ThinkingTokens, r.CachedInputTokens,
       CONVERT(bigint, r.InputTokens) + r.OutputTokens + r.ThinkingTokens AS TotalTokens,
       r.InputRate, r.OutputRate, r.CachedRate, r.CostUsd_Stored, cost.CostUsd_Recalc, fx.ThbPerUsd,
       CONVERT(decimal(24,12), cost.CostUsd_Recalc * fx.ThbPerUsd) AS CostThb_Recalc
FROM ocr.GeminiRequestLog r
LEFT JOIN ocr.JobLog j ON j.JobId = r.JobId
CROSS APPLY (SELECT CONVERT(date, DATEADD(hour, 7, r.StartedUtc)) AS ThaiUsageDate) tz
LEFT JOIN ocr.BotUsdThbReferenceRate fx ON fx.RateDate = tz.ThaiUsageDate
CROSS APPLY (SELECT CONVERT(decimal(22,12), (
    CONVERT(decimal(12,0), r.InputTokens - r.CachedInputTokens) * r.InputRate
    + CONVERT(decimal(12,0), r.CachedInputTokens) * r.CachedRate
    + CONVERT(decimal(12,0), CONVERT(bigint, r.OutputTokens) + r.ThinkingTokens) * r.OutputRate
) / 1000000.0) AS CostUsd_Recalc) cost;
GO

CREATE OR ALTER VIEW ocr.vGeminiFileUsage
AS
SELECT EnvironmentName, KeyName, Module, DocId, MAX(FileName) AS FileName,
       COUNT_BIG(*) AS Attempts, COUNT(DISTINCT JobId) AS Jobs,
       COUNT_BIG(*) - COUNT_BIG(CostUsd_Recalc) AS AttemptsMissingUsd,
       COUNT_BIG(CostUsd_Recalc) - COUNT_BIG(CostThb_Recalc) AS PricedAttemptsMissingFx,
       SUM(CostUsd_Recalc) AS KnownUsd, SUM(CostThb_Recalc) AS KnownThb,
       MIN(CostUsd_Recalc) AS MinRequestUsd, MAX(CostUsd_Recalc) AS MaxRequestUsd,
       CONVERT(decimal(22,12), CONVERT(decimal(25,12), SUM(CostUsd_Recalc))
           / NULLIF(SUM(CASE WHEN CostUsd_Recalc IS NOT NULL THEN TotalTokens END), 0)) AS UsdPerToken
FROM ocr.vGeminiRequestCost
WHERE DocId IS NOT NULL
GROUP BY EnvironmentName, KeyName, Module, DocId;
GO

CREATE OR ALTER VIEW ocr.vGeminiPageUsage
AS
SELECT r.EnvironmentName, r.KeyName, r.Module, r.DocId, CONVERT(int, p.value) AS PageNumber,
       COUNT_BIG(*) AS TimesSent, COUNT_BIG(*) - COUNT_BIG(a.EstimatedUsd) AS AttemptsMissingUsd,
       SUM(a.EstimatedUsd) AS KnownEstimatedUsd, MIN(a.EstimatedUsd) AS MinEstimatedUsd,
       MAX(a.EstimatedUsd) AS MaxEstimatedUsd, SUM(a.EstimatedThb) AS KnownEstimatedThb,
       MIN(a.EstimatedThb) AS MinEstimatedThb, MAX(a.EstimatedThb) AS MaxEstimatedThb
FROM ocr.vGeminiRequestCost r
CROSS APPLY OPENJSON(r.PageNumbers) p
CROSS APPLY (SELECT COUNT(*) AS PageCount FROM OPENJSON(r.PageNumbers)) n
CROSS APPLY (SELECT CONVERT(decimal(22,12), r.CostUsd_Recalc / NULLIF(n.PageCount, 0)) AS EstimatedUsd,
                    CONVERT(decimal(24,12), r.CostThb_Recalc / NULLIF(n.PageCount, 0)) AS EstimatedThb) a
WHERE r.DocId IS NOT NULL
GROUP BY r.EnvironmentName, r.KeyName, r.Module, r.DocId, CONVERT(int, p.value);
GO

CREATE OR ALTER VIEW ocr.vGeminiDailyUsage
AS
SELECT GoogleUsageDate, EnvironmentName, KeyName, Model, Operation, COUNT_BIG(*) AS Attempts,
       COUNT_BIG(*) - COUNT_BIG(CostUsd_Recalc) AS AttemptsMissingUsd,
       COUNT_BIG(CostUsd_Recalc) - COUNT_BIG(CostThb_Recalc) AS PricedAttemptsMissingFx,
       SUM(CostUsd_Recalc) AS KnownUsd, SUM(CostThb_Recalc) AS KnownThb
FROM ocr.vGeminiRequestCost
GROUP BY GoogleUsageDate, EnvironmentName, KeyName, Model, Operation;
GO

-- SELECT * FROM ocr.vGeminiDailyUsage WHERE GoogleUsageDate = '2026-11-18';
-- SELECT * FROM ocr.vGeminiRequestCost WHERE DocId = 884 ORDER BY StartedUtc;
