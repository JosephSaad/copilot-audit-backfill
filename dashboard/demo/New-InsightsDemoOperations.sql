/*
    Copilot Insights demo: data for the operations pages, on top of
    New-InsightsDemoData.sql. Run after the bi build and before the
    dashboard's (the disabled account is set in bi too, as a rebuild would).

      - the license pool: 60 nightly snapshots of the Copilot SKU and one other
      - three licensed people whose accounts are disabled (two in Entra, one in AD)
      - five recorded pipeline runs, the second-to-last of them failed
      - two training sessions, a target for the organization and one for a unit,
        minutes saved for two apps, and a weekly digest (email is off, so it only
        records itself)
*/
SET NOCOUNT ON;

DECLARE @licensed INT = (SELECT COUNT(*) FROM audit.CopilotLicense WHERE IsCopilotLic = 1);
DECLARE @people INT = (SELECT COUNT(*) FROM audit.UserDirectory);
DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);

/* ---- The license pool ---- */
DELETE FROM audit.LicenseSkuDay;
DELETE FROM audit.LicenseSku;
DECLARE @d INT = 59;
WHILE @d >= 0
BEGIN
    INSERT INTO audit.LicenseSkuDay (SnapshotDate, SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed)
    VALUES (DATEADD(DAY, -@d, @today), '639dec6b-bb19-468b-871c-c5c441c4b0cb', N'Microsoft_365_Copilot', 1, (@licensed + 60) / 50 * 50, 0, 0, 0, @licensed - @d / 3),
           (DATEADD(DAY, -@d, @today), '06ebc4ee-1bb5-47dd-8120-11324bc54e06', N'SPE_E5', 0, (@people + 250) / 100 * 100, 0, 0, 0, @people - @d);
    SET @d -= 1;
END;
INSERT INTO audit.LicenseSku (SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed, CapabilityStatus)
SELECT SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed, N'Enabled' FROM audit.LicenseSkuDay WHERE SnapshotDate = @today;

/* ---- Licenses on disabled accounts: the 11th and 12th licensed people in Entra, the 13th in AD ---- */
WITH l AS (SELECT UserPrincipalName, ROW_NUMBER() OVER (ORDER BY UserPrincipalName) AS n FROM audit.CopilotLicense WHERE IsCopilotLic = 1)
UPDATE c SET EntraAccountEnabled = CASE WHEN l.n IN (11, 12) THEN 0 ELSE 1 END
FROM audit.CopilotLicense AS c JOIN l ON l.UserPrincipalName = c.UserPrincipalName;
WITH l AS (SELECT UserPrincipalName, ROW_NUMBER() OVER (ORDER BY UserPrincipalName) AS n FROM audit.CopilotLicense WHERE IsCopilotLic = 1)
UPDATE u SET AccountEnabled = 0
FROM audit.UserDirectory AS u JOIN l ON l.UserPrincipalName = u.UserPrincipalName AND l.n = 13;
WITH l AS (SELECT UserPrincipalName, ROW_NUMBER() OVER (ORDER BY UserPrincipalName) AS n FROM audit.CopilotLicense WHERE IsCopilotLic = 1)
UPDATE u SET AccountEnabled = 0
FROM bi.DimUser AS u JOIN l ON l.UserPrincipalName = u.UserPrincipalName AND l.n = 13;
/* Hire dates: the new joiners have theirs from New-InsightsDemoData.sql (2.6.6); everyone else in the org chart was
   hired one to seventeen years ago. Accounts outside it (service accounts, guests) have none, as in Entra. */
UPDATE c SET EmployeeHireDate = DATEADD(DAY, -(400 + ABS(CHECKSUM(c.UserPrincipalName)) % 6000), @today)
FROM audit.CopilotLicense AS c JOIN audit.UserDirectory AS u ON u.UserPrincipalName = c.UserPrincipalName
WHERE c.EmployeeHireDate IS NULL AND u.OrgDepth IS NOT NULL;

