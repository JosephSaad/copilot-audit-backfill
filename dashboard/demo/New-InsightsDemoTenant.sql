/*
    Copilot Insights demo: the rest of a real tenant (2.6.6), on top of New-InsightsDemoData.sql and before the
    sessions and the bi schema are built from it. Deterministic, relative to today, like the rest of the demo.

      - Eight service, shared, test and room accounts holding Copilot licenses: five synced from AD with no manager,
        three cloud-only. A script uses one every night and the people behind two shared mailboxes use theirs; the
        rest are never or seldom used.
      - An interim adviser to the CFO with a cloud-only account outside the org chart: a person, which an
        administrator has confirmed (as have they for a licensed guest consultant).
      - About 25 guests from eight partner organizations using Copilot in the tenant, two licensed here, one who
        tried a jailbreak, one who reached for the payroll file, and some reading labeled content.
      - Leave dates: about 22 licensed people due to leave in the next 30 days, 7 more within 80, and three past
        their date with the account still enabled; from Entra, and for contractors from AD's account expiry.
      - The agent workbook: its three sheets as tools/Import-AgentWorkbook.ps1 loads them (dbo.AgentInventory,
        dbo.AgentsInProd, dbo.ParentChildAgent), 46 agents in nine environments, and the use of the 25 agents it
        lists beyond the eight New-InsightsDemoData.sql launches, each by the departments it was built for, growing
        from its launch. New-InsightsDemoAgents.sql, after the bi build, loads the workbook into the dashboard.
      - About 9 people without a license whose use of agents, paid in Copilot Credits, costs more than a license.

    Counts are for 5,000 people and scale with -People (@scale, as in New-InsightsDemoCosts.sql: per 2,300 people).
*/
SET NOCOUNT ON;

DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);
DECLARE @days INT = 182;
DECLARE @staff INT = (SELECT COUNT(*) FROM audit.UserDirectory);   -- before the accounts below are added
DECLARE @scale FLOAT = @staff / 2300.0;
DECLARE @org NVARCHAR(60) = N'5e1f0c2a-1111-4000-8000-000000000000';

DROP TABLE IF EXISTS #days, #acct, #guest, #env, #cat, #link, #pop, #reach, #x, #lic, #leave, #credit, #xs;
WITH t AS (SELECT TOP (@days) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns)
SELECT DATEADD(DAY, -t.n, @today) AS [Day], CAST(t.n AS INT) AS DaysAgo,
       CASE WHEN DATEDIFF(DAY, '19000101', DATEADD(DAY, -t.n, @today)) % 7 IN (5, 6) THEN 1 ELSE 0 END AS Weekend
INTO #days FROM t;

/* ---- Accounts that are not people, and one that is ------------------------------------------------------- */

/* InAd: synced from AD (no manager, OrgDepth 0, as the directory load records an account nobody reports to), else
   cloud-only, known only to Entra. Use: how it is used - a nightly script, the people behind a shared mailbox, now
   and then for testing, like a person, or never. */
CREATE TABLE #acct (n INT PRIMARY KEY, Upn NVARCHAR(256), Name NVARCHAR(256), InAd BIT, Department NVARCHAR(200), Division NVARCHAR(256),
                    JobTitle NVARCHAR(128), EmployeeType NVARCHAR(32), LicensedDaysAgo INT, Use_ VARCHAR(10));
INSERT INTO #acct VALUES
 (1, N'svc-copilot-reports@contoso.com',    N'Copilot Reporting Service',       1, N'IT Operations',        N'Technology', N'Service account', N'SERVICE', 120, 'nightly'),
 (2, N'srv-pbi-gateway@contoso.com',        N'Power BI Gateway Service',        1, N'Data & Analytics',     N'Technology', N'Service account', N'SERVICE',  95, 'none'),
 (3, N'shared.finance@contoso.com',         N'Finance Shared Mailbox',          1, N'Accounting',           N'Finance',    NULL,               NULL,       140, 'mailbox'),
 (4, N'helpdesk@contoso.com',               N'IT Helpdesk Mailbox',             1, N'IT Operations',        N'Technology', NULL,               NULL,        70, 'mailbox'),
 (5, N'noreply-hr@contoso.com',             N'HR Notifications (do not reply)', 1, N'HR Business Partners', N'People',     NULL,               NULL,        55, 'none'),
 (6, N'room-tor-boardroom@contoso.com',     N'Toronto Boardroom',               0, NULL, NULL, NULL, NULL,  88, 'none'),
 (7, N'test.copilot01@contoso.com',         N'Copilot Test 01',                 0, NULL, NULL, NULL, NULL, 160, 'test'),
 (8, N'test.copilot02@contoso.com',         N'Copilot Test 02',                 0, NULL, NULL, NULL, NULL, 160, 'test'),
 (9, N'theodora.vance@contoso.com',         N'Theodora Vance',                  0, NULL, NULL, NULL, NULL,  75, 'person');

INSERT INTO audit.UserDirectory
    (UserPrincipalName, Mail, FullName, EmployeeType, IsManager, AccountEnabled, JobTitle, Department, Division, Office, City, Country,
     Company, ManagerUPN, ManagerName, OrgDepth)
SELECT Upn, Upn, Name, EmployeeType, 0, 1, JobTitle, Department, Division, N'Toronto HQ', N'Toronto', N'Canada', N'Contoso', NULL, NULL, 0
FROM #acct WHERE InAd = 1;

/* Guests, as Entra names them: their own address with @ as _, then #EXT# and the tenant's domain. Taken in order, so a
   smaller company has fewer guests from the same mix of partners. Licensed: two Fabrikam consultants embedded in the
   ERP program. */
CREATE TABLE #guest (n INT PRIMARY KEY, First NVARCHAR(40), Last NVARCHAR(40), Domain NVARCHAR(60), Loc NVARCHAR(4), Site NVARCHAR(40), Licensed BIT);
INSERT INTO #guest VALUES
 (1, N'Lena', N'Kraus', N'fabrikam.com', N'DE', N'erp-program', 1),      (2, N'Pieter', N'deJong', N'fabrikam.com', N'NL', N'erp-program', 1),
 (3, N'Sophie', N'Laurent', N'northwindtraders.com', N'FR', N'supplier-portal', 0), (4, N'Rahul', N'Mehta', N'adatum.com', N'US', N'audit-fy26', 0),
 (5, N'Emily', N'Carter', N'fabrikam.com', N'US', N'erp-program', 0),    (6, N'Jonas', N'Weiss', N'woodgrovebank.com', N'DE', N'treasury-partners', 0),
 (7, N'Ana', N'Ribeiro', N'northwindtraders.com', N'PT', N'supplier-portal', 0), (8, N'Kenji', N'Watanabe', N'fabrikam.com', N'JP', N'erp-program', 0),
 (9, N'Claire', N'Bernard', N'adatum.com', N'FR', N'audit-fy26', 0),       (10, N'Tom', N'Hale', N'litwareinc.com', N'US', N'data-platform', 0),
 (11, N'Maya', N'Cohen', N'fabrikam.com', N'US', N'erp-program', 0),     (12, N'Lukas', N'Brandt', N'northwindtraders.com', N'DE', N'supplier-portal', 0),
 (13, N'Grace', N'Okoro', N'adatum.com', N'GB', N'audit-fy26', 0),        (14, N'Felix', N'Hartmann', N'woodgrovebank.com', N'DE', N'treasury-partners', 0),
 (15, N'Isabel', N'Navarro', N'tailspintoys.com', N'ES', N'marketing-agency', 0), (16, N'Oscar', N'Lindahl', N'fabrikam.com', N'SE', N'erp-program', 0),
 (17, N'Priyanka', N'Rao', N'litwareinc.com', N'IN', N'data-platform', 0), (18, N'Daniel', N'Moss', N'northwindtraders.com', N'US', N'supplier-portal', 0),
 (19, N'Hana', N'Kobayashi', N'proseware.com', N'JP', N'engineering-partners', 0), (20, N'Ethan', N'Brooks', N'adatum.com', N'US', N'audit-fy26', 0),
 (21, N'Julia', N'Meyer', N'fabrikam.com', N'DE', N'erp-program', 0),   (22, N'Victor', N'Duval', N'woodgrovebank.com', N'FR', N'treasury-partners', 0),
 (23, N'Chiara', N'Galli', N'tailspintoys.com', N'IT', N'marketing-agency', 0), (24, N'Sean', N'Byrne', N'alpineskihouse.com', N'IE', N'facilities-vendors', 0),
 (25, N'Mila', N'Horvat', N'northwindtraders.com', N'HR', N'supplier-portal', 0), (26, N'Arjun', N'Pillai', N'proseware.com', N'IN', N'engineering-partners', 0),
 (27, N'Nora', N'Fitzgerald', N'adatum.com', N'IE', N'audit-fy26', 0);
