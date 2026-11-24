/*
    Add-ScaleHistory.sql - a lab tool, never for a real install.

    Adds an older year to a synthetic stress database (CopilotScaleTest) so the 730-day and All data periods read
    two years. Each target day is a copy of the day @ShiftDays later (364: the same weekday, 52 weeks on), thinned
    into a rollout: a share of the people (@PeopleFrom of them on the first day, all of them on the last), and a
    share of those people's rows (@RowsFrom to @RowsTo). People and rows are picked by a hash, so a person who starts
    stays, and an interaction is kept or dropped with every resource it read.

    Written at the bi level, as the 120M lab data was: bi.FactInteraction, bi.FactAccessedResource, bi.FactSession,
    bi.FactLicensedDay and bi.FactLicenseChange, with bi.DimDate extended and each person's first use and license
    start moved back where the older year starts them earlier. The audit tables (raw records, sessions, the weekly
    rollup) are left as they are. Run dash.usp_RefreshDashboard afterwards for the dashboard's daily totals.

    Copied rows are told apart by their RecordId: the version digit of the GUID (character 15) is 'e', where a
    real one is '4'; copied license changes start 'y-' (their ids are not GUIDs); copied sessions are numbered from
    100,000,000,000. A month is deleted and written again in one
    transaction, so a run stopped part way can be run again.

    Parameters (from Add-ScaleHistory.ps1): @From, @To (target days), @ShiftDays, @PeopleFrom, @RowsFrom, @RowsTo.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @SessionOffset BIGINT = 100000000000, @msg NVARCHAR(400), @started DATETIME2(0) = SYSUTCDATETIME();
DECLARE @days INT = DATEDIFF(DAY, @From, @To);
IF @days < 1 THROW 50901, N'@To must be after @From.', 1;
IF @PeopleFrom <= 0 OR @PeopleFrom > 1 OR @RowsFrom <= 0 OR @RowsTo > 1 OR @RowsFrom > @RowsTo
    THROW 50902, N'@PeopleFrom must be in (0, 1], and 0 < @RowsFrom <= @RowsTo <= 1.', 1;
IF EXISTS (SELECT 1 FROM bi.FactInteraction WHERE DateKey <= YEAR(@To) * 10000 + MONTH(@To) * 100 + DAY(@To)
                                             AND SUBSTRING(RecordId, 15, 1) <> 'e')
    THROW 50903, N'The target days already hold interactions that are not copies: this adds history before the data only.', 1;

/* People, each with a fixed place in the rollout (0 to 1). */
DROP TABLE IF EXISTS #u;
CREATE TABLE #u (UserKey INT NOT NULL PRIMARY KEY, U FLOAT NOT NULL);
INSERT INTO #u (UserKey, U)
SELECT UserKey, (ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'u', UserKey)), 1, 4) AS INT) % 1000000)) / 1000000.0
FROM bi.DimUser;

/* Target days, the day each one copies, and its shares. */
DROP TABLE IF EXISTS #d;
CREATE TABLE #d (TargetKey INT NOT NULL PRIMARY KEY, SourceKey INT NOT NULL UNIQUE, P FLOAT NOT NULL, K INT NOT NULL);
WITH n AS (SELECT TOP (@days + 1) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b)
INSERT INTO #d (TargetKey, SourceKey, P, K)
SELECT YEAR(t.D) * 10000 + MONTH(t.D) * 100 + DAY(t.D),
       YEAR(s.D) * 10000 + MONTH(s.D) * 100 + DAY(s.D),
       @PeopleFrom + (1.0 - @PeopleFrom) * n.i / @days,
       CAST(1000 * (@RowsFrom + (@RowsTo - @RowsFrom) * n.i / @days) AS INT)
FROM n
CROSS APPLY (SELECT DATEADD(DAY, n.i, @From) AS D) AS t
CROSS APPLY (SELECT DATEADD(DAY, @ShiftDays, t.D) AS D) AS s;