/* ---- The two certificates the pipeline signs in with: one well in date, one inside the warning ---- */
DELETE FROM audit.PipelineCertificate;
INSERT INTO audit.PipelineCertificate (Purpose, Thumbprint, Found, Subject, Issuer, NotBeforeUtc, ExpiresUtc, Store, ClientId, TenantId, Host)
VALUES ('Purview', '3F2A9C0D1B4E5F60718293A4B5C6D7E8F9012345', 1, N'CN=Copilot audit backfill (JOBSRV01)', N'CN=Copilot audit backfill (JOBSRV01)',
        DATEADD(DAY, -120, SYSUTCDATETIME()), DATEADD(DAY, 245, SYSUTCDATETIME()), 'LocalMachine\My', N'11111111-2222-3333-4444-555555555555', N'contoso.onmicrosoft.com', N'JOBSRV01'),
       ('AgentWorkbook', 'A1B2C3D4E5F60718293A4B5C6D7E8F9012345678', 1, N'CN=Copilot agent workbook (JOBSRV01)', N'CN=Contoso Issuing CA 2, DC=contoso, DC=com',
        DATEADD(DAY, -344, SYSUTCDATETIME()), DATEADD(DAY, 21, SYSUTCDATETIME()), 'LocalMachine\My', N'66666666-7777-8888-9999-000000000000', N'contoso.onmicrosoft.com', N'JOBSRV01');

/* ---- Pipeline runs ---- */
DELETE FROM audit.PipelineRunStep;
DELETE FROM audit.PipelineRun;
SET @d = 4;
WHILE @d >= 0
BEGIN
    INSERT INTO audit.PipelineRun (Kind, StartedUtc, EndedUtc, Status, FailedStep, Detail, Host, RunAs)
    VALUES (N'Nightly', DATEADD(HOUR, 2, CAST(DATEADD(DAY, -@d, @today) AS DATETIME2(0))),
            DATEADD(MINUTE, CASE WHEN @d = 1 THEN 14 ELSE 41 END, DATEADD(HOUR, 2, CAST(DATEADD(DAY, -@d, @today) AS DATETIME2(0)))),
            CASE WHEN @d = 1 THEN 'Failed' ELSE 'Succeeded' END,
            CASE WHEN @d = 1 THEN N'Copilot licenses from Graph' END,
            CASE WHEN @d = 1 THEN N'2026-09-26T02:13:58 Connecting to Microsoft Graph as the app...
Connect-MgGraph: ClientCertificateCredential authentication failed: The certificate has expired.
2026-09-26T02:14:02 Copilot licenses from Graph failed (exit code 1). Run the same command again to resume.' END,
            N'JOBSRV01', N'CONTOSO\gmsa-copilot$');
    SET @d -= 1;
END;

/* ---- Training, targets, assumptions, a digest ---- */
DELETE FROM dash.TrainingAttendance;
WITH a AS (SELECT u.UserPrincipalName, ROW_NUMBER() OVER (ORDER BY CHECKSUM(u.UserPrincipalName)) AS n
           FROM bi.DimUser AS u WHERE u.DirectoryStatus = N'Found' AND u.AccountEnabled = 1 AND u.OrgDepth IS NOT NULL)   -- people, not service accounts
INSERT INTO dash.TrainingAttendance (Upn, Session, AttendedOn, ImportedBy)
SELECT UserPrincipalName, CASE WHEN n <= 40 THEN N'Copilot in Outlook and Teams' ELSE N'Prompting that works' END,
       DATEADD(DAY, CASE WHEN n <= 40 THEN -75 ELSE -45 END, @today), N'demo'
FROM a WHERE n <= 70;

DELETE FROM dash.Target;
INSERT INTO dash.Target (RootUpn, Metric, TargetValue, StartDate, DueDate, Note, CreatedBy)
VALUES (NULL, 'Adoption', 95, DATEADD(DAY, -90, @today), DATEADD(DAY, 60, @today), N'Every license in use by year end', N'demo'),
       ((SELECT TOP (1) UserPrincipalName FROM bi.DimUser WHERE OrgDepth = 1 AND IsManager = 1 ORDER BY UserPrincipalName), 'ActiveShare', 60, DATEADD(DAY, -60, @today), DATEADD(DAY, 30, @today), NULL, N'demo');

MERGE dash.TimeSaving AS t
USING (VALUES (N'Word', 6.0), (N'Microsoft Teams', 4.0)) AS s (App, Minutes) ON t.App = s.App
WHEN MATCHED THEN UPDATE SET Minutes = s.Minutes
WHEN NOT MATCHED THEN INSERT (App, Minutes, UpdatedBy) VALUES (s.App, s.Minutes, N'demo');

IF NOT EXISTS (SELECT 1 FROM dash.Subscription)
    INSERT INTO dash.Subscription (Name, Report, Recipients, Frequency, DayOfWeek, HourLocal, Days, CreatedBy, LastSentUtc)
    VALUES (N'Monday leadership summary', 'Leadership', N'leadership@contoso.com', 'Weekly', 1, 7, 30, N'demo', SYSUTCDATETIME());

