/*
    Copilot Insights - demo data
    ------------------------------------------------------------------------
    A synthetic company for trying the dashboard without real data: 2,317
    people in five levels (a CEO, six divisions, 30 departments, 120 teams),
    Copilot licenses rolled out in waves over 26 weeks with their license
    history, and about half a million interactions shaped like real use -
    champions, regular, light and dormant users, unlicensed people on the
    free Copilot Chat, Copilot Studio agents launched over time, a shifting
    model mix, SharePoint sites with sensitivity labels, and rare security
    flags.

    Deterministic: every choice comes from a hash, so the same database comes
    out every time (relative to today). Run by New-InsightsDemo.ps1 into an
    empty database after the repository's schema scripts; it fills
    audit.UserDirectory, audit.CopilotLicense, audit.LicenseEvent(+Property)
    and audit.RawLogFlattened. contoso.com is Microsoft's documentation domain.
*/
SET NOCOUNT ON;

DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);
DECLARE @days  INT  = 182;

/* ---- People ------------------------------------------------------------- */

DROP TABLE IF EXISTS #first, #last, #div, #dept, #org;
CREATE TABLE #first (i INT PRIMARY KEY, n NVARCHAR(40));
INSERT INTO #first VALUES (0,N'Avery'),(1,N'Jordan'),(2,N'Taylor'),(3,N'Morgan'),(4,N'Riley'),(5,N'Casey'),(6,N'Jamie'),(7,N'Alex'),
 (8,N'Sam'),(9,N'Drew'),(10,N'Priya'),(11,N'Wei'),(12,N'Fatima'),(13,N'Mateo'),(14,N'Sofia'),(15,N'Liam'),(16,N'Noor'),(17,N'Hiro'),
 (18,N'Elena'),(19,N'Omar'),(20,N'Grace'),(21,N'Diego'),(22,N'Aisha'),(23,N'Lucas'),(24,N'Mei'),(25,N'Ivan'),(26,N'Chloe'),(27,N'Ravi'),
 (28,N'Nadia'),(29,N'Owen'),(30,N'Leila'),(31,N'Tomas'),(32,N'Yuki'),(33,N'Hannah'),(34,N'Kofi'),(35,N'Ines'),(36,N'Marco'),(37,N'Zara'),
 (38,N'Ben'),(39,N'Amara');
CREATE TABLE #last (i INT PRIMARY KEY, n NVARCHAR(40));
INSERT INTO #last VALUES (0,N'Morgan'),(1,N'Chen'),(2,N'Patel'),(3,N'Nguyen'),(4,N'Garcia'),(5,N'Smith'),(6,N'Kim'),(7,N'Okafor'),(8,N'Rossi'),
 (9,N'Dubois'),(10,N'Silva'),(11,N'Khan'),(12,N'Novak'),(13,N'Tanaka'),(14,N'Murphy'),(15,N'Haddad'),(16,N'Larsen'),(17,N'Mensah'),(18,N'Costa'),
 (19,N'Fischer'),(20,N'Ali'),(21,N'Walsh'),(22,N'Singh'),(23,N'Lopez'),(24,N'Brown'),(25,N'Ivanova'),(26,N'Sato'),(27,N'Moreau'),(28,N'Park'),
 (29,N'Ahmed'),(30,N'Kowalski'),(31,N'Reyes'),(32,N'Clarke'),(33,N'Yilmaz'),(34,N'Bauer'),(35,N'Jensen'),(36,N'Castillo'),(37,N'Adeyemi'),
 (38,N'Hughes'),(39,N'Wong'),(40,N'Pereira'),(41,N'Schmidt'),(42,N'Mahmoud'),(43,N'Olsen'),(44,N'Rivera'),(45,N'Bianchi'),(46,N'Kaur'),
 (47,N'Lindqvist'),(48,N'Osei'),(49,N'Marin'),(50,N'Fraser'),(51,N'Petrov'),(52,N'Abe'),(53,N'Grant'),(54,N'Vargas'),(55,N'Nowak'),(56,N'Ito'),
 (57,N'Barros'),(58,N'Quinn'),(59,N'Saleh');