DECLARE @missing INT = (SELECT COUNT(*) FROM #d AS d WHERE NOT EXISTS (SELECT 1 FROM dash.UserDay AS x WHERE x.DateKey = d.SourceKey));
IF @missing > 0
BEGIN
    SET @msg = CONCAT(@missing, N' day(s) to copy from have no data: move @From or @To.');
    THROW 50904, @msg, 1;
END;

/* Dates, as bi.usp_RefreshBi writes them (Saturday to Friday weeks). */
DECLARE @d0 DATE = @From, @d1 DATE = (SELECT MIN([Date]) FROM bi.DimDate);
IF @d1 IS NULL OR @d1 > @d0
BEGIN
    DECLARE @dn INT = DATEDIFF(DAY, @d0, ISNULL(@d1, DATEADD(DAY, 1, @To)));
    WITH n AS (SELECT TOP (@dn) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b)
    INSERT INTO bi.DimDate
        (DateKey, [Date], [Year], [Quarter], MonthNumber, [MonthName], YearMonth, DayOfWeekNumber, DayOfWeekName,
         IsWeekday, WeekEnding, WeekYear, WeekNumber, WeekLabel)
    SELECT YEAR(d.D) * 10000 + MONTH(d.D) * 100 + DAY(d.D), d.D, YEAR(d.D), DATEPART(QUARTER, d.D), MONTH(d.D),
           CHOOSE(MONTH(d.D), N'January', N'February', N'March', N'April', N'May', N'June', N'July',
                  N'August', N'September', N'October', N'November', N'December'),
           CONVERT(CHAR(7), d.D, 126),
           w.Dow,
           CHOOSE(w.Dow, N'Monday', N'Tuesday', N'Wednesday', N'Thursday', N'Friday', N'Saturday', N'Sunday'),
           CASE WHEN w.Dow <= 5 THEN 1 ELSE 0 END,
           w.WeekEnding, YEAR(w.WeekEnding), k.WeekNumber,
           CONCAT(YEAR(w.WeekEnding), N'-W', RIGHT(CONCAT(N'0', k.WeekNumber), 2))
    FROM n
    CROSS APPLY (SELECT DATEADD(DAY, n.i, @d0) AS D) AS d
    CROSS APPLY (SELECT DATEDIFF(DAY, '19000101', d.D) % 7 + 1 AS Dow,
                        DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', d.D) % 7, d.D) AS WeekEnding) AS w
    CROSS APPLY (SELECT DATEFROMPARTS(YEAR(w.WeekEnding), 1, 1) AS Jan1) AS j
    CROSS APPLY (SELECT DATEDIFF(DAY, DATEADD(DAY, 6 - DATEDIFF(DAY, '19000106', j.Jan1) % 7, j.Jan1), w.WeekEnding) / 7 + 1
                        AS WeekNumber) AS k
    WHERE NOT EXISTS (SELECT 1 FROM bi.DimDate AS x WHERE x.[Date] = d.D);
    SET @msg = CONCAT(N'Dates: ', @@ROWCOUNT, N' added from ', CONVERT(CHAR(10), @d0, 23), N'.');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END;

/* Month by month. */
DECLARE @m DATE = DATEFROMPARTS(YEAR(@From), MONTH(@From), 1);
DECLARE @a INT, @b INT, @sa INT, @sb INT, @ni BIGINT, @nr BIGINT, @ns BIGINT, @nl BIGINT, @nc BIGINT;
DECLARE @ti BIGINT = 0, @tr BIGINT = 0, @ts BIGINT = 0;
WHILE @m <= @To
BEGIN
    SELECT @a = MIN(TargetKey), @b = MAX(TargetKey), @sa = MIN(SourceKey), @sb = MAX(SourceKey)
    FROM #d WHERE TargetKey / 100 = YEAR(@m) * 100 + MONTH(@m);

    BEGIN TRANSACTION;

    DELETE FROM bi.FactInteraction WHERE DateKey BETWEEN @a AND @b AND SUBSTRING(RecordId, 15, 1) = 'e';
    DELETE FROM bi.FactAccessedResource WHERE DateKey BETWEEN @a AND @b AND SUBSTRING(RecordId, 15, 1) = 'e';
    DELETE FROM bi.FactSession WHERE DateKey BETWEEN @a AND @b AND SessionKey >= @SessionOffset;
    DELETE FROM bi.FactLicensedDay WHERE DateKey BETWEEN @a AND @b;
    DELETE FROM bi.FactLicenseChange WHERE DateKey BETWEEN @a AND @b AND RecordId LIKE N'y-%';

    /* TABLOCK lets the columnstore be written in parallel; the lab server is otherwise idle, so every processor. */
    INSERT INTO bi.FactInteraction WITH (TABLOCK)
        (RecordId, CreatedUtc, ActivityHour, DateKey, HourOfDay, UserKey, AppKey, AgentKey, ModelKey, LicenseStatusKey,
         PromptCount, AccessedResourceCount, UsedWebSearch, JailbreakDetected, PromptInjectionDetected, HasBlockedResource,
         HasSensitivityLabel, LicenseTypeKey, SensitivityLabelKey, MessageCount, MemoryUpdated, DlpEvaluationDeferred,
         CalledAnotherAgent, TargetAgentName, PremiumUse, UseCaseKey, UseKind)
    SELECT STUFF(f.RecordId, 15, 1, 'e'), DATEADD(DAY, -@ShiftDays, f.CreatedUtc), DATEADD(DAY, -@ShiftDays, f.ActivityHour),
           d.TargetKey, f.HourOfDay, f.UserKey, f.AppKey, f.AgentKey, f.ModelKey, f.LicenseStatusKey,
           f.PromptCount, f.AccessedResourceCount, f.UsedWebSearch, f.JailbreakDetected, f.PromptInjectionDetected, f.HasBlockedResource,
           f.HasSensitivityLabel, f.LicenseTypeKey, f.SensitivityLabelKey, f.MessageCount, f.MemoryUpdated, f.DlpEvaluationDeferred,
           f.CalledAnotherAgent, f.TargetAgentName, f.PremiumUse, f.UseCaseKey, f.UseKind
    FROM bi.FactInteraction AS f
    JOIN #d AS d ON d.SourceKey = f.DateKey
    JOIN #u AS u ON u.UserKey = f.UserKey AND u.U < d.P
    WHERE f.DateKey BETWEEN @sa AND @sb
      AND SUBSTRING(f.RecordId, 15, 1) <> 'e'
      AND ABS(CAST(SUBSTRING(HASHBYTES('MD5', f.RecordId), 1, 4) AS INT) % 1000) < d.K
    OPTION (MAXDOP 8);
    SET @ni = @@ROWCOUNT;

    INSERT INTO bi.FactAccessedResource WITH (TABLOCK)
        (RecordId, Ordinal, CreatedUtc, ActivityHour, DateKey, UserKey, AppKey, ResourceTypeKey, ResourceActionKey,
         SiteKey, SensitivityLabelKey, IsBlocked, XPIADetected, FileTypeKey)
    SELECT STUFF(r.RecordId, 15, 1, 'e'), r.Ordinal, DATEADD(DAY, -@ShiftDays, r.CreatedUtc), DATEADD(DAY, -@ShiftDays, r.ActivityHour),
           d.TargetKey, r.UserKey, r.AppKey, r.ResourceTypeKey, r.ResourceActionKey,
           r.SiteKey, r.SensitivityLabelKey, r.IsBlocked, r.XPIADetected, r.FileTypeKey
    FROM bi.FactAccessedResource AS r
    JOIN #d AS d ON d.SourceKey = r.DateKey
    JOIN #u AS u ON u.UserKey = r.UserKey AND u.U < d.P
    WHERE r.DateKey BETWEEN @sa AND @sb
      AND SUBSTRING(r.RecordId, 15, 1) <> 'e'
      AND ABS(CAST(SUBSTRING(HASHBYTES('MD5', r.RecordId), 1, 4) AS INT) % 1000) < d.K
    OPTION (MAXDOP 8);
    SET @nr = @@ROWCOUNT;

    INSERT INTO bi.FactSession
        (SessionKey, SessionStart, SessionEnd, DateKey, UserKey, AppKey, LicenseStatusKey, PromptCount, SpanMinutes, IsMultiTurn)
    SELECT s.SessionKey + @SessionOffset, DATEADD(DAY, -@ShiftDays, s.SessionStart), DATEADD(DAY, -@ShiftDays, s.SessionEnd),
           d.TargetKey, s.UserKey, s.AppKey, s.LicenseStatusKey, s.PromptCount, s.SpanMinutes, s.IsMultiTurn
    FROM bi.FactSession AS s
    JOIN #d AS d ON d.SourceKey = s.DateKey
    JOIN #u AS u ON u.UserKey = s.UserKey AND u.U < d.P
    WHERE s.DateKey BETWEEN @sa AND @sb
      AND s.SessionKey < @SessionOffset
      AND ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N's', s.SessionKey)), 1, 4) AS INT) % 1000) < d.K
    OPTION (MAXDOP 8);
    SET @ns = @@ROWCOUNT;

    INSERT INTO bi.FactLicensedDay (DateKey, UserKey)
    SELECT d.TargetKey, l.UserKey
    FROM bi.FactLicensedDay AS l
    JOIN #d AS d ON d.SourceKey = l.DateKey
    JOIN #u AS u ON u.UserKey = l.UserKey AND u.U < d.P
    WHERE l.DateKey BETWEEN @sa AND @sb;
    SET @nl = @@ROWCOUNT;

    INSERT INTO bi.FactLicenseChange (RecordId, ChangedUtc, DateKey, UserKey, SkuKey, Change, ChangedBy)
    SELECT CONCAT(N'y-', c.RecordId), DATEADD(DAY, -@ShiftDays, c.ChangedUtc), d.TargetKey, c.UserKey, c.SkuKey, c.Change, c.ChangedBy
    FROM bi.FactLicenseChange AS c
    JOIN #d AS d ON d.SourceKey = c.DateKey
    JOIN #u AS u ON u.UserKey = c.UserKey AND u.U < d.P
    WHERE c.DateKey BETWEEN @sa AND @sb
      AND c.RecordId NOT LIKE N'y-%';
    SET @nc = @@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT @ti += @ni, @tr += @nr, @ts += @ns;
    SET @msg = CONCAT(CONVERT(CHAR(7), @m, 126), N': ', FORMAT(@ni, 'N0'), N' interactions, ', FORMAT(@nr, 'N0'), N' resources, ',
                      FORMAT(@ns, 'N0'), N' sessions, ', FORMAT(@nl, 'N0'), N' licensed days, ', @nc, N' license changes (',
                      DATEDIFF(SECOND, @started, SYSUTCDATETIME()), N' s so far).');
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @m = DATEADD(MONTH, 1, @m);
END;