DELETE FROM #guest WHERE n > CAST(ROUND(11 * @scale, 0) AS INT) AND n > 4;
ALTER TABLE #guest ADD Upn NVARCHAR(256);
UPDATE #guest SET Upn = LOWER(CONCAT(First, N'.', Last, N'_', Domain, N'#EXT#@contoso.onmicrosoft.com'));

/* What the license tool records for them: Entra knows every one; the guests are Guests. */
INSERT INTO audit.CopilotLicense (UserPrincipalName, IsCopilotLic, SkuPartNumber, LicensedSinceUtc, CapabilityStatus, DirectoryStatus, UserType, EntraAccountEnabled)
SELECT a.Upn, 1, N'Microsoft_365_Copilot', DATEADD(HOUR, 14, CAST(DATEADD(DAY, -a.LicensedDaysAgo, @today) AS DATETIME2(0))), N'Enabled', N'Found', N'Member', 1
FROM #acct AS a
UNION ALL
SELECT g.Upn, g.Licensed, CASE WHEN g.Licensed = 1 THEN N'Microsoft_365_Copilot' END,
       CASE WHEN g.Licensed = 1 THEN DATEADD(HOUR, 14, CAST(DATEADD(DAY, -(100 + g.n * 9), @today) AS DATETIME2(0))) END,
       CASE WHEN g.Licensed = 1 THEN N'Enabled' END, N'Found', N'Guest', 1
FROM #guest AS g;
UPDATE audit.CopilotLicense SET UserType = N'Member' WHERE UserType IS NULL;

/* Their licenses were assigned as everyone's were, so the license history has them too. */
DECLARE @licAdded TABLE (RecordId VARCHAR(64), Upn NVARCHAR(256), Ts DATETIME2(0));
INSERT INTO @licAdded
SELECT CONCAT('lic-add-acct-', a.n), a.Upn, DATEADD(HOUR, 14, CAST(DATEADD(DAY, -a.LicensedDaysAgo, @today) AS DATETIME2(0))) FROM #acct AS a
UNION ALL
SELECT CONCAT('lic-add-guest-', g.n), g.Upn, DATEADD(HOUR, 14, CAST(DATEADD(DAY, -(100 + g.n * 9), @today) AS DATETIME2(0))) FROM #guest AS g WHERE g.Licensed = 1;
INSERT INTO audit.LicenseEvent (RecordId, CreatedDateTime, ActivityDate, Operation, ResultStatus, ActorUpn, TargetUpn, CorrelationId)
SELECT RecordId, Ts, CAST(Ts AS DATE), N'Change user license', N'Success', N'licensing.admin@contoso.com', Upn, CONCAT(N'corr-', RecordId) FROM @licAdded;
INSERT INTO audit.LicenseEventProperty (RecordId, Ordinal, [Name], OldValue, NewValue, Source)
SELECT RecordId, 0, N'AssignedLicense', N'["[SkuName=SPE_E5, AccountId=t, SkuId=a, DisabledPlans=[]]"]',
       N'["[SkuName=SPE_E5, AccountId=t, SkuId=a, DisabledPlans=[]]","[SkuName=Microsoft_365_Copilot, AccountId=t, SkuId=x, DisabledPlans=[]]"]', 'record'
FROM @licAdded;
EXEC audit.usp_RefreshLicenseSkuChange @All = 1;

/* Who held a license when: from the license history, as the bi build will read it. */
SELECT CAST(TargetUpn AS NVARCHAR(256)) AS Upn,
       MIN(CASE WHEN RecordId LIKE 'lic-add-%' THEN CreatedDateTime END) AS FromUtc,
       ISNULL(MIN(CASE WHEN RecordId LIKE 'lic-rem-%' THEN CreatedDateTime END), CAST('99991231' AS DATETIME2(0))) AS ToUtc
INTO #lic FROM audit.LicenseEvent GROUP BY TargetUpn;
CREATE UNIQUE CLUSTERED INDEX IX_lic ON #lic (Upn);

/* ---- Leave dates ------------------------------------------------------------------------------------------ */

/* License holders who are due to leave: a team's managers and staff, not this quarter's new joiners, and not the
   first licensed people by sign-in, whose accounts New-InsightsDemoOperations.sql disables. Entra's leave date for
   employees, set by HR; for contractors AD's account expiry, the end of the contract, and for one in four of them the
   leave date in Entra too. Three are past their date with the account still enabled: the licenses to reclaim now. */
SELECT d.UserPrincipalName AS Upn, d.EmployeeType, ROW_NUMBER() OVER (ORDER BY HASHBYTES('MD5', CONCAT(N'leave', d.UserPrincipalName))) AS k
INTO #leave
FROM audit.UserDirectory AS d
JOIN audit.CopilotLicense AS c ON c.UserPrincipalName = d.UserPrincipalName AND c.IsCopilotLic = 1
WHERE d.AccountEnabled = 1 AND d.OrgDepth IN (3, 4) AND c.EmployeeHireDate IS NULL
  AND d.UserPrincipalName > (SELECT MAX(x.UserPrincipalName) FROM (SELECT TOP (20) UserPrincipalName FROM audit.CopilotLicense
                                                                    WHERE IsCopilotLic = 1 ORDER BY UserPrincipalName) AS x);
DECLARE @soon INT = CAST(ROUND(10 * @scale, 0) AS INT), @later INT = CAST(ROUND(3.2 * @scale, 0) AS INT);
DELETE FROM #leave WHERE k > @soon + @later + 3;
ALTER TABLE #leave ADD LeaveUtc DATETIME2(0);
UPDATE #leave SET LeaveUtc = DATEADD(HOUR, 21, CAST(DATEADD(DAY,
    CASE WHEN k <= @soon THEN 1 + (k - 1) * 29 / @soon WHEN k <= @soon + @later THEN 31 + (k - @soon - 1) * 49 / @later ELSE -(2 + (k - @soon - @later) * 3) END,
    @today) AS DATETIME2(0)));
UPDATE c SET EmployeeLeaveUtc = l.LeaveUtc
FROM audit.CopilotLicense AS c JOIN #leave AS l ON l.Upn = c.UserPrincipalName
WHERE l.EmployeeType <> N'CONTRACTOR' OR l.k % 4 = 0;
UPDATE d SET AccountExpiresUtc = DATEADD(HOUR, 7, l.LeaveUtc)   -- AD's expiry: the end of the day, Eastern
FROM audit.UserDirectory AS d JOIN #leave AS l ON l.Upn = d.UserPrincipalName
WHERE l.EmployeeType = N'CONTRACTOR';

/* ---- The agent workbook ------------------------------------------------------------------------------------ */

/* Environments: the default one, where the first agents were made (its id is the tenant's), one production
   environment for each line of business, a developer environment and a sandbox. */