/* Division: name, the head's title, SharePoint site, license share for staff (%), rollout wave (days ago). */
CREATE TABLE #div (d INT PRIMARY KEY, Division NVARCHAR(40), HeadTitle NVARCHAR(80), Site NVARCHAR(40), LicPct INT, WaveDaysAgo INT, Office NVARCHAR(40), City NVARCHAR(40), Country NVARCHAR(40), Loc NVARCHAR(4));
INSERT INTO #div VALUES
 (0, N'Finance',    N'Chief Financial Officer', N'finance',     55, 120, N'Toronto HQ',  N'Toronto',  N'Canada',         N'CA'),
 (1, N'Operations', N'Chief Operating Officer', N'operations',  35,  80, N'Chicago',     N'Chicago',  N'United States',  N'US'),
 (2, N'Sales',      N'Chief Revenue Officer',   N'sales',       65, 140, N'New York',    N'New York', N'United States',  N'US'),
 (3, N'Technology', N'Chief Technology Officer',N'engineering', 85, 175, N'Toronto HQ',  N'Toronto',  N'Canada',         N'CA'),
 (4, N'People',     N'Chief People Officer',    N'hr',          50, 100, N'London',      N'London',   N'United Kingdom', N'GB'),
 (5, N'Legal',      N'General Counsel',         N'legal',       30,  60, N'Toronto HQ',  N'Toronto',  N'Canada',         N'CA');

CREATE TABLE #dept (d INT, k INT, Department NVARCHAR(60), PRIMARY KEY (d, k));
INSERT INTO #dept VALUES
 (0,0,N'Accounting'),(0,1,N'FP&A'),(0,2,N'Treasury'),(0,3,N'Tax'),(0,4,N'Procurement'),
 (1,0,N'Supply Chain'),(1,1,N'Facilities'),(1,2,N'Customer Care'),(1,3,N'Quality'),(1,4,N'Logistics'),
 (2,0,N'Sales East'),(2,1,N'Sales West'),(2,2,N'Partner Sales'),(2,3,N'Sales Operations'),(2,4,N'Marketing'),
 (3,0,N'Engineering'),(3,1,N'IT Operations'),(3,2,N'Security'),(3,3,N'Data & Analytics'),(3,4,N'Product'),
 (4,0,N'Talent Acquisition'),(4,1,N'HR Business Partners'),(4,2,N'Learning'),(4,3,N'Total Rewards'),(4,4,N'Employee Experience'),
 (5,0,N'Corporate Legal'),(5,1,N'Compliance'),(5,2,N'Contracts'),(5,3,N'Privacy'),(5,4,N'Risk');

/* Ids: 0 the CEO; 1-6 division heads; 7-36 department directors; 37-156 team managers; 157-2316 staff. */
WITH t AS (SELECT TOP (2317) ISNULL(CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS INT), 0) AS id FROM sys.all_columns a CROSS JOIN sys.all_columns b)
SELECT t.id,
       CASE WHEN t.id = 0 THEN 1 WHEN t.id <= 6 THEN 2 WHEN t.id <= 36 THEN 3 WHEN t.id <= 156 THEN 4 ELSE 5 END AS Lvl,
       CASE WHEN t.id = 0 THEN NULL WHEN t.id <= 6 THEN t.id - 1 WHEN t.id <= 36 THEN (t.id - 7) / 5
            WHEN t.id <= 156 THEN (t.id - 37) / 20 ELSE (t.id - 157) / 360 END AS d,
       CASE WHEN t.id <= 6 THEN NULL WHEN t.id <= 36 THEN (t.id - 7) % 5
            WHEN t.id <= 156 THEN ((t.id - 37) / 4) % 5 ELSE ((t.id - 157) / 72) % 5 END AS k,
       CASE WHEN t.id = 0 THEN NULL WHEN t.id <= 6 THEN 0 WHEN t.id <= 36 THEN 1 + (t.id - 7) / 5
            WHEN t.id <= 156 THEN 7 + (t.id - 37) / 4 ELSE 37 + (t.id - 157) / 18 END AS ManagerId,
       f.n AS First, l.n AS Last,
       ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'person', t.id)), 1, 4) AS INT)) AS h
