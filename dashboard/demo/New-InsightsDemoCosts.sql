/*
    Copilot Insights demo: what the company spends on AI and automation beyond its Copilot licenses (2.6.6), for
    Leadership > Costs and savings. Run after New-InsightsDemoOperations.sql (the license pool) and before the
    dashboard's refresh. Deterministic, relative to today, like the rest of the demo.

    The story, written so every rule on the page has something to find:
      - Fabric: three capacities. fab-prod-eus, an F64 (F128 from 3,500 people) on a one-year reservation, busy on weekdays, peaking over
        100% at month end and throttled then, with Copilot in Power BI growing from almost nothing to about a tenth
        of it. fab-dev-eus, an F32 (F64) on pay-as-you-go running around the clock at under a fifth of its size and all
        but idle at weekends. fab-sandbox-cac, an F8 on pay-as-you-go nobody has used in weeks.
      - Power Platform licenses from Graph: Power BI Pro and Premium Per User, Power Automate Premium and Process,
        Power Apps Premium, and four Copilot Credit packs; each held by people chosen by department, a part of whom
        have not used the product in over a month.
      - About 430 cloud flows in five environments, with their owners, premium or not, how they are licensed and
        their runs: Process licenses on flows that run a few times a month, and a handful failing often.
      - Dataverse capacity growing past the database entitlement six weeks ago, and logs over theirs.
      - Copilot Studio credits outgrowing the four prepaid packs, the overflow billed pay-as-you-go.
      - GitHub Copilot: 180 seats for every 2,300 people (390 at 5,000), a fifth of them idle, with GitHub's bill.
      - Foundry: an Azure OpenAI account in daily use, a provisioned (PTU) deployment nobody has called in five
        weeks, and a sandbox account with a little cost and no requests.
*/
SET NOCOUNT ON;

DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);
DECLARE @days INT = 182;
/* Everything scales with the company: the counts below are for about 2,300 people, the demo before 2.6.6, and are
   multiplied out for the size New-InsightsDemo.ps1 built (5,000 by default). */
/* People in the org chart: the service accounts New-InsightsDemoTenant.sql adds (no manager, and nobody under them) are not. */
DECLARE @people INT = (SELECT COUNT(*) FROM audit.UserDirectory WHERE AccountEnabled = 1 AND (ManagerUPN IS NOT NULL OR IsManager = 1));
DECLARE @scale FLOAT = @people / 2300.0;
DECLARE @big BIT = CASE WHEN @people >= 3500 THEN 1 ELSE 0 END;   -- a company this size runs a capacity twice the size

DROP TABLE IF EXISTS #n, #day;
SELECT TOP (5000) ISNULL(CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS INT), 0) AS n INTO #n FROM sys.all_columns a CROSS JOIN sys.all_columns b;
ALTER TABLE #n ADD PRIMARY KEY (n);
/* Every day of the six months, oldest first: its weekday (Monday 1 ... Sunday 7), the day of the month, and how far through the six months it is (0 to 1). */
SELECT ISNULL(DATEADD(DAY, -n, @today), '19000101') AS [Day], n AS Ago,
       (DATEDIFF(DAY, '19000101', DATEADD(DAY, -n, @today)) % 7) + 1 AS Dow,
       DAY(DATEADD(DAY, -n, @today)) AS Dom,
       DAY(EOMONTH(DATEADD(DAY, -n, @today))) AS MonthDays,
       CAST(@days - n AS FLOAT) / @days AS Progress
INTO #day FROM #n WHERE n < @days;
ALTER TABLE #day ADD PRIMARY KEY ([Day]);

/* A repeatable "random" number from 0 to 1 for any text. */
DROP TABLE IF EXISTS #people;
SELECT d.UserPrincipalName AS Upn, d.Department, d.Division, d.OrgDepth, d.IsManager,
       ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'cost', d.UserPrincipalName)), 1, 4) AS INT)) % 10000 / 10000.0 AS R1,
       ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'cost2', d.UserPrincipalName)), 1, 4) AS INT)) % 10000 / 10000.0 AS R2,
       ROW_NUMBER() OVER (ORDER BY HASHBYTES('MD5', CONCAT(N'order', d.UserPrincipalName))) AS Seq
INTO #people FROM audit.UserDirectory AS d WHERE d.AccountEnabled = 1;

/* ---- Suites (2.6.6): Microsoft 365 E3 for operations and the front line, E5 for the rest, and an E7 pilot ---- */
/* E7: the executive team and the Technology division's Copilot holders, about one in nine of those licensed. Most
   dropped the Copilot add-on when they moved to E7; a few still hold it, and a few kept E5 beside it. The disabled
   accounts keep the E5 they had. */