CREATE TABLE #env (e INT PRIMARY KEY, Name NVARCHAR(100), Id NVARCHAR(100), Sku NVARCHAR(40));
INSERT INTO #env VALUES
 (0, N'Contoso (default)',    CONCAT(N'Default-', @org),                   N'Default'),
 (1, N'Finance Automation',   N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b01',     N'Production'),
 (2, N'HR Services',          N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b02',     N'Production'),
 (3, N'IT Operations',        N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b03',     N'Production'),
 (4, N'Sales Ops',            N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b04',     N'Production'),
 (5, N'Customer Care',        N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b05',     N'Production'),
 (6, N'Legal and Compliance', N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b06',     N'Production'),
 (7, N'Contoso Dev',          N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b07',     N'Developer'),
 (8, N'Maker Sandbox',        N'3b8f2c1d-7a4e-4d2b-9c61-0f5a8e2d1b08',     N'Sandbox');

/*
    Each agent: the workbook's columns, then how it is used. The first eight are New-InsightsDemoData.sql's, whose
    use is in the audit log already, from their launch LaunchAgo days ago. The rest are used from their launch by the
    people of Scope (departments or divisions, separated by ;, or Everyone): ReachPm per thousand of them at all, each
    on DayPm per thousand weekdays (a tenth of that at weekends), with up to MaxPrompts prompts, WebPm per thousand of
    them searching the web, and reading the knowledge on Site. With no Scope, no more use. Owner: the department's
    director (dir), one of its team managers (mgr) or staff (staff), one of its staff whose account is disabled
    (disabled), someone who has left (left, Gone), or nobody (none). Calls: the agents it hands work to, for the audit
    log's TargetAgentName.
    The checks Agents > Agent checks makes have something to find: Tax Research Assistant is quarantined and used,
    Security Advisor has web search off and searches the web, Returns Assistant takes anyone without signing in, Code
    Review Helper is used like a production agent in the developer environment, Contract Reviewer's owner has left and
    Account Research's is disabled, and Procurement Policy Bot and Fleet Tracker are in production and unused.
*/
CREATE TABLE #cat (a INT PRIMARY KEY, Schema_ NVARCHAR(100), Name NVARCHAR(200), e INT, OwnerDept NVARCHAR(60), OwnerKind VARCHAR(10), Gone NVARCHAR(100),
                   CreatedAgo INT, PublishedAgo INT, Channels NVARCHAR(200), VU INT, VG INT, EU INT, EG INT, Auth NVARCHAR(60), Quarantined BIT, Web BIT,
                   Triggers NVARCHAR(200), CreatedIn NVARCHAR(40), InProd BIT, Knowledge NVARCHAR(200),
                   Scope NVARCHAR(200), LaunchAgo INT, ReachPm INT, DayPm INT, MaxPrompts INT, WebPm INT, Site NVARCHAR(60), Doc NVARCHAR(80), Calls NVARCHAR(200));
INSERT INTO #cat VALUES
 (1,  N'crd20_expenseAssistant',    N'Expense Assistant',        0, N'Accounting',          'mgr',   NULL, 240,   9, N'Microsoft Teams; Microsoft 365 Copilot', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Finance policies', NULL, 170, 0, 0, 0, 0, NULL, NULL, NULL),
 (2,  N'crd20_itHelpdesk',          N'IT Helpdesk',              0, N'IT Operations',       'dir',   NULL, 300,   3, N'Microsoft Teams; Microsoft 365 Copilot; SharePoint', 0, 1, 4, 1, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio', 1, N'SharePoint: IT knowledge base; ServiceNow', NULL, 150, 0, 0, 0, 0, NULL, NULL, NULL),
 (3,  N'crd20_hrPolicyHelper',      N'HR Policy Helper',         0, N'HR Business Partners','mgr',   NULL, 200,  14, N'Microsoft Teams; Microsoft 365 Copilot', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: HR policies', NULL, 120, 0, 0, 0, 0, NULL, NULL, NULL),
 (4,  N'crd20_salesProposalWriter', N'Sales Proposal Writer',    0, N'Sales Operations',    'staff', NULL, 150,   6, N'Microsoft Teams; Microsoft 365 Copilot', 0, 3, 3, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Proposal library', NULL, 100, 0, 0, 0, 0, NULL, NULL, NULL),
 (5,  N'crd20_contractReviewer',    N'Contract Reviewer',        0, N'Contracts',           'left',  N'tessa.lindholm@contoso.com', 130, 33, N'Microsoft 365 Copilot', 0, 2, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Contract templates', NULL, 80, 0, 0, 0, 0, NULL, NULL, NULL),
 (6,  N'crd20_onboardingBuddy',     N'Onboarding Buddy',         0, N'Employee Experience', 'staff', NULL,  95,  11, N'Microsoft Teams', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: New starter guide', NULL, 60, 0, 0, 0, 0, NULL, NULL, NULL),
 (7,  N'crd20_financeCloseAgent',   N'Finance Close Agent',      0, N'Accounting',          'dir',   NULL,  70,   4, N'Microsoft Teams; Microsoft 365 Copilot', 0, 1, 3, 1, N'Authenticate with Microsoft', 0, 0, N'Recurrence: daily at 06:00', N'Copilot Studio', 1, N'Dataverse: Close calendar', NULL, 40, 0, 0, 0, 0, NULL, NULL, NULL),
 (8,  N'crd20_travelBooker',        N'Travel Booker',            0, N'Facilities',          'staff', NULL,  40,  15, N'Microsoft Teams', 25, 0, 1, 0, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio', 0, N'Public websites', NULL, 15, 0, 0, 0, 0, NULL, NULL, NULL),
 (9,  N'cr4f1_invoiceMatcher',      N'Invoice Matcher',          1, N'Accounting',          'mgr',   NULL, 330,  21, N'Microsoft Teams', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, N'When a new email arrives (V3); When a file is created', N'Copilot Studio', 1, N'Dataverse: Invoices', N'Accounting', 175, 500, 250, 3, 0, N'finance', N'Invoice Register.xlsx', NULL),
 (10, N'cr4f1_vendorOnboarding',    N'Vendor Onboarding',        1, N'Procurement',         'mgr',   NULL, 160,  18, N'Microsoft Teams; SharePoint', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Supplier onboarding', N'Procurement', 150, 600, 200, 3, 0, N'procurement', N'Supplier Checklist.docx', NULL),
 (11, N'cr4f1_budgetQa',            N'Budget Q&A',               1, N'FP&A',                'staff', NULL, 120,  25, N'Microsoft 365 Copilot', 0, 2, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Budget packs', N'FP&A;Treasury', 110, 450, 220, 3, 0, N'finance', N'FY27 Budget.xlsx', NULL),
 (12, N'cr4f1_taxResearch',         N'Tax Research Assistant',   1, N'Tax',                 'staff', NULL, 100,  45, N'Microsoft Teams', 8, 0, 1, 0, N'Authenticate with Microsoft', 1, 1, NULL, N'Copilot Studio', 0, N'Public websites; SharePoint: Tax memos', N'Tax', 95, 450, 250, 4, 300, N'finance', N'Transfer Pricing Memo.docx', NULL),
 (13, N'cr2a9_benefitsNavigator',   N'Benefits Navigator',       2, N'Total Rewards',       'mgr',   NULL, 210,   8, N'Microsoft Teams; Microsoft 365 Copilot', 0, 1, 2, 1, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Benefits guide', N'Everyone', 160, 40, 60, 3, 0, N'hr', N'Benefits Guide 2026.docx', NULL),
 (14, N'cr2a9_recruitingCoordinator',N'Recruiting Coordinator',  2, N'Talent Acquisition',  'dir',   NULL, 140,  12, N'Microsoft Teams', 0, 1, 3, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'Workday: Requisitions', N'Talent Acquisition;HR Business Partners', 130, 450, 220, 3, 0, N'hr', N'Interview Guide.docx', NULL),
 (15, N'crd20_learningCoach',       N'Learning Coach',           0, N'Learning',            'staff', NULL,  85,   7, N'Microsoft Teams; Microsoft 365 Copilot', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Learning catalog', N'Everyone', 80, 35, 50, 3, 0, N'learning', N'Course Catalog.xlsx', NULL),
 (16, N'crd20_softwareCatalog',     N'Software Catalog',         0, N'IT Operations',       'staff', NULL, 260,  30, N'Microsoft Teams', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'ServiceNow: Software catalog', N'Technology', 175, 120, 80, 2, 0, NULL, NULL, NULL),
 (17, N'crd20_serviceNowTicketing', N'ServiceNow Ticketing',     0, N'IT Operations',       'mgr',   NULL, 280,  52, N'Microsoft Teams', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, N'ServiceNow: When a record is created', N'Copilot Studio', 1, N'ServiceNow: Incidents', N'IT Operations', 175, 300, 150, 2, 0, NULL, NULL, NULL),
 (18, N'cr3c7_securityAdvisor',     N'Security Advisor',         3, N'Security',            'dir',   NULL, 115,  19, N'Microsoft Teams; Microsoft 365 Copilot', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Security standards', N'Security;Engineering;IT Operations', 110, 250, 150, 3, 250, N'engineering', N'Security Standards.docx', NULL),
 (19, N'cr3c7_incidentSummarizer',  N'Incident Summarizer',      3, N'IT Operations',       'staff', NULL,  75,  10, N'Microsoft Teams', 0, 2, 1, 0, N'Authenticate with Microsoft', 0, 0, N'When an incident is resolved', N'Copilot Studio', 1, N'ServiceNow: Incidents', N'IT Operations;Security', 70, 450, 200, 2, 0, NULL, NULL, NULL),
 (20, N'cr5d4_dealDesk',            N'Deal Desk',                4, N'Sales Operations',    'dir',   NULL, 180,   5, N'Microsoft Teams; Microsoft 365 Copilot', 0, 3, 2, 1, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'Dynamics 365: Opportunities', N'Sales', 170, 220, 180, 4, 0, N'sales', N'Pricing Model.xlsx', N'Pricing Calculator;Account Research'),
 (21, N'cr5d4_pricingCalculator',   N'Pricing Calculator',       4, N'Sales Operations',    'staff', NULL, 175,  40, N'Microsoft Teams', 0, 1, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'Dataverse: Price lists', N'Sales Operations;Partner Sales', 170, 300, 120, 2, 0, N'sales', N'Pricing Model.xlsx', NULL),
 (22, N'cr5d4_accountResearch',     N'Account Research',         4, N'Sales East',          'disabled', NULL, 90, 28, N'Microsoft 365 Copilot', 0, 2, 1, 0, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio', 0, N'Public websites; Dynamics 365: Accounts', N'Sales East;Sales West;Partner Sales', 85, 300, 250, 3, 600, NULL, NULL, NULL),
 (23, N'cr5d4_campaignCopywriter',  N'Campaign Copywriter',      4, N'Marketing',           'mgr',   NULL,  60,   9, N'Microsoft Teams', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio', 0, N'SharePoint: Brand guidelines', N'Marketing', 55, 550, 300, 3, 200, N'sales', N'Brand Guidelines.pptx', NULL),
 (24, N'cr6e8_caseTriage',          N'Case Triage',              5, N'Customer Care',       'dir',   NULL, 150,  13, N'Microsoft Teams; Dynamics 365 Customer Service', 0, 1, 3, 1, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'Dynamics 365: Cases; SharePoint: Care playbooks', N'Customer Care', 140, 700, 300, 4, 0, N'operations', N'Care Playbook.docx', N'Returns Assistant;Knowledge Finder'),
 (25, N'cr6e8_returnsAssistant',    N'Returns Assistant',        5, N'Customer Care',       'mgr',   NULL, 110,  16, N'Website; Microsoft Teams', 0, 1, 1, 0, N'No authentication', 0, 0, NULL, N'Copilot Studio', 1, N'Public website: Returns policy', N'Customer Care;Logistics', 100, 400, 200, 3, 0, NULL, NULL, NULL),
 (26, N'cr6e8_knowledgeFinder',     N'Knowledge Finder',         5, N'Customer Care',       'staff', NULL,  50,  22, N'Microsoft Teams', 12, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'SharePoint: Care playbooks', N'Customer Care;Quality', 45, 250, 200, 2, 0, N'operations', N'Care Playbook.docx', NULL),
 (27, N'cr8b1_policyFinder',        N'Policy Finder',            6, N'Compliance',          'mgr',   NULL, 190,  26, N'Microsoft Teams; Microsoft 365 Copilot; SharePoint', 0, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Policy library', N'Everyone', 180, 30, 40, 2, 0, N'legal', N'Code of Conduct.docx', NULL),
 (28, N'cr8b1_privacyRequestIntake',N'Privacy Request Intake',   6, N'Privacy',             'staff', NULL,  65,  20, N'Microsoft Teams', 0, 1, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Privacy notices', N'Privacy;Compliance;Customer Care', 60, 250, 120, 2, 0, N'legal', N'Privacy Notice.docx', NULL),
 (29, N'cr9z3_codeReviewHelper',    N'Code Review Helper',       7, N'Engineering',         'staff', NULL, 100,   6, N'Microsoft Teams', 30, 1, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'Azure DevOps: Repositories', N'Engineering;Product', 95, 420, 330, 3, 0, N'engineering', N'Coding Standards.docx', NULL),
 (30, N'cr9z3_dataCatalogQa',       N'Data Catalog Q&A',         7, N'Data & Analytics',    'mgr',   NULL,  45,  17, N'Microsoft Teams', 9, 0, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'Fabric: Data catalog', N'Data & Analytics', 40, 120, 200, 2, 0, N'engineering', N'Data Dictionary.xlsx', NULL),
 (31, N'crd20_meetingPrep',         N'Meeting Prep',             0, N'Marketing',           'staff', NULL,  70,  70, N'Microsoft 365 Copilot', 6, 0, 1, 0, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio Lite', 0, N'Outlook; Teams meetings', N'Marketing;Sales East', 65, 60, 150, 2, 100, NULL, NULL, NULL),
 (32, N'crd20_weeklyStatus',        N'Weekly Status Writer',     0, N'Product',             'staff', NULL,  35,  35, N'Microsoft 365 Copilot', 3, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio Lite', 0, N'SharePoint: Product team site', N'Product;Engineering', 33, 30, 200, 2, 0, N'engineering', N'Project Status.docx', NULL),
 (33, N'crd20_customerEmailDrafter',N'Customer Email Drafter',   0, N'Customer Care',       'staff', NULL,  25,  25, N'Microsoft 365 Copilot', 14, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio Lite', 0, N'Outlook', N'Customer Care', 22, 150, 250, 2, 0, NULL, NULL, NULL),
 (34, N'cr4f1_procurementPolicy',   N'Procurement Policy Bot',   1, N'Procurement',         'staff', NULL, 230, 120, N'Microsoft Teams', 0, 1, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'SharePoint: Procurement policy', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (35, N'crd20_fleetTracker',        N'Fleet Tracker',            0, N'Logistics',           'mgr',   NULL, 200, 150, N'Microsoft Teams', 0, 1, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 1, N'Dataverse: Vehicles', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (36, N'cr3c7_patchComplianceReporter', N'Patch Compliance Reporter', 3, N'IT Operations',   'mgr',   NULL, 150,  60, N'Microsoft Teams', 0, 0, 2, 0, N'Authenticate with Microsoft', 0, 0, N'Recurrence: weekly on Monday', N'Copilot Studio', 1, N'Intune: Device compliance', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (37, N'cr4f1_paymentReminder',     N'Payment Reminder',         1, N'Accounting',          'staff', NULL, 260,  90, N'Email', 0, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, N'Recurrence: daily at 07:00', N'Copilot Studio', 1, N'Dataverse: Invoices', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (38, N'cr9z3_testBot',             N'Test bot',                 8, N'Engineering',         'left',  N'raj.iyer@contoso.com', 300, NULL, NULL, 0, 0, 1, 0, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio', 0, NULL, NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (39, N'cr9z3_hrFaqPrototype',      N'HR FAQ prototype',         8, N'HR Business Partners','none',  NULL, 170, NULL, NULL, 0, 0, 0, 0, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio', 0, N'SharePoint: HR policies', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (40, N'crd20_teamsTips',           N'Teams Tips',               0, N'Learning',            'disabled', NULL, 210, 210, N'Microsoft 365 Copilot', 40, 0, 1, 0, N'Authenticate with Microsoft', 0, 1, NULL, N'Copilot Studio Lite', 0, N'Public websites', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (41, N'cr9z3_leadScorer',          N'Lead Scorer',              7, N'Sales Operations',    'none',  NULL, 140, NULL, NULL, 0, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'Dynamics 365: Leads', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (42, N'cr2a9_exitInterview',       N'Exit Interview Assistant', 2, N'HR Business Partners','staff', NULL, 120, 100, N'Microsoft Teams', 0, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'SharePoint: HR policies', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (43, N'cr8b1_riskRegister',        N'Risk Register Assistant',  6, N'Risk',                'left',  N'gareth.pryce@contoso.com', 280, 200, N'Microsoft Teams', 0, 1, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'SharePoint: Risk register', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (44, N'cr6e8_surveyAnalyzer',      N'Survey Analyzer',          5, N'Customer Care',       'staff', NULL,  30, NULL, NULL, 0, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'Dataverse: Survey responses', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (45, N'crd20_projectKickoff',      N'Project Kickoff',          0, N'Product',             'staff', NULL,  15,  15, N'Microsoft 365 Copilot', 0, 0, 1, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio Lite', 0, N'SharePoint: Product team site', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL),
 (46, N'cr4f1_auditEvidence',       N'Audit Evidence Collector', 1, N'Accounting',          'mgr',   NULL,  20, NULL, NULL, 0, 0, 2, 0, N'Authenticate with Microsoft', 0, 0, NULL, N'Copilot Studio', 0, N'SharePoint: Audit FY26', NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL);

/* Its ids, its owner and maker, and how the audit log names it: Copilot.Studio.<environment id>-<schema name>; an
   agent made in Copilot Studio Lite by its title id, Copilot.Studio.Declarative.T_<id>. */
ALTER TABLE #cat ADD AgentId NVARCHAR(36), TitleId NVARCHAR(40), EntraAgentId NVARCHAR(36), OwnerUpn NVARCHAR(256), CreatedBy NVARCHAR(256), Ident NVARCHAR(200);
UPDATE #cat SET AgentId = LOWER(CONVERT(NVARCHAR(36), CAST(HASHBYTES('MD5', CONCAT(N'agent', Schema_)) AS UNIQUEIDENTIFIER))),
                TitleId = CASE WHEN CreatedIn = N'Copilot Studio Lite' THEN N'T_' + LOWER(CONVERT(NVARCHAR(36), CAST(HASHBYTES('MD5', CONCAT(N'title', Schema_)) AS UNIQUEIDENTIFIER))) END,
                EntraAgentId = CASE WHEN PublishedAgo IS NOT NULL THEN LOWER(CONVERT(NVARCHAR(36), CAST(HASHBYTES('MD5', CONCAT(N'entra', Schema_)) AS UNIQUEIDENTIFIER))) END;
UPDATE c SET Ident = CASE WHEN c.TitleId IS NOT NULL THEN N'Copilot.Studio.Declarative.' + c.TitleId ELSE CONCAT(N'Copilot.Studio.', v.Id, N'-', c.Schema_) END
FROM #cat AS c JOIN #env AS v ON v.e = c.e;
UPDATE c SET OwnerUpn = CASE c.OwnerKind WHEN 'left' THEN c.Gone WHEN 'none' THEN NULL ELSE o.Upn END,
             CreatedBy = COALESCE(CASE WHEN c.OwnerKind = 'left' THEN c.Gone END, o.Upn, s.Upn)
FROM #cat AS c
OUTER APPLY (SELECT TOP (1) d.UserPrincipalName AS Upn FROM audit.UserDirectory AS d
             WHERE d.Department = c.OwnerDept AND d.OrgDepth = CASE c.OwnerKind WHEN 'dir' THEN 2 WHEN 'mgr' THEN 3 ELSE 4 END
               AND d.AccountEnabled = CASE WHEN c.OwnerKind = 'disabled' THEN 0 ELSE 1 END AND c.OwnerKind NOT IN ('left', 'none')
             ORDER BY HASHBYTES('MD5', CONCAT(d.UserPrincipalName, c.Schema_))) AS o
OUTER APPLY (SELECT TOP (1) d.UserPrincipalName AS Upn FROM audit.UserDirectory AS d
             WHERE d.Department = c.OwnerDept AND d.OrgDepth = 4 AND d.AccountEnabled = 1
             ORDER BY HASHBYTES('MD5', CONCAT(N'maker', d.UserPrincipalName, c.Schema_))) AS s;

/* Parent agents and the agents they call, as the workbook's ParentChildAgent sheet lists them. A child agent lives
   inside its parent, and Copilot Studio names a new one "Agent" until its maker renames it; a connected agent is an
   agent of its own, and is valid only in its parent's environment (Benefits Navigator moved to HR Services). */
CREATE TABLE #link (ParentA INT, Child NVARCHAR(200), ChildA INT NULL, Component NVARCHAR(100) NULL);
INSERT INTO #link VALUES
 (2, N'Software Catalog', 16, NULL), (2, N'ServiceNow Ticketing', 17, NULL), (2, N'Password Reset', NULL, N'agent.PasswordReset'), (2, N'Agent', NULL, N'agent.Agent'),
 (3, N'Benefits Navigator', 13, NULL), (3, N'Leave Balance', NULL, N'agent.LeaveBalance'),
 (6, N'IT Helpdesk', 2, NULL), (6, N'Learning Coach', 15, NULL), (6, N'Benefits Navigator', 13, NULL),
 (7, N'Journal Entry Checker', NULL, N'agent.JournalEntryChecker'), (7, N'Reconciliation', NULL, N'agent.Reconciliation'),
 (20, N'Pricing Calculator', 21, NULL), (20, N'Account Research', 22, NULL), (20, N'Discount Approval', NULL, N'agent.DiscountApproval'), (20, N'Agent', NULL, N'agent.Agent'),
 (24, N'Returns Assistant', 25, NULL), (24, N'Knowledge Finder', 26, NULL), (24, N'Escalation', NULL, N'agent.Escalation'),
 (29, N'Agent', NULL, N'agent.Agent');

/* The sheets, as the import keeps them: every column as text, dates as yyyy-MM-dd HH:mm:ss. */
DROP TABLE IF EXISTS dbo.AgentInventory, dbo.AgentsInProd, dbo.ParentChildAgent;
CREATE TABLE dbo.AgentInventory
    (AgentId NVARCHAR(MAX) NULL, AgentName NVARCHAR(MAX) NULL, SchemaName NVARCHAR(MAX) NULL, EnvironmentDisplayName NVARCHAR(MAX) NULL,
     EnvironmentId NVARCHAR(MAX) NULL, EnvironmentSku NVARCHAR(MAX) NULL, CreatedIn NVARCHAR(MAX) NULL, OwnerIdUPN NVARCHAR(MAX) NULL,
     CreatedByUPN NVARCHAR(MAX) NULL, CreatedAt NVARCHAR(MAX) NULL, LastPublishedAt NVARCHAR(MAX) NULL, Channels NVARCHAR(MAX) NULL,
     ViewerUsers NVARCHAR(MAX) NULL, ViewerGroups NVARCHAR(MAX) NULL, EditorUsers NVARCHAR(MAX) NULL, EditorGroups NVARCHAR(MAX) NULL,
     [Authentication] NVARCHAR(MAX) NULL, IsQuarantined NVARCHAR(MAX) NULL, IsWebSearchEnabled NVARCHAR(MAX) NULL, Triggers NVARCHAR(MAX) NULL,
     KnowledgeSources NVARCHAR(MAX) NULL, TitleId NVARCHAR(MAX) NULL, EntraAgentId NVARCHAR(MAX) NULL);
INSERT INTO dbo.AgentInventory
SELECT c.AgentId, c.Name, c.Schema_, v.Name, v.Id, v.Sku, c.CreatedIn, c.OwnerUpn, c.CreatedBy,
       CONVERT(NVARCHAR(19), DATEADD(MINUTE, 600 + c.a * 17, CAST(DATEADD(DAY, -c.CreatedAgo, @today) AS DATETIME2(0))), 120),
       CONVERT(NVARCHAR(19), DATEADD(MINUTE, 660 + c.a * 23, CAST(DATEADD(DAY, -c.PublishedAgo, @today) AS DATETIME2(0))), 120),
       c.Channels, CAST(c.VU AS NVARCHAR(10)), CAST(c.VG AS NVARCHAR(10)), CAST(c.EU AS NVARCHAR(10)), CAST(c.EG AS NVARCHAR(10)),
       c.Auth, CASE WHEN c.Quarantined = 1 THEN N'True' ELSE N'False' END, CASE WHEN c.Web = 1 THEN N'True' ELSE N'False' END,
       c.Triggers, c.Knowledge, c.TitleId, c.EntraAgentId
FROM #cat AS c JOIN #env AS v ON v.e = c.e;

CREATE TABLE dbo.AgentsInProd (AgentId NVARCHAR(MAX) NULL, AgentName NVARCHAR(MAX) NULL, EnvironmentDisplayName NVARCHAR(MAX) NULL,
                               PromotedOn NVARCHAR(MAX) NULL, ApprovedBy NVARCHAR(MAX) NULL, SupportTeam NVARCHAR(MAX) NULL);
INSERT INTO dbo.AgentsInProd
SELECT c.AgentId, c.Name, v.Name, CONVERT(NVARCHAR(10), DATEADD(DAY, -(c.CreatedAgo * 3 / 4), @today), 120),
       (SELECT TOP (1) d.UserPrincipalName FROM audit.UserDirectory AS d WHERE d.Department = N'IT Operations' AND d.OrgDepth = 2),
       CONCAT(c.OwnerDept, N' agent team')
FROM #cat AS c JOIN #env AS v ON v.e = c.e WHERE c.InProd = 1;

CREATE TABLE dbo.ParentChildAgent (ParentAgentId NVARCHAR(MAX) NULL, ParentAgentName NVARCHAR(MAX) NULL, ChildAgentName NVARCHAR(MAX) NULL,
                                   ComponentName NVARCHAR(MAX) NULL, ComponentId NVARCHAR(MAX) NULL, RelationshipType NVARCHAR(MAX) NULL,
                                   IsValidParentChildAgent NVARCHAR(MAX) NULL, EnvironmentId NVARCHAR(MAX) NULL, EnvironmentSku NVARCHAR(MAX) NULL,
                                   SchemaName NVARCHAR(MAX) NULL);
INSERT INTO dbo.ParentChildAgent
SELECT p.AgentId, p.Name, l.Child,
       CASE WHEN l.ChildA IS NULL THEN l.Child END,
       CASE WHEN l.ChildA IS NULL THEN LOWER(CONVERT(NVARCHAR(36), CAST(HASHBYTES('MD5', CONCAT(N'component', p.Schema_, l.Component)) AS UNIQUEIDENTIFIER))) END,
       CASE WHEN l.ChildA IS NULL THEN N'ChildAgent' ELSE N'ConnectedAgent' END,
       CASE WHEN l.ChildA IS NULL OR ch.e = p.e THEN N'True' ELSE N'False' END,
       pv.Id, pv.Sku,
       CASE WHEN l.ChildA IS NULL THEN CONCAT(p.Schema_, N'.', l.Component) ELSE CONCAT(p.Schema_, N'.InvokeConnectedAgentTaskAction.', ch.Schema_) END
FROM #link AS l
JOIN #cat AS p ON p.a = l.ParentA JOIN #env AS pv ON pv.e = p.e
LEFT JOIN #cat AS ch ON ch.a = l.ChildA;
IF DATABASE_PRINCIPAL_ID(N'CopilotInsightsApp') IS NOT NULL
    EXEC (N'GRANT SELECT ON dbo.AgentInventory TO CopilotInsightsApp; GRANT SELECT ON dbo.AgentsInProd TO CopilotInsightsApp; GRANT SELECT ON dbo.ParentChildAgent TO CopilotInsightsApp;');

/* ---- Use: every session this script adds, then its prompts ------------------------------------------------- */

/* The people an agent can reach: everyone in the org chart with an account in use, where they sit, and the day they
   first used Copilot. Someone who never has (a dormant license holder) does not start with an agent, so the
   license and adoption pages keep the people they had. */
SELECT d.UserPrincipalName AS Upn, d.Department, d.Division,
       CASE d.Country WHEN N'Canada' THEN N'CA' WHEN N'United States' THEN N'US' WHEN N'United Kingdom' THEN N'GB' ELSE N'CA' END AS Loc,
       f.FirstUse
INTO #pop
FROM audit.UserDirectory AS d
LEFT JOIN (SELECT [User] AS Upn, CAST(MIN(UtcTimeStamp) AS DATE) AS FirstUse FROM audit.RawLogFlattened GROUP BY [User]) AS f
       ON f.Upn = d.UserPrincipalName
WHERE d.AccountEnabled = 1 AND (d.ManagerUPN IS NOT NULL OR d.IsManager = 1);

/* Kind: a an agent's use, c credits, g a guest, s an account above. Site and Doc: what a prompt reads, where it reads
   anything; LabelPm per thousand of those reads carry Label. */
CREATE TABLE #x (Kind CHAR(1) NOT NULL, Upn NVARCHAR(256) NOT NULL, [Day] DATE NOT NULL, DaysAgo INT NOT NULL, StartUtc DATETIME2(0) NOT NULL,
                 App NVARCHAR(60) NOT NULL, Ident NVARCHAR(200) NULL, Prompts INT NOT NULL, WebPm INT NOT NULL, ReadPm INT NOT NULL,
                 Site NVARCHAR(60) NULL, Doc NVARCHAR(100) NULL, LabelPm INT NOT NULL, Label NVARCHAR(40) NULL,
                 Jailbreak BIT NOT NULL, Blocked BIT NOT NULL, Calls NVARCHAR(200) NULL, Loc NVARCHAR(4) NOT NULL, h INT NOT NULL);

/* The agents: who uses each, then on which days. Use grows over an agent's first five weeks. */
SELECT c.a, p.Upn, p.Loc, p.FirstUse
INTO #reach
FROM #cat AS c
JOIN #pop AS p ON c.Scope = N'Everyone' OR EXISTS (SELECT 1 FROM STRING_SPLIT(c.Scope, N';') AS s WHERE s.value IN (p.Department, p.Division))
WHERE c.Scope IS NOT NULL AND p.FirstUse IS NOT NULL
  AND ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'reach', c.a, N'-', p.Upn)), 1, 4) AS INT)) % 1000 < c.ReachPm;

INSERT INTO #x (Kind, Upn, [Day], DaysAgo, StartUtc, App, Ident, Prompts, WebPm, ReadPm, Site, Doc, LabelPm, Label, Jailbreak, Blocked, Calls, Loc, h)
SELECT 'a', r.Upn, d.[Day], d.DaysAgo,
       DATEADD(MINUTE, hh.v % 50, DATEADD(HOUR, 12 + hh.v % 9, CAST(d.[Day] AS DATETIME2(0)))),
       N'Copilot Studio', c.Ident, 1 + (hh.v / 7) % c.MaxPrompts, c.WebPm, CASE WHEN c.Site IS NULL THEN 0 ELSE 550 END,
       CASE WHEN c.Site IS NOT NULL THEN CONCAT(N'https://contoso.sharepoint.com/sites/', c.Site) END, c.Doc,
       CASE WHEN c.Site IN (N'finance', N'hr', N'legal') THEN 250 ELSE 60 END, N'5e1f0c2a-0000-4000-8000-000000000002',
       0, 0, c.Calls, r.Loc, hh.v
FROM #reach AS r
JOIN #cat AS c ON c.a = r.a
JOIN #days AS d ON d.DaysAgo <= c.LaunchAgo AND d.[Day] >= r.FirstUse
CROSS APPLY (SELECT ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'use', c.a, N'-', r.Upn, N'-', d.DaysAgo)), 1, 4) AS INT)) AS v) AS hh
WHERE hh.v % 1000 < c.DayPm * CASE WHEN d.Weekend = 1 THEN 1 ELSE 10 END / 10
                    * CASE WHEN c.LaunchAgo - d.DaysAgo < 35 THEN (c.LaunchAgo - d.DaysAgo + 5) / 40.0 ELSE 1.0 END;

/* Credits: people with no license, ever, whose work runs through an agent - case handlers, the service desk,
   recruiters, the contracts team and deal desk analysts - three to five sessions a working day, three to six prompts
   each: about 12 to 30 metered interactions a day, worth more a month in Copilot Credits than a license costs. */
SELECT TOP (CAST(ROUND(4 * @scale, 0) AS INT)) p.Upn, p.Loc,
       CASE p.Department WHEN N'Customer Care' THEN 24 WHEN N'IT Operations' THEN 2 WHEN N'Talent Acquisition' THEN 14
                         WHEN N'Contracts' THEN 5 ELSE 20 END AS a,
       ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'credit', p.Upn)), 1, 4) AS INT)) AS h
INTO #credit
FROM #pop AS p
JOIN audit.UserDirectory AS d ON d.UserPrincipalName = p.Upn AND d.OrgDepth = 4
JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = p.Upn AND cl.IsCopilotLic = 0 AND cl.EmployeeHireDate IS NULL
WHERE p.Department IN (N'Customer Care', N'IT Operations', N'Talent Acquisition', N'Contracts', N'Sales Operations')
  AND NOT EXISTS (SELECT 1 FROM #lic AS l WHERE l.Upn = p.Upn)
ORDER BY HASHBYTES('MD5', CONCAT(N'credit', p.Upn));
WITH k AS (SELECT n FROM (VALUES (1), (2), (3), (4), (5)) AS v (n))
INSERT INTO #x (Kind, Upn, [Day], DaysAgo, StartUtc, App, Ident, Prompts, WebPm, ReadPm, Site, Doc, LabelPm, Label, Jailbreak, Blocked, Calls, Loc, h)
SELECT 'c', u.Upn, d.[Day], d.DaysAgo,
       DATEADD(MINUTE, hh.v % 40, DATEADD(HOUR, 12 + 2 * k.n - 1, CAST(d.[Day] AS DATETIME2(0)))),
       N'Copilot Studio', c.Ident, 3 + (hh.v / 11) % 4, c.WebPm, CASE WHEN c.Site IS NULL THEN 0 ELSE 400 END,
       CASE WHEN c.Site IS NOT NULL THEN CONCAT(N'https://contoso.sharepoint.com/sites/', c.Site) END, c.Doc, 60, N'5e1f0c2a-0000-4000-8000-000000000001',
       0, 0, c.Calls, u.Loc, hh.v
FROM #credit AS u
JOIN #cat AS c ON c.a = u.a
JOIN #days AS d ON d.Weekend = 0 AND d.DaysAgo <= CASE WHEN c.LaunchAgo IS NULL OR c.LaunchAgo > 150 THEN 150 ELSE c.LaunchAgo END
JOIN k ON k.n <= 3 + u.h % 3
CROSS APPLY (SELECT ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'cday', u.Upn, N'-', d.DaysAgo, N'-', k.n)), 1, 4) AS INT)) AS v) AS hh
WHERE ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'cday', u.Upn, N'-', d.DaysAgo)), 1, 4) AS INT)) % 100 < 88;

/* Guests: each from the start of their engagement, two to five days in ten on weekdays, in Teams and Copilot Chat,
   reading the project site their organization works on; the licensed two across Microsoft 365. Grace Okoro tried a
   jailbreak nine days ago; Victor Duval's prompt reached for the payroll file. */
INSERT INTO #x (Kind, Upn, [Day], DaysAgo, StartUtc, App, Ident, Prompts, WebPm, ReadPm, Site, Doc, LabelPm, Label, Jailbreak, Blocked, Calls, Loc, h)
SELECT 'g', g.Upn, d.[Day], d.DaysAgo,
       DATEADD(MINUTE, hh.v % 50, DATEADD(HOUR, CASE WHEN g.Loc IN (N'US', N'CA') THEN 13 ELSE 8 END + hh.v % 7, CAST(d.[Day] AS DATETIME2(0)))),
       a.App, CONCAT(N'Copilot.M365Copilot.', REPLACE(a.App, N' ', N'')), 1 + (hh.v / 7) % CASE WHEN g.Licensed = 1 THEN 4 ELSE 3 END,
       150, 450, CONCAT(N'https://contoso.sharepoint.com/sites/', g.Site),
       CHOOSE(hh.v / 13 % 4 + 1, N'Project Status.docx', N'Meeting Notes.docx', N'Team Plan.docx', N'Q3 Forecast.xlsx'),
       CASE WHEN g.Domain IN (N'adatum.com', N'woodgrovebank.com') THEN 300 ELSE 120 END, N'5e1f0c2a-0000-4000-8000-000000000002',
       CASE WHEN g.n = 13 AND d.DaysAgo = 9 THEN 1 ELSE 0 END, CASE WHEN g.n = 22 AND d.DaysAgo = 23 THEN 1 ELSE 0 END, NULL, g.Loc, hh.v
FROM #guest AS g
JOIN #days AS d ON d.DaysAgo <= 25 + (g.n * 37) % 150
CROSS APPLY (SELECT ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'guest', g.n, N'-', d.DaysAgo)), 1, 4) AS INT)) AS v) AS hh
CROSS APPLY (SELECT CASE WHEN g.Licensed = 1 THEN CHOOSE(hh.v % 6 + 1, N'Microsoft Teams', N'Word', N'Outlook', N'Office', N'Excel', N'Microsoft Teams')
                         ELSE CHOOSE(hh.v % 3 + 1, N'Microsoft Teams', N'Office', N'Microsoft Teams') END AS App) AS a
WHERE g.n % 7 <> 5   -- a few guests have access but have not used Copilot
  AND ((d.Weekend = 0 AND hh.v % 100 < CASE WHEN g.Licensed = 1 THEN 70 ELSE 20 + (g.n * 3) % 30 END)
       OR (g.n = 13 AND d.DaysAgo = 9) OR (g.n = 22 AND d.DaysAgo = 23));

/* The accounts: the reporting service every night, the shared mailboxes on most working days, the test accounts
   now and then (the second not since its tester moved on), and the adviser like any licensed person. */
INSERT INTO #x (Kind, Upn, [Day], DaysAgo, StartUtc, App, Ident, Prompts, WebPm, ReadPm, Site, Doc, LabelPm, Label, Jailbreak, Blocked, Calls, Loc, h)
SELECT 's', a.Upn, d.[Day], d.DaysAgo,
       CASE WHEN a.Use_ = 'nightly' THEN DATEADD(MINUTE, 10 + hh.v % 5, DATEADD(HOUR, 6, CAST(d.[Day] AS DATETIME2(0))))
            ELSE DATEADD(MINUTE, hh.v % 50, DATEADD(HOUR, 12 + hh.v % 9, CAST(d.[Day] AS DATETIME2(0)))) END,
       x.App, CONCAT(N'Copilot.M365Copilot.', REPLACE(x.App, N' ', N'')),
       CASE a.Use_ WHEN 'nightly' THEN 6 + hh.v % 4 WHEN 'mailbox' THEN 1 + hh.v % 4 ELSE 1 + hh.v % 3 END,
       CASE WHEN a.Use_ = 'person' THEN 200 ELSE 0 END, CASE WHEN a.Use_ = 'nightly' THEN 1000 ELSE 400 END,
       CASE a.Use_ WHEN 'nightly' THEN N'https://contoso.sharepoint.com/sites/engineering' WHEN 'test' THEN N'https://contoso.sharepoint.com/sites/allcompany'
                   ELSE N'https://contoso.sharepoint.com/sites/finance' END,
       CASE a.Use_ WHEN 'nightly' THEN N'Service Health Report.xlsx' WHEN 'test' THEN N'Team Plan.docx' WHEN 'person' THEN N'Board Deck.pptx' ELSE N'Q3 Forecast.xlsx' END,
       CASE WHEN a.Use_ = 'person' THEN 300 ELSE 50 END, N'5e1f0c2a-0000-4000-8000-000000000003', 0, 0, NULL, N'CA', hh.v
FROM #acct AS a
JOIN #days AS d ON d.DaysAgo < a.LicensedDaysAgo
CROSS APPLY (SELECT ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'acct', a.n, N'-', d.DaysAgo)), 1, 4) AS INT)) AS v) AS hh
CROSS APPLY (SELECT CASE a.Use_ WHEN 'nightly' THEN N'BizChat' WHEN 'mailbox' THEN CASE WHEN a.n = 4 AND hh.v % 3 = 0 THEN N'Microsoft Teams' ELSE N'Outlook' END
                                WHEN 'test' THEN CHOOSE(hh.v % 4 + 1, N'Word', N'Microsoft Teams', N'Excel', N'Outlook')
                                ELSE CHOOSE(hh.v % 6 + 1, N'Outlook', N'Word', N'Microsoft Teams', N'Excel', N'PowerPoint', N'Office') END AS App) AS x
WHERE CASE a.Use_ WHEN 'nightly' THEN 1
                  WHEN 'mailbox' THEN CASE WHEN d.Weekend = 0 AND hh.v % 100 < 55 THEN 1 ELSE 0 END
                  WHEN 'test' THEN CASE WHEN d.Weekend = 0 AND hh.v % 100 < 12 AND (a.n = 7 OR d.DaysAgo > 40) THEN 1 ELSE 0 END
                  WHEN 'person' THEN CASE WHEN d.Weekend = 0 AND hh.v % 100 < 65 THEN 1 ELSE 0 END
                  ELSE 0 END = 1;

/* ---- The prompts, as the flattening writes them ---------------------------------------------------------- */

/* Numbered in a fixed order, so the same records come out every time. */
SELECT ROW_NUMBER() OVER (ORDER BY x.Kind, x.Upn, x.StartUtc, x.Ident) AS SessionNo, x.* INTO #xs FROM #x AS x;
WITH k AS (SELECT TOP (9) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns),
p AS
(
    SELECT x.*, k.n AS PromptNo,
           DATEADD(SECOND, (k.n - 1) * (60 + x.h % 480), x.StartUtc) AS Utc,
           ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'x', x.Kind, x.SessionNo, N'-', k.n)), 1, 4) AS INT)) AS hp,
           ABS(CAST(SUBSTRING(HASHBYTES('MD5', CONCAT(N'x', x.Kind, x.SessionNo, N'-', k.n)), 5, 4) AS INT)) AS hp2
    FROM #xs AS x JOIN k ON k.n <= x.Prompts
)
INSERT INTO audit.RawLogFlattened WITH (TABLOCK)
    (RecordId, [TimeStamp], UtcTimeStamp, [User], App, Location, [App context], [Accessed Resources], [Accessed Resource Locations],
     [Action], AIPlugin, JailbreakDetected, PromptInjectionDetected, BlockedResourceUrls, SensitivityLabelIds,
     IsCopilotLic, AppIdentity, ThreadId, ModelName, ModelProvider, AccessedResourceCount, PromptCount,
     CopilotEventData_LicenseType, CopilotEventData_MemoryUpdated, CopilotEventData_TargetAgentName, OrganizationId, MessageCount)