INTO #org
FROM t
JOIN #first AS f ON f.i = t.id % 40
/* Unique names with varied surnames at every level: for a given first name
   (id % 40) the surname index steps with id / 40, which is below 60. */
JOIN #last AS l ON l.i = (t.id / 40 + 17 * (t.id % 40)) % 60;
ALTER TABLE #org ADD PRIMARY KEY (id);
ALTER TABLE #org ADD Upn NVARCHAR(256), FullName NVARCHAR(100);
UPDATE #org SET Upn = LOWER(CONCAT(First, N'.', Last, N'@contoso.com')), FullName = CONCAT(First, N' ', Last);

TRUNCATE TABLE audit.UserDirectory;
INSERT INTO audit.UserDirectory
    (UserPrincipalName, Mail, FullName, EmployeeType, IsManager, AccountEnabled, JobTitle, Department, Division, Office, City, Country,
     Company, ManagerUPN, ManagerName, OrgDepth, Mgr1, Mgr2, Mgr3, Mgr4)
SELECT o.Upn, o.Upn, o.FullName,
       CASE WHEN o.Lvl = 5 AND o.h % 9 = 0 THEN N'CONTRACTOR' ELSE N'EMPLOYEE' END,
       CASE WHEN o.Lvl < 5 THEN 1 ELSE 0 END,
       CASE WHEN o.Lvl = 5 AND o.h % 53 = 0 THEN 0 ELSE 1 END,
       CASE o.Lvl WHEN 1 THEN N'Chief Executive Officer' WHEN 2 THEN dv.HeadTitle
                  WHEN 3 THEN CONCAT(N'Director, ', dp.Department) WHEN 4 THEN CONCAT(N'Manager, ', dp.Department)
                  ELSE CHOOSE(o.h % 5 + 1, N'Analyst', N'Specialist', N'Senior Specialist', N'Associate',
                              CASE WHEN o.d = 3 THEN N'Engineer' ELSE N'Coordinator' END) END,
       CASE WHEN o.Lvl <= 2 THEN COALESCE(dv.Division, N'Executive Office') ELSE dp.Department END,
       COALESCE(dv.Division, N'Executive Office'),
       COALESCE(dv.Office, N'Toronto HQ'), COALESCE(dv.City, N'Toronto'), COALESCE(dv.Country, N'Canada'),
       N'Contoso',
       m.Upn, m.FullName,
       o.Lvl - 1,
       CASE WHEN o.Lvl >= 2 THEN ceo.FullName END,
       CASE WHEN o.Lvl >= 3 THEN l2.FullName END,
       CASE WHEN o.Lvl >= 4 THEN l3.FullName END,
       CASE WHEN o.Lvl >= 5 THEN m.FullName END
FROM #org AS o
LEFT JOIN #div AS dv ON dv.d = o.d
LEFT JOIN #dept AS dp ON dp.d = o.d AND dp.k = o.k
LEFT JOIN #org AS m ON m.id = o.ManagerId
LEFT JOIN #org AS ceo ON ceo.id = 0
LEFT JOIN #org AS l2 ON l2.id = 1 + o.d
LEFT JOIN #org AS l3 ON l3.Lvl = 3 AND l3.d = o.d AND l3.k = o.k;

/* ---- Licenses and their history -------------------------------------------- */