/* Copilot Studio's authoring records are written with the agent workbook, for the same agents, by
   New-InsightsDemoAgents.sql (2.6.6). */

/* ---- 2.3.0, second part: job families, a contract, events on the trends, license exports, triage ---- */
IF OBJECT_ID(N'dash.JobFamilyRule', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dash.JobFamilyRule)
    INSERT INTO dash.JobFamilyRule (Pattern, Family, Priority, UpdatedBy) VALUES
        (N'Manager%', N'Managers', 10, N'demo'), (N'%Engineer%', N'Engineering', 20, N'demo'), (N'%Analyst%', N'Analysts', 20, N'demo'),
        (N'%Specialist%', N'Specialists', 30, N'demo'), (N'Associate', N'Associates', 40, N'demo'), (N'Coordinator', N'Coordinators', 40, N'demo');

IF OBJECT_ID(N'dash.LicenseContract', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dash.LicenseContract)
    INSERT INTO dash.LicenseContract (Name, Seats, MonthlyPrice, StartDate, RenewalDate, Note, UpdatedBy)
    VALUES (N'Microsoft 365 Copilot, enterprise agreement', 1200, 30, DATEADD(MONTH, -10, @today), DATEADD(DAY, 75, @today), N'Annual true-up', N'demo');

IF OBJECT_ID(N'dash.EventMarker', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dash.EventMarker)
    INSERT INTO dash.EventMarker (Day, Label, Kind, UpdatedBy) VALUES
        (DATEADD(DAY, -45, @today), N'Wave 2 licenses', 'Rollout', N'demo'),
        (DATEADD(DAY, -20, @today), N'Prompting workshop', 'Training', N'demo'),
        (DATEADD(DAY, -8, @today), N'Copilot service incident', 'Outage', N'demo');

IF OBJECT_ID(N'dash.LicenseAction', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dash.LicenseAction)
BEGIN
    DECLARE @k1 INT = (SELECT MAX(DateKey) FROM bi.FactLicensedDay);
    /* A revocation export three weeks ago: most applied since (no longer licensed), a few still pending. */
    INSERT INTO dash.LicenseAction (Kind, Upn, ExportedUtc, ExportedBy)
    SELECT TOP (12) 'revoke', u.UserPrincipalName, DATEADD(DAY, -21, CAST(@today AS DATETIME2(0))), N'wei.okafor@contoso.com'
    FROM bi.DimUser AS u WHERE NOT EXISTS (SELECT 1 FROM bi.FactLicensedDay AS l WHERE l.UserKey = u.UserKey AND l.DateKey = @k1) AND u.AccountEnabled = 1
    ORDER BY CHECKSUM(u.UserPrincipalName);
    INSERT INTO dash.LicenseAction (Kind, Upn, ExportedUtc, ExportedBy)
    SELECT TOP (3) 'revoke', l.Upn, DATEADD(DAY, -21, CAST(@today AS DATETIME2(0))), N'wei.okafor@contoso.com'
    FROM (SELECT u.UserPrincipalName AS Upn FROM bi.FactLicensedDay AS f JOIN bi.DimUser AS u ON u.UserKey = f.UserKey WHERE f.DateKey = @k1) AS l
    ORDER BY CHECKSUM(l.Upn);
    /* An upgrade wave two weeks ago: licensed now. */
    INSERT INTO dash.LicenseAction (Kind, Upn, ExportedUtc, ExportedBy)
    SELECT TOP (10) 'wave', u.UserPrincipalName, DATEADD(DAY, -14, CAST(@today AS DATETIME2(0))), N'wei.okafor@contoso.com'
    FROM bi.FactLicensedDay AS f JOIN bi.DimUser AS u ON u.UserKey = f.UserKey WHERE f.DateKey = @k1
    ORDER BY CHECKSUM(u.UserPrincipalName) DESC;
END;

IF OBJECT_ID(N'dash.FlagCase', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dash.FlagCase)
    INSERT INTO dash.FlagCase (RecordId, Status, Owner, Note, UpdatedBy)
    SELECT TOP (6) f.RecordId, CASE WHEN ROW_NUMBER() OVER (ORDER BY f.RecordId) <= 2 THEN 'Investigating' WHEN ROW_NUMBER() OVER (ORDER BY f.RecordId) <= 4 THEN 'Closed' ELSE 'FalsePositive' END,
           N'mei.osei@contoso.com', N'Demo triage', N'mei.osei@contoso.com'
    FROM bi.FactInteraction AS f WHERE f.JailbreakDetected = 1 OR f.PromptInjectionDetected = 1 ORDER BY f.RecordId;
