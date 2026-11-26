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
      dash.Reservation, dash.ReservationRate, dash.TechnologyAllocation,
      dash.ReservationSnapshot, dash.fnReservationMember,
      dash.usp_TakeReservationSnapshot
                        Copilot reservations, their rate, the Technology
                        pool, and each month's position kept as it was
      dash.CreditRoute, dash.CreditChargeSnapshot, dash.CreditCommitment,
      dash.usp_TakeCreditChargeSnapshot
                        Copilot Credit chargeback by line of business,
                        its month-end snapshots, and the credit commitment (P3)
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
    ('MinutesSavedDefault',     N'6',     N'Minutes one interaction saves, for apps without their own figure (Admin > Targets and assumptions).'),
    ('HourlyCost',              N'72',    N'What an hour of an employee''s time is worth, in LicenseCurrency, for the value report.'),
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
    instance, the secrets cannot be read: run this again, then enter them again
    in Admin > Email and Admin > Alerts and digests.

    On another instance neither the service master key nor the
    password opens the master key, so the secrets could be neither read nor
    stored again, and re-running this kept the keys that could not be opened.
    Now a chain that cannot be opened is made again, and the secrets it can no
    longer read are removed. It is left as it is when the master key protects
    anything else in the database, which only its owner can recover. */
IF CERT_ID(N'CopilotInsightsSecretCert') IS NOT NULL AND KEY_ID(N'CopilotInsightsSecretKey') IS NOT NULL
BEGIN
    BEGIN TRY
        OPEN SYMMETRIC KEY CopilotInsightsSecretKey DECRYPTION BY CERTIFICATE CopilotInsightsSecretCert;
        CLOSE SYMMETRIC KEY CopilotInsightsSecretKey;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() NOT IN (15581, 15329, 15466)
            THROW;
        IF EXISTS (SELECT 1 FROM sys.certificates WHERE pvt_key_encryption_type = 'MK' AND name <> N'CopilotInsightsSecretCert')
           OR EXISTS (SELECT 1 FROM sys.asymmetric_keys WHERE pvt_key_encryption_type = 'MK')
           OR EXISTS (SELECT 1 FROM sys.database_scoped_credentials)
            RAISERROR('The stored secrets cannot be read on this SQL Server instance, and its database master key protects other keys too, so it is left as it is: open the master key with its password, or restore the original instance''s service master key, then run this again.', 0, 1) WITH NOWAIT;
        ELSE
        BEGIN
            IF OBJECT_ID(N'dash.Secret', N'U') IS NOT NULL EXEC (N'DELETE FROM dash.Secret;');
            EXEC (N'DROP SYMMETRIC KEY CopilotInsightsSecretKey;');
            EXEC (N'DROP CERTIFICATE CopilotInsightsSecretCert;');
            EXEC (N'DROP MASTER KEY;');
            RAISERROR('The stored secrets were encrypted on another SQL Server instance and cannot be read here: their keys are made again. Enter the email password or client secret, and the webhook address, again in Admin.', 0, 1) WITH NOWAIT;
        END;
    END CATCH;
END;
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

/* What was said between the dashboard and the mail server for a message, for troubleshooting on
   Admin > Email: where it connected and how, each command and the server's answer, and how it ended. Kept
   for every test message and every message that was not sent; never the password, nor the message itself. */
IF COL_LENGTH(N'dash.NotificationLog', N'Detail') IS NULL
    ALTER TABLE dash.NotificationLog ADD Detail NVARCHAR(MAX) NULL;

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

/* Names for the app codes Copilot Chat records (column 2, the app), which pages showed as they come:
   BizChat, Office, OfficeCopilotSearchAnswer. Only where an administrator has not named them. */
MERGE dash.ValueName AS t
USING (VALUES
    (CAST(2 AS SMALLINT), N'BizChat',                   N'Copilot Chat'),
    (CAST(2 AS SMALLINT), N'Office',                    N'Microsoft 365 app'),
    (CAST(2 AS SMALLINT), N'OfficeCopilotSearchAnswer', N'Copilot search answers'),
    (CAST(2 AS SMALLINT), N'Edge',                      N'Edge')
) AS s (ColumnId, Value, Name)
ON t.ColumnId = s.ColumnId AND t.Value = s.Value
WHEN NOT MATCHED THEN INSERT (ColumnId, Value, Name, Note, UpdatedBy) VALUES (s.ColumnId, s.Value, s.Name, N'Named by the install (2.7.1).', N'install');

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

/* Managers who do not get their team digest, kept by administrators (Admin > Alerts and digests). PersonalDigest is unused since 2.4.4. */
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

/* Accounts a non-human rule matches that an administrator confirmed are a person, with the reason: a Copilot license
   on a non-human account is a policy breach, and these are not. One row per sign-in; removing it flags the account again. */
IF OBJECT_ID(N'dash.NonHumanException', N'U') IS NULL
    CREATE TABLE dash.NonHumanException
    (
        Upn          NVARCHAR(256)   NOT NULL CONSTRAINT PK_DashNonHumanException PRIMARY KEY,
        Reason       NVARCHAR(400)   NOT NULL,
        ConfirmedBy  NVARCHAR(256)   NOT NULL,
        ConfirmedUtc DATETIME2(0)    NOT NULL CONSTRAINT DF_DashNonHumanException_Confirmed DEFAULT (SYSUTCDATETIME())
    );

/* ---- Copilot reservations -------------------------------------------- */

/* The reservation register: the Microsoft 365 Copilot seats each line of business, leader or group committed to, by
   persistent chargeback organization (OrgId). A row is effective from one date to another (NULL: open), so a change is
   a new row and the old one keeps its dates. The people a row covers are everyone under its leader in the org chart
   (dash.fnReservationMember). Entered on Leadership > Copilot reservations by an administrator. */
IF OBJECT_ID(N'dash.Reservation', N'U') IS NULL
    CREATE TABLE dash.Reservation
    (
        ReservationId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DashReservation PRIMARY KEY,
        OrgId         NVARCHAR(64)    NOT NULL,   -- the persistent chargeback organization ID
        Name          NVARCHAR(200)   NOT NULL,
        LeaderUpn     NVARCHAR(256)   NULL,       -- whose people it covers; NULL: a pool with no one under it
        CostCenter    NVARCHAR(100)   NULL,
        Reserved      INT             NOT NULL CONSTRAINT CK_DashReservation_Reserved CHECK (Reserved >= 0),
        EffectiveFrom DATE            NOT NULL,
        EffectiveTo   DATE            NULL,
        Note          NVARCHAR(400)   NULL,
        UpdatedBy     NVARCHAR(256)   NOT NULL,
        UpdatedUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_DashReservation_Updated DEFAULT (SYSUTCDATETIME()),
        INDEX IX_DashReservation_Org (OrgId, EffectiveFrom)
    );

/* The rate a reserved seat is charged a month, effective from a date (Finance's, after any discount). No row: the
   page shows Pending Finance Decision rather than a figure. */