SELECT CONCAT(N'demo-', p.Kind, N'-', p.SessionNo, N'-', p.PromptNo),
       CONVERT(NVARCHAR(30), p.Utc, 126) + N'Z', p.Utc, p.Upn, p.App, p.Loc, NULL,
       CASE WHEN r.Reads = 1 THEN p.Doc END,
       CASE WHEN r.Reads = 1 THEN CONCAT(p.Site, N'/Shared Documents/', p.Doc) END,
       CASE WHEN r.Reads = 1 THEN N'Read' END,
       CASE WHEN p.hp % 1000 < p.WebPm THEN N'BingWebSearch' END,
       CASE WHEN p.Jailbreak = 1 AND p.PromptNo = 1 THEN N'true' ELSE N'false' END, N'false',
       CASE WHEN p.Blocked = 1 AND p.PromptNo = 1 THEN N'https://contoso.sharepoint.com/sites/hr/Payroll/Salary Bands.xlsx' END,
       CASE WHEN r.Reads = 1 AND p.hp2 % 1000 < p.LabelPm THEN p.Label END,
       l.Held, p.Ident, CONCAT(N'thread-demo-', p.Kind, N'-', p.SessionNo),
       m.Model, CASE WHEN m.Model LIKE N'claude%' THEN N'Anthropic' ELSE N'OpenAI' END,
       CASE WHEN r.Reads = 1 THEN 1 + p.hp % 2 ELSE 0 END, 1,
       CASE WHEN l.Held = 1 THEN N'Premium' ELSE N'Starter' END,
       CASE WHEN p.hp2 % 40 = 0 THEN 1 ELSE 0 END,
       CASE WHEN p.Calls IS NOT NULL AND p.hp2 % 10 < 3
            THEN CASE WHEN p.hp2 % 2 = 0 OR CHARINDEX(N';', p.Calls) = 0 THEN LEFT(p.Calls, CHARINDEX(N';', p.Calls + N';') - 1)
                      ELSE SUBSTRING(p.Calls, CHARINDEX(N';', p.Calls) + 1, 200) END END,
       @org, 2