DROP TABLE IF EXISTS #u;
SELECT o.id, o.Upn, o.Lvl, o.d, o.h, dv.Site, dv.Loc,
       /* managers are all licensed; staff by their division's share */
       CAST(CASE WHEN o.Lvl < 5 OR o.h % 100 < dv.LicPct THEN 1 ELSE 0 END AS BIT) AS Licensed,
       DATEADD(DAY, -(COALESCE(dv.WaveDaysAgo, 180) - (o.h / 7) % 21), @today) AS LicensedOn,
       /* 1 in 40 licensed staff lost the license 10-40 days ago */
       CASE WHEN o.Lvl = 5 AND o.h % 40 = 3 THEN DATEADD(DAY, -(10 + o.h % 31), @today) END AS RemovedOn,
       /* engagement: 0 champion, 1 regular, 2 light, 3 dormant; Legal and Operations lean light */
       CASE WHEN (o.h / 101) % 100 + CASE o.d WHEN 5 THEN 15 WHEN 1 THEN 10 ELSE 0 END < 12 THEN 0
            WHEN (o.h / 101) % 100 + CASE o.d WHEN 5 THEN 15 WHEN 1 THEN 10 ELSE 0 END < 52 THEN 1
            WHEN (o.h / 101) % 100 + CASE o.d WHEN 5 THEN 15 WHEN 1 THEN 10 ELSE 0 END < 82 THEN 2 ELSE 3 END AS Seg,
       /* unlicensed people who use the free Copilot Chat */
       CAST(CASE WHEN (o.h / 7919) % 100 < 18 THEN 1 ELSE 0 END AS BIT) AS FreeChat
INTO #u
FROM #org AS o
LEFT JOIN #div AS dv ON dv.d = o.d;
UPDATE #u SET RemovedOn = NULL WHERE Licensed = 0 OR RemovedOn <= LicensedOn;

TRUNCATE TABLE audit.CopilotLicense;
INSERT INTO audit.CopilotLicense (UserPrincipalName, IsCopilotLic, SkuPartNumber, LicensedSinceUtc, CapabilityStatus, DirectoryStatus)
SELECT Upn,
       CASE WHEN Licensed = 1 AND RemovedOn IS NULL THEN 1 ELSE 0 END,
       CASE WHEN Licensed = 1 AND RemovedOn IS NULL THEN N'Microsoft_365_Copilot' END,
       CASE WHEN Licensed = 1 AND RemovedOn IS NULL THEN CAST(DATEADD(HOUR, 14, CAST(LicensedOn AS DATETIME2(0))) AS DATETIME2(0)) END,
       CASE WHEN Licensed = 1 AND RemovedOn IS NULL THEN N'Enabled' END,
       N'Found'
FROM #u;

DELETE FROM audit.LicenseEventProperty;
DELETE FROM audit.LicenseEvent;
DROP TABLE IF EXISTS #ev;
SELECT CONCAT('lic-add-', id) AS RecordId, Upn, DATEADD(HOUR, 14, CAST(LicensedOn AS DATETIME2(0))) AS Ts, 1 AS Added
INTO #ev FROM #u WHERE Licensed = 1
UNION ALL
SELECT CONCAT('lic-rem-', id), Upn, DATEADD(HOUR, 15, CAST(RemovedOn AS DATETIME2(0))), 0 FROM #u WHERE RemovedOn IS NOT NULL;

INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus, ActorUpn, TargetUpn, CorrelationId)
SELECT RecordId, Ts, CAST(Ts AS DATE), N'Change user license', N'Success',
       CASE WHEN Added = 1 THEN N'licensing.admin@contoso.com' ELSE N'it.servicedesk@contoso.com' END, Upn, CONCAT(N'corr-', RecordId)
FROM #ev;
INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source)
SELECT RecordId, 0, N'AssignedLicense',
       CASE WHEN Added = 1 THEN N'["[SkuName=SPE_E5, AccountId=t, SkuId=a, DisabledPlans=[]]"]'
            ELSE N'["[SkuName=SPE_E5, AccountId=t, SkuId=a, DisabledPlans=[]]","[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]"]' END,
       CASE WHEN Added = 1 THEN N'["[SkuName=SPE_E5, AccountId=t, SkuId=a, DisabledPlans=[]]","[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]"]'
            ELSE N'["[SkuName=SPE_E5, AccountId=t, SkuId=a, DisabledPlans=[]]"]' END,
       'record'
FROM #ev;

/* ---- Activity: person-days, then sessions, then prompts ---------------------- */