IF OBJECT_ID(N'dash.ReservationRate', N'U') IS NULL
    CREATE TABLE dash.ReservationRate
    (
        EffectiveFrom DATE            NOT NULL CONSTRAINT PK_DashReservationRate PRIMARY KEY,
        MonthlyRate   DECIMAL(12, 2)  NOT NULL CONSTRAINT CK_DashReservationRate_Rate CHECK (MonthlyRate >= 0),
        Currency      NVARCHAR(8)     NULL,       -- NULL: LicenseCurrency
        Note          NVARCHAR(400)   NULL,
        UpdatedBy     NVARCHAR(256)   NOT NULL,
        UpdatedUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_DashReservationRate_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Technology-funded allocations: approved executive, service, test, shared, migration and exception accounts whose
   license is charged to the Technology pool, never to a line of business. One row per account. */
IF OBJECT_ID(N'dash.TechnologyAllocation', N'U') IS NULL
    CREATE TABLE dash.TechnologyAllocation
    (
        Upn           NVARCHAR(256)   NOT NULL CONSTRAINT PK_DashTechnologyAllocation PRIMARY KEY,
        Category      NVARCHAR(40)    NOT NULL,   -- Executive | Service | Test | Shared | Migration | Exception
        Owner         NVARCHAR(256)   NOT NULL,
        ApprovalRef   NVARCHAR(200)   NULL,
        EffectiveFrom DATE            NOT NULL,
        ReviewDate    DATE            NULL,
        Note          NVARCHAR(400)   NULL,
        UpdatedBy     NVARCHAR(256)   NOT NULL,
        UpdatedUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_DashTechnologyAllocation_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Each month's position, kept as it was: one row per organization (and ~unmapped, ~technology) at the last day of a
   month, written once by dash.usp_TakeReservationSnapshot and never rewritten. The inputs are stored (reserved,
   allocated, rate); the dashboard works out unused, over, billable and the obligations from them, one way. */
IF OBJECT_ID(N'dash.ReservationSnapshot', N'U') IS NULL
    CREATE TABLE dash.ReservationSnapshot
    (
        SnapshotDate  DATE            NOT NULL,
        OrgId         NVARCHAR(64)    NOT NULL,   -- ~unmapped: licensed, under no leader; ~technology: the Technology pool
        Name          NVARCHAR(200)   NOT NULL,
        LeaderUpn     NVARCHAR(256)   NULL,
        CostCenter    NVARCHAR(100)   NULL,
        Reserved      INT             NOT NULL,
        Allocated     INT             NOT NULL,
        MonthlyRate   DECIMAL(12, 2)  NULL,       -- NULL: no rate was in force
        State         VARCHAR(16)     NOT NULL CONSTRAINT DF_DashReservationSnapshot_State DEFAULT ('Operational'),
        TakenUtc      DATETIME2(0)    NOT NULL CONSTRAINT DF_DashReservationSnapshot_Taken DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashReservationSnapshot PRIMARY KEY (SnapshotDate, OrgId)
    );

/* The people each reservation covers on a date: everyone under its leader in the org chart, the leader included.
   Under two listed leaders (one reporting to the other), a person counts with the nearer one, so nobody is in two
   organizations; the same leader on two rows falls to the lower ReservationId (the page lists it as an exception). */
EXEC (N'
CREATE OR ALTER FUNCTION dash.fnReservationMember (@AsOf DATE)
RETURNS TABLE
AS
RETURN
WITH r AS (SELECT ReservationId, LeaderUpn FROM dash.Reservation
           WHERE LeaderUpn IS NOT NULL AND EffectiveFrom <= @AsOf AND (EffectiveTo IS NULL OR EffectiveTo >= @AsOf)),
m AS (SELECT r.ReservationId, c.DescendantUpn,
             ROW_NUMBER() OVER (PARTITION BY c.DescendantUpn ORDER BY c.Depth, r.ReservationId) AS n
      FROM r JOIN dash.OrgClosure AS c ON c.AncestorUpn = r.LeaderUpn)
SELECT m.ReservationId, u.UserKey, u.UserPrincipalName AS Upn
FROM m JOIN bi.DimUser AS u ON u.UserPrincipalName = m.DescendantUpn
WHERE m.n = 1;');

/* The month-end snapshot: by default the last day of the month before the latest licensed day loaded, once. Allocated
   is the Microsoft 365 Copilot add-on held that day (bi.FactLicensedDay, less Copilot held only inside a suite), Technology
   accounts counted in their own pool. The org chart is today's. A snapshot already taken is left as it is.
   Taken only once the licenses reach two days past the month end (a license event can reach Purview a day
   late, and the month end could not be taken again), only when a reservation is in effect that day (a register entered
   ahead kept snapshots of nobody but the pools, for good), and in one transaction (a timeout between its two inserts kept
   the organizations without the pools, and the snapshot could never be finished). */
EXEC (N'
CREATE OR ALTER PROCEDURE dash.usp_TakeReservationSnapshot
    @AsOf DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    /* With the Workbench''s lines of business as the seat record, the register is kept as it is and nothing is added to it. */
    IF EXISTS (SELECT 1 FROM dash.Setting WHERE Name = N''SeatModel'' AND Value = N''Governance'') RETURN;
    DECLARE @last INT = (SELECT MAX(DateKey) FROM bi.FactLicensedDay);
    IF @last IS NULL RETURN;
    DECLARE @lastDay DATE = CONVERT(DATE, CAST(@last AS CHAR(8)), 112);
    IF @AsOf IS NULL SET @AsOf = EOMONTH(@lastDay, -1);
    DECLARE @k INT = YEAR(@AsOf) * 10000 + MONTH(@AsOf) * 100 + DAY(@AsOf);
    IF @AsOf > @lastDay OR NOT EXISTS (SELECT 1 FROM bi.FactLicensedDay WHERE DateKey = @k) RETURN;
    IF @lastDay < DATEADD(DAY, 2, @AsOf) RETURN;
    IF NOT EXISTS (SELECT 1 FROM dash.Reservation WHERE EffectiveFrom <= @AsOf AND (EffectiveTo IS NULL OR EffectiveTo >= @AsOf)) RETURN;
    IF EXISTS (SELECT 1 FROM dash.ReservationSnapshot WHERE SnapshotDate = @AsOf) RETURN;
    DECLARE @rate DECIMAL(12, 2) = (SELECT TOP (1) MonthlyRate FROM dash.ReservationRate WHERE EffectiveFrom <= @AsOf ORDER BY EffectiveFrom DESC);

    SELECT u.UserKey, u.UserPrincipalName AS Upn,
           CAST(CASE WHEN t.Upn IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS Tech
    INTO #held
    FROM bi.FactLicensedDay AS ld
    JOIN bi.DimUser AS u ON u.UserKey = ld.UserKey
    LEFT JOIN dash.TechnologyAllocation AS t ON t.Upn = u.UserPrincipalName AND t.EffectiveFrom <= @AsOf
    WHERE ld.DateKey = @k
      AND NOT EXISTS (SELECT 1 FROM dash.CopilotSuiteOnly AS so WHERE so.Upn = u.UserPrincipalName);

    BEGIN TRANSACTION;
    INSERT INTO dash.ReservationSnapshot (SnapshotDate, OrgId, Name, LeaderUpn, CostCenter, Reserved, Allocated, MonthlyRate)
    SELECT @AsOf, r.OrgId, MAX(r.Name), NULLIF(MAX(ISNULL(r.LeaderUpn, N'''')), N''''), NULLIF(MAX(ISNULL(r.CostCenter, N'''')), N''''), SUM(r.Reserved),
           ISNULL((SELECT COUNT(*) FROM dash.fnReservationMember(@AsOf) AS m JOIN #held AS h ON h.UserKey = m.UserKey AND h.Tech = 0
                   JOIN dash.Reservation AS r2 ON r2.ReservationId = m.ReservationId WHERE r2.OrgId = r.OrgId), 0),
           @rate
    FROM dash.Reservation AS r
    WHERE r.EffectiveFrom <= @AsOf AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= @AsOf)
    GROUP BY r.OrgId;

    INSERT INTO dash.ReservationSnapshot (SnapshotDate, OrgId, Name, Reserved, Allocated, MonthlyRate)
    SELECT @AsOf, N''~unmapped'', N''Under no listed leader'', 0,
           (SELECT COUNT(*) FROM #held AS h WHERE h.Tech = 0 AND NOT EXISTS (SELECT 1 FROM dash.fnReservationMember(@AsOf) AS m WHERE m.UserKey = h.UserKey)), @rate
    UNION ALL
    SELECT @AsOf, N''~technology'', N''Technology-funded pool'', 0, (SELECT COUNT(*) FROM #held WHERE Tech = 1), @rate;
    COMMIT TRANSACTION;
END;');

/*
    Copilot Credit chargeback by line of business, and the Copilot Credit commitment (P3).
      dash.CreditRoute          each Power Platform environment's path: LoB Productivity, LoB Business Application
                                (both charged to a line of business, OrgId as in dash.Reservation), Enterprise Shared
                                (enterprise-funded, never charged to a line of business) or Innovation (a sponsor's,
                                where it names one), with its cost center, financial owner and billing policy, from a
                                date. A change is a new row from its date, as in the reservation register.
      dash.CreditChargeSnapshot each month's credits by environment and route, kept once and never rewritten, at the
                                charge rate in force: Finance can reproduce any month.
      dash.CreditCommitment     a prepaid commitment (the Copilot Credit Pre-Purchase Plan): its amount, what one
                                credit draws from it, its term and its Azure scope.
    The credits are audit.PowerPlatformCreditDay's (the Power Platform admin center's consumption report, Copilot
    Studio's credits only); an environment is matched to its route by name, as the report names it.
*/
IF OBJECT_ID(N'dash.CreditRoute', N'U') IS NULL
    CREATE TABLE dash.CreditRoute
    (
        RouteId          INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashCreditRoute PRIMARY KEY,
        EnvironmentName  NVARCHAR(200)  NOT NULL,
        Path             VARCHAR(24)    NOT NULL CONSTRAINT CK_DashCreditRoute_Path CHECK (Path IN ('Productivity', 'BusinessApplication', 'EnterpriseShared', 'Innovation')),
        OrgId            NVARCHAR(64)   NULL,   -- the line of business charged (dash.Reservation.OrgId); NULL for Enterprise Shared
        CostCenter       NVARCHAR(100)  NULL,
        FinancialOwner   NVARCHAR(256)  NULL,
        BillingPolicy    NVARCHAR(200)  NULL,   -- the Power Platform billing policy the environment is linked to, as a label
        EffectiveFrom    DATE           NOT NULL,
        EffectiveTo      DATE           NULL,
        Note             NVARCHAR(400)  NULL,
        UpdatedBy        NVARCHAR(256)  NULL,
        UpdatedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_DashCreditRoute_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_DashCreditRoute_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom),
        CONSTRAINT CK_DashCreditRoute_Org CHECK (Path NOT IN ('Productivity', 'BusinessApplication') OR OrgId IS NOT NULL),
        INDEX IX_DashCreditRoute_Env (EnvironmentName, EffectiveFrom)
    );

IF OBJECT_ID(N'dash.CreditChargeSnapshot', N'U') IS NULL
    CREATE TABLE dash.CreditChargeSnapshot
    (
        SnapshotId       INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashCreditChargeSnapshot PRIMARY KEY,
        SnapshotMonth    DATE           NOT NULL,   -- the month's first day
        EnvironmentName  NVARCHAR(200)  NOT NULL,
        Path             VARCHAR(24)    NOT NULL,   -- as dash.CreditRoute, or Unmapped: no route in effect
        OrgId            NVARCHAR(64)   NULL,
        OrgName          NVARCHAR(200)  NULL,
        CostCenter       NVARCHAR(100)  NULL,
        Credits          DECIMAL(19, 2) NOT NULL,
        PaygCredits      DECIMAL(19, 2) NOT NULL,
        PaygCost         DECIMAL(19, 4) NULL,       -- what pay-as-you-go billed, as the report gives it
        Currency         NVARCHAR(8)    NULL,
        ChargeRate       DECIMAL(12, 6) NULL,       -- what one credit was charged back at; NULL: no rate in force
        TakenUtc         DATETIME2(0)   NOT NULL CONSTRAINT DF_DashCreditChargeSnapshot_Taken DEFAULT (SYSUTCDATETIME()),
        INDEX IX_DashCreditChargeSnapshot_Month (SnapshotMonth)
    );

IF OBJECT_ID(N'dash.CreditCommitment', N'U') IS NULL
    CREATE TABLE dash.CreditCommitment
    (
        CommitmentId     INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashCreditCommitment PRIMARY KEY,
        Name             NVARCHAR(200)  NOT NULL,
        Amount           DECIMAL(19, 2) NOT NULL,   -- the commitment, in its currency
        Currency         NVARCHAR(8)    NULL,       -- NULL: LicenseCurrency
        DrawRate         DECIMAL(12, 6) NOT NULL CONSTRAINT DF_DashCreditCommitment_Draw DEFAULT (0.01),   -- what one credit draws from it
        StartDate        DATE           NOT NULL,
        EndDate          DATE           NOT NULL,
        Scope            NVARCHAR(200)  NULL,       -- the Azure scope it applies to, as a label
        Note             NVARCHAR(400)  NULL,
        UpdatedBy        NVARCHAR(256)  NULL,
        UpdatedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_DashCreditCommitment_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_DashCreditCommitment_Term CHECK (EndDate > StartDate),
        CONSTRAINT CK_DashCreditCommitment_Amount CHECK (Amount > 0 AND DrawRate > 0)
    );

MERGE dash.Setting AS t
USING (VALUES
    ('CreditChargeRate',          CAST(NULL AS NVARCHAR(400)), N'2.7.2: what one Copilot Credit is charged back to a line of business at, in LicenseCurrency. Empty: the pay-as-you-go credit price in the price list.'),
    ('CreditCommitmentAlertDays', N'30', N'2.7.2: alert when the Copilot Credit commitment is projected to run out more than this many days before its term ends, or to leave this many days'' worth unused; 0 turns it off.')
) AS s (Name, Value, Description)
ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy) VALUES (s.Name, s.Value, s.Description, N'install');

/*
    Model tokens by business unit, spending limits, and agent policies.
      dash.TokenRoute          each value the AI gateway stamps as the business unit, to a unit: an L2 executive (level 2
                               of the org chart) and, within it, an L3 executive (level 3; NULL for the L2's whole unit),
                               from a date. A value that is a person's sign-in needs no route: the org chart gives it.
      dash.SpendLimit          each spending limit the dashboard tracks: the ones read at their source (matched by Source
                               and ExternalId, so a reload keeps what an administrator set here) and the ones entered;
                               the unit it belongs to, when to warn, hidden or not. A limit no longer at its source keeps
                               its row, with GoneUtc set.
      dash.SpendLimitSnapshot  each limit's position at the end of a period (limit and spent), kept once.
      dash.AgentPolicyRule     the agent rules checked each night (no owner, unused, costly for few people, shared and
                               not certified): a threshold and what to propose. Off until turned on.
      dash.AgentAction         each proposed action on an agent (notify its owner, quarantine, release, reassign its
                               owner): who decided, when it ran, and the result. Never a deletion.
*/
IF OBJECT_ID(N'dash.TokenRoute', N'U') IS NULL
    CREATE TABLE dash.TokenRoute
    (
        RouteId          INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashTokenRoute PRIMARY KEY,
        GatewayValue     NVARCHAR(256)  NOT NULL,   -- audit.GatewayTokenDay.BusinessUnit
        L2Upn            NVARCHAR(256)  NOT NULL,   -- the L2 executive whose unit it is
        L3Upn            NVARCHAR(256)  NULL,       -- the L3 executive within it; NULL: the L2's whole unit
        EffectiveFrom    DATE           NOT NULL,
        EffectiveTo      DATE           NULL,
        Note             NVARCHAR(400)  NULL,
        UpdatedBy        NVARCHAR(256)  NULL,
        UpdatedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_DashTokenRoute_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_DashTokenRoute_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom),
        INDEX IX_DashTokenRoute_Value (GatewayValue, EffectiveFrom)
    );

IF OBJECT_ID(N'dash.SpendLimit', N'U') IS NULL
    CREATE TABLE dash.SpendLimit
    (
        LimitId          INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashSpendLimit PRIMARY KEY,
        Source           VARCHAR(16)    NOT NULL,   -- Graph | GitHub | Gateway | Entered
        ExternalId       NVARCHAR(400)  NULL,       -- audit.SpendLimitSource's; NULL for an entered limit
        Kind             VARCHAR(24)    NOT NULL,   -- as audit.SpendLimitSource
        Name             NVARCHAR(400)  NOT NULL,
        ScopeType        VARCHAR(24)    NULL,
        Scope            NVARCHAR(1000) NULL,
        Unit             VARCHAR(8)     NOT NULL,   -- Credits | Tokens | USD
        Amount           DECIMAL(19, 4) NULL,
        Period           VARCHAR(16)    NOT NULL CONSTRAINT DF_DashSpendLimit_Period DEFAULT ('Monthly'),
        PerUser          BIT            NOT NULL CONSTRAINT DF_DashSpendLimit_PerUser DEFAULT (0),
        L2Upn            NVARCHAR(256)  NULL,       -- the unit it belongs to: its L2 executive
        L3Upn            NVARCHAR(256)  NULL,       -- and L3 executive; NULL: the L2's whole unit
        WarnAtPercent    INT            NULL,       -- NULL: warn only when it is projected to be reached
        Hidden           BIT            NOT NULL CONSTRAINT DF_DashSpendLimit_Hidden DEFAULT (0),
        GoneUtc          DATETIME2(0)   NULL,       -- no longer at its source since
        Note             NVARCHAR(400)  NULL,
        UpdatedBy        NVARCHAR(256)  NULL,
        UpdatedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_DashSpendLimit_Updated DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_DashSpendLimit_Warn CHECK (WarnAtPercent IS NULL OR WarnAtPercent BETWEEN 1 AND 100)
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dash.SpendLimit') AND name = N'UX_DashSpendLimit_Source')
    EXEC (N'CREATE UNIQUE INDEX UX_DashSpendLimit_Source ON dash.SpendLimit (Source, ExternalId) WHERE ExternalId IS NOT NULL;');

IF OBJECT_ID(N'dash.SpendLimitSnapshot', N'U') IS NULL
    CREATE TABLE dash.SpendLimitSnapshot
    (
        LimitId          INT            NOT NULL,
        PeriodStart      DATE           NOT NULL,
        PeriodEnd        DATE           NOT NULL,
        Amount           DECIMAL(19, 4) NULL,
        Spent            DECIMAL(19, 4) NULL,
        TakenUtc         DATETIME2(0)   NOT NULL CONSTRAINT DF_DashSpendLimitSnapshot_Taken DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashSpendLimitSnapshot PRIMARY KEY (LimitId, PeriodStart)
    );

IF OBJECT_ID(N'dash.AgentPolicyRule', N'U') IS NULL
    CREATE TABLE dash.AgentPolicyRule
    (
        RuleId           INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashAgentPolicyRule PRIMARY KEY,
        RuleKind         VARCHAR(24)    NOT NULL CONSTRAINT CK_DashAgentPolicyRule_Kind CHECK (RuleKind IN ('NoOwner', 'Unused', 'CostlyFewUsers', 'SharedNotCertified')),
        Days             INT            NULL,       -- NoOwner, Unused: for this many days
        Credits          INT            NULL,       -- CostlyFewUsers: more credits a month than this
        People           INT            NULL,       -- CostlyFewUsers: with fewer people than this
        ProposedAction   VARCHAR(16)    NOT NULL CONSTRAINT CK_DashAgentPolicyRule_Action CHECK (ProposedAction IN ('Notify', 'Quarantine')),
        Enabled          BIT            NOT NULL CONSTRAINT DF_DashAgentPolicyRule_Enabled DEFAULT (0),
        AutoApprove      BIT            NOT NULL CONSTRAINT DF_DashAgentPolicyRule_Auto DEFAULT (0),   -- run without a decision (reversible actions only)
        UpdatedBy        NVARCHAR(256)  NULL,
        UpdatedUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_DashAgentPolicyRule_Updated DEFAULT (SYSUTCDATETIME())
    );
IF NOT EXISTS (SELECT 1 FROM dash.AgentPolicyRule)
    INSERT INTO dash.AgentPolicyRule (RuleKind, Days, Credits, People, ProposedAction, UpdatedBy)
    VALUES ('NoOwner', 30, NULL, NULL, 'Notify', N'install'),
           ('Unused', 90, NULL, NULL, 'Quarantine', N'install'),
           ('CostlyFewUsers', NULL, 10000, 5, 'Notify', N'install'),
           ('SharedNotCertified', 30, NULL, NULL, 'Notify', N'install');

IF OBJECT_ID(N'dash.AgentAction', N'U') IS NULL
    CREATE TABLE dash.AgentAction
    (
        ActionId         INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_DashAgentAction PRIMARY KEY,
        InventoryAgentId NVARCHAR(100)  NOT NULL,   -- dash.AgentInventory's: the agent's id in Copilot Studio
        AgentName        NVARCHAR(400)  NULL,
        Environment      NVARCHAR(400)  NULL,
        RuleId           INT            NULL,       -- NULL: proposed by a person
        ActionKind       VARCHAR(16)    NOT NULL CONSTRAINT CK_DashAgentAction_Kind CHECK (ActionKind IN ('Notify', 'Quarantine', 'Release', 'Reassign')),
        Reason           NVARCHAR(1000) NULL,
        NewOwnerUpn      NVARCHAR(256)  NULL,       -- Reassign: who takes the agent over
        Status           VARCHAR(16)    NOT NULL CONSTRAINT DF_DashAgentAction_Status DEFAULT ('Proposed')
                                        CONSTRAINT CK_DashAgentAction_Status CHECK (Status IN ('Proposed', 'Approved', 'Rejected', 'Done', 'Failed', 'Dismissed')),
        ProposedUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_DashAgentAction_Proposed DEFAULT (SYSUTCDATETIME()),
        ProposedBy       NVARCHAR(256)  NULL,       -- 'rule' for a rule's
        DecidedBy        NVARCHAR(256)  NULL,
        DecidedUtc       DATETIME2(0)   NULL,
        DoneUtc          DATETIME2(0)   NULL,
        Result           NVARCHAR(2000) NULL,       -- what the call answered, or why it failed
        CheckedUtc       DATETIME2(0)   NULL,       -- the next night's check that it holds
        INDEX IX_DashAgentAction_Agent (InventoryAgentId, Status)
    );

MERGE dash.Setting AS t
USING (VALUES
    ('TokenUnitLevels',             N'2,3', N'3.5.0: the org chart levels a business unit is made of, for Leadership > Model tokens: its L2 and L3 executives.'),
    ('SpendLimitAlertTo',           CAST(NULL AS NVARCHAR(400)), N'3.5.0: who gets A spend limit will be reached, separated by semicolons. Empty: nobody, the alert is off.'),
    ('SpendLimitAlertToExecutives', N'0', N'3.5.0: 1 also sends a limit''s alert to the L2 and L3 executives of the unit it belongs to.'),
    ('SpendLimitAlertDays',         N'5', N'3.5.0: warn when a limit is projected to be reached this many days or more before its period ends.'),
    ('SpendLimitHeadroomPercent',   N'10', N'3.5.0: headroom added to the suggested limit for next quarter, in percent.'),
    ('SpendLimitSuggestFrom',       N'P80', N'3.5.0: the forecast the suggested limit is taken from: P80 or P95.'),
    ('AgentActionsEnabled',         N'0', N'3.5.0: 1 lets tools/Invoke-AgentActions.ps1 carry out approved agent actions (quarantine, release, reassign) through the Power Platform API. 0: proposals only.')
) AS s (Name, Value, Description)
ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, Value, Description, UpdatedBy) VALUES (s.Name, s.Value, s.Description, N'install');

/* The credit routes' added rule: Enterprise Shared is funded centrally and names no line of business, as the
   page refuses; a row written in SQL with one was left out of the charge yet shown in its credits. */
IF OBJECT_ID(N'dash.CK_DashCreditRoute_Shared', N'C') IS NULL
   AND NOT EXISTS (SELECT 1 FROM dash.CreditRoute WHERE Path = 'EnterpriseShared' AND OrgId IS NOT NULL)
    ALTER TABLE dash.CreditRoute ADD CONSTRAINT CK_DashCreditRoute_Shared CHECK (Path <> 'EnterpriseShared' OR OrgId IS NULL);

/* Each complete month's credits by environment and the route in effect each day, kept once. A month is complete once
   the consumption report reaches its last day (the report is loaded by hand, a month at a time or part way), and is kept
   when a route is in effect at its end. With no @Month, every complete month not yet kept, so a load two months at once
   skips none; with @Month, that month alone (Credit chargeback's "Take it again" after its snapshot was cleared). Each
   month in one transaction, its check under a range lock, so two runs at once cannot keep it twice. A rate of 0 is no
   rate, as the page reads it. */
/* With the Governance Workbench as the seat record, a line of business is a funding owner: its code and name, kept here
   by the dashboard (every quarter hour), so a month's snapshot keeps the name it had when it was taken. */
IF OBJECT_ID(N'dash.LineOfBusinessName', N'U') IS NULL
    CREATE TABLE dash.LineOfBusinessName
    (
        OrgId       NVARCHAR(64)    NOT NULL CONSTRAINT PK_DashLineOfBusinessName PRIMARY KEY,   -- the funding owner's code
        Name        NVARCHAR(200)   NOT NULL,
        UpdatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashLineOfBusinessName_Updated DEFAULT (SYSUTCDATETIME())
    );

EXEC (N'
CREATE OR ALTER PROCEDURE dash.usp_TakeCreditChargeSnapshot
    @Month DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF OBJECT_ID(N''audit.PowerPlatformCreditDay'', N''U'') IS NULL RETURN;
    DECLARE @lastDay DATE = (SELECT MAX([Day]) FROM audit.PowerPlatformCreditDay WHERE Product = ''Copilot Studio'');
    IF @lastDay IS NULL RETURN;
    DECLARE @m DATE, @stop DATE, @end DATE;
    IF @Month IS NOT NULL
        SELECT @m = DATEFROMPARTS(YEAR(@Month), MONTH(@Month), 1), @stop = DATEFROMPARTS(YEAR(@Month), MONTH(@Month), 1);
    ELSE
        SELECT @m = DATEFROMPARTS(YEAR(MIN([Day])), MONTH(MIN([Day])), 1), @stop = DATEFROMPARTS(YEAR(@lastDay), MONTH(@lastDay), 1)
        FROM audit.PowerPlatformCreditDay WHERE Product = ''Copilot Studio'';
    DECLARE @rate DECIMAL(12, 6) = NULLIF(TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = N''CreditChargeRate'') AS DECIMAL(12, 6)), 0);
    /* Whose names: the Workbench''s funding owners where it is the seat record, else the reservation register. */
    DECLARE @model NVARCHAR(40) = (SELECT Value FROM dash.Setting WHERE Name = N''SeatModel'');
    IF @rate IS NULL AND OBJECT_ID(N''dash.CostPrice'', N''U'') IS NOT NULL
        SET @rate = NULLIF((SELECT MonthlyPrice FROM dash.CostPrice WHERE Item = ''COPILOT_STUDIO_PAYG_CREDIT''), 0);

    WHILE @m <= @stop
    BEGIN
        SET @end = EOMONTH(@m);
        IF @lastDay >= @end
           AND EXISTS (SELECT 1 FROM dash.CreditRoute WHERE EffectiveFrom <= @end AND (EffectiveTo IS NULL OR EffectiveTo >= @end))
        BEGIN
            BEGIN TRANSACTION;
            IF NOT EXISTS (SELECT 1 FROM dash.CreditChargeSnapshot WITH (UPDLOCK, HOLDLOCK) WHERE SnapshotMonth = @m)
                INSERT INTO dash.CreditChargeSnapshot (SnapshotMonth, EnvironmentName, Path, OrgId, OrgName, CostCenter, Credits, PaygCredits, PaygCost, Currency, ChargeRate)
                SELECT @m, c.EnvironmentName, c.Path, c.OrgId,
                       CASE WHEN @model = N''Governance'' THEN (SELECT l.Name FROM dash.LineOfBusinessName AS l WHERE l.OrgId = c.OrgId)
                            ELSE (SELECT TOP (1) o.Name FROM dash.Reservation AS o WHERE o.OrgId = c.OrgId ORDER BY o.EffectiveFrom DESC) END,
                       c.CostCenter, c.Credits, c.PaygCredits, c.PaygCost, c.Currency, @rate
                FROM (SELECT d.EnvironmentName, ISNULL(r.Path, ''Unmapped'') AS Path, r.OrgId, r.CostCenter,
                             SUM(ISNULL(d.Credits, 0)) AS Credits,
                             SUM(CASE WHEN d.Billing = ''PayAsYouGo'' THEN ISNULL(d.Credits, 0) ELSE 0 END) AS PaygCredits,
                             SUM(CASE WHEN d.Billing = ''PayAsYouGo'' THEN d.Cost END) AS PaygCost, MAX(d.Currency) AS Currency
                      FROM audit.PowerPlatformCreditDay AS d
                      OUTER APPLY (SELECT TOP (1) x.Path, x.OrgId, x.CostCenter FROM dash.CreditRoute AS x
                                   WHERE x.EnvironmentName = d.EnvironmentName AND x.EffectiveFrom <= d.[Day] AND (x.EffectiveTo IS NULL OR x.EffectiveTo >= d.[Day])
                                   ORDER BY x.EffectiveFrom DESC, x.RouteId DESC) AS r
                      WHERE d.Product = ''Copilot Studio'' AND d.[Day] BETWEEN @m AND @end
                      GROUP BY d.EnvironmentName, ISNULL(r.Path, ''Unmapped''), r.OrgId, r.CostCenter) AS c;
            COMMIT TRANSACTION;
        END;
        SET @m = DATEADD(MONTH, 1, @m);
    END;
END;');

/* Each person's saved views; one may be their start page. Unused since 2.4.4, which removed My Copilot; kept, with its rows. */
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

/* One retry policy. A scheduled digest or extract that fails is tried again an hour later, three tries in all,
   before it waits for its next period (as manager digests do, through dash.DigestSend). dash.Extract's
   columns follow its CREATE TABLE below. */
IF COL_LENGTH(N'dash.Subscription', N'FailedTries') IS NULL
    ALTER TABLE dash.Subscription ADD FailedTries TINYINT NOT NULL CONSTRAINT DF_DashSubscription_Failed DEFAULT (0), RetryAfterUtc DATETIME2(0) NULL;

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
/* The retry policy's columns, after the table exists. */
IF COL_LENGTH(N'dash.Extract', N'FailedTries') IS NULL
    ALTER TABLE dash.Extract ADD FailedTries TINYINT NOT NULL CONSTRAINT DF_DashExtract_Failed DEFAULT (0), RetryAfterUtc DATETIME2(0) NULL;

MERGE dash.Setting AS t
USING (VALUES
    ('AlertWebhookFormat',     N'Teams', N'How alerts are posted to the webhook (its address is stored encrypted): Teams (a Workflows or incoming-webhook URL) or Json (any endpoint).'),
    ('ManagerDigest',          N'Off',   N'Off, Weekly or Monthly: every manager with enough people under them gets their team''s leadership digest.'),
    ('ManagerDigestMinPeople', N'10',    N'Managers with at least this many people under them get the manager digest.'),
    ('ManagerDigestDay',       N'1',     N'The manager digest''s day: 0 Sunday to 6 Saturday (weekly), or 1 to 28 (monthly).'),
    ('DigestHour',             N'7',     N'The hour the manager digests go out, in the reporting time zone.'),
    ('CertificateWarnDays',    N'30',    N'Alert when the pipeline''s app certificate expires within this many days (and again at 14 and 3).'),
    ('RolloutWaveSize',        N'50',    N'The default number of people in a rollout wave.'),
    ('NonHumanGuests',         N'On',    N'A Copilot license on a guest is a non-human-account breach: On or Off (Admin > Reference data).'),
    ('NonHumanNames',          N'On',    N'Sign-ins and names that read like a service, shared, test or room account count as non-human: On or Off.'),
    ('NonHumanNoChart',        N'On',    N'Accounts with no manager and outside the org chart count as non-human: On or Off.'),
    ('NonHumanPatterns',       NULL,     N'More sign-in patterns that mark a non-human account, comma-separated, * for any text (bot-*, *-svc).'),
    ('NonHumanEmployeeTypes',  NULL,     N'Employee types that mark a non-human account, comma-separated (SERVICE, RESOURCE).'),
    ('NonHumanGroups',         NULL,     N'AD groups (DOMAIN\Group) whose members are non-human accounts, comma-separated; the directory load reads their members.'),
    ('NonHumanAlert',          N'1',     N'1 alerts Security and Licensing the first time a Copilot license is found on a non-human account; 0 does not.'),
    ('ReservationGatePercent', N'95',    N'2.6.8: no more Copilot licenses are bought until this share of the purchase-gate baseline is assigned (Leadership > Copilot reservations).'),
    ('ReservationGateBaseline', NULL,    N'2.6.8: the unassigned Copilot add-on licenses the purchase gate counts from (1,136 in the policy example); empty: no gate.'),
    ('DirectoryDomain',        NULL,      N'3.1.3: the users'' AD domain (users.contoso.com). Every read of users or groups from AD uses it: the nightly load and the dashboard''s lookup of who is signed in. Empty: TargetDomain in copilot-audit.settings.psd1 (Admin > Directory quality).'),
    ('CopilotLicenseGroup',    NULL,      N'3.1.3: the AD group (DOMAIN\group) of Microsoft 365 Copilot license holders the weekly report counts as Users Enabled. Empty: the settings file''s.'),
    ('CopilotChatGroup',       NULL,      N'3.1.3: the AD group (DOMAIN\group) of Copilot Chat users the weekly report counts as Users Enabled. Empty: the settings file''s.'),
    ('WeeklyReportGroupCount', N'Enabled', N'3.1.3: who the weekly Copilot report''s Users Enabled and Active Users count: Enabled, accounts enabled today only; All, everyone, disabled accounts and leavers included (Admin > Alerts and digests).'),
    ('SeatModel',              N'Leader', N'The Copilot seat record: Leader (the reservation register on Leadership > Copilot reservations) or Governance (the Workbench''s lines of business). The dashboard sets it when it starts; with Governance the nightly run keeps no reservation snapshot.')
) AS src (Name, Value, Description)
ON t.Name = src.Name
WHEN MATCHED AND ISNULL(t.Description, N'') <> src.Description THEN UPDATE SET Description = src.Description
WHEN NOT MATCHED THEN INSERT (Name, Value, Description) VALUES (src.Name, src.Value, src.Description);
/* 2.4.4 removed the personal weekly email, and with it its setting. */
DELETE FROM dash.Setting WHERE Name = 'PersonalDigest';

/* ---- Privacy and retention ------------------------------------------------ */

/* Privacy mode lives in audit.PipelineSetting, which bi.Users reads. Owned by dbo like the
   table, so the dashboard's account needs EXECUTE here to change it. It reads it with SELECT
   on audit.PipelineSetting (granted below): without that, the dashboard treats privacy mode as
   on, where a read that failed used to turn it off. */
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
    /* The audit log is kept at least a year (0 keeps it): a stored 1-364 counts as 365 (2.4.12).
       Before, only the admin form held that floor, so a value set in SQL deleted it sooner. */
    IF @audit BETWEEN 1 AND 364 SET @audit = 365;
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

/* ---- Several web servers: one report cache behind them all ------------------ */

/* The query cache shared by the web servers. Each keeps its results in memory; behind that, the results are kept here,
   so a page one server built is read, not built again, by the others. A result is kept under the data's stamp, the
   change count below and the query with its parameters (KeyHash), compressed JSON (Payload). Rows of an older stamp are
   cleared by the server that builds the pages; a row past its time is not read. */
IF OBJECT_ID(N'dash.QueryCacheShared', N'U') IS NULL
    CREATE TABLE dash.QueryCacheShared
    (
        KeyHash     BINARY(32)      NOT NULL CONSTRAINT PK_DashQueryCacheShared PRIMARY KEY,
        StampHash   BINARY(32)      NOT NULL,
        Payload     VARBINARY(MAX)  NOT NULL,
        Bytes       INT             NOT NULL,
        CreatedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashQueryCacheShared_Created DEFAULT (SYSUTCDATETIME()),
        ExpiresUtc  DATETIME2(0)    NOT NULL,
        Server      NVARCHAR(128)   NULL,
        INDEX IX_DashQueryCacheShared_Stamp (StampHash, CreatedUtc)
    );

/* How many changes the dashboard has made, on any web server: each change that can alter a report adds one. It is part
   of the data's stamp, so every server sees a change made on another at its next stamp read (30 seconds at most), and
   the server that made it at once. One row. */
IF OBJECT_ID(N'dash.CacheGeneration', N'U') IS NULL
    CREATE TABLE dash.CacheGeneration
    (
        Id          TINYINT         NOT NULL CONSTRAINT PK_DashCacheGeneration PRIMARY KEY CONSTRAINT CK_DashCacheGeneration_One CHECK (Id = 1),
        N           BIGINT          NOT NULL,
        ChangedUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashCacheGeneration_Changed DEFAULT (SYSUTCDATETIME())
    );
IF NOT EXISTS (SELECT 1 FROM dash.CacheGeneration)
    INSERT INTO dash.CacheGeneration (Id, N) VALUES (1, 0);

/* GitHub logins matched to people by hand (3.3.0): an administrator's list, for logins the SCIM identities do
   not cover (an enterprise of personal accounts, or SCIM not readable). Kept across loads. */
IF OBJECT_ID(N'dash.GitHubIdentityMap', N'U') IS NULL
    CREATE TABLE dash.GitHubIdentityMap
    (
        Login      NVARCHAR(100)  NOT NULL CONSTRAINT PK_DashGitHubIdentityMap PRIMARY KEY,
        Upn        NVARCHAR(256)  NOT NULL,
        Note       NVARCHAR(400)  NULL,
        UpdatedBy  NVARCHAR(256)  NOT NULL,
        UpdatedUtc DATETIME2(0)   NOT NULL CONSTRAINT DF_DashGitHubIdentityMap_Updated DEFAULT (SYSUTCDATETIME())
    );

/* ---- The saving ledger ---------------------------------------------------- */

/* Money saved and found, a month at a time, so the history survives a change of price and Power BI can read it.
   Saved: what the dashboard's own exports took back (dash.LicenseAction), written by dash.usp_RefreshSavingLedger
   at each refresh: the month in progress is rewritten each night, a month gone by is kept as it was last written.
   Found: the savings Leadership > Costs and savings finds, by confidence, written by the dashboard each time it works
   them out for the whole organization (this month's rows replaced, earlier months kept). Amounts are a month's. */
IF OBJECT_ID(N'dash.SavingLedger', N'U') IS NULL
    CREATE TABLE dash.SavingLedger
    (
        [Month]     DATE            NOT NULL,   -- the first day of the month
        Area        NVARCHAR(100)   NOT NULL,   -- as Costs and savings names it: Microsoft 365 Copilot, ...
        Kind        VARCHAR(8)      NOT NULL,   -- Saved | Found
        Confidence  VARCHAR(8)      NOT NULL CONSTRAINT DF_DashSavingLedger_Confidence DEFAULT (''),   -- High | Medium | Low; '' for Saved
        Source      NVARCHAR(400)   NOT NULL,   -- what it is: the page that shows it, or a short name
        Items       INT             NULL,       -- licenses, where it is a count of them
        Amount      DECIMAL(19, 2)  NOT NULL,   -- a month's, in Currency
        Currency    NVARCHAR(8)     NOT NULL,
        Title       NVARCHAR(400)   NULL,       -- the saving as the page words it
        WrittenUtc  DATETIME2(0)    NOT NULL CONSTRAINT DF_DashSavingLedger_Written DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DashSavingLedger PRIMARY KEY ([Month], Kind, Area, Confidence, Source),
        CONSTRAINT CK_DashSavingLedger_Kind CHECK (Kind IN ('Saved', 'Found'))
    );

/* Saved: each month, the Copilot licenses the dashboard's exports named (revoke, leaver, non-human) that were no longer
   held on the month's last day with license data, at the Copilot license price. The same rule as the Already saved
   tile on Costs and savings, which is the month in progress. Months before the first export are not written. */
EXEC (N'CREATE OR ALTER PROCEDURE dash.usp_RefreshSavingLedger
AS
BEGIN
    SET NOCOUNT ON;
    IF OBJECT_ID(N''dash.SavingLedger'', N''U'') IS NULL OR OBJECT_ID(N''dash.LicenseAction'', N''U'') IS NULL OR OBJECT_ID(N''bi.FactLicensedDay'', N''U'') IS NULL RETURN;
    DECLARE @lastKey INT = (SELECT MAX(DateKey) FROM bi.FactLicensedDay);
    DECLARE @firstExport DATETIME2(0) = (SELECT MIN(ExportedUtc) FROM dash.LicenseAction WHERE Kind IN (''revoke'', ''leaver'', ''nonhuman''));
    IF @lastKey IS NULL OR @firstExport IS NULL RETURN;
    DECLARE @price DECIMAL(19, 4) = ISNULL(TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = N''LicenseMonthlyCost'') AS DECIMAL(19, 4)), 30);
    DECLARE @cur NVARCHAR(8) = ISNULL(NULLIF(LTRIM(RTRIM((SELECT Value FROM dash.Setting WHERE Name = N''LicenseCurrency''))), N''''), N''USD'');
    DECLARE @thisMonth DATE = DATEFROMPARTS(@lastKey / 10000, @lastKey / 100 % 100, 1);
    DECLARE @month DATE = DATEFROMPARTS(YEAR(@firstExport), MONTH(@firstExport), 1);

    DECLARE @m TABLE ([Month] DATE NOT NULL PRIMARY KEY, EndKey INT NULL, Before DATETIME2(0) NOT NULL);
    WHILE @month <= @thisMonth
    BEGIN
        IF @month = @thisMonth OR NOT EXISTS (SELECT 1 FROM dash.SavingLedger WHERE [Month] = @month AND Kind = ''Saved'')
            INSERT INTO @m ([Month], EndKey, Before)
            SELECT @month,
                   (SELECT MAX(DateKey) FROM bi.FactLicensedDay
                    WHERE DateKey BETWEEN YEAR(@month) * 10000 + MONTH(@month) * 100 + 1 AND YEAR(@month) * 10000 + MONTH(@month) * 100 + 31),
                   CAST(DATEADD(MONTH, 1, @month) AS DATETIME2(0));
        SET @month = DATEADD(MONTH, 1, @month);
    END;

    DECLARE @rows TABLE ([Month] DATE NOT NULL PRIMARY KEY, Items INT NOT NULL);
    INSERT INTO @rows ([Month], Items)
    SELECT m.[Month],
           (SELECT COUNT(DISTINCT la.Upn)
            FROM dash.LicenseAction AS la JOIN bi.DimUser AS du ON du.UserPrincipalName = la.Upn
            WHERE la.Kind IN (''revoke'', ''leaver'', ''nonhuman'') AND la.ExportedUtc < m.Before
              AND NOT EXISTS (SELECT 1 FROM bi.FactLicensedDay AS ld WHERE ld.UserKey = du.UserKey AND ld.DateKey = m.EndKey))
    FROM @m AS m
    WHERE m.EndKey IS NOT NULL;

    MERGE dash.SavingLedger AS t
    USING (SELECT [Month], N''Microsoft 365 Copilot'' AS Area, ''Saved'' AS Kind, '''' AS Confidence, N''/forecasting/outcomes'' AS Source,
                  Items, CAST(Items * @price AS DECIMAL(19, 2)) AS Amount, @cur AS Currency,
                  CONCAT(Items, N'' Copilot license'', CASE WHEN Items = 1 THEN N'''' ELSE N''s'' END, N'' reclaimed through this dashboard'') AS Title
           FROM @rows) AS s
       ON t.[Month] = s.[Month] AND t.Kind = s.Kind AND t.Area = s.Area AND t.Confidence = s.Confidence AND t.Source = s.Source
    WHEN MATCHED THEN UPDATE SET Items = s.Items, Amount = s.Amount, Currency = s.Currency, Title = s.Title, WrittenUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT ([Month], Area, Kind, Confidence, Source, Items, Amount, Currency, Title)
        VALUES (s.[Month], s.Area, s.Kind, s.Confidence, s.Source, s.Items, s.Amount, s.Currency, s.Title);
END;');

/* ---- The dashboard's database role ---------------------------------------- */

IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NULL
    CREATE ROLE CopilotInsightsApp AUTHORIZATION dbo;
GRANT SELECT ON SCHEMA::bi TO CopilotInsightsApp;
GRANT SELECT ON SCHEMA::dash TO CopilotInsightsApp;
DENY SELECT ON dash.Secret TO CopilotInsightsApp;   -- read only through dash.usp_GetSecret
GRANT INSERT, UPDATE, DELETE ON dash.Setting TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.RoleGrant TO CopilotInsightsApp;
/* Admin > Access profiles. */
IF OBJECT_ID(N'dash.AccessProfile', N'U') IS NOT NULL GRANT INSERT, UPDATE, DELETE ON dash.AccessProfile TO CopilotInsightsApp;
IF OBJECT_ID(N'dash.AccessProfileRole', N'U') IS NOT NULL GRANT INSERT, UPDATE, DELETE ON dash.AccessProfileRole TO CopilotInsightsApp;
IF OBJECT_ID(N'dash.ProfileGrant', N'U') IS NOT NULL GRANT INSERT, UPDATE, DELETE ON dash.ProfileGrant TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.AgentAlias TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.SensitivityLabel TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Subscription TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.AlertState TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Target TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.TimeSaving TO CopilotInsightsApp;
IF OBJECT_ID(N'dash.CostPrice', N'U') IS NOT NULL GRANT UPDATE ON dash.CostPrice TO CopilotInsightsApp;   -- Admin > Targets and assumptions sets the prices
IF OBJECT_ID(N'dash.CostSuite', N'U') IS NOT NULL GRANT UPDATE ON dash.CostSuite TO CopilotInsightsApp;   -- And turns a suite's inclusions on or off
IF OBJECT_ID(N'dash.SkuName', N'U') IS NOT NULL GRANT INSERT, UPDATE, DELETE ON dash.SkuName TO CopilotInsightsApp;   -- Admin > Value names names the licenses
GRANT INSERT, UPDATE, DELETE ON dash.TrainingAttendance TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.ValueName TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.FlagCase TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.DigestPreference TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.DigestSend TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.LicenseAction TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.SavingLedger TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.GitHubIdentityMap TO CopilotInsightsApp;   -- the administrator's list of logins and people   -- Costs and savings writes the month's savings found
GRANT INSERT, UPDATE, DELETE ON dash.LicenseContract TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Reservation TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.ReservationRate TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.TechnologyAllocation TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.CreditRoute TO CopilotInsightsApp;        -- Leadership > Credit chargeback's register
GRANT INSERT, UPDATE, DELETE ON dash.LineOfBusinessName TO CopilotInsightsApp; -- the Workbench's lines of business, kept by the dashboard
GRANT INSERT, UPDATE, DELETE ON dash.CreditCommitment TO CopilotInsightsApp;   -- Forecasting > Credit commitment
GRANT DELETE ON dash.CreditChargeSnapshot TO CopilotInsightsApp;                -- An administrator clears a month to take it again
GRANT EXECUTE ON dash.usp_TakeCreditChargeSnapshot TO CopilotInsightsApp;      -- and takes it again
GRANT INSERT, UPDATE, DELETE ON dash.TokenRoute TO CopilotInsightsApp;         -- Leadership > Model tokens routes a gateway value to a unit
GRANT INSERT, UPDATE ON dash.SpendLimit TO CopilotInsightsApp;                 -- Forecasting > Spend limits: an entered limit, a limit's unit and warning
GRANT INSERT ON dash.SpendLimitSnapshot TO CopilotInsightsApp;                 -- a period's end, kept once
GRANT UPDATE ON dash.AgentPolicyRule TO CopilotInsightsApp;                    -- Governance > Agent policies turns a rule on and sets it
GRANT INSERT, UPDATE ON dash.AgentAction TO CopilotInsightsApp;                -- and decides on a proposal
GRANT INSERT, UPDATE, DELETE ON dash.SavedView TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.NonHumanException TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.JobFamilyRule TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.UseCaseRule TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.EventMarker TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.Extract TO CopilotInsightsApp;
GRANT INSERT, UPDATE, DELETE ON dash.QueryCacheShared TO CopilotInsightsApp;   -- the web servers' shared report cache
GRANT UPDATE ON dash.CacheGeneration TO CopilotInsightsApp;                      -- a change counted for every web server
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
/* The pipeline tables Admin > Health, the license reports and the value catalog read, and the members of the groups that mark non-human accounts. */
DECLARE @t SYSNAME, @g NVARCHAR(400);
DECLARE t CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM (VALUES (N'CopilotLoad'), (N'CopilotCsvImport'), (N'CopilotSessionRun'), (N'UserDirectory'), (N'PipelineRun'),
                             (N'PipelineRunStep'), (N'PipelineSetting'), (N'LicenseSku'), (N'LicenseSkuDay'), (N'CopilotLicense'), (N'UserLicenseSku'),
                             (N'ValueCatalog'), (N'ValueCatalogColumn'), (N'CopilotLoadWindow'), (N'CopilotBackfillWindow'), (N'GroupMember'), (N'PipelineCertificate'),
                             (N'GitHubCopilotSeat'), (N'GitHubCopilotDay'), (N'GitHubCopilotUserDay'), (N'GitHubCopilotCost'),
                             (N'FoundryDeployment'), (N'FoundryUsageDay'), (N'FoundryCostDay'), (N'ExternalUsageRun'),
                             /* 2.6.6's cost tables and 2.6.9's AI apps: a new install makes the role after their scripts ran. */
                             (N'FabricCapacityDay'), (N'FabricWorkloadDay'), (N'PowerAutomateFlow'), (N'PowerPlatformUserActivity'),
                             (N'PowerPlatformCapacityDay'), (N'PowerPlatformCreditDay'), (N'AIAppUserDay'), (N'AIAppResource'), (N'ActivityUserDay'), (N'DlpDay'),
                             /* 3.1.3: the weekly report's AD groups, shown on Admin > Alerts and digests. */
                             (N'ReportGroupDay'),
                             /* 3.5.0: the gateway's tokens and the limits read at their sources. */
                             (N'GatewayTokenDay'), (N'SpendLimitSource')) AS v (name)
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
/* Administrators keep the AI resources' line of business, environment and owner. */
IF OBJECT_ID(N'audit.AIAppResource', N'U') IS NOT NULL
    GRANT INSERT, UPDATE ON audit.AIAppResource TO CopilotInsightsApp;

/* ---- For Power BI ----------------------------------------------------------- */

/*
    Row-level security from the org chart: one row per (viewer, person they may
    see). A manager sees everyone under them; the top of the organization, the
    people reporting to them and anyone granted Organization see everyone. The
    Power BI model in powerbi/ uses it in its Organization role:
    Users[UserKey] IN the viewer's rows, by USERPRINCIPALNAME(). Organization
    granted to an AD group counts for its members too: the nightly
    directory load expands the group into audit.GroupMember.

    A grant by DOMAIN\name counts whatever type it was recorded with,
    as the dashboard matches it at sign-in. The directory load expands every
    one into audit.GroupMember: a group recorded as a user (GrantRole records
    one so when it cannot ask AD) into its members, and a person into their
    own UPN, which is what USERPRINCIPALNAME() gives. Before, both saw only
    their own people in Power BI, though the dashboard showed them everyone.
    A user grant by UPN (name@domain) names the viewer directly.

    WholeOrganization marks the viewers who see everyone, so the role
    can show the tables of whole-organization figures (bi.LicensePool,
    bi.ValueNames, bi.DepartmentWeek) to them alone. Before, every member of
    the role read those tables, and this one, whole.

    Empty in privacy mode: each row pairs a real sign-in with a user key, which
    would undo the pseudonyms in bi.Users. With privacy mode on, share
    bi.DepartmentWeek rather than the person-level model (the model's
    Departments role).
*/
IF OBJECT_ID(N'audit.GroupMember', N'U') IS NULL
    CREATE TABLE audit.GroupMember (GroupName NVARCHAR(256) NOT NULL, UserPrincipalName NVARCHAR(256) NOT NULL,
        LoadedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_GroupMember_Loaded DEFAULT (SYSUTCDATETIME()), CONSTRAINT PK_GroupMember PRIMARY KEY (GroupName, UserPrincipalName));
/* Organization held through an access profile counts the same way, granted to the person or to a group they are in. */
DECLARE @profiles NVARCHAR(MAX) = CASE WHEN OBJECT_ID(N'dash.ProfileGrant', N'U') IS NULL THEN N'' ELSE N'
    UNION SELECT pg.Principal FROM dash.ProfileGrant AS pg JOIN dash.AccessProfileRole AS pr ON pr.ProfileId = pg.ProfileId AND pr.Role = ''Organization''
          WHERE pg.PrincipalType = ''User'' AND pg.Principal LIKE N''%@%''
    UNION SELECT gm.UserPrincipalName FROM audit.GroupMember AS gm
          JOIN dash.ProfileGrant AS pg ON pg.Principal = gm.GroupName
          JOIN dash.AccessProfileRole AS pr ON pr.ProfileId = pg.ProfileId AND pr.Role = ''Organization''' END;
EXEC (N'CREATE OR ALTER VIEW bi.OrgSecurity AS
WITH whole AS (   -- everyone who sees the whole organization: the top two levels, and Organization granted to them or to a group they are in
    SELECT Upn FROM dash.Person WHERE Level IN (1, 2)
    UNION SELECT Principal FROM dash.RoleGrant WHERE PrincipalType = ''User'' AND Role = ''Organization'' AND Principal LIKE N''%@%''
    UNION SELECT gm.UserPrincipalName FROM audit.GroupMember AS gm
          JOIN dash.RoleGrant AS g ON g.Principal = gm.GroupName AND g.Role = ''Organization''' + @profiles + N'
)
SELECT oc.AncestorUpn AS ViewerUpn, u.UserKey, CAST(0 AS BIT) AS WholeOrganization
FROM dash.OrgClosure AS oc
JOIN bi.DimUser AS u ON u.UserPrincipalName = oc.DescendantUpn
WHERE NOT EXISTS (SELECT 1 FROM whole WHERE whole.Upn = oc.AncestorUpn)
  AND NOT EXISTS (SELECT 1 FROM audit.PipelineSetting WHERE Name = N''PrivacyMode'' AND Value = N''Pseudonymize'')
UNION ALL
SELECT w.Upn, u.UserKey, CAST(1 AS BIT)
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

/* The dashboard's classifications for Power BI. Every combination of app, web search and agent
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
   for a model that must not show individuals (a works council''s condition).

   A row for every department and every week of the data, used or not,
   so whether a row exists never depends on anyone's use. Before, a row existed
   only where someone was active, so a hidden row for a one-person department
   still told whether that person used Copilot that week. Departments with
   fewer enabled people than the minimum are combined as (smaller departments);
   a row is hidden (figures blank) when fewer people than the minimum were
   active that week, none included. */
EXEC (N'CREATE OR ALTER VIEW bi.DepartmentWeek AS
WITH m AS (SELECT ISNULL(TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = ''MinGroupSize'') AS INT), 10) AS MinGroup),
dept AS
(
    SELECT ISNULL(NULLIF(Department, N''''), N''(no department)'') AS Department, SUM(CASE WHEN AccountEnabled = 1 THEN 1 ELSE 0 END) AS Headcount
    FROM bi.DimUser
    GROUP BY ISNULL(NULLIF(Department, N''''), N''(no department)'')
),
grp AS
(
    SELECT dept.Department, CASE WHEN dept.Headcount >= m.MinGroup THEN dept.Department ELSE N''(smaller departments)'' END AS ShownAs
    FROM dept CROSS JOIN m
),
wk AS
(
    SELECT DISTINCT d.WeekEnding
    FROM bi.DimDate AS d
    JOIN (SELECT MIN(DateKey) AS FirstKey, MAX(DateKey) AS LastKey FROM dash.UserDay) AS r ON d.DateKey BETWEEN r.FirstKey AND r.LastKey
),
used AS
(
    SELECT d.WeekEnding, g.ShownAs,
           COUNT(DISTINCT ud.UserKey) AS Active, SUM(CAST(ud.Interactions AS BIGINT)) AS Interactions,
           SUM(CAST(ud.Sessions AS BIGINT)) AS Sessions
    FROM dash.UserDay AS ud
    JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
    JOIN bi.DimUser AS u ON u.UserKey = ud.UserKey
    JOIN grp AS g ON g.Department = ISNULL(NULLIF(u.Department, N''''), N''(no department)'')
    WHERE ud.Interactions > 0
    GROUP BY d.WeekEnding, g.ShownAs
)
SELECT wk.WeekEnding, s.ShownAs AS Department,
       CASE WHEN ISNULL(x.Active, 0) >= m.MinGroup THEN x.Active END AS ActivePeople,
       CASE WHEN ISNULL(x.Active, 0) >= m.MinGroup THEN x.Interactions END AS Interactions,
       CASE WHEN ISNULL(x.Active, 0) >= m.MinGroup THEN x.Sessions END AS Sessions,
       CAST(CASE WHEN ISNULL(x.Active, 0) < m.MinGroup THEN 1 ELSE 0 END AS BIT) AS Hidden
FROM wk
CROSS JOIN (SELECT DISTINCT ShownAs FROM grp) AS s
CROSS JOIN m
LEFT JOIN used AS x ON x.WeekEnding = wk.WeekEnding AND x.ShownAs = s.ShownAs;');

/* The reporting accounts' role (Install-CopilotAudit.ps1 -ReportingAccount) reads every bi view, these
   included. The installer grants the views that exist when it runs, and the dashboard installer
   runs this script after it, so OrgSecurity, DepartmentWeek and the others here were left out. */
IF DATABASE_PRINCIPAL_ID(N'CopilotReporting') IS NOT NULL
BEGIN
    DECLARE @rv SYSNAME, @rg NVARCHAR(400);
    DECLARE rv CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(N'bi');
    OPEN rv;
    FETCH NEXT FROM rv INTO @rv;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @rg = N'GRANT SELECT ON bi.' + QUOTENAME(@rv) + N' TO CopilotReporting;';
        EXEC (@rg);
        FETCH NEXT FROM rv INTO @rv;
    END;
    CLOSE rv;
    DEALLOCATE rv;
END;