FROM p
CROSS APPLY (SELECT CASE WHEN p.Site IS NOT NULL AND p.hp % 1000 < p.ReadPm THEN 1 ELSE 0 END AS Reads) AS r
CROSS APPLY (SELECT CASE WHEN p.DaysAgo > 120 AND p.hp % 10 < 6 THEN N'gpt-4o' WHEN p.DaysAgo <= 60 AND p.hp % 10 < 2 THEN N'claude-sonnet-4-6'
                         ELSE N'gpt-5' END AS Model) AS m
OUTER APPLY (SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM #lic AS li WHERE li.Upn = p.Upn AND li.FromUtc <= p.Utc AND li.ToUtc > p.Utc) THEN 1 ELSE 0 END AS BIT) AS Held) AS l;

/* The element rows for what those prompts read, and the blocked payroll file, as New-InsightsDemoData.sql writes them. */
INSERT INTO audit.RawLogAccessedResource WITH (TABLOCK)
    (RecordId, Ordinal, [Action], [Type], Name, SiteUrl, Site, SensitivityLabelId, XPIADetected, [Status], IsBlocked)
SELECT f.RecordId, x.Ordinal, N'Read', x.[Type], x.Name, x.Url,
       LEFT(x.Url, CHARINDEX(N'/', x.Url + N'/', CHARINDEX(N'/', x.Url + N'/', CHARINDEX(N'/', x.Url, 9) + 1) + 1) - 1),
       x.Label, 0, x.[Status], x.Blocked
FROM audit.RawLogFlattened AS f
CROSS APPLY (
    SELECT 0 AS Ordinal, N'File' AS [Type], f.[Accessed Resources] AS Name, f.[Accessed Resource Locations] AS Url,
           TRY_CONVERT(UNIQUEIDENTIFIER, f.SensitivityLabelIds) AS Label, CAST(NULL AS VARCHAR(64)) AS [Status], CAST(0 AS BIT) AS Blocked
    WHERE f.[Accessed Resource Locations] IS NOT NULL
    UNION ALL
    SELECT 1, N'File', N'Salary Bands.xlsx', f.BlockedResourceUrls, NULL, 'Failure', CAST(1 AS BIT)
    WHERE f.BlockedResourceUrls IS NOT NULL
) AS x
WHERE f.RecordId LIKE N'demo-[acgs]-%';

/* ---- Two accounts an administrator confirmed are people ------------------------------------------------- */
DELETE FROM dash.NonHumanException WHERE Upn IN (N'theodora.vance@contoso.com', (SELECT Upn FROM #guest WHERE n = 1));
INSERT INTO dash.NonHumanException (Upn, Reason, ConfirmedBy, ConfirmedUtc)
SELECT N'theodora.vance@contoso.com', N'Interim adviser to the CFO on a twelve-month contract; a cloud-only account by design, so no manager in AD.',
       d.UserPrincipalName, DATEADD(HOUR, 15, CAST(DATEADD(DAY, -61, @today) AS DATETIME2(0)))
FROM audit.UserDirectory AS d WHERE d.Department = N'IT Operations' AND d.OrgDepth = 2
UNION ALL
SELECT g.Upn, N'Fabrikam consultant embedded in the ERP program; licensed here on Procurement''s approval until the program ends.',
       d.UserPrincipalName, DATEADD(HOUR, 16, CAST(DATEADD(DAY, -34, @today) AS DATETIME2(0)))
FROM #guest AS g CROSS JOIN audit.UserDirectory AS d WHERE g.n = 1 AND d.Department = N'IT Operations' AND d.OrgDepth = 2;

/* What was added, for the build's log; then the temp tables go, before the session and bi procedures make their own. */
SELECT (SELECT COUNT(*) FROM #acct) AS Accounts, (SELECT COUNT(*) FROM #guest) AS Guests,
       (SELECT COUNT(*) FROM #leave) AS Leaving, (SELECT COUNT(*) FROM #cat) AS Agents,
       (SELECT COUNT(*) FROM #credit) AS CreditUsers,
       (SELECT COUNT_BIG(*) FROM audit.RawLogFlattened WHERE RecordId LIKE N'demo-[acgs]-%') AS Interactions;
DROP TABLE IF EXISTS #days, #acct, #guest, #env, #cat, #link, #pop, #reach, #x, #lic, #leave, #credit, #xs;
