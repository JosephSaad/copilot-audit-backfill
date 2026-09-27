/*
    Copilot Insights - operations schema
    ------------------------------------------------------------------------
    What the dashboard's operations features need, on top of the dashboard
    schema (Create-CopilotDashboardSchema.sql, run first):

      settings          the new dash.Setting rows: email, alert thresholds,
                        recipients, assumptions, retention
      dash.Secret       passwords and client secrets, encrypted in the database
                        (a certificate and symmetric key kept by SQL Server)
      dash.Subscription scheduled digests by email
      dash.NotificationLog, dash.AlertState
                        what was sent, and what an alert last fired on
      dash.Target       adoption targets by unit
      dash.TimeSaving   minutes saved per interaction, by app (the value report)
      dash.TrainingAttendance
                        who attended which training, for the effect report
      dash.ValueName    readable names for the codes in audit.ValueCatalog
      dash.usp_ApplyRetention, dash.usp_SetPrivacyMode, dash.usp_SetSecret,
      dash.usp_GetSecret
      CopilotInsightsApp
                        the database role the dashboard's account belongs to:
                        every grant the site needs, kept here so an upgrade
                        needs no grant script
      bi.OrgSecurity, bi.ValueNames, bi.LicensePool, bi.DepartmentWeek
                        for Power BI: row-level security from the org chart,
                        value names, the license pool, and department figures
                        with small groups hidden

    Idempotent. Single batch. Requires SQL Server 2017+.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dash.Setting', N'U') IS NULL
    THROW 50001, N'Run Create-CopilotDashboardSchema.sql first.', 1;

/* ---- Settings ---------------------------------------------------------- */

MERGE dash.Setting AS t
USING (VALUES
    ('DashboardUrl',            CAST(NULL AS NVARCHAR(400)), N'The dashboard''s address, for links in email (https://insights.contoso.com).'),
    ('MailMode',                N'Off',   N'How email is sent: Off, ExchangeOnline or Smtp.'),
    ('MailFrom',                NULL,     N'The From address. For Exchange Online, the mailbox that sends.'),
    ('MailReplyTo',             NULL,     N'Optional Reply-To address.'),
    ('SmtpHost',                NULL,     N'SMTP server (Smtp mode). Exchange Online uses smtp.office365.com.'),
    ('SmtpPort',                N'587',   N'SMTP port: any, such as 25, 465, 587 or 2525.'),
    ('SmtpSecurity',            N'StartTls', N'StartTls, Implicit (TLS from the first byte, usually 465), Auto or None.'),
    ('SmtpUser',                NULL,     N'User name for SMTP authentication; empty for none. The password is kept encrypted (dash.Secret).'),
    ('SmtpAcceptAnyCertificate', N'0',    N'1 accepts the SMTP server''s certificate without checking it (a lab relay with a self-signed certificate).'),
    ('ExoAuth',                 N'Password', N'Exchange Online sign-in: Password (SMTP AUTH with the mailbox''s password) or OAuth (an app with SMTP.SendAsApp).'),
    ('ExoTenantId',             NULL,     N'Tenant for Exchange Online OAuth.'),
    ('ExoClientId',             NULL,     N'App (client) id for Exchange Online OAuth. The client secret is kept encrypted (dash.Secret).'),
    ('AlertAdminTo',            NULL,     N'Who hears about pipeline failures and stale data (addresses separated by ;).'),
    ('AlertSecurityTo',         NULL,     N'Who gets security alerts.'),
    ('AlertLicensingTo',        NULL,     N'Who gets license alerts: the pool running out, licenses on disabled accounts.'),
    ('AlertAdoptionTo',         NULL,     N'Who gets usage drops and spikes.'),
    ('AlertAgentsTo',           NULL,     N'Who hears about agents seen for the first time.'),
    ('EventLogAlerts',          N'1',     N'1 also writes each alert to the Windows Application log (source Copilot Insights), for a SIEM to collect.'),
    ('FreshnessHours',          N'36',    N'Data older than this is stale: a banner on every page and an alert.'),
    ('AlertJailbreakPerDay',    N'1',     N'Jailbreak attempts in a day that raise a security alert; 0 turns it off.'),
    ('AlertInjectionPerDay',    N'1',     N'Prompt injections in a day that raise a security alert; 0 turns it off.'),
    ('AlertBlockedPerDay',      N'25',    N'Interactions with a blocked resource in a day that raise a security alert; 0 turns it off.'),
    ('AlertLabelRank',          NULL,     N'Label order (Admin > Reference data) at or above which labeled content is alert-worthy; empty turns it off.'),
    ('AlertLabelPerDay',        N'1',     N'Interactions touching content at or above AlertLabelRank in a day that raise a security alert.'),
    ('AlertSpikeFactor',        N'3',     N'A signal also alerts when a day is this many times its 28-day daily average.'),
    ('AnomalyDropPercent',      N'50',    N'A unit''s day this far below its usual level for that weekday is a drop.'),
    ('AnomalySpikePercent',     N'200',   N'A unit''s day this far above its usual level is a spike.'),
    ('PoolWarnPercent',         N'95',    N'Alert when this share of the Copilot seats bought is assigned.'),
    ('NewAgentAlert',           N'1',     N'1 sends an alert when an agent is used for the first time.'),
    ('MinutesSavedDefault',     N'3',     N'Minutes one interaction saves, for apps without their own figure (Admin > Targets and assumptions).'),
    ('HourlyCost',              N'50',    N'What an hour of an employee''s time is worth, in LicenseCurrency, for the value report.'),
    ('ChampionsPerDepartment',  N'3',     N'How many champions to suggest per department.'),
    ('LicenseGroupId',          NULL,     N'Object id of the Entra group that assigns Copilot licenses, if licenses are group-based: the license export adds and removes its members.'),
    ('AuditLogRetentionDays',   N'730',   N'Dashboard audit-log rows older than this are deleted each night; 0 keeps them.'),
    ('NotificationRetentionDays', N'365', N'Notification-log rows older than this are deleted each night; 0 keeps them.'),
    ('JsonRetentionDays',       N'0',     N'Clear the stored JSON of records older than this each night (the reports keep working); 0 keeps it. At least 200.'),
    ('ApiEnabled',              N'1',     N'1 answers the read-only data API (/api/v1).')
) AS s (Name, Value, Description)
ON t.Name = s.Name
WHEN MATCHED AND ISNULL(t.Description, N'') <> s.Description THEN UPDATE SET Description = s.Description
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (s.Name, s.Value, s.Description);