DROP TABLE IF EXISTS #days, #active, #sess;
WITH t AS (SELECT TOP (@days) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns)
SELECT DATEADD(DAY, -t.n, @today) AS Day, t.n AS DaysAgo,
       CASE WHEN DATEPART(WEEKDAY, DATEADD(DAY, -t.n, @today)) IN (1, 7) THEN 1 ELSE 0 END AS Weekend
INTO #days FROM t;
/* DATEPART(WEEKDAY) depends on DATEFIRST; pin it. */
UPDATE #days SET Weekend = CASE WHEN (DATEDIFF(DAY, '19000101', Day) % 7) IN (5, 6) THEN 1 ELSE 0 END;

/* Probability (per mille) that a person uses Copilot on a day, ramping up over their first four weeks. */
SELECT u.id, u.Upn, u.d, u.Seg, u.Site, u.Loc, dd.Day, dd.DaysAgo,
       CASE WHEN u.Licensed = 1 AND dd.Day >= u.LicensedOn AND (u.RemovedOn IS NULL OR dd.Day < u.RemovedOn) THEN 1 ELSE 0 END AS LicensedToday,
       ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'day', u.id, N'-', dd.DaysAgo)), 1, 4) AS INT)) AS hd
INTO #active
FROM #u AS u
CROSS JOIN #days AS dd
WHERE (u.Licensed = 1 AND u.Seg < 3 AND dd.Day >= u.LicensedOn AND (u.RemovedOn IS NULL OR dd.Day < u.RemovedOn))
   OR (u.FreeChat = 1 AND (u.Licensed = 0 OR dd.Day < u.LicensedOn));

DELETE a FROM #active AS a
JOIN #u AS u ON u.id = a.id
JOIN #days AS dd ON dd.Day = a.Day
WHERE a.hd % 1000 >=
      (CASE WHEN a.LicensedToday = 0 THEN 60
            ELSE CASE a.Seg WHEN 0 THEN 850 WHEN 1 THEN 450 ELSE 140 END END
       * CASE WHEN dd.Weekend = 1 THEN 1 ELSE 10 END / 10)
      * CASE WHEN a.LicensedToday = 1 AND DATEDIFF(DAY, u.LicensedOn, a.Day) < 28
             THEN (DATEDIFF(DAY, u.LicensedOn, a.Day) + 4) / 32.0 ELSE 1.0 END;

/* Sessions: champions 2-4 a day, regulars 1-2, others 1. */
WITH k AS (SELECT n FROM (VALUES (1), (2), (3), (4)) AS v (n))
SELECT ROW_NUMBER() OVER (ORDER BY a.id, a.Day, k.n) AS SessionId, a.*, k.n AS SessionNo,
       ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'sess', a.id, N'-', a.DaysAgo, N'-', k.n)), 1, 4) AS INT)) AS hs,
       ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'sess', a.id, N'-', a.DaysAgo, N'-', k.n)), 5, 4) AS INT)) AS hs2
INTO #sess
FROM #active AS a
JOIN k ON k.n <= CASE WHEN a.LicensedToday = 0 THEN 1
                      WHEN a.Seg = 0 THEN 2 + a.hd % 3
                      WHEN a.Seg = 1 THEN 1 + a.hd % 2 ELSE 1 END;

ALTER TABLE #sess ADD App NVARCHAR(60), AgentName NVARCHAR(60), AgentSource NVARCHAR(20), StartUtc DATETIME2(0), Prompts INT,
                      Model NVARCHAR(40), Provider NVARCHAR(40);

/* The app: licensed people across Microsoft 365; the free tier in Copilot Chat only. */
UPDATE #sess SET App =
    CASE WHEN LicensedToday = 0 THEN CHOOSE(hs % 3 + 1, N'Office', N'Office', N'BizChat')
         ELSE CHOOSE(hs % 20 + 1, N'Office', N'Office', N'Office', N'Office', N'Office', N'Office',
                     N'Microsoft Teams', N'Microsoft Teams', N'Microsoft Teams', N'Outlook', N'Outlook', N'Outlook',
                     N'Word', N'Word', N'Excel', N'PowerPoint', N'BizChat', N'Copilot Studio', N'Copilot Studio', N'Edge') END;