/* Rows arrived a month at a time: compress what was left open, so the older year reads as the rest does. */
RAISERROR(N'Compressing the open row groups...', 0, 1) WITH NOWAIT;
ALTER INDEX CCI_FactInteraction ON bi.FactInteraction REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);
ALTER INDEX CCI_FactAccessedResource ON bi.FactAccessedResource REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);

/* First use and license start, where the older year starts them earlier. */
UPDATE u SET FirstSeenUtc = m.FirstUtc
FROM bi.DimUser AS u
JOIN (SELECT UserKey, MIN(CreatedUtc) AS FirstUtc FROM bi.FactInteraction GROUP BY UserKey) AS m ON m.UserKey = u.UserKey
WHERE u.FirstSeenUtc IS NULL OR m.FirstUtc < u.FirstSeenUtc;
SET @msg = CONCAT(N'First use moved earlier for ', FORMAT(@@ROWCOUNT, 'N0'), N' people.');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DROP TABLE IF EXISTS #ls;
SELECT l.UserKey, CAST(CONVERT(DATE, CONVERT(CHAR(8), MIN(l.DateKey))) AS DATETIME2(0)) AS SinceUtc
INTO #ls
FROM bi.FactLicensedDay AS l GROUP BY l.UserKey;
UPDATE u SET LicensedSinceUtc = s.SinceUtc
FROM bi.DimUser AS u JOIN #ls AS s ON s.UserKey = u.UserKey
WHERE u.LicensedSinceUtc IS NOT NULL AND s.SinceUtc < u.LicensedSinceUtc;
SET @msg = CONCAT(N'License start moved earlier for ', FORMAT(@@ROWCOUNT, 'N0'), N' people.');
RAISERROR(@msg, 0, 1) WITH NOWAIT;
IF OBJECT_ID(N'audit.CopilotLicense', N'U') IS NOT NULL
    UPDATE c SET LicensedSinceUtc = s.SinceUtc
    FROM audit.CopilotLicense AS c
    JOIN bi.DimUser AS u ON u.UserPrincipalName = c.UserPrincipalName
    JOIN #ls AS s ON s.UserKey = u.UserKey
    WHERE c.LicensedSinceUtc IS NOT NULL AND s.SinceUtc < c.LicensedSinceUtc;

UPDATE STATISTICS bi.FactInteraction;
UPDATE STATISTICS bi.FactAccessedResource;
UPDATE STATISTICS bi.FactSession;
UPDATE STATISTICS bi.FactLicensedDay;

SET @msg = CONCAT(N'Older year added: ', FORMAT(@ti, 'N0'), N' interactions, ', FORMAT(@tr, 'N0'), N' resources, ',
                  FORMAT(@ts, 'N0'), N' sessions, in ', DATEDIFF(MINUTE, @started, SYSUTCDATETIME()), N' min.');
RAISERROR(@msg, 0, 1) WITH NOWAIT;