/* ---- Secrets ------------------------------------------------------------
    Encrypted with a symmetric key protected by a certificate, whose private
    key the database master key protects. The master key is created here with
    a random password nobody needs: SQL Server also protects it with the
    instance's service master key and opens it itself. Restored to another
    instance, the secrets cannot be read: enter them again in Admin > Email. */
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = N'##MS_DatabaseMasterKey##')
BEGIN
    DECLARE @mk NVARCHAR(200) = CONCAT(CONVERT(NVARCHAR(64), CRYPT_GEN_RANDOM(32), 2), N'aA1!');
    EXEC (N'CREATE MASTER KEY ENCRYPTION BY PASSWORD = ''' + @mk + N''';');
END;
IF CERT_ID(N'CopilotInsightsSecretCert') IS NULL
    EXEC (N'CREATE CERTIFICATE CopilotInsightsSecretCert WITH SUBJECT = N''Copilot Insights secrets'', EXPIRY_DATE = ''20991231'';');
IF KEY_ID(N'CopilotInsightsSecretKey') IS NULL
    EXEC (N'CREATE SYMMETRIC KEY CopilotInsightsSecretKey WITH ALGORITHM = AES_256 ENCRYPTION BY CERTIFICATE CopilotInsightsSecretCert;');

IF OBJECT_ID(N'dash.Secret', N'U') IS NULL
    CREATE TABLE dash.Secret
    (
        Name        VARCHAR(64)     NOT NULL CONSTRAINT PK_DashSecret PRIMARY KEY,
        Cipher      VARBINARY(8000) NOT NULL,
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSecret_Updated DEFAULT (SYSUTCDATETIME())
    );

EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_SetSecret
    @Name VARCHAR(64), @Value NVARCHAR(2000), @By NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Value IS NULL OR @Value = N''''
    BEGIN
        DELETE FROM dash.Secret WHERE Name = @Name;
        RETURN;
    END;
    OPEN SYMMETRIC KEY CopilotInsightsSecretKey DECRYPTION BY CERTIFICATE CopilotInsightsSecretCert;
    /* The name is the authenticator: a cipher copied onto another name will not decrypt. */
    DECLARE @c VARBINARY(8000) = ENCRYPTBYKEY(KEY_GUID(N''CopilotInsightsSecretKey''), @Value, 1, CONVERT(VARBINARY(64), @Name));
    CLOSE SYMMETRIC KEY CopilotInsightsSecretKey;
    UPDATE dash.Secret SET Cipher = @c, UpdatedBy = @By, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @Name;
    IF @@ROWCOUNT = 0 INSERT INTO dash.Secret (Name, Cipher, UpdatedBy) VALUES (@Name, @c, @By);
END;');

EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_GetSecret
    @Name VARCHAR(64), @Value NVARCHAR(2000) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Value = CONVERT(NVARCHAR(2000), DECRYPTBYKEYAUTOCERT(CERT_ID(N''CopilotInsightsSecretCert''), NULL, Cipher, 1, CONVERT(VARBINARY(64), @Name)))
    FROM dash.Secret WHERE Name = @Name;
END;');

/* ---- Email: subscriptions, what was sent, alert state -------------------- */

IF OBJECT_ID(N'dash.Subscription', N'U') IS NULL
    CREATE TABLE dash.Subscription
    (
        SubscriptionId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashSubscription PRIMARY KEY,
        Name        NVARCHAR(200)   NOT NULL,
        Report      VARCHAR(32)     NOT NULL,   -- Leadership | Adoption | Licensing | Security | Value
        Recipients  NVARCHAR(2000)  NOT NULL,   -- addresses separated by ;
        Frequency   VARCHAR(8)      NOT NULL CONSTRAINT CK_DashSubscription_Frequency CHECK (Frequency IN ('Daily', 'Weekly', 'Monthly')),
        DayOfWeek   TINYINT         NULL,       -- Weekly: 0 Sunday to 6 Saturday
        DayOfMonth  TINYINT         NULL,       -- Monthly: 1 to 28
        HourLocal   TINYINT         NOT NULL CONSTRAINT DF_DashSubscription_Hour DEFAULT (7),   -- in the reporting time zone
        RootUpn     NVARCHAR(256)   NULL,       -- a manager's organization; NULL for the whole organization
        Days        INT             NOT NULL CONSTRAINT DF_DashSubscription_Days DEFAULT (30),
        Enabled     BIT             NOT NULL CONSTRAINT DF_DashSubscription_Enabled DEFAULT (1),
        LastSentUtc DATETIME2(0)    NULL,
        LastStatus  NVARCHAR(400)   NULL,
        CreatedBy   NVARCHAR(256)   NOT NULL,
        CreatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSubscription_Created DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'dash.NotificationLog', N'U') IS NULL
    CREATE TABLE dash.NotificationLog
    (
        LogId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashNotificationLog PRIMARY KEY,
        AtUtc       DATETIME2(0)    NOT NULL CONSTRAINT DF_DashNotificationLog_At DEFAULT (SYSUTCDATETIME()),
        Kind        VARCHAR(32)     NOT NULL,   -- Pipeline | Freshness | Security | Anomaly | Pool | Leavers | NewAgent | Digest | Test
        Subject     NVARCHAR(400)   NOT NULL,
        Recipients  NVARCHAR(2000)  NULL,
        Status      VARCHAR(16)     NOT NULL,   -- Sent | Failed | NoRecipients | MailOff
        Error       NVARCHAR(2000)  NULL,
        INDEX IX_DashNotificationLog_At (AtUtc)
    );

/* Which alert fired last on what, so an alert is sent once per occurrence, not every few minutes. */
IF OBJECT_ID(N'dash.AlertState', N'U') IS NULL
    CREATE TABLE dash.AlertState
    (
        AlertKey     NVARCHAR(200)  NOT NULL CONSTRAINT PK_DashAlertState PRIMARY KEY,
        LastValue    NVARCHAR(400)  NULL,
        LastFiredUtc DATETIME2(0)   NOT NULL CONSTRAINT DF_DashAlertState_Fired DEFAULT (SYSUTCDATETIME())
    );

/* ---- Targets and assumptions -------------------------------------------- */

IF OBJECT_ID(N'dash.Target', N'U') IS NULL
    CREATE TABLE dash.Target
    (
        TargetId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashTarget PRIMARY KEY,
        RootUpn     NVARCHAR(256)   NULL,       -- a manager's organization; NULL for the whole organization
        Metric      VARCHAR(16)     NOT NULL CONSTRAINT CK_DashTarget_Metric CHECK (Metric IN ('Adoption', 'ActivePeople', 'ActiveShare')),
        TargetValue DECIMAL(12, 2)  NOT NULL,   -- a percentage for Adoption and ActiveShare, a count for ActivePeople
        StartDate   DATE            NOT NULL,
        DueDate     DATE            NOT NULL,
        Note        NVARCHAR(400)   NULL,
        CreatedBy   NVARCHAR(256)   NOT NULL,
        CreatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashTarget_Created DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_DashTarget_Dates CHECK (DueDate > StartDate)
    );

IF OBJECT_ID(N'dash.TimeSaving', N'U') IS NULL
    CREATE TABLE dash.TimeSaving
    (
        App         NVARCHAR(200)   NOT NULL CONSTRAINT PK_DashTimeSaving PRIMARY KEY,   -- bi.DimApp.App
        Minutes     DECIMAL(6, 2)   NOT NULL CONSTRAINT CK_DashTimeSaving_Minutes CHECK (Minutes >= 0 AND Minutes <= 240),
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashTimeSaving_Updated DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'dash.TrainingAttendance', N'U') IS NULL
    CREATE TABLE dash.TrainingAttendance
    (
        Upn         NVARCHAR(256)   NOT NULL,
        Session     NVARCHAR(190)   NOT NULL,   -- short enough for the key (900 bytes)
        AttendedOn  DATE            NOT NULL,
        ImportedBy  NVARCHAR(256)   NOT NULL,
        ImportedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_DashTraining_Imported DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashTrainingAttendance PRIMARY KEY (Upn, Session, AttendedOn),
        INDEX IX_DashTraining_Session (Session, AttendedOn)
    );

/* ---- Value names ---------------------------------------------------------- */

IF OBJECT_ID(N'dash.ValueName', N'U') IS NULL
    CREATE TABLE dash.ValueName
    (
        ColumnId    SMALLINT        NOT NULL,   -- audit.ValueCatalogColumn
        Value       NVARCHAR(400)   NOT NULL,
        Name        NVARCHAR(200)   NOT NULL,
        Note        NVARCHAR(400)   NULL,
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashValueName_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashValueName PRIMARY KEY (ColumnId, Value)
    );

/* A value's name where an administrator gave one, else the value itself: for the pages that show codes. */
EXEC (N'CREATE OR ALTER FUNCTION dash.fnValueName (@ColumnId SMALLINT, @Value NVARCHAR(400))
RETURNS NVARCHAR(400)
AS
BEGIN
    RETURN COALESCE((SELECT Name FROM dash.ValueName WHERE ColumnId = @ColumnId AND Value = @Value), @Value);
END;');

/* ---- 2.3.0, second part: triage, preferences, actions, contracts, views, classifications ---- */

/* Security's triage of each flagged interaction. */
IF OBJECT_ID(N'dash.FlagCase', N'U') IS NULL
    CREATE TABLE dash.FlagCase
    (
        RecordId    VARCHAR(64)     NOT NULL CONSTRAINT PK_DashFlagCase PRIMARY KEY,
        Status      VARCHAR(16)     NOT NULL CONSTRAINT CK_DashFlagCase_Status CHECK (Status IN ('New', 'Investigating', 'Closed', 'FalsePositive')),
        Owner       NVARCHAR(256)   NULL,
        Note        NVARCHAR(2000)  NULL,
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashFlagCase_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Each person's email choices: managers can opt out of their team digest, anyone can opt in to their own. */
IF OBJECT_ID(N'dash.DigestPreference', N'U') IS NULL
    CREATE TABLE dash.DigestPreference
    (
        Upn                 NVARCHAR(256)   NOT NULL CONSTRAINT PK_DashDigestPreference PRIMARY KEY,
        ManagerDigestOptOut BIT             NOT NULL CONSTRAINT DF_DashDigestPreference_Out DEFAULT (0),
        PersonalDigest      BIT             NOT NULL CONSTRAINT DF_DashDigestPreference_Personal DEFAULT (0),
        UpdatedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_DashDigestPreference_Updated DEFAULT (SYSUTCDATETIME())
    );

/* One send per person, kind and period: a restart mid-round never sends twice. */
IF OBJECT_ID(N'dash.DigestSend', N'U') IS NULL
    CREATE TABLE dash.DigestSend
    (
        Upn         NVARCHAR(256)   NOT NULL,
        Kind        VARCHAR(16)     NOT NULL,   -- Manager | Personal
        PeriodKey   VARCHAR(16)     NOT NULL,   -- 2026-W40, 2026-10
        SentUtc     DATETIME2(0)    NOT NULL CONSTRAINT DF_DashDigestSend_Sent DEFAULT (SYSUTCDATETIME()),
        Status      VARCHAR(16)     NOT NULL,
        CONSTRAINT PK_DashDigestSend PRIMARY KEY (Upn, Kind, PeriodKey)
    );

/* The license changes the dashboard exported for someone to apply, to measure what happened next. */
IF OBJECT_ID(N'dash.LicenseAction', N'U') IS NULL
    CREATE TABLE dash.LicenseAction
    (
        ActionId    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashLicenseAction PRIMARY KEY,
        Kind        VARCHAR(16)     NOT NULL,   -- upgrade | revoke | leaver | wave
        Upn         NVARCHAR(256)   NOT NULL,
        ExportedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_DashLicenseAction_Exported DEFAULT (SYSUTCDATETIME()),
        ExportedBy  NVARCHAR(256)   NOT NULL,
        INDEX IX_DashLicenseAction_Upn (Upn, ExportedUtc)
    );

/* License contracts, for the budget and renewal planner. */
IF OBJECT_ID(N'dash.LicenseContract', N'U') IS NULL
    CREATE TABLE dash.LicenseContract
    (
        ContractId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashLicenseContract PRIMARY KEY,
        Name          NVARCHAR(200)   NOT NULL,
        Seats         INT             NOT NULL CONSTRAINT CK_DashLicenseContract_Seats CHECK (Seats >= 0),
        MonthlyPrice  DECIMAL(12, 2)  NOT NULL,
        StartDate     DATE            NOT NULL,
        RenewalDate   DATE            NOT NULL,
        Note          NVARCHAR(400)   NULL,
        UpdatedBy     NVARCHAR(256)   NOT NULL,
        UpdatedUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_DashLicenseContract_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Each person's saved views; one may be their start page. */
IF OBJECT_ID(N'dash.SavedView', N'U') IS NULL
    CREATE TABLE dash.SavedView
    (
        ViewId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashSavedView PRIMARY KEY,
        Owner       NVARCHAR(256)   NOT NULL,   -- the viewer's UPN or Windows name
        Name        NVARCHAR(100)   NOT NULL,
        Url         NVARCHAR(600)   NOT NULL,   -- a path and query on this site
        IsStart     BIT             NOT NULL CONSTRAINT DF_DashSavedView_Start DEFAULT (0),
        CreatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSavedView_Created DEFAULT (SYSUTCDATETIME()),
        INDEX IX_DashSavedView_Owner (Owner)
    );

/* Job titles to job families, first matching rule wins (LIKE patterns). */
IF OBJECT_ID(N'dash.JobFamilyRule', N'U') IS NULL
    CREATE TABLE dash.JobFamilyRule
    (
        RuleId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashJobFamilyRule PRIMARY KEY,
        Pattern     NVARCHAR(200)   NOT NULL,   -- a LIKE pattern on bi.DimUser.JobTitle, such as %engineer%
        Family      NVARCHAR(100)   NOT NULL,
        Priority    INT             NOT NULL CONSTRAINT DF_DashJobFamilyRule_Priority DEFAULT (100),
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashJobFamilyRule_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Use cases from the app and what the interaction did, first matching rule wins. */
IF OBJECT_ID(N'dash.UseCaseRule', N'U') IS NULL
BEGIN
    CREATE TABLE dash.UseCaseRule
    (
        RuleId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashUseCaseRule PRIMARY KEY,
        AppPattern   NVARCHAR(200)   NOT NULL,   -- a LIKE pattern on bi.DimApp.App
        WebGrounded  BIT             NULL,       -- NULL: either
        WithAgent    BIT             NULL,       -- NULL: either
        UseCase      NVARCHAR(100)   NOT NULL,
        Priority     INT             NOT NULL CONSTRAINT DF_DashUseCaseRule_Priority DEFAULT (100),
        UpdatedBy    NVARCHAR(256)   NOT NULL,
        UpdatedUtc   DATETIME2(0)    NOT NULL CONSTRAINT DF_DashUseCaseRule_Updated DEFAULT (SYSUTCDATETIME())
    );
    INSERT INTO dash.UseCaseRule (AppPattern, WebGrounded, WithAgent, UseCase, Priority, UpdatedBy) VALUES
        (N'%', NULL, 1, N'Working with an agent', 10, N'default'),
        (N'%Teams%', NULL, NULL, N'Meetings and chats', 20, N'default'),
        (N'Outlook', NULL, NULL, N'Email', 20, N'default'),
        (N'Word', NULL, NULL, N'Writing documents', 20, N'default'),
        (N'Excel', NULL, NULL, N'Analyzing data', 20, N'default'),
        (N'PowerPoint', NULL, NULL, N'Presentations', 20, N'default'),
        (N'%Loop%', NULL, NULL, N'Collaborative notes', 20, N'default'),
        (N'%OneNote%', NULL, NULL, N'Collaborative notes', 20, N'default'),
        (N'%', 1, NULL, N'Research on the web', 50, N'default'),
        (N'%', NULL, NULL, N'Asking questions (Copilot Chat)', 999, N'default');
END;

/* 2.4.0: one retry policy. A scheduled digest or extract that fails is tried again an hour later, three tries in all,
   before it waits for its next period (as manager and personal digests do, through dash.DigestSend). */
IF COL_LENGTH(N'dash.Subscription', N'FailedTries') IS NULL
    ALTER TABLE dash.Subscription ADD FailedTries TINYINT NOT NULL CONSTRAINT DF_DashSubscription_Failed DEFAULT (0), RetryAfterUtc DATETIME2(0) NULL;
IF OBJECT_ID(N'dash.Extract', N'U') IS NOT NULL AND COL_LENGTH(N'dash.Extract', N'FailedTries') IS NULL
    ALTER TABLE dash.Extract ADD FailedTries TINYINT NOT NULL CONSTRAINT DF_DashExtract_Failed DEFAULT (0), RetryAfterUtc DATETIME2(0) NULL;

/* Markers on trend charts: a rollout wave, a training push, an outage, a policy change. */
IF OBJECT_ID(N'dash.EventMarker', N'U') IS NULL
    CREATE TABLE dash.EventMarker
    (
        MarkerId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashEventMarker PRIMARY KEY,
        Day         DATE            NOT NULL,
        Label       NVARCHAR(100)   NOT NULL,
        Kind        VARCHAR(16)     NOT NULL CONSTRAINT CK_DashEventMarker_Kind CHECK (Kind IN ('Rollout', 'Training', 'Outage', 'Policy', 'Other')),
        UpdatedBy   NVARCHAR(256)   NOT NULL,
        INDEX IX_DashEventMarker_Day (Day)
    );

/* Scheduled CSV extracts to a folder or share. */
IF OBJECT_ID(N'dash.Extract', N'U') IS NULL
    CREATE TABLE dash.Extract
    (
        ExtractId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashExtract PRIMARY KEY,
        Name        NVARCHAR(100)   NOT NULL,
        Dataset     VARCHAR(32)     NOT NULL,
        Folder      NVARCHAR(400)   NOT NULL,   -- a local path or \\server\share\folder the site's account may write
        Frequency   VARCHAR(8)      NOT NULL CONSTRAINT CK_DashExtract_Frequency CHECK (Frequency IN ('Daily', 'Weekly', 'Monthly')),
        DayOfWeek   TINYINT         NULL,
        DayOfMonth  TINYINT         NULL,
        HourLocal   TINYINT         NOT NULL CONSTRAINT DF_DashExtract_Hour DEFAULT (6),
        Days        INT             NOT NULL CONSTRAINT DF_DashExtract_Days DEFAULT (30),
        Enabled     BIT             NOT NULL CONSTRAINT DF_DashExtract_Enabled DEFAULT (1),
        LastRunUtc  DATETIME2(0)    NULL,
        LastStatus  NVARCHAR(400)   NULL,
        CreatedBy   NVARCHAR(256)   NOT NULL
    );

MERGE dash.Setting AS t
USING (VALUES
    ('AlertWebhookFormat',     N'Teams', N'How alerts are posted to the webhook (its address is stored encrypted): Teams (a Workflows or incoming-webhook URL) or Json (any endpoint).'),
    ('ManagerDigest',          N'Off',   N'Off, Weekly or Monthly: every manager with enough people under them gets their team''s leadership digest.'),
    ('ManagerDigestMinPeople', N'10',    N'Managers with at least this many people under them get the manager digest.'),
    ('ManagerDigestDay',       N'1',     N'The manager digest''s day: 0 Sunday to 6 Saturday (weekly), or 1 to 28 (monthly).'),
    ('DigestHour',             N'7',     N'The hour the manager and personal digests go out, in the reporting time zone.'),
    ('PersonalDigest',         N'On',    N'On lets people opt in to a weekly email of their own use (My Copilot > Email); Off hides the choice.'),
    ('CertificateWarnDays',    N'30',    N'Alert when the pipeline''s app certificate expires within this many days (and again at 14 and 3).'),
    ('RolloutWaveSize',        N'50',    N'The default number of people in a rollout wave.')
) AS src (Name, Value, Description)
ON t.Name = src.Name
WHEN MATCHED AND ISNULL(t.Description, N'') <> src.Description THEN UPDATE SET Description = src.Description
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (src.Name, src.Value, src.Description);

/* ---- Privacy and retention ------------------------------------------------ */

/* Privacy mode lives in audit.PipelineSetting, which bi.Users reads. Owned by dbo like the
   table, so the dashboard's account needs EXECUTE here and nothing on the audit schema. */
EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_SetPrivacyMode
    @Mode NVARCHAR(16), @By NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Mode NOT IN (N''Off'', N''Pseudonymize'') THROW 50002, N''@Mode is Off or Pseudonymize.'', 1;
    IF NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PseudonymSalt'')
        INSERT INTO audit.PipelineSetting (Name, Value, UpdatedBy) VALUES (N''PseudonymSalt'', CONVERT(NVARCHAR(64), CRYPT_GEN_RANDOM(32), 2), @By);
    UPDATE audit.PipelineSetting SET Value = @Mode, UpdatedBy = @By, UpdatedUtc = SYSUTCDATETIME() WHERE Name = N''PrivacyMode'';
    IF @@ROWCOUNT = 0 INSERT INTO audit.PipelineSetting (Name, Value, UpdatedBy) VALUES (N''PrivacyMode'', @Mode, @By);
END;');

EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_ApplyRetention
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @audit INT = TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''AuditLogRetentionDays'') AS INT),
            @notes INT = TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''NotificationRetentionDays'') AS INT),
            @json  INT = TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''JsonRetentionDays'') AS INT),
            @a INT = 0, @n INT = 0, @j NVARCHAR(200) = N''JSON kept'';
    IF @audit > 0
    BEGIN
        DELETE FROM dash.AuditLog WHERE AtUtc < DATEADD(DAY, -@audit, SYSUTCDATETIME());
        SET @a = @@ROWCOUNT;
    END;
    IF @notes > 0
    BEGIN
        DELETE FROM dash.NotificationLog WHERE AtUtc < DATEADD(DAY, -@notes, SYSUTCDATETIME());
        SET @n = @@ROWCOUNT;
    END;
    /* Past Purview''s own 180 days the JSON cannot be fetched again, so never less than 200. */
    IF @json >= 200 AND OBJECT_ID(N''audit.usp_PurgeCopilotAuditJson'', N''P'') IS NOT NULL
    BEGIN
        EXEC audit.usp_PurgeCopilotAuditJson @RetainDays = @json, @WhatIf = 0;
        SET @j = CONCAT(N''JSON cleared past '', @json, N'' days'');
    END;
    SELECT CONCAT(@a, N'' audit-log row(s) and '', @n, N'' notification row(s) past retention deleted; '', @j, N''.'');
