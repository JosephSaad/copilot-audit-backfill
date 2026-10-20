/*
    AI apps outside Copilot, Power BI and Power Apps, in the Copilot database (2.6.9)
    ------------------------------------------------------------------------
    The activity database (Create-ActivitySchema.sql), where the settings name
    one (ActivityDatabase), holds every record. audit.usp_RollupActivity copies
    what the dashboard shows from it into the Copilot database, so the
    dashboard never reads another database:

      audit.AIAppUserDay   one row per day (the reporting time zone's, as Copilot's), resource, app, subscription and
                           caller: calls (messages), prompts, conversations
                           (threads), failures. A caller is a person's sign-in,
                           or 'unattributed' where the app called with its own
                           key or identity
      audit.AIAppResource  one row per resource seen: its line of business,
                           environment and owner. Guessed from the name the
                           first time it is seen (retail-prod-aif-0147: Retail,
                           Production); what an administrator sets on the page
                           is kept
      audit.PowerPlatformUserActivity  (Create-CopilotExternalUsageSchema.sql)
                           the Power BI and Power Apps rows: last active and
                           active days in the last 30, from the records. Each
                           product's rows are replaced only when the activity
                           database holds records of it, so a CSV loaded for a
                           product it does not hold is kept

    Each run is recorded in audit.ExternalUsageRun (Source 'Activity') for
    Admin > Health.

      @ActivityDatabase  the activity database's name
      @SinceUtc          the days from then on; NULL: every day

    Idempotent. Requires SQL Server 2017+.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'audit.AIAppUserDay', N'U') IS NULL
    CREATE TABLE audit.AIAppUserDay
    (
        [Day]           DATE           NOT NULL,   -- a day in the reporting time zone (audit.ReportingTimeZone())
        [Resource]      NVARCHAR(200)  NOT NULL,
        ApplicationName NVARCHAR(200)  NOT NULL,   -- '' where the record names none
        SubscriptionId  VARCHAR(64)    NOT NULL,   -- '' where the record names none
        CallerKind      VARCHAR(16)    NOT NULL,   -- Person | Unattributed
        Caller          NVARCHAR(256)  NOT NULL,   -- the sign-in, or 'unattributed'
        Calls           INT            NOT NULL,   -- records: a prompt or a response each
        Prompts         INT            NOT NULL,
        Threads         INT            NOT NULL,   -- distinct conversations that day
        Failures        INT            NOT NULL,
        LoadedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_AIAppUserDay_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_AIAppUserDay PRIMARY KEY ([Day], [Resource], ApplicationName, SubscriptionId, Caller)
    );
GO

IF OBJECT_ID(N'audit.AIAppResource', N'U') IS NULL
    CREATE TABLE audit.AIAppResource
    (
        [Resource]     NVARCHAR(200)  NOT NULL CONSTRAINT PK_AIAppResource PRIMARY KEY,
        LineOfBusiness NVARCHAR(100)  NULL,
        Environment    VARCHAR(16)    NOT NULL CONSTRAINT DF_AIAppResource_Env DEFAULT ('Unknown'),   -- Production | Development | Test | Unknown
        Owner          NVARCHAR(256)  NULL,
        Notes          NVARCHAR(400)  NULL,
        SetBy          VARCHAR(8)     NOT NULL CONSTRAINT DF_AIAppResource_SetBy DEFAULT ('Name'),    -- Name (guessed) | Admin
        FirstSeen      DATE           NULL,
        UpdatedBy      NVARCHAR(256)  NULL,
        UpdatedUtc     DATETIME2(0)   NOT NULL CONSTRAINT DF_AIAppResource_Updated DEFAULT (SYSUTCDATETIME())
    );
GO

/*
    audit.fnAIAppResourceGuess: a resource's line of business and environment from its name, as names like
    retail-prod-aif-0147 and aza-dev-aif-0046 carry them. The environment is the first label that names one; the line of
    business the first label before it, unless that label is a short prefix (three letters or fewer, such as aza).
    Pure, for the tests.
*/
CREATE OR ALTER FUNCTION audit.fnAIAppResourceGuess (@Resource NVARCHAR(200))
RETURNS TABLE
AS
RETURN
WITH parts AS
(
    SELECT CAST(p.[key] AS INT) AS n, LOWER(p.[value]) AS v
    FROM OPENJSON(N'["' + REPLACE(REPLACE(REPLACE(STRING_ESCAPE(ISNULL(@Resource, N''), 'json'), N'_', N'-'), N'.', N'-'), N'-', N'","') + N'"]') AS p
),
env AS
(
    SELECT TOP (1) n,
           CASE WHEN v IN (N'prod', N'prd', N'production', N'p') THEN 'Production'
                WHEN v IN (N'dev', N'development', N'd', N'sbx', N'sandbox') THEN 'Development'
                WHEN v IN (N'test', N'tst', N'qa', N'uat', N'stg', N'staging', N'preprod', N'nonprod') THEN 'Test' END AS Environment
    FROM parts
    /* A one-letter label names an environment only after the first (aza-p-aif-01), never as a prefix. */
    WHERE v IN (N'prod', N'prd', N'production', N'dev', N'development', N'sbx', N'sandbox',
                N'test', N'tst', N'qa', N'uat', N'stg', N'staging', N'preprod', N'nonprod')
       OR (n > 0 AND v IN (N'p', N'd'))
    ORDER BY n
)
SELECT ISNULL((SELECT Environment FROM env), 'Unknown') AS Environment,
       (SELECT TOP (1) UPPER(LEFT(p.v, 1)) + SUBSTRING(p.v, 2, 99)
        FROM parts AS p
        WHERE p.n < ISNULL((SELECT n FROM env), 0) AND LEN(p.v) > 3 AND p.v NOT LIKE N'%[0-9]%'
        ORDER BY p.n) AS LineOfBusiness;