/* Copilot Studio agents, each live from its launch; divisions lean to their own. */
DROP TABLE IF EXISTS #agent;
CREATE TABLE #agent (a INT PRIMARY KEY, Name NVARCHAR(60), LaunchDaysAgo INT, HomeDivision INT);
INSERT INTO #agent VALUES (0, N'crd20_expenseAssistant', 170, 0), (1, N'crd20_itHelpdesk', 150, 3), (2, N'crd20_hrPolicyHelper', 120, 4),
    (3, N'crd20_salesProposalWriter', 100, 2), (4, N'crd20_contractReviewer', 80, 5), (5, N'crd20_onboardingBuddy', 60, 4),
    (6, N'crd20_financeCloseAgent', 40, 0), (7, N'crd20_travelBooker', 15, 1);
UPDATE s SET AgentName = g.Name, AgentSource = N'Copilot Studio'
FROM #sess AS s
CROSS APPLY (SELECT TOP (1) ag.Name FROM #agent AS ag
             WHERE ag.LaunchDaysAgo >= s.DaysAgo
             ORDER BY CASE WHEN ag.HomeDivision = s.d AND s.hs2 % 3 > 0 THEN 0 ELSE 1 END, (s.hs2 / 3 + ag.a * 7) % 11) AS g
WHERE s.App = N'Copilot Studio';
UPDATE #sess SET App = N'Office' WHERE App = N'Copilot Studio' AND AgentName IS NULL;
/* Microsoft's own agents in some Copilot Chat sessions. */
UPDATE #sess SET AgentName = CHOOSE(hs2 % 2 + 1, N'Researcher', N'Analyst'), AgentSource = N'Microsoft'
WHERE App = N'Office' AND LicensedToday = 1 AND hs2 % 100 < 6 AND DaysAgo < 120;

/* Start: working hours Eastern (UTC-4 here), a few people also late at night. */
UPDATE #sess SET StartUtc = DATEADD(MINUTE, hs2 % 50,
    DATEADD(HOUR, 4 + CASE WHEN id % 97 = 5 AND hs % 5 = 0 THEN CHOOSE(hs % 4 + 1, 22, 23, 1, 2) ELSE 8 + hs % 10 END,
            CAST(Day AS DATETIME2(0))));

/* Prompts per session: champions go deeper, light users mostly one-shot. */
UPDATE #sess SET Prompts =
    CASE WHEN LicensedToday = 0 THEN 1 + hs2 % 2
         WHEN Seg = 0 THEN 2 + hs2 % 5
         WHEN Seg = 1 THEN 1 + hs2 % 4
         ELSE CASE WHEN hs2 % 10 < 7 THEN 1 ELSE 2 END END;

/* Models: gpt-4o fading out, gpt-5 the default, Claude from 60 days ago. */
UPDATE #sess SET Model = CASE WHEN DaysAgo > 120 AND hs % 10 < 6 THEN N'gpt-4o'
                              WHEN DaysAgo <= 60 AND hs % 10 < 2 THEN N'claude-sonnet-4-6'
                              WHEN DaysAgo <= 120 AND DaysAgo > 60 AND hs % 10 < 2 THEN N'gpt-4o'
                              ELSE N'gpt-5' END;
UPDATE #sess SET Provider = CASE WHEN Model LIKE N'claude%' THEN N'Anthropic' ELSE N'OpenAI' END;

/* ---- Interactions ------------------------------------------------------------------ */

DROP TABLE IF EXISTS #files;
CREATE TABLE #files (f INT PRIMARY KEY, FileName NVARCHAR(100));
INSERT INTO #files VALUES (0, N'Q3 Forecast.xlsx'), (1, N'Board Deck.pptx'), (2, N'Team Plan.docx'), (3, N'Customer List.xlsx'),
    (4, N'Policy Handbook.docx'), (5, N'Pricing Model.xlsx'), (6, N'Project Status.docx'), (7, N'Meeting Notes.docx'),
    (8, N'Salary Bands.xlsx'), (9, N'Contract Template.docx'), (10, N'Architecture.vsdx'), (11, N'Roadmap.pptx');