END;');

/* ---- The dashboard's database role ---------------------------------------- */

IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NULL
    CREATE ROLE CopilotInsightsApp AUTHORIZATION dbo;
GRANT SELECT ON SCHEMA::bi TO CopilotInsightsApp;
GRANT SELECT ON SCHEMA::dash TO CopilotInsightsApp;
DENY SELECT ON dash.Secret TO CopilotInsightsApp;   -- read only through dash.usp_GetSecret
GRANT INSERT, UPDATE, DELETE ON dash.Setting TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.RoleGrant TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.AgentAlias TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.SensitivityLabel TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Subscription TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.AlertState TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Target TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.TimeSaving TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.TrainingAttendance TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.ValueName TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.FlagCase TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.DigestPreference TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.DigestSend TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.LicenseAction TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.LicenseContract TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.SavedView TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.JobFamilyRule TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.UseCaseRule TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.EventMarker TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Extract TO CopilotInsightsApp;
GRANT INSERT ON dash.AuditLog TO CopilotInsightsApp;
GRANT INSERT ON dash.NotificationLog TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_RefreshDashboard TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_SetSecret TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_GetSecret TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_SetPrivacyMode TO CopilotInsightsApp;
GRANT EXECUTE ON dash.usp_ApplyRetention TO CopilotInsightsApp;
GRANT EXECUTE ON dash.fnValueName TO CopilotInsightsApp;
/* Opening the secrets key checks the caller's own rights on it; ownership chaining does not cover keys. */
GRANT VIEW DEFINITION ON SYMMETRIC KEY::CopilotInsightsSecretKey TO CopilotInsightsApp;
GRANT CONTROL ON CERTIFICATE::CopilotInsightsSecretCert TO CopilotInsightsApp;
IF OBJECT_ID(N'audit.SessionAppCategory', N'U') IS NOT NULL
    GRANT SELECT, INSERT, UPDATE ON audit.SessionAppCategory TO CopilotInsightsApp;