DROP TABLE IF EXISTS #suite;
CREATE TABLE #suite (Upn NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, Suite NVARCHAR(10) NOT NULL, AddOn BIT NOT NULL, AlsoE5 BIT NOT NULL);
DECLARE @e7 INT = CAST(ROUND(120 * @scale, 0) AS INT);
INSERT INTO #suite (Upn, Suite, AddOn, AlsoE5)
SELECT TOP (@e7) p.Upn, N'E7', CASE WHEN p.R2 < 0.15 THEN 1 ELSE 0 END, CASE WHEN p.R2 > 0.95 THEN 1 ELSE 0 END
FROM #people AS p JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = p.Upn AND cl.IsCopilotLic = 1
ORDER BY CASE WHEN p.OrgDepth <= 2 AND p.IsManager = 1 THEN 0 WHEN p.Division = N'Technology' THEN 1 ELSE 2 END, p.R1;
INSERT INTO #suite (Upn, Suite, AddOn, AlsoE5)
SELECT p.Upn, CASE WHEN p.Division = N'Operations' OR p.Department IN (N'Customer Care', N'Facilities', N'Logistics', N'Quality') OR p.R1 < 0.10 THEN N'E3' ELSE N'E5' END, 0, 0
FROM #people AS p WHERE NOT EXISTS (SELECT 1 FROM #suite AS s WHERE s.Upn = p.Upn);
INSERT INTO #suite (Upn, Suite, AddOn, AlsoE5)
SELECT d.UserPrincipalName, N'E5', 0, 0 FROM audit.UserDirectory AS d
WHERE d.AccountEnabled = 0 AND NOT EXISTS (SELECT 1 FROM #suite AS s WHERE s.Upn = d.UserPrincipalName);

/* The pool: each suite bought with a little to spare, and the Copilot add-on for everyone licensed who is not on E7
   alone, with about 3% bought and never assigned. */
DECLARE @nE3 INT = (SELECT COUNT(*) FROM #suite WHERE Suite = N'E3'),
        @nE5 INT = (SELECT COUNT(*) FROM #suite WHERE Suite = N'E5' OR AlsoE5 = 1),
        @nE7 INT = (SELECT COUNT(*) FROM #suite WHERE Suite = N'E7');
DECLARE @addOn INT = (SELECT COUNT(*) FROM audit.CopilotLicense AS cl WHERE cl.IsCopilotLic = 1
                      AND NOT EXISTS (SELECT 1 FROM #suite AS s WHERE s.Upn = cl.UserPrincipalName AND s.Suite = N'E7' AND s.AddOn = 0));
DROP TABLE IF EXISTS #pool;
CREATE TABLE #pool (SkuId UNIQUEIDENTIFIER NOT NULL, Part NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL, IsCopilot BIT NOT NULL, Bought INT NOT NULL, Held INT NOT NULL);
INSERT INTO #pool VALUES
    ('05e9a617-0261-4cee-bb44-138d3ef5d965', N'SPE_E3',                0, (@nE3 + 60) / 10 * 10, @nE3),
    ('06ebc4ee-1bb5-47dd-8120-11324bc54e06', N'SPE_E5',                0, (@nE5 + 80) / 10 * 10, @nE5),
    ('7e7e7e70-0e70-4e70-9e70-7e7e7e7e7e70', N'Microsoft_365_E7',      1, @nE7 + 10,             @nE7),
    ('639dec6b-bb19-468b-871c-c5c441c4b0cb', N'Microsoft_365_Copilot', 1, (@addOn * 103 / 100 + 9) / 10 * 10, @addOn);
DELETE FROM audit.LicenseSkuDay WHERE SkuPartNumber IN (SELECT Part FROM #pool);
INSERT INTO audit.LicenseSkuDay (SnapshotDate, SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed)
SELECT d.[Day], p.SkuId, p.Part, p.IsCopilot, p.Bought, 0, 0, 0, p.Held - CASE WHEN p.Part = N'Microsoft_365_Copilot' THEN d.Ago / 3 ELSE 0 END
FROM #day AS d CROSS JOIN #pool AS p;
DELETE FROM audit.LicenseSku WHERE SkuPartNumber IN (SELECT Part FROM #pool);
INSERT INTO audit.LicenseSku (SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed, CapabilityStatus)
SELECT SkuId, Part, IsCopilot, Bought, 0, 0, 0, Held, N'Enabled' FROM #pool;
IF COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NOT NULL
    EXEC (N'UPDATE audit.LicenseSku SET CopilotInSuite = CASE WHEN SkuPartNumber = N''Microsoft_365_E7'' THEN 1 ELSE 0 END;');

/* Who holds what: each person's suite, the E5 a few kept beside E7, and the add-on. */
DELETE u FROM audit.UserLicenseSku AS u JOIN #pool AS p ON p.SkuId = u.SkuId;
INSERT INTO audit.UserLicenseSku (UserPrincipalName, SkuId)
SELECT s.Upn, p.SkuId FROM #suite AS s JOIN #pool AS p ON p.Part = CASE s.Suite WHEN N'E3' THEN N'SPE_E3' WHEN N'E5' THEN N'SPE_E5' ELSE N'Microsoft_365_E7' END
UNION
SELECT s.Upn, p.SkuId FROM #suite AS s JOIN #pool AS p ON p.Part = N'SPE_E5' WHERE s.AlsoE5 = 1
UNION
SELECT cl.UserPrincipalName, p.SkuId FROM audit.CopilotLicense AS cl JOIN #pool AS p ON p.Part = N'Microsoft_365_Copilot'
WHERE cl.IsCopilotLic = 1 AND NOT EXISTS (SELECT 1 FROM #suite AS s WHERE s.Upn = cl.UserPrincipalName AND s.Suite = N'E7' AND s.AddOn = 0);
UPDATE cl SET SkuPartNumber = N'Microsoft_365_E7'
FROM audit.CopilotLicense AS cl JOIN #suite AS s ON s.Upn = cl.UserPrincipalName AND s.Suite = N'E7' AND s.AddOn = 0;

/* ---- Power Platform licenses (the pool, from Graph's subscribedSkus) ---- */
/* Each SKU: bought now, held now, and how many were held six months ago (it grows to now in a straight line). */
DROP TABLE IF EXISTS #sku;
CREATE TABLE #sku (SkuId UNIQUEIDENTIFIER, Part NVARCHAR(100), Bought INT, HeldNow INT, HeldThen INT);
INSERT INTO #sku
SELECT SkuId, Part, CAST(ROUND(Bought * @scale, 0) AS INT), CAST(ROUND(HeldNow * @scale, 0) AS INT), CAST(ROUND(HeldThen * @scale, 0) AS INT)
FROM (VALUES
    ('f8a1db68-be16-40ed-86d5-cb42ce701560', N'POWER_BI_PRO',                  640,  600,  545),
    ('c1d032e0-5619-4761-9b5c-75b6831e1711', N'PBI_PREMIUM_PER_USER',           60,   52,   38),
    ('eda1941c-3c4f-4995-b5eb-e85a42175ab9', N'POWERAUTOMATE_ATTENDED_RPA',    320,  296,  205),
    ('3539d28c-6e35-4a30-b3a9-cd43d5d3e0e2', N'POWERAUTOMATE_UNATTENDED_RPA',   12,   12,    8),
    ('b30411f5-fea1-4a59-9ad9-3db7c7ead579', N'POWERAPPS_PER_USER',            240,  231,  170),
    ('4b74a65c-8b4a-4fc8-9f6b-5177ed11ddfa', N'COPILOT_STUDIO_PACK',             4,    4,    2)) AS v (SkuId, Part, Bought, HeldNow, HeldThen);
DECLARE @nPro INT = (SELECT HeldNow FROM #sku WHERE Part = N'POWER_BI_PRO'), @nPpu INT = (SELECT HeldNow FROM #sku WHERE Part = N'PBI_PREMIUM_PER_USER'),
        @nPa INT = (SELECT HeldNow FROM #sku WHERE Part = N'POWERAUTOMATE_ATTENDED_RPA'), @nApps INT = (SELECT HeldNow FROM #sku WHERE Part = N'POWERAPPS_PER_USER'),
        @packsNow INT = (SELECT HeldNow FROM #sku WHERE Part = N'COPILOT_STUDIO_PACK'), @packsThen INT = (SELECT HeldThen FROM #sku WHERE Part = N'COPILOT_STUDIO_PACK');
DELETE FROM audit.LicenseSkuDay WHERE SkuPartNumber IN (SELECT Part FROM #sku);
INSERT INTO audit.LicenseSkuDay (SnapshotDate, SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed)
SELECT d.[Day], s.SkuId, s.Part, 0, s.Bought, 0, 0, 0, CAST(ROUND(s.HeldThen + (s.HeldNow - s.HeldThen) * d.Progress, 0) AS INT)
FROM #day AS d CROSS JOIN #sku AS s;
DELETE FROM audit.LicenseSku WHERE SkuPartNumber IN (SELECT Part FROM #sku);
INSERT INTO audit.LicenseSku (SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed, CapabilityStatus)
SELECT SkuId, Part, 0, Bought, 0, 0, 0, HeldNow, N'Enabled' FROM #sku;

/* Who holds them. Power BI Pro: most of Finance, Sales, Technology and Operations' managers and analysts.
   Premium Per User: Data & Analytics and FP&A. Power Automate Premium and Power Apps Premium: the makers, most
   in Operations, Finance and IT. */
DROP TABLE IF EXISTS #held;
CREATE TABLE #held (Upn NVARCHAR(256), Part NVARCHAR(100));
INSERT INTO #held
SELECT TOP (@nPro) p.Upn, N'POWER_BI_PRO' FROM #people AS p JOIN #suite AS su ON su.Upn = p.Upn
ORDER BY CASE WHEN su.Suite <> N'E3' AND p.R2 > 0.965 THEN 0 WHEN su.Suite = N'E3' THEN 1 ELSE 2 END,
         CASE WHEN p.Division IN (N'Finance', N'Sales', N'Technology') THEN 0 WHEN p.Division = N'Operations' THEN 1 ELSE 2 END, p.R1;
INSERT INTO #held
SELECT TOP (@nPpu) p.Upn, N'PBI_PREMIUM_PER_USER' FROM #people AS p
ORDER BY CASE WHEN p.Department IN (N'Data & Analytics', N'FP&A') THEN 0 ELSE 1 END, p.R2;
INSERT INTO #held
SELECT TOP (@nPa) p.Upn, N'POWERAUTOMATE_ATTENDED_RPA' FROM #people AS p
ORDER BY CASE WHEN p.Department IN (N'IT Operations', N'Supply Chain', N'Accounting', N'Customer Care', N'Logistics', N'Procurement', N'Sales Operations') THEN 0 ELSE 1 END, p.R2;
INSERT INTO #held
SELECT TOP (@nApps) p.Upn, N'POWERAPPS_PER_USER' FROM #people AS p
ORDER BY CASE WHEN p.Department IN (N'Facilities', N'Quality', N'Customer Care', N'HR Business Partners', N'IT Operations', N'Logistics') THEN 0 ELSE 1 END, p.R1 DESC;
DELETE u FROM audit.UserLicenseSku AS u JOIN #sku AS s ON s.SkuId = u.SkuId;
INSERT INTO audit.UserLicenseSku (UserPrincipalName, SkuId)
SELECT DISTINCT h.Upn, s.SkuId FROM #held AS h JOIN #sku AS s ON s.Part = h.Part;

/* ---- Who used each product lately (the admin center's usage export) ---- */
/* Most holders are active; a share have not opened the product in over a month: about a fifth of Power Automate
   Premium, a sixth of Power Apps Premium, one in seven Power BI Pro, one in eight Premium Per User. */
DELETE FROM audit.PowerPlatformUserActivity;
INSERT INTO audit.PowerPlatformUserActivity (UserPrincipalName, Product, LastActiveUtc, ActiveDays30)
SELECT h.Upn, x.Product,
       CASE WHEN x.Idle = 1 THEN DATEADD(DAY, -(35 + CAST(p.R1 * 120 AS INT)), CAST(@today AS DATETIME2(0)))
            ELSE DATEADD(HOUR, -(1 + CAST(p.R2 * 160 AS INT)), CAST(@today AS DATETIME2(0))) END,
       CASE WHEN x.Idle = 1 THEN 0 ELSE 2 + CAST(p.R2 * 19 AS INT) END
FROM (SELECT Upn, MIN(Part) AS Part FROM #held GROUP BY Upn, CASE WHEN Part LIKE N'P%BI%' OR Part = N'POWER_BI_PRO' THEN 1 WHEN Part LIKE N'POWERAUTOMATE%' THEN 2 ELSE 3 END) AS h
JOIN #people AS p ON p.Upn = h.Upn
CROSS APPLY (SELECT CASE WHEN h.Part IN (N'POWER_BI_PRO', N'PBI_PREMIUM_PER_USER') THEN 'Power BI'
                         WHEN h.Part LIKE N'POWERAUTOMATE%' THEN 'Power Automate' ELSE 'Power Apps' END AS Product,
                    CASE WHEN h.Part = N'POWERAUTOMATE_ATTENDED_RPA' AND p.R1 < 0.21 THEN 1
                         WHEN h.Part = N'POWERAPPS_PER_USER' AND p.R1 > 0.835 THEN 1
                         WHEN h.Part = N'POWER_BI_PRO' AND p.R2 < 0.14 THEN 1
                         WHEN h.Part = N'PBI_PREMIUM_PER_USER' AND p.R2 > 0.875 THEN 1 ELSE 0 END AS Idle) AS x;
/* Power BI through E5 and E7: two in five open it in a month, more in Finance and Sales; the rest never do. */
INSERT INTO audit.PowerPlatformUserActivity (UserPrincipalName, Product, LastActiveUtc, ActiveDays30)
SELECT s.Upn, 'Power BI', DATEADD(HOUR, -(1 + CAST(p.R2 * 300 AS INT)), CAST(@today AS DATETIME2(0))), 1 + CAST(p.R1 * 14 AS INT)
FROM #suite AS s JOIN #people AS p ON p.Upn = s.Upn
WHERE s.Suite IN (N'E5', N'E7') AND (p.R1 < 0.40 OR p.Division IN (N'Finance', N'Sales'))
  AND NOT EXISTS (SELECT 1 FROM audit.PowerPlatformUserActivity AS a WHERE a.UserPrincipalName = s.Upn AND a.Product = 'Power BI');
/* Copilot Studio's makers: the people who build the company's agents. */
INSERT INTO audit.PowerPlatformUserActivity (UserPrincipalName, Product, LastActiveUtc, ActiveDays30)
SELECT TOP (CAST(64 * @scale AS INT)) p.Upn, 'Copilot Studio', DATEADD(HOUR, -(2 + CAST(p.R1 * 90 AS INT)), CAST(@today AS DATETIME2(0))), 1 + CAST(p.R2 * 16 AS INT)
FROM #people AS p WHERE NOT EXISTS (SELECT 1 FROM audit.PowerPlatformUserActivity AS a WHERE a.UserPrincipalName = p.Upn AND a.Product = 'Copilot Studio')
ORDER BY CASE WHEN p.Department IN (N'IT Operations', N'Data & Analytics', N'Employee Experience', N'Customer Care') THEN 0 ELSE 1 END, p.R2;

/* ---- Cloud flows ---- */
DROP TABLE IF EXISTS #flowname;
CREATE TABLE #flowname (i INT PRIMARY KEY, Env NVARCHAR(100), Name NVARCHAR(200), Premium BIT, Minutes DECIMAL(9, 2), RunsPerDay DECIMAL(9, 2));
INSERT INTO #flowname VALUES
 (0,  N'Finance Automation', N'Invoice approval routing',                1, 12, 85),
 (1,  N'Finance Automation', N'Vendor onboarding with SAP lookup',       1, 25, 6),
 (2,  N'Finance Automation', N'Expense report reminders',                0, 2, 40),
 (3,  N'Finance Automation', N'Month-end close checklist',               0, 15, 1.5),
 (4,  N'Finance Automation', N'Purchase order to Dynamics 365',          1, 9, 60),
 (5,  N'Finance Automation', N'Payment run notification',                0, 3, 4),
 (6,  N'HR Services',        N'New hire onboarding',                     1, 45, 3),
 (7,  N'HR Services',        N'Leave request approval',                  0, 4, 55),
 (8,  N'HR Services',        N'Offboarding: revoke access tickets',      1, 30, 2),
 (9,  N'HR Services',        N'Training completion to Workday',          1, 4, 30),
 (10, N'IT Operations',      N'ServiceNow incident to Teams',            1, 1, 220),
 (11, N'IT Operations',      N'Laptop request approval',                 0, 10, 12),
 (12, N'IT Operations',      N'License request to group membership',     1, 8, 18),
 (13, N'IT Operations',      N'Certificate expiry alerts',               0, 20, 0.6),
 (14, N'Sales Ops',          N'Lead assignment from web form',           1, 3, 140),
 (15, N'Sales Ops',          N'Contract signed to Salesforce',           1, 14, 9),
 (16, N'Sales Ops',          N'Weekly pipeline summary email',           0, 30, 0.3),
 (17, N'Contoso (default)',  N'Save email attachments to SharePoint',    0, 2, 25),
 (18, N'Contoso (default)',  N'Daily standup reminder',                  0, 1, 1),
 (19, N'Contoso (default)',  N'Approval for marketing assets',           0, 8, 7),
 (20, N'Contoso (default)',  N'Forms response to Excel',                 0, 3, 15),
 (21, N'Contoso (default)',  N'Copy Planner tasks to To Do',             0, 1, 6),
 (22, N'Finance Automation', N'Bank statement reconciliation (desktop)', 1, 40, 2),
 (23, N'IT Operations',      N'User provisioning from Workday',          1, 20, 7);
DELETE FROM audit.PowerAutomateFlow;
WITH makers AS (SELECT h.Upn, ROW_NUMBER() OVER (ORDER BY p.R2) AS k, COUNT(*) OVER () AS m FROM #held AS h JOIN #people AS p ON p.Upn = h.Upn WHERE h.Part = N'POWERAUTOMATE_ATTENDED_RPA'),
     anyone AS (SELECT Upn, ROW_NUMBER() OVER (ORDER BY R1) AS k, COUNT(*) OVER () AS m FROM #people),
     f AS (SELECT n.n, fn.*, ABS(CHECKSUM(CONCAT(N'flow', n.n))) % 10000 / 10000.0 AS R
           FROM #n AS n JOIN #flowname AS fn ON fn.i = n.n % 24 WHERE n.n < CAST(432 * @scale AS INT))
INSERT INTO audit.PowerAutomateFlow (EnvironmentName, FlowId, FlowName, OwnerUpn, IsPremium, LicenseModel, [State], CreatedUtc, LastRunUtc, Runs30, FailedRuns30, MinutesSavedPerRun)
SELECT f.Env, LOWER(CONVERT(VARCHAR(36), CAST(HASHBYTES('MD5', CONCAT(N'flowid', f.n)) AS UNIQUEIDENTIFIER))),
       CASE WHEN f.n < 24 THEN f.Name ELSE CONCAT(f.Name, N' (', CASE f.n % 5 WHEN 0 THEN N'EMEA' WHEN 1 THEN N'Americas' WHEN 2 THEN N'copy' WHEN 3 THEN N'v2' ELSE N'team ' + CAST(f.n / 24 AS NVARCHAR(4)) END, N')') END,
       CASE WHEN f.Premium = 1 THEN (SELECT Upn FROM makers WHERE k = 1 + f.n % m) ELSE (SELECT Upn FROM anyone WHERE k = 1 + (f.n * 7) % m) END,
       f.Premium,
       /* Twelve Process licenses: four on the busiest flows, eight on flows that run a few times a month. */
       CASE WHEN f.n IN (0, 10, 14, 4) OR f.n IN (3, 13, 16, 27, 37, 40, 61, 85) THEN 'Process' WHEN f.Premium = 1 THEN 'PerUser' ELSE 'Standard' END,
       CASE WHEN f.R < 0.07 THEN 'Suspended' WHEN f.R < 0.16 THEN 'Stopped' ELSE 'Started' END,
       DATEADD(DAY, -(20 + CAST(f.R * 700 AS INT)), CAST(@today AS DATETIME2(0))),
       CASE WHEN f.R < 0.16 THEN DATEADD(DAY, -(31 + CAST(f.R * 400 AS INT)), CAST(@today AS DATETIME2(0)))
            ELSE DATEADD(MINUTE, -CAST(f.R * 2000 AS INT), CAST(@today AS DATETIME2(0))) END,
       CASE WHEN f.R < 0.16 THEN 0
            WHEN f.n IN (3, 13, 16, 27, 37, 40, 61, 85) THEN 2 + CAST(f.R * 20 AS INT)
            ELSE CAST(f.RunsPerDay * 30 * (0.35 + f.R) / CASE WHEN f.n < 24 THEN 1 ELSE 12 END AS INT) END,
       CASE WHEN f.R < 0.16 THEN 0
            WHEN f.R > 0.965 THEN CAST(f.RunsPerDay * 30 * (0.35 + f.R) / CASE WHEN f.n < 24 THEN 1 ELSE 12 END * 0.45 AS INT)
            ELSE CAST(f.RunsPerDay * 30 * (0.35 + f.R) / CASE WHEN f.n < 24 THEN 1 ELSE 12 END * 0.02 AS INT) END,
       CASE WHEN f.n % 3 = 0 THEN f.Minutes END
FROM f;

/* ---- Dataverse capacity: the database passes its entitlement six weeks ago, the logs earlier ---- */
DELETE FROM audit.PowerPlatformCapacityDay;
INSERT INTO audit.PowerPlatformCapacityDay ([Day], CapacityType, EntitledGb, UsedGb)
SELECT d.[Day], c.T,
       ROUND((c.EntThen + (c.EntNow - c.EntThen) * d.Progress) * @scale, 1),
       ROUND((c.UsedThen + (c.UsedNow - c.UsedThen) * POWER(d.Progress, 1.6) + c.Wobble * SIN(d.Ago / 3.0)) * @scale, 2)
FROM #day AS d
CROSS JOIN (VALUES ('Database', 114.0, 122.0, 86.0, 134.6, 0.4), ('File', 790.0, 836.0, 351.0, 428.0, 2.0), ('Log', 18.0, 20.0, 15.5, 27.8, 0.15)) AS c (T, EntThen, EntNow, UsedThen, UsedNow, Wobble);

/* ---- Copilot Credits: four prepaid packs (100,000 a month) and pay-as-you-go beyond them ---- */
DELETE FROM audit.PowerPlatformCreditDay;
WITH use_ AS (
    SELECT d.[Day], e.Env,
           /* Weekdays carry the agents' traffic; it grows about threefold over the six months. */
           ROUND(e.Share * @scale * (1100 + 5600 * POWER(d.Progress, 1.3)) * CASE WHEN d.Dow >= 6 THEN 0.22 ELSE 1 END
                 * (0.9 + (ABS(CHECKSUM(CONCAT(e.Env, d.[Day]))) % 200) / 1000.0), 0) AS Credits,
           d.Dom, d.MonthDays
    FROM #day AS d CROSS JOIN (VALUES (N'Contoso (default)', 0.18), (N'HR Services', 0.34), (N'IT Operations', 0.38), (N'Customer Care', 0.10)) AS e (Env, Share)),
run_ AS (SELECT u.*, SUM(u.Credits) OVER (PARTITION BY YEAR(u.[Day]), MONTH(u.[Day]) ORDER BY u.[Day], u.Env ROWS UNBOUNDED PRECEDING) AS MonthSoFar,
                25000 * CASE WHEN u.[Day] >= DATEADD(MONTH, -3, @today) THEN @packsNow ELSE @packsThen END AS Packed
         FROM use_ AS u)
INSERT INTO audit.PowerPlatformCreditDay ([Day], EnvironmentName, Product, Billing, Credits, Cost, Currency)
SELECT [Day], Env, 'Copilot Studio', b.Billing, b.Credits, CASE WHEN b.Billing = 'PayAsYouGo' THEN b.Credits * 0.01 END, N'USD'
FROM run_
CROSS APPLY (VALUES ('Prepaid', CASE WHEN MonthSoFar <= Packed THEN Credits WHEN MonthSoFar - Credits >= Packed THEN 0 ELSE Packed - (MonthSoFar - Credits) END),
                    ('PayAsYouGo', CASE WHEN MonthSoFar <= Packed THEN 0 WHEN MonthSoFar - Credits >= Packed THEN Credits ELSE MonthSoFar - Packed END)) AS b (Billing, Credits)
WHERE b.Credits > 0;
/* AI Builder: a steady trickle of document processing, inside its prepaid credits. */
INSERT INTO audit.PowerPlatformCreditDay ([Day], EnvironmentName, Product, Billing, Credits, Cost, Currency)
SELECT d.[Day], N'Finance Automation', 'AI Builder', 'Prepaid', ROUND((CASE WHEN d.Dow >= 6 THEN 120 ELSE 1450 END + ABS(CHECKSUM(d.[Day])) % 300) * @scale, 0), NULL, N'USD' FROM #day AS d;

/* ---- Fabric capacities ---- */
DROP TABLE IF EXISTS #cap;
CREATE TABLE #cap (Id NVARCHAR(200), Name NVARCHAR(200), Sku VARCHAR(16), Cu INT, Region NVARCHAR(64), Billing VARCHAR(16), Weekday FLOAT, Weekend FLOAT, Grow FLOAT, DailyCost DECIMAL(19, 4));
/* F SKUs cost 0.18 USD per capacity unit an hour on pay-as-you-go in East US (F2 is 0.36); a one-year reservation takes about 41% off. */
INSERT INTO #cap VALUES
    (N'fab-prod-eus',    N'fab-prod-eus',    CASE WHEN @big = 1 THEN 'F128' ELSE 'F64' END, CASE WHEN @big = 1 THEN 128 ELSE 64 END, N'East US', 'Reserved', 0.58, 0.16, 0.05, CASE WHEN @big = 1 THEN 128 ELSE 64 END * 0.18 * 24 * 0.59),
    (N'fab-dev-eus',     N'fab-dev-eus',     CASE WHEN @big = 1 THEN 'F64' ELSE 'F32' END, CASE WHEN @big = 1 THEN 64 ELSE 32 END, N'East US', 'PayAsYouGo', 0.17, 0.012, 0.03, CASE WHEN @big = 1 THEN 64 ELSE 32 END * 0.18 * 24),
    (N'fab-sandbox-cac', N'fab-sandbox-cac', 'F8',   8, N'Canada Central', 'PayAsYouGo', 0.012, 0.004, -0.06, 8 * 0.198 * 24);
DELETE FROM audit.FabricCapacityDay;
DELETE FROM audit.FabricWorkloadDay;
WITH u AS (
    SELECT c.Id, d.[Day], d.Dow, d.Dom, d.MonthDays, d.Progress,
           /* Utilization: weekday or weekend level, the growth over the six months, and a month-end rush on production. */
           ((CASE WHEN d.Dow >= 6 THEN c.Weekend ELSE c.Weekday END) * (1 + c.Grow * d.Progress * 4)
                    * (1 + CASE WHEN c.Id = N'fab-prod-eus' AND d.Dom >= d.MonthDays - 2 THEN 0.45 ELSE 0 END)
                    * (0.85 + (ABS(CHECKSUM(CONCAT(c.Id, d.[Day]))) % 300) / 1000.0)
                    * CASE WHEN c.Id = N'fab-sandbox-cac' AND d.Ago < 40 THEN 0.08 ELSE 1 END) AS Util
    FROM #cap AS c CROSS JOIN #day AS d)
INSERT INTO audit.FabricCapacityDay (CapacityId, [Day], CapacityName, Sku, CapacityUnits, Region, Billing, ActiveHours, CuSecondsUsed, PeakPercent, ThrottledMinutes, Cost, Currency)
SELECT c.Id, u.[Day], c.Name, c.Sku, c.Cu, c.Region, c.Billing, 24,
       ROUND(c.Cu * 86400.0 * u.Util, 0),
       ROUND(CASE WHEN 100 * u.Util * CASE WHEN c.Id = N'fab-prod-eus' THEN 1.35 ELSE 2.1 END > 148 THEN 148 ELSE 100 * u.Util * CASE WHEN c.Id = N'fab-prod-eus' THEN 1.35 ELSE 2.1 END END, 1),
       CASE WHEN 100 * u.Util * 1.35 > 100 AND c.Id = N'fab-prod-eus' THEN CAST((100 * u.Util * 1.35 - 100) * 6 AS INT) ELSE 0 END,
       c.DailyCost, N'USD'
FROM u JOIN #cap AS c ON c.Id = u.Id;
/* The workloads: production is mostly Power BI, with data engineering and the warehouse; Copilot in Power BI
   grows from a pilot of about forty people to over three hundred, and from 1% of the capacity to about 11%. */
INSERT INTO audit.FabricWorkloadDay (CapacityId, [Day], Workload, CuSeconds, Operations, Users)
SELECT f.CapacityId, f.[Day], w.Workload,
       ROUND(f.CuSecondsUsed * w.Share, 0),
       CAST(ROUND(f.CuSecondsUsed * w.Share / w.CuPerOp, 0) AS BIGINT),
       CAST(ROUND(w.Users * CASE WHEN d.Dow >= 6 THEN 0.12 ELSE 1 END, 0) AS INT)
FROM audit.FabricCapacityDay AS f
JOIN #day AS d ON d.[Day] = f.[Day]
CROSS APPLY (
    SELECT * FROM (VALUES
        (N'Power BI',               CASE f.CapacityId WHEN N'fab-prod-eus' THEN 0.60 - 0.10 * d.Progress WHEN N'fab-dev-eus' THEN 0.30 ELSE 1.0 END, 4.0,  (CASE f.CapacityId WHEN N'fab-prod-eus' THEN 900 + 300 * d.Progress WHEN N'fab-dev-eus' THEN 35 ELSE 3 END) * @scale),
        (N'Copilot in Power BI',    CASE f.CapacityId WHEN N'fab-prod-eus' THEN 0.01 + 0.10 * POWER(d.Progress, 1.4) ELSE 0 END, 400.0, (40 + 280 * POWER(d.Progress, 1.2)) * @scale),
        (N'Data Engineering',       CASE f.CapacityId WHEN N'fab-prod-eus' THEN 0.18 WHEN N'fab-dev-eus' THEN 0.50 ELSE 0 END, 900.0, CASE f.CapacityId WHEN N'fab-prod-eus' THEN 22 ELSE 14 END),
        (N'Data Warehouse',         CASE f.CapacityId WHEN N'fab-prod-eus' THEN 0.12 ELSE 0 END, 30.0, 45),
        (N'Data Factory',           CASE f.CapacityId WHEN N'fab-prod-eus' THEN 0.09 WHEN N'fab-dev-eus' THEN 0.20 ELSE 0 END, 600.0, 8)
    ) AS x (Workload, Share, CuPerOp, Users) WHERE x.Share > 0) AS w;

/* ---- GitHub Copilot: 180 seats for engineering, a fifth idle, and the bill ---- */
DELETE FROM audit.GitHubCopilotSeat WHERE Scope = N'enterprise:contoso';
DELETE FROM audit.GitHubCopilotDay WHERE Scope = N'enterprise:contoso';
DELETE FROM audit.GitHubCopilotCost WHERE Scope = N'enterprise:contoso';
DECLARE @seats INT = CAST(ROUND(180 * @scale, 0) AS INT);
WITH pick AS (SELECT TOP (@seats) p.Upn, p.R1, p.R2 FROM #people AS p
              ORDER BY CASE WHEN p.Department IN (N'Engineering', N'Product', N'Data & Analytics', N'IT Operations', N'Security') THEN 0 ELSE 1 END, p.R2),
     devs AS (SELECT Upn, R1, R2, ROW_NUMBER() OVER (ORDER BY R1) AS k FROM pick)
INSERT INTO audit.GitHubCopilotSeat (Scope, Login, UserId, Organization, AssigningTeam, PlanType, CreatedUtc, LastActivityUtc, LastActivityEditor, LastAuthenticatedUtc)
SELECT N'enterprise:contoso', REPLACE(LEFT(Upn, CHARINDEX(N'@', Upn) - 1), N'.', N'-') + N'-contoso', 9100000 + k, N'contoso-eng',
       CASE WHEN k % 4 = 0 THEN N'platform' WHEN k % 4 = 1 THEN N'apps' WHEN k % 4 = 2 THEN N'data' ELSE N'web' END,
       CASE WHEN k <= @seats / 6 THEN 'enterprise' ELSE 'business' END,
       DATEADD(DAY, -(40 + CAST(R2 * 300 AS INT)), CAST(@today AS DATETIME2(0))),
       /* 9 never used; 27 idle more than 30 days; the rest in the last fortnight. */
       CASE WHEN k > @seats * 19 / 20 THEN NULL WHEN k > @seats * 4 / 5 THEN DATEADD(DAY, -(32 + CAST(R2 * 90 AS INT)), CAST(@today AS DATETIME2(0)))
            ELSE DATEADD(HOUR, -(1 + CAST(R2 * 300 AS INT)), CAST(@today AS DATETIME2(0))) END,
       CASE WHEN k > @seats * 19 / 20 THEN NULL WHEN k % 3 = 0 THEN N'vscode/1.105' WHEN k % 3 = 1 THEN N'JetBrains-IU/252' ELSE N'VisualStudio/17.14' END,
       DATEADD(HOUR, -(1 + CAST(R1 * 200 AS INT)), CAST(@today AS DATETIME2(0)))
FROM devs;
INSERT INTO audit.GitHubCopilotDay (Scope, [Day], DailyActiveUsers, WeeklyActiveUsers, MonthlyActiveUsers, Interactions, CodeGenerations, CodeAcceptances, LocAdded, LocDeleted)
SELECT N'enterprise:contoso', d.[Day],
       CAST(@scale * CASE WHEN d.Dow >= 6 THEN 14 ELSE 96 + 30 * d.Progress END AS INT), CAST(@scale * (128 + 16 * d.Progress) AS INT), CAST(@scale * (140 + 5 * d.Progress) AS INT),
       CAST(@scale * CASE WHEN d.Dow >= 6 THEN 300 ELSE 2600 + 1400 * d.Progress END AS BIGINT),
       CAST(@scale * CASE WHEN d.Dow >= 6 THEN 900 ELSE 7800 + 2600 * d.Progress END AS BIGINT),
       CAST(@scale * CASE WHEN d.Dow >= 6 THEN 260 ELSE 2300 + 900 * d.Progress END AS BIGINT),
       CAST(@scale * CASE WHEN d.Dow >= 6 THEN 2100 ELSE 19000 + 7000 * d.Progress END AS BIGINT), CAST(@scale * CASE WHEN d.Dow >= 6 THEN 700 ELSE 6100 END AS BIGINT)
FROM #day AS d WHERE d.Ago < 120;
INSERT INTO audit.GitHubCopilotCost (Scope, UsageDate, Product, Sku, Model, Organization, UnitType, PricePerUnit, Quantity, GrossAmount, DiscountAmount, NetAmount)
SELECT N'enterprise:contoso', d.[Day], N'Copilot', s.Sku, NULL, N'contoso-eng', N'UserMonths', s.Price, s.Seats * 1.0 / d.MonthDays,
       s.Seats * s.Price / d.MonthDays, 0, s.Seats * s.Price / d.MonthDays
FROM #day AS d CROSS APPLY (VALUES (N'Copilot Business', 19.0, @seats - @seats / 6 - CAST(@seats / 6 * (1 - d.Progress) AS INT)), (N'Copilot Enterprise', 39.0, @seats / 6)) AS s (Sku, Price, Seats)
WHERE d.Ago < 120;

/* ---- Foundry and Azure OpenAI ---- */
DELETE FROM audit.FoundryDeployment WHERE SubscriptionId = '6b2c9d1e-0000-4000-8000-00000000c0de';
DELETE FROM audit.FoundryUsageDay WHERE SubscriptionId = '6b2c9d1e-0000-4000-8000-00000000c0de';
DELETE FROM audit.FoundryCostDay WHERE SubscriptionId = '6b2c9d1e-0000-4000-8000-00000000c0de';
INSERT INTO audit.FoundryDeployment (SubscriptionId, ResourceGroup, Account, Deployment, Kind, Location, ModelName, ModelVersion, ModelPublisher, SkuName, Capacity)
VALUES ('6b2c9d1e-0000-4000-8000-00000000c0de', N'rg-ai-prod', N'contoso-openai-eus', N'gpt-4o', N'OpenAI', N'eastus', N'gpt-4o', N'2024-11-20', N'OpenAI', N'GlobalStandard', 450),
       ('6b2c9d1e-0000-4000-8000-00000000c0de', N'rg-ai-prod', N'contoso-openai-eus', N'gpt-4o-mini', N'OpenAI', N'eastus', N'gpt-4o-mini', N'2024-07-18', N'OpenAI', N'GlobalStandard', 1000),
       ('6b2c9d1e-0000-4000-8000-00000000c0de', N'rg-ai-prod', N'contoso-openai-eus', N'embeddings', N'OpenAI', N'eastus', N'text-embedding-3-large', N'1', N'OpenAI', N'Standard', 350),
       ('6b2c9d1e-0000-4000-8000-00000000c0de', N'rg-ai-ptu', N'contoso-ptu-eus', N'gpt-4o-ptu', N'OpenAI', N'eastus', N'gpt-4o', N'2024-11-20', N'OpenAI', N'ProvisionedManaged', 15),
       ('6b2c9d1e-0000-4000-8000-00000000c0de', N'rg-ai-lab', N'contoso-ai-sandbox', N'phi-4', N'AIServices', N'canadacentral', N'Phi-4', N'7', N'Microsoft', N'GlobalStandard', 10);
INSERT INTO audit.FoundryUsageDay (SubscriptionId, ResourceGroup, Account, Deployment, ModelName, ModelVersion, [Day], Requests, InputTokens, OutputTokens, TotalTokens)
SELECT '6b2c9d1e-0000-4000-8000-00000000c0de', x.Rg, x.Account, x.Dep, x.Model, x.Ver, d.[Day], x.Req, x.Req * x.InTok, x.Req * x.OutTok, x.Req * (x.InTok + x.OutTok)
FROM #day AS d
CROSS APPLY (VALUES
    (N'rg-ai-prod', N'contoso-openai-eus', N'gpt-4o',      N'gpt-4o',                 N'2024-11-20', CAST(@scale * (CASE WHEN d.Dow >= 6 THEN 1100 ELSE 9200 END) * (0.6 + d.Progress) AS BIGINT), 1800, 420),
    (N'rg-ai-prod', N'contoso-openai-eus', N'gpt-4o-mini', N'gpt-4o-mini',            N'2024-07-18', CAST(@scale * (CASE WHEN d.Dow >= 6 THEN 6000 ELSE 41000 END) * (0.5 + d.Progress) AS BIGINT), 900, 180),
    (N'rg-ai-prod', N'contoso-openai-eus', N'embeddings',  N'text-embedding-3-large', N'1',          CAST(@scale * (CASE WHEN d.Dow >= 6 THEN 2000 ELSE 15000 END) AS BIGINT), 600, 0),
    /* The provisioned deployment: a pilot that stopped five weeks ago. */
    (N'rg-ai-ptu',  N'contoso-ptu-eus',    N'gpt-4o-ptu',  N'gpt-4o',                 N'2024-11-20', CASE WHEN d.Ago < 35 THEN 0 ELSE CAST((CASE WHEN d.Dow >= 6 THEN 300 ELSE 5200 END) AS BIGINT) END, 2200, 500)
) AS x (Rg, Account, Dep, Model, Ver, Req, InTok, OutTok)
WHERE d.Ago < 90;
INSERT INTO audit.FoundryCostDay (SubscriptionId, ResourceGroup, Account, Meter, [Day], Cost, Currency)
SELECT '6b2c9d1e-0000-4000-8000-00000000c0de', u.ResourceGroup, u.Account, u.ModelName + N' tokens', u.[Day],
       ROUND(u.InputTokens / 1000000.0 * CASE u.ModelName WHEN N'gpt-4o' THEN 2.5 WHEN N'gpt-4o-mini' THEN 0.15 ELSE 0.13 END
             + u.OutputTokens / 1000000.0 * CASE u.ModelName WHEN N'gpt-4o' THEN 10 WHEN N'gpt-4o-mini' THEN 0.6 ELSE 0 END, 4), N'USD'
FROM audit.FoundryUsageDay AS u WHERE u.SubscriptionId = '6b2c9d1e-0000-4000-8000-00000000c0de' AND u.Account = N'contoso-openai-eus';
/* Provisioned throughput is billed by the hour whether it is called or not: 15 PTU at about 1 USD each an hour. */
INSERT INTO audit.FoundryCostDay (SubscriptionId, ResourceGroup, Account, Meter, [Day], Cost, Currency)
SELECT '6b2c9d1e-0000-4000-8000-00000000c0de', N'rg-ai-ptu', N'contoso-ptu-eus', N'Provisioned Managed Throughput Unit', d.[Day], 15 * 1.0 * 24, N'USD' FROM #day AS d WHERE d.Ago < 90;
INSERT INTO audit.FoundryCostDay (SubscriptionId, ResourceGroup, Account, Meter, [Day], Cost, Currency)
SELECT '6b2c9d1e-0000-4000-8000-00000000c0de', N'rg-ai-lab', N'contoso-ai-sandbox', N'Storage and networking', d.[Day], 3.85, N'USD' FROM #day AS d WHERE d.Ago < 90;

/* ---- AI apps beyond Copilot (2.6.9): what the activity database's rollup leaves in audit.AIAppUserDay ----
   Six Azure AI resources. Most calls come from production apps with their own key (no user identity); a few hundred
   people call two apps with their own sign-in, and the Technology division's developers a dev resource. The busiest is
   the Foundry loader's contoso-openai-eus, so its cost shows beside its calls. Four months, weekdays heavier. */
DELETE FROM audit.AIAppUserDay;
INSERT INTO audit.AIAppUserDay ([Day], [Resource], ApplicationName, SubscriptionId, CallerKind, Caller, Calls, Prompts, Threads, Failures)
SELECT d.[Day], x.Res, x.App, x.Sub, 'Unattributed', N'unattributed', c.Calls, c.Calls / 2, c.Calls / 7 + 1, CAST(c.Calls * x.Fail AS INT)
FROM #day AS d
CROSS APPLY (VALUES
    (N'contoso-openai-eus',     N'',                     '6b2c9d1e-0000-4000-8000-00000000c0de', 9000, 0.004),
    (N'retail-prod-aif-0147',   N'',                     'b1b8637c-0000-4000-8000-0000000000a1', 5200, 0.006),
    (N'mortgage-prod-aif-0021', N'Underwriting copilot', 'b1b8637c-0000-4000-8000-0000000000a1', 2100, 0.002),
    (N'hr-uat-oai-03',          N'',                     '79906e47-0000-4000-8000-0000000000b2',  260, 0.03),
    (N'aza-dev-aif-0046',       N'',                     '79906e47-0000-4000-8000-0000000000b2',  140, 0.08)
) AS x (Res, App, Sub, Base, Fail)
CROSS APPLY (SELECT CAST(x.Base * @scale * CASE WHEN d.Dow >= 6 THEN 0.18 ELSE 1 END * (0.55 + 0.6 * d.Progress)
                        * (0.9 + (ABS(CHECKSUM(CONCAT(x.Res, d.[Day]))) % 200) / 1000.0) AS INT) AS Calls) AS c
WHERE d.Ago < 120 AND c.Calls > 0
UNION ALL
SELECT d.[Day], a.Res, a.App, a.Sub, 'Person', LOWER(p.Upn), u.Calls, u.Calls / 2, u.Calls / 10 + 1, CASE WHEN a.Res = N'aza-dev-aif-0046' AND u.Calls > 30 THEN 2 ELSE 0 END
FROM #people AS p
CROSS APPLY (SELECT CASE WHEN p.Division = N'Technology' AND p.R2 < 0.12 THEN N'aza-dev-aif-0046'
                         WHEN p.R2 < 0.04 THEN N'contoso-openai-eus' WHEN p.R2 < 0.07 THEN N'retail-prod-aif-0147' END AS Res) AS r
CROSS APPLY (SELECT r.Res, CASE r.Res WHEN N'aza-dev-aif-0046' THEN N'My app' WHEN N'contoso-openai-eus' THEN N'Claims assistant' ELSE N'Branch helper' END AS App,
                    CASE WHEN r.Res = N'contoso-openai-eus' THEN '6b2c9d1e-0000-4000-8000-00000000c0de' WHEN r.Res = N'aza-dev-aif-0046' THEN '79906e47-0000-4000-8000-0000000000b2'
                         ELSE 'b1b8637c-0000-4000-8000-0000000000a1' END AS Sub) AS a
JOIN #day AS d ON d.Ago < 120 AND d.Dow < 6
CROSS APPLY (SELECT ABS(CHECKSUM(CONCAT(p.Upn, d.[Day]))) % 1000 / 1000.0 AS H) AS h
CROSS APPLY (SELECT 2 + CAST(h.H * 60 AS INT) AS Calls) AS u
WHERE r.Res IS NOT NULL AND h.H < 0.25 + 0.2 * d.Progress;

/* The resources, as the rollup's first sight of them leaves them; one an administrator has already set. */
DELETE FROM audit.AIAppResource;
INSERT INTO audit.AIAppResource ([Resource], LineOfBusiness, Environment, SetBy, FirstSeen)
SELECT f.[Resource], g.LineOfBusiness, g.Environment, 'Name', f.FirstSeen
FROM (SELECT [Resource], MIN([Day]) AS FirstSeen FROM audit.AIAppUserDay GROUP BY [Resource]) AS f
CROSS APPLY audit.fnAIAppResourceGuess(f.[Resource]) AS g;
UPDATE audit.AIAppResource SET LineOfBusiness = N'Claims', Environment = 'Production', Owner = N'Claims platform team', SetBy = 'Admin', UpdatedBy = N'demo'
WHERE [Resource] = N'contoso-openai-eus';

/* The leaders, levels 1 to 3 of the demo's five, are kept off the revoke and train lists (the default, 5, would keep
   the whole demo company off them, and every page's recommendations empty). */
UPDATE dash.Setting SET Value = N'3', UpdatedBy = N'demo' WHERE Name = N'ProtectedLevels';

/* The hourly cost the value is counted at. */
UPDATE dash.Setting SET Value = N'65', UpdatedBy = N'demo' WHERE Name = N'HourlyCost';
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, Description, UpdatedBy) VALUES (N'HourlyCost', N'65', N'Demo: the loaded cost of an hour of work.', N'demo');

/* The session goes on to the dashboard's refresh, whose procedure has temporary tables of the same names. */
DROP TABLE IF EXISTS #n, #day, #people, #sku, #held, #flowname, #cap, #suite, #pool;

SELECT (SELECT COUNT(*) FROM audit.FabricCapacityDay) AS FabricDays, (SELECT COUNT(*) FROM audit.FabricWorkloadDay) AS WorkloadDays,
       (SELECT COUNT(*) FROM audit.PowerAutomateFlow) AS Flows, (SELECT COUNT(*) FROM audit.PowerPlatformUserActivity) AS Activity,
       (SELECT COUNT(*) FROM audit.PowerPlatformCreditDay) AS CreditDays, (SELECT COUNT(*) FROM audit.GitHubCopilotSeat) AS Seats,
       (SELECT COUNT(*) FROM audit.FoundryCostDay) AS FoundryCostDays;