TRUNCATE TABLE audit.RawLogFlattened;
WITH k AS (SELECT TOP (6) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns),
p AS
(
    SELECT s.*, k.n AS PromptNo,
           DATEADD(SECOND, (k.n - 1) * (60 + s.hs % 480), s.StartUtc) AS Utc,
           ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'p', s.SessionId, N'-', k.n)), 1, 4) AS INT)) AS hp,
           ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'p', s.SessionId, N'-', k.n)), 5, 4) AS INT)) AS hp2
    FROM #sess AS s
    JOIN k ON k.n <= s.Prompts
)
INSERT INTO audit.RawLogFlattened WITH (TABLOCK)
    (RecordId, [TimeStamp], UtcTimeStamp, [User], App, Location, [App context], [Accessed Resources], [Accessed Resource Locations],
     [Action], AIPlugin, JailbreakDetected, PromptInjectionDetected, BlockedResourceUrls, SensitivityLabelIds,
     IsCopilotLic, AppIdentity, ThreadId, ModelName, ModelProvider, AccessedResourceCount, PromptCount)
SELECT CONCAT(N'demo-', p.SessionId, N'-', p.PromptNo),
       CONVERT(NVARCHAR(30), p.Utc, 126) + N'Z',
       p.Utc, p.Upn, p.App, p.Loc,
       NULL,
       CASE WHEN r.Touches = 1 THEN fl.FileName END,
       CASE WHEN r.Touches = 1 THEN CASE WHEN p.hp % 5 = 0
                                        THEN CONCAT(N'https://contoso-my.sharepoint.com/personal/', REPLACE(REPLACE(p.Upn, N'.', N'_'), N'@', N'_'), N'/Documents/', fl.FileName)
                                        ELSE CONCAT(N'https://contoso.sharepoint.com/sites/', CASE WHEN p.hp % 7 = 0 THEN N'allcompany' ELSE ISNULL(p.Site, N'allcompany') END,
                                                    N'/Shared Documents/', fl.FileName) END END,
       CASE WHEN r.Touches = 1 THEN N'Read' END,
       CASE WHEN p.App IN (N'Office', N'BizChat', N'Edge') AND p.hp % 4 = 0 THEN N'BingWebSearch' END,
       CASE WHEN p.hp % 2500 = 17 OR (p.id % 211 = 7 AND p.hp % 40 = 0) THEN N'true' ELSE N'false' END,
       CASE WHEN r.Touches = 1 AND p.hp2 % 3500 = 11 THEN N'true' ELSE N'false' END,
       CASE WHEN p.hp2 % 700 = 5 THEN N'https://contoso.sharepoint.com/sites/hr/Payroll/Salary Bands.xlsx' END,
       CASE WHEN r.Touches = 1 AND p.hp2 % 100 < CASE WHEN p.d IN (0, 4, 5) THEN 30 ELSE 15 END
            THEN CONCAT(N'5e1f0c2a-0000-4000-8000-00000000000',
                        CASE WHEN p.hp2 % 100 < 4 THEN N'0' WHEN p.hp2 % 100 < 13 AND p.d IN (0, 4, 5) THEN N'3'
                             WHEN p.hp2 % 100 < 16 THEN N'2' ELSE N'1' END) END,
       p.LicensedToday,
       CASE WHEN p.AgentSource = N'Copilot Studio'
            THEN CONCAT(N'Copilot.Studio.Default-5e1f0c2a-1111-4000-8000-000000000000-', p.AgentName)
            WHEN p.AgentSource = N'Microsoft' THEN CONCAT(N'MicrosoftAgent.', p.AgentName, N'.BuiltIn_', p.AgentName, N'Agent')
            ELSE CONCAT(N'Copilot.M365Copilot.', REPLACE(p.App, N' ', N'')) END,
       CONCAT(N'thread-demo-', p.SessionId),
       p.Model, p.Provider,
       CASE WHEN r.Touches = 1 THEN 1 + p.hp % 3 ELSE 0 END,
       1