/* The pipeline tables Admin > Health, the license reports and the value catalog read. */
DECLARE @t SYSNAME, @g NVARCHAR(400);
DECLARE t CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM (VALUES (N'CopilotLoad'), (N'CopilotCsvImport'), (N'CopilotSessionRun'), (N'UserDirectory'), (N'PipelineRun'),
                             (N'PipelineRunStep'), (N'PipelineSetting'), (N'LicenseSku'), (N'LicenseSkuDay'), (N'CopilotLicense'),
                             (N'ValueCatalog'), (N'ValueCatalogColumn'), (N'CopilotLoadWindow'), (N'CopilotBackfillWindow')) AS v (name)
    WHERE OBJECT_ID(N'audit.' + name, N'U') IS NOT NULL;
OPEN t;
FETCH NEXT FROM t INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @g = N'GRANT SELECT ON audit.' + QUOTENAME(@t) + N' TO CopilotInsightsApp;';
    EXEC (@g);
    FETCH NEXT FROM t INTO @t;
END;
CLOSE t;
DEALLOCATE t;
IF OBJECT_ID(N'audit.ReportingTimeZone', N'FN') IS NOT NULL
    GRANT EXECUTE ON audit.ReportingTimeZone TO CopilotInsightsApp;

/* ---- For Power BI ----------------------------------------------------------- */