GO

IF OBJECT_ID(N'audit.usp_RollupActivity', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE audit.usp_RollupActivity AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE audit.usp_RollupActivity
    @ActivityDatabase SYSNAME,
    @SinceUtc         DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @db NVARCHAR(300) = QUOTENAME(@ActivityDatabase), @sql NVARCHAR(MAX), @from DATE = CAST(@SinceUtc AS DATE),
            @run INT, @days INT = 0, @users INT = 0, @hasCalls BIT, @hasPlatform BIT, @msg NVARCHAR(2000);

    IF DB_ID(@ActivityDatabase) IS NULL
        THROW 50031, N'The activity database named does not exist on this server.', 1;
    SET @sql = N'SELECT @c = CASE WHEN OBJECT_ID(N''' + REPLACE(@db, N'''', N'''''') + N'.audit.AIAppCall'', N''U'') IS NULL THEN 0 ELSE 1 END,
                        @p = CASE WHEN OBJECT_ID(N''' + REPLACE(@db, N'''', N'''''') + N'.audit.PlatformActivity'', N''U'') IS NULL THEN 0 ELSE 1 END;';
    EXEC sys.sp_executesql @sql, N'@c BIT OUTPUT, @p BIT OUTPUT', @c = @hasCalls OUTPUT, @p = @hasPlatform OUTPUT;
    IF @hasCalls = 0 OR @hasPlatform = 0
        THROW 50032, N'The activity database has no audit.AIAppCall or audit.PlatformActivity: install it with Install-CopilotAudit.ps1 -ActivityDatabase.', 1;

    /* Owed days are made up: from three days before the last rollup that succeeded, where that is earlier than asked
       (nights the optional activity steps failed), and every day where none has. */
    DECLARE @last DATE = (SELECT MAX(ToDay) FROM audit.ExternalUsageRun WHERE Source = 'Activity' AND Status = 'Succeeded');
    IF @from IS NOT NULL AND (@last IS NULL OR DATEADD(DAY, -3, @last) < @from)
        SET @from = CASE WHEN @last IS NULL THEN NULL ELSE DATEADD(DAY, -3, @last) END;

    INSERT INTO audit.ExternalUsageRun (Source, Scope, FromDay) VALUES ('Activity', @ActivityDatabase, @from);
    SET @run = SCOPE_IDENTITY();

    BEGIN TRY
        /* ---- AI apps: a row per day, resource, app, subscription and caller ---- */
        /* Days in the reporting time zone, as every Copilot day is cut, so an AI app's day lines up with the same person's
           Copilot day. AT TIME ZONE once for each quarter hour of the range (a zone can be offset by 30 or 45 minutes), not
           once for each of tens of millions of records. */
        DECLARE @zone SYSNAME = audit.ReportingTimeZone(), @lo DATETIME2(0), @hi DATETIME2(0);
        SET @sql = N'SELECT @lo = MIN(CreatedDateTime), @hi = MAX(CreatedDateTime) FROM ' + @db + N'.audit.AIAppCall;';
        EXEC sys.sp_executesql @sql, N'@lo DATETIME2(0) OUTPUT, @hi DATETIME2(0) OUTPUT', @lo = @lo OUTPUT, @hi = @hi OUTPUT;
        DECLARE @fromUtc DATETIME2(0) = CASE WHEN @from IS NULL THEN NULL
                                            ELSE CAST((CAST(@from AS DATETIME2(0)) AT TIME ZONE @zone) AT TIME ZONE 'UTC' AS DATETIME2(0)) END;
        DROP TABLE IF EXISTS #q;
        CREATE TABLE #q (UtcQuarter DATETIME2(0) NOT NULL PRIMARY KEY, LocalDay DATE NOT NULL);
        IF @lo IS NOT NULL
        BEGIN
            DECLARE @q0 DATETIME2(0) = DATEADD(MINUTE, DATEDIFF(MINUTE, '2000-01-01', ISNULL(CASE WHEN @fromUtc > @lo THEN @fromUtc END, @lo)) / 15 * 15, CAST('2000-01-01' AS DATETIME2(0)));
            DECLARE @n INT = DATEDIFF(MINUTE, @q0, @hi) / 15 + 1;
            IF @n > 0
            WITH t AS (SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b)
            INSERT INTO #q (UtcQuarter, LocalDay)
            SELECT DATEADD(MINUTE, 15 * t.i, @q0), CAST((DATEADD(MINUTE, 15 * t.i, @q0) AT TIME ZONE 'UTC') AT TIME ZONE @zone AS DATE) FROM t;
        END;

        BEGIN TRANSACTION;
        DELETE FROM audit.AIAppUserDay WHERE @from IS NULL OR [Day] >= @from;
        SET @sql = N'
INSERT INTO audit.AIAppUserDay ([Day], [Resource], ApplicationName, SubscriptionId, CallerKind, Caller, Calls, Prompts, Threads, Failures)
SELECT q.LocalDay, c.[Resource], ISNULL(c.ApplicationName, N''''), ISNULL(c.SubscriptionId, ''''),
       MAX(c.CallerKind), c.Caller, COUNT(*), SUM(c.Prompts), COUNT(DISTINCT c.ThreadId), SUM(CAST(c.Failed AS INT))
FROM ' + @db + N'.audit.AIAppCall AS c
JOIN #q AS q ON q.UtcQuarter = DATEADD(MINUTE, DATEDIFF(MINUTE, ''2000-01-01'', c.CreatedDateTime) / 15 * 15, CAST(''2000-01-01'' AS DATETIME2(0)))
WHERE (@fromUtc IS NULL OR c.CreatedDateTime >= @fromUtc) AND (@from IS NULL OR q.LocalDay >= @from)
GROUP BY q.LocalDay, c.[Resource], ISNULL(c.ApplicationName, N''''), ISNULL(c.SubscriptionId, ''''), c.Caller;';
        EXEC sys.sp_executesql @sql, N'@from DATE, @fromUtc DATETIME2(0)', @from = @from, @fromUtc = @fromUtc;
        SET @days = @@ROWCOUNT;

        /* Resources seen for the first time, with what their names suggest. Never one an administrator set. */
        INSERT INTO audit.AIAppResource ([Resource], LineOfBusiness, Environment, SetBy, FirstSeen)
        SELECT d.[Resource], g.LineOfBusiness, g.Environment, 'Name', d.FirstSeen
        FROM (SELECT [Resource], MIN([Day]) AS FirstSeen FROM audit.AIAppUserDay GROUP BY [Resource]) AS d
        CROSS APPLY audit.fnAIAppResourceGuess(d.[Resource]) AS g
        WHERE NOT EXISTS (SELECT 1 FROM audit.AIAppResource AS r WHERE r.[Resource] = d.[Resource]);
        COMMIT TRANSACTION;

        /* ---- Power BI and Power Apps: who used them, and lately ---- */
        DROP TABLE IF EXISTS #pp;
        CREATE TABLE #pp (UserPrincipalName NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL, Product VARCHAR(32) COLLATE DATABASE_DEFAULT NOT NULL,
                          LastActiveUtc DATETIME2(0) NULL, ActiveDays30 INT NOT NULL, PRIMARY KEY (UserPrincipalName, Product));
        SET @sql = N'
INSERT INTO #pp (UserPrincipalName, Product, LastActiveUtc, ActiveDays30)
SELECT LEFT(a.UserId, 256), a.Product, MAX(a.CreatedDateTime),
       COUNT(DISTINCT CASE WHEN a.CreatedDateTime >= DATEADD(DAY, -30, SYSUTCDATETIME()) THEN CAST(a.CreatedDateTime AS DATE) END)
FROM ' + @db + N'.audit.PlatformActivity AS a
WHERE a.UserId LIKE N''%@%''
GROUP BY LEFT(a.UserId, 256), a.Product;';
        EXEC sys.sp_executesql @sql;

        BEGIN TRANSACTION;
        DELETE u FROM audit.PowerPlatformUserActivity AS u
        WHERE u.Product IN (SELECT DISTINCT Product FROM #pp);
        INSERT INTO audit.PowerPlatformUserActivity (UserPrincipalName, Product, LastActiveUtc, ActiveDays30)
        SELECT UserPrincipalName, Product, LastActiveUtc, ActiveDays30 FROM #pp;
        SET @users = @@ROWCOUNT;
        COMMIT TRANSACTION;

        SET @msg = CONCAT(N'AI apps: ', FORMAT(@days, 'N0'), N' day-resource-caller row(s)', CASE WHEN @from IS NULL THEN N' (every day)' ELSE CONCAT(N' from ', CONVERT(NCHAR(10), @from, 120)) END,
                          N'; Power BI and Power Apps: ', FORMAT(@users, 'N0'), N' person-product row(s).');
        UPDATE audit.ExternalUsageRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Succeeded', RowsWritten = @days + @users, Detail = @msg,
               ToDay = CAST(SYSUTCDATETIME() AS DATE)
        WHERE RunId = @run;
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @msg = LEFT(ERROR_MESSAGE(), 2000);
        UPDATE audit.ExternalUsageRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Failed', Detail = @msg WHERE RunId = @run;
        THROW;
    END CATCH;
END;
GO

/* The dashboard reads them, and its administrators keep the resources' line of business, environment and owner. */
IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NOT NULL
BEGIN
    GRANT SELECT ON audit.AIAppUserDay TO CopilotInsightsApp;
    GRANT SELECT, INSERT, UPDATE ON audit.AIAppResource TO CopilotInsightsApp;
END;
GO