FROM p
CROSS APPLY (SELECT CASE WHEN p.App IN (N'Word', N'Excel', N'PowerPoint', N'Microsoft Teams', N'Outlook') OR p.hp % 10 < 4 THEN 1 ELSE 0 END AS Touches) AS r
JOIN #files AS fl ON fl.f = CASE WHEN p.d = 4 AND p.hp % 9 = 0 THEN 8 WHEN p.d = 5 AND p.hp % 5 = 0 THEN 9 ELSE p.hp % 12 END;

/* What the rest of the record says: license type, memory, and calls to another agent. */
UPDATE audit.RawLogFlattened
SET CopilotEventData_LicenseType = CASE WHEN IsCopilotLic = 1 THEN N'Premium' ELSE N'Starter' END,
    CopilotEventData_MemoryUpdated = CASE WHEN ABS(CHECKSUM(RecordId)) % 40 = 0 THEN 1 ELSE 0 END,
    CopilotEventData_TargetAgentName = CASE WHEN AppIdentity LIKE N'MicrosoftAgent.Researcher.%' THEN N'Researcher'
                                            WHEN AppIdentity LIKE N'MicrosoftAgent.Analyst.%' THEN N'Analyst' END,
    OrganizationId = '5e1f0c2a-1111-4000-8000-000000000000',
    MessageCount = 2 * PromptCount;

/* The element rows the flattening would have written for those records: the
   file each read, and the blocked payroll file. Sites cut as the flattening
   cuts them (all demo URLs are under /sites/ or /personal/). */
TRUNCATE TABLE audit.RawLogAccessedResource;
INSERT INTO audit.RawLogAccessedResource WITH (TABLOCK)
    (RecordId, Ordinal, [Action], [Type], Name, SiteUrl, Site, SensitivityLabelId, XPIADetected, [Status], IsBlocked)
SELECT f.RecordId, x.Ordinal, N'Read', x.[Type], x.Name, x.Url,
       LEFT(x.Url, CHARINDEX(N'/', x.Url + N'/', CHARINDEX(N'/', x.Url + N'/', CHARINDEX(N'/', x.Url, 9) + 1) + 1) - 1),
       x.Label, CASE WHEN x.Ordinal = 0 AND f.PromptInjectionDetected = N'true' THEN 1 ELSE 0 END, x.[Status], x.Blocked
FROM audit.RawLogFlattened AS f
CROSS APPLY (
    SELECT 0 AS Ordinal, N'File' AS [Type], f.[Accessed Resources] AS Name, f.[Accessed Resource Locations] AS Url,
           TRY_CONVERT(UNIQUEIDENTIFIER, f.SensitivityLabelIds) AS Label, CAST(NULL AS VARCHAR(64)) AS [Status], CAST(0 AS BIT) AS Blocked
    WHERE f.[Accessed Resource Locations] IS NOT NULL
    UNION ALL
    SELECT 1, N'File', N'Salary Bands.xlsx', f.BlockedResourceUrls, NULL, 'Failure', CAST(1 AS BIT)
    WHERE f.BlockedResourceUrls IS NOT NULL
) AS x;

/* Temp tables go now: the session and bi procedures have their own of the same names. */
DROP TABLE IF EXISTS #first, #last, #div, #dept, #org, #u, #ev, #days, #active, #sess, #agent, #files;

SELECT (SELECT COUNT(*) FROM audit.UserDirectory) AS People,
       (SELECT COUNT(*) FROM audit.CopilotLicense WHERE IsCopilotLic = 1) AS LicensedToday,
       (SELECT COUNT(*) FROM audit.LicenseEvent) AS LicenseEvents,
       (SELECT COUNT(DISTINCT ThreadId) FROM audit.RawLogFlattened) AS Sessions,
       (SELECT COUNT_BIG(*) FROM audit.RawLogFlattened) AS Interactions,
       (SELECT COUNT(DISTINCT [User]) FROM audit.RawLogFlattened) AS ActiveEver;