/*
    Row-level security from the org chart: one row per (viewer, person they may
    see). A manager sees everyone under them; the top of the organization, the
    people reporting to them and anyone granted Organization see everyone. The
    Power BI model in powerbi/ uses it in its Organization role:
    Users[UserKey] IN the viewer's rows, by USERPRINCIPALNAME(). Organization
    granted to an AD group counts for its members too (2.4.0): the nightly
    directory load expands the group into audit.GroupMember.

    Empty in privacy mode: each row pairs a real sign-in with a user key, which
    would undo the pseudonyms in bi.Users. With privacy mode on, share
    bi.DepartmentWeek rather than the person-level model.
*/
IF OBJECT_ID(N'audit.GroupMember', N'U') IS NULL
    CREATE TABLE audit.GroupMember (GroupName NVARCHAR(256) NOT NULL, UserPrincipalName NVARCHAR(256) NOT NULL,
        LoadedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_GroupMember_Loaded DEFAULT (SYSUTCDATETIME()), CONSTRAINT PK_GroupMember PRIMARY KEY (GroupName, UserPrincipalName));
EXEC (N'CREATE OR ALTER VIEW bi.OrgSecurity AS
WITH whole AS (   -- everyone who sees the whole organization: the top two levels, and Organization granted to them or to a group they are in
    SELECT Upn FROM dash.Person WHERE Level IN (1, 2)
    UNION SELECT Principal FROM dash.RoleGrant WHERE PrincipalType = ''User'' AND Role = ''Organization''
    UNION SELECT gm.UserPrincipalName FROM audit.GroupMember AS gm
          JOIN dash.RoleGrant AS g ON g.Principal = gm.GroupName AND g.PrincipalType = ''Group'' AND g.Role = ''Organization''
)
SELECT oc.AncestorUpn AS ViewerUpn, u.UserKey
FROM dash.OrgClosure AS oc
JOIN bi.DimUser AS u ON u.UserPrincipalName = oc.DescendantUpn
WHERE NOT EXISTS (SELECT 1 FROM whole WHERE whole.Upn = oc.AncestorUpn)
  AND NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'')
UNION ALL
SELECT w.Upn, u.UserKey
FROM whole AS w
CROSS JOIN bi.DimUser AS u
WHERE NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'');');

/* Readable names for the cataloged codes; Name is NULL until someone names it. */
EXEC (N'CREATE OR ALTER VIEW bi.ValueNames AS
SELECT c.TableName, c.ColumnName, c.Label AS ColumnLabel, v.Value,
       COALESCE(n.Name, CASE WHEN c.ColumnId IN (11, 30) THEN sl.Name END) AS Name,
       v.Records, v.FirstSeenUtc, v.LastSeenUtc
FROM audit.ValueCatalog AS v
JOIN audit.ValueCatalogColumn AS c ON c.ColumnId = v.ColumnId
LEFT JOIN dash.ValueName AS n ON n.ColumnId = v.ColumnId AND n.Value = v.Value
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = v.Value;');

/* Seats bought and assigned, per SKU per day. */
IF OBJECT_ID(N'audit.LicenseSkuDay', N'U') IS NOT NULL
    EXEC (N'CREATE OR ALTER VIEW bi.LicensePool AS
SELECT SnapshotDate, SkuPartNumber, IsCopilot, Enabled + Warning AS Bought, Consumed AS Assigned,
       Enabled + Warning - Consumed AS Available, Warning AS InGracePeriod, Suspended
FROM audit.LicenseSkuDay;');

/* 2.3.0: the dashboard's classifications for Power BI. Every combination of app, web search and agent
   with the use case the rules in dash.UseCaseRule give it (bi.Interactions.UseCaseKey points here). */
EXEC (N'CREATE OR ALTER VIEW bi.UseCases AS
SELECT a.AppKey * 4 + w.Web * 2 + g.WithAgent AS UseCaseKey, a.App, CAST(w.Web AS BIT) AS WebGrounded, CAST(g.WithAgent AS BIT) AS WithAgent,
       ISNULL(r.UseCase, N''Other'') AS UseCase
FROM bi.DimApp AS a
CROSS JOIN (VALUES (0), (1)) AS w (Web)
CROSS JOIN (VALUES (0), (1)) AS g (WithAgent)
OUTER APPLY (SELECT TOP (1) ur.UseCase FROM dash.UseCaseRule AS ur
             WHERE a.App LIKE ur.AppPattern AND (ur.WebGrounded IS NULL OR ur.WebGrounded = w.Web) AND (ur.WithAgent IS NULL OR ur.WithAgent = g.WithAgent)
             ORDER BY ur.Priority, ur.RuleId) AS r;');

/* Each person's job family from dash.JobFamilyRule (first matching pattern by priority). */
EXEC (N'CREATE OR ALTER VIEW bi.JobFamilies AS
SELECT u.UserKey, ISNULL(r.Family, N''(no family)'') AS JobFamily
FROM bi.DimUser AS u
OUTER APPLY (SELECT TOP (1) jr.Family FROM dash.JobFamilyRule AS jr WHERE u.JobTitle LIKE jr.Pattern ORDER BY jr.Priority, jr.RuleId) AS r;');

/* Events marked on the dashboard's trends (a rollout wave, a training push, an outage), for chart annotations. */
EXEC (N'CREATE OR ALTER VIEW bi.EventMarkers AS
SELECT Day, YEAR(Day) * 10000 + MONTH(Day) * 100 + DAY(Day) AS DateKey, Label, Kind FROM dash.EventMarker;');

/* Weekly figures by department with any group under the minimum size hidden,
   for a model that must not show individuals (a works council''s condition). */
EXEC (N'CREATE OR ALTER VIEW bi.DepartmentWeek AS
WITH w AS
(
    SELECT d.WeekEnding, ISNULL(NULLIF(u.Department, N''''), N''(no department)'') AS Department,
           COUNT(DISTINCT ud.UserKey) AS Active, SUM(CAST(ud.Interactions AS BIGINT)) AS Interactions,
           SUM(CAST(ud.Sessions AS BIGINT)) AS Sessions
    FROM dash.UserDay AS ud
    JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
    JOIN bi.DimUser AS u ON u.UserKey = ud.UserKey
    WHERE ud.Interactions > 0
    GROUP BY d.WeekEnding, ISNULL(NULLIF(u.Department, N''''), N''(no department)'')
),
m AS (SELECT ISNULL(TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''MinGroupSize'') AS INT), 5) AS MinGroup)
SELECT w.WeekEnding, w.Department,
       CASE WHEN w.Active >= m.MinGroup THEN w.Active END AS ActivePeople,
       CASE WHEN w.Active >= m.MinGroup THEN w.Interactions END AS Interactions,
       CASE WHEN w.Active >= m.MinGroup THEN w.Sessions END AS Sessions,
       CAST(CASE WHEN w.Active < m.MinGroup THEN 1 ELSE 0 END AS BIT) AS Hidden
FROM w CROSS JOIN m;');
