/*
    Copilot audit backfill - enrichment lookup tables
    ------------------------------------------------------------------------
    Facts about users that the audit records cannot carry, kept in their own
    tables so nothing here touches the backfill path.

      audit.CopilotLicense   who holds a Copilot license, and since when -
                             from Microsoft Graph, by
                             tools/Update-CopilotAuditEnrichment.ps1
      audit.UserDirectory    title, department, division, office, manager and
                             the management chain - from on-premises Active
                             Directory, by tools/Export-UsersWithHierarchy-ToSql.ps1

    Before 2.0.0 the Graph tool also filled audit.UserProfile and
    audit.AdministrativeUnit. Nothing writes or reads them any more; on a
    database that has them they are left in place, and can be dropped.

    audit.CopilotLicense is also created by Create-CopilotFlattenedSchema.sql,
    which shipped before this file existed and made only three columns. The
    ALTERs below add the rest, so a database created by either script ends up
    the same shape.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* ---- Licenses ---------------------------------------------------------- */

IF OBJECT_ID(N'audit.CopilotLicense', N'U') IS NULL
    CREATE TABLE audit.CopilotLicense
    (
        UserPrincipalName   NVARCHAR(512)   NOT NULL
                                CONSTRAINT PK_CopilotLicense PRIMARY KEY,
        IsCopilotLic        BIT             NULL,   -- NULL = not known, see above
        UpdatedUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_CopilotLicense_Updated DEFAULT (SYSUTCDATETIME())
    );

/*
    IsCopilotLic must allow NULL, which shipped as NOT NULL in
    Create-CopilotFlattenedSchema.sql before there was anything to record but a
    yes or a no. There is now a third answer: a user who appears in the audit
    data but no longer exists in the directory is unknown, not unlicensed, and
    conflating the two would understate licensed usage.
*/
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'audit.CopilotLicense')
             AND name = N'IsCopilotLic' AND is_nullable = 0)
    ALTER TABLE audit.CopilotLicense ALTER COLUMN IsCopilotLic BIT NULL;

/* The Copilot SKU the license came from, e.g. Microsoft_365_Copilot. */
IF COL_LENGTH(N'audit.CopilotLicense', N'SkuPartNumber') IS NULL
    ALTER TABLE audit.CopilotLicense ADD SkuPartNumber NVARCHAR(100) NULL;

/*
    assignedPlans[].assignedDateTime for the Copilot service plan. This is the
    only history Graph offers: it says when the CURRENT assignment began, so
    "licensed since March" is answerable but "was this user licensed last
    March" is not - a license granted and later removed leaves no trace.
*/
IF COL_LENGTH(N'audit.CopilotLicense', N'LicensedSinceUtc') IS NULL
    ALTER TABLE audit.CopilotLicense ADD LicensedSinceUtc DATETIME2(0) NULL;

/* The Entra account's state and hire date, from the same user lookup (User.Read.All):
   a license on a disabled account is wasted, and the hire date starts the
   new-joiner report's clock. AD's own AccountEnabled is in audit.UserDirectory. */
IF COL_LENGTH(N'audit.CopilotLicense', N'EntraAccountEnabled') IS NULL
    ALTER TABLE audit.CopilotLicense ADD EntraAccountEnabled BIT NULL;
IF COL_LENGTH(N'audit.CopilotLicense', N'EmployeeHireDate') IS NULL
    ALTER TABLE audit.CopilotLicense ADD EmployeeHireDate DATE NULL;

/* Entra's employeeLeaveDateTime, when the license tool runs with -IncludeLeaveDates (it needs
   User-LifeCycleInfo.Read.All), for Licensing > Departures ahead. */
IF COL_LENGTH(N'audit.CopilotLicense', N'EmployeeLeaveUtc') IS NULL
    ALTER TABLE audit.CopilotLicense ADD EmployeeLeaveUtc DATETIME2(0) NULL;
/* Member or Guest, from the same lookup: guests using Copilot are a report of their own. */
IF COL_LENGTH(N'audit.CopilotLicense', N'UserType') IS NULL
    ALTER TABLE audit.CopilotLicense ADD UserType NVARCHAR(20) NULL;

/*
    Members of the AD groups granted Organization in the dashboard (dash.RoleGrant), expanded
    by the directory load, nested groups included. bi.OrgSecurity gives them the whole organization,
    so Power BI honors a group grant the way the dashboard does at sign-in. Replaced whole each load.
*/
IF OBJECT_ID(N'audit.GroupMember', N'U') IS NULL
    CREATE TABLE audit.GroupMember
    (
        GroupName          NVARCHAR(256)  NOT NULL,   -- as granted: DOMAIN\Group
        UserPrincipalName  NVARCHAR(256)  NOT NULL,
        LoadedUtc          DATETIME2(0)   NOT NULL CONSTRAINT DF_GroupMember_Loaded DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_GroupMember PRIMARY KEY (GroupName, UserPrincipalName)
    );

/*
    The tenant's license pool, from /subscribedSkus (LicenseAssignment.Read.All),
    which the license tool already reads to find the Copilot SKUs: seats bought
    (prepaidUnits) and assigned (consumedUnits) per SKU. LicenseSku is the
    latest; LicenseSkuDay keeps one row per SKU per day for the trend.
*/
IF OBJECT_ID(N'audit.LicenseSku', N'U') IS NULL
    CREATE TABLE audit.LicenseSku
    (
        SkuId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_LicenseSku PRIMARY KEY,
        SkuPartNumber  NVARCHAR(100)    NOT NULL,
        IsCopilot      BIT              NOT NULL,
        Enabled        INT              NOT NULL,   -- prepaidUnits.enabled: seats bought and active
        Warning        INT              NOT NULL,   -- prepaidUnits.warning: in the grace period after expiry
        Suspended      INT              NOT NULL,
        LockedOut      INT              NOT NULL,
        Consumed       INT              NOT NULL,   -- consumedUnits: seats assigned
        CapabilityStatus NVARCHAR(32)   NULL,
        CapturedUtc    DATETIME2(0)     NOT NULL CONSTRAINT DF_LicenseSku_Captured DEFAULT (SYSUTCDATETIME())
    );
IF OBJECT_ID(N'audit.LicenseSkuDay', N'U') IS NULL
    CREATE TABLE audit.LicenseSkuDay
    (
        SnapshotDate   DATE             NOT NULL,
        SkuId          UNIQUEIDENTIFIER NOT NULL,
        SkuPartNumber  NVARCHAR(100)    NOT NULL,
        IsCopilot      BIT              NOT NULL,
        Enabled        INT              NOT NULL,
        Warning        INT              NOT NULL,
        Suspended      INT              NOT NULL,
        LockedOut      INT              NOT NULL,
        Consumed       INT              NOT NULL,
        CONSTRAINT PK_LicenseSkuDay PRIMARY KEY (SnapshotDate, SkuId)
    );

/* A suite that carries Copilot inside it (Microsoft 365 E7; the tool's -CopilotSuiteSku). Its holders are
   licensed for Copilot, but the Copilot cannot be taken off alone, and its price is the suite's. NULL before 2.6.6. */
IF COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NULL
    ALTER TABLE audit.LicenseSku ADD CopilotInSuite BIT NULL;

/* Every SKU each person looked up holds (assignedLicenses), not only Copilot, so the dashboard can find
   licenses that overlap. Replaced whole by each run; names come from audit.LicenseSku. */
IF OBJECT_ID(N'audit.UserLicenseSku', N'U') IS NULL
    CREATE TABLE audit.UserLicenseSku
    (
        UserPrincipalName   NVARCHAR(512)    NOT NULL,
        SkuId               UNIQUEIDENTIFIER NOT NULL,
        CapturedUtc         DATETIME2(0)     NOT NULL CONSTRAINT DF_UserLicenseSku_Captured DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_UserLicenseSku PRIMARY KEY (UserPrincipalName, SkuId)
    );

/* Enabled | Suspended | Deleted | Warning, from assignedPlans[]. */
IF COL_LENGTH(N'audit.CopilotLicense', N'CapabilityStatus') IS NULL
    ALTER TABLE audit.CopilotLicense ADD CapabilityStatus NVARCHAR(50) NULL;

/*
    Audit records outlive directory objects. A user who has left resolves to
    nothing in Graph, and must not be reported as unlicensed - that would be a
    different claim from "we do not know". DirectoryStatus records which it is.
*/
IF COL_LENGTH(N'audit.CopilotLicense', N'DirectoryStatus') IS NULL
    ALTER TABLE audit.CopilotLicense ADD DirectoryStatus NVARCHAR(20) NULL;   -- Found | NotFound

/* ---- The user directory (on-premises AD) ------------------------------ */
/*
    One row per user, replaced whole on every load. Joined to the audit data
    on UserPrincipalName, so AD's UPN must be the one users sign in to
    Microsoft 365 with; the load reports how many audit users it matched, by
    UPN and by Mail, so a mismatch shows at once.

    Mgr1 is the top of the chain (the CEO) and Mgr<OrgDepth> the direct
    manager; the columns after it are empty. That is the layout a Power BI
    hierarchy wants. Chains deeper than 15 keep their top 15 levels; OrgDepth
    is always the full depth.
*/
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NULL
    CREATE TABLE audit.UserDirectory
    (
        UserPrincipalName  NVARCHAR(256)  NOT NULL CONSTRAINT PK_UserDirectory PRIMARY KEY,
        Mail               NVARCHAR(256)  NULL,
        FullName           NVARCHAR(256)  NULL,       -- displayName
        EmployeeType       NVARCHAR(32)   NULL,       -- after -EmployeeTypeMap, e.g. EMPLOYEE / CONTRACTOR
        IsManager          BIT            NOT NULL,   -- someone reports to them
        AccountEnabled     BIT            NOT NULL,
        JobTitle           NVARCHAR(128)  NULL,       -- title
        Department         NVARCHAR(200)  NULL,       -- department, or the first of -DepartmentAttribute that has a value
        Division           NVARCHAR(256)  NULL,       -- division: the line of business
        Office             NVARCHAR(128)  NULL,       -- physicalDeliveryOfficeName
        City               NVARCHAR(128)  NULL,       -- l
        Country            NVARCHAR(128)  NULL,       -- co, else the English name of c
        Company            NVARCHAR(64)   NULL,       -- company
        ManagerUPN         NVARCHAR(256)  NULL,
        ManagerName        NVARCHAR(256)  NULL,
        OrgDepth           TINYINT        NOT NULL,   -- managers above this user; 0 = the top
        Mgr1  NVARCHAR(256) NULL, Mgr2  NVARCHAR(256) NULL, Mgr3  NVARCHAR(256) NULL,
        Mgr4  NVARCHAR(256) NULL, Mgr5  NVARCHAR(256) NULL, Mgr6  NVARCHAR(256) NULL,
        Mgr7  NVARCHAR(256) NULL, Mgr8  NVARCHAR(256) NULL, Mgr9  NVARCHAR(256) NULL,
        Mgr10 NVARCHAR(256) NULL, Mgr11 NVARCHAR(256) NULL, Mgr12 NVARCHAR(256) NULL,
        Mgr13 NVARCHAR(256) NULL, Mgr14 NVARCHAR(256) NULL, Mgr15 NVARCHAR(256) NULL,
        LoadedUtc          DATETIME2(0)   NOT NULL CONSTRAINT DF_UserDirectory_Loaded DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.UserDirectory') AND name = N'IX_UserDirectory_Mail')
    EXEC (N'CREATE INDEX IX_UserDirectory_Mail ON audit.UserDirectory (Mail);');

/* The account's SID (objectSid, S-1-5-21-...): the dashboard recognizes a
   Windows sign-in by it, with no directory lookup at sign-in - so it works
   when the web servers are in another domain from the users. */
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL AND COL_LENGTH(N'audit.UserDirectory', N'Sid') IS NULL
    ALTER TABLE audit.UserDirectory ADD Sid VARCHAR(184) NULL;

/* AD's accountExpires, when the account is set to expire (NULL for never), for Licensing > Departures ahead. */
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL AND COL_LENGTH(N'audit.UserDirectory', N'AccountExpiresUtc') IS NULL
    ALTER TABLE audit.UserDirectory ADD AccountExpiresUtc DATETIME2(0) NULL;

/* Department holds 200 characters, as bi.DimUser and dash.Person do. It held 64, AD's limit for department,
   and a custom attribute named by -DepartmentAttribute can be longer. COL_LENGTH is in bytes: 400 is nvarchar(200). */
IF OBJECT_ID(N'audit.UserDirectory', N'U') IS NOT NULL AND COL_LENGTH(N'audit.UserDirectory', N'Department') < 400
    EXEC (N'ALTER TABLE audit.UserDirectory ALTER COLUMN Department NVARCHAR(200) NULL;');

/* ---- Reporting views ---------------------------------------------------- */

/*
    Both views read audit.CopilotInteraction, which finalize creates; before
    then they are skipped, so the tables can be loaded first.

    Adoption: licensed users against users who actually interacted. The LEFT
    JOIN direction matters - a licensed user with no interactions is the
    shelfware this is meant to surface.
*/
IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NOT NULL
EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotAdoption
AS
SELECT l.UserPrincipalName,
       l.IsCopilotLic,
       l.SkuPartNumber,
       l.LicensedSinceUtc,
       d.Division,
       d.Department,
       d.Office,
       d.Country,
       d.ManagerUPN,
       d.EmployeeType,
       d.AccountEnabled,
       u.Interactions,
       u.FirstInteractionUtc,
       u.LastInteractionUtc,
       CASE WHEN u.Interactions IS NULL THEN 1 ELSE 0 END AS NeverUsed,
       DATEDIFF(DAY, l.LicensedSinceUtc, u.FirstInteractionUtc) AS DaysToFirstUse
FROM audit.CopilotLicense AS l
LEFT JOIN audit.UserDirectory AS d
       ON d.UserPrincipalName = l.UserPrincipalName
LEFT JOIN (
    SELECT UserPrincipalName,
           COUNT_BIG(*)         AS Interactions,
           MIN(CreatedDateTime) AS FirstInteractionUtc,
           MAX(CreatedDateTime) AS LastInteractionUtc
    FROM audit.CopilotInteraction
    GROUP BY UserPrincipalName
) AS u ON u.UserPrincipalName = l.UserPrincipalName
WHERE l.IsCopilotLic = 1;');

/* Interactions by department, for the cut every rollout report asks for. */
IF OBJECT_ID(N'audit.CopilotInteraction', N'U') IS NOT NULL
EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotUsageByDepartment
AS
SELECT ISNULL(d.Department, N''(unknown)'') AS Department,
       i.ActivityDate,
       i.AppHost,
       COUNT_BIG(*)                          AS Interactions,
       COUNT(DISTINCT i.UserPrincipalName)   AS ActiveUsers
FROM audit.CopilotInteraction AS i
LEFT JOIN audit.UserDirectory AS d
       ON d.UserPrincipalName = i.UserPrincipalName
GROUP BY ISNULL(d.Department, N''(unknown)''), i.ActivityDate, i.AppHost;');

/* Dash.CopilotSuiteHolder is everyone who holds a suite with Copilot inside (E7), with the add-on or not;
   dash.CopilotSuiteOnly, the people who hold Microsoft 365 Copilot only through a suite and not as the add-on as well: their
   Copilot cannot be taken off alone, so they are never on a list to revoke. A view the dashboard reads, defined in
   full once the license pool and its suite mark exist, and empty until then; Create-CopilotEnrichmentSchema.sql and
   Create-CopilotDashboardSchema.sql both write it, so whichever runs last with the tables there leaves the full one. */
IF SCHEMA_ID(N'dash') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'audit.UserLicenseSku', N'U') IS NOT NULL AND COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NOT NULL
    BEGIN
        EXEC (N'CREATE OR ALTER VIEW dash.CopilotSuiteOnly AS
SELECT DISTINCT su.UserPrincipalName AS Upn
FROM audit.UserLicenseSku AS su JOIN audit.LicenseSku AS ss ON ss.SkuId = su.SkuId
WHERE ss.CopilotInSuite = 1
  AND NOT EXISTS (SELECT 1 FROM audit.UserLicenseSku AS au JOIN audit.LicenseSku AS sa ON sa.SkuId = au.SkuId
                  WHERE au.UserPrincipalName = su.UserPrincipalName AND sa.IsCopilot = 1 AND ISNULL(sa.CopilotInSuite, 0) = 0
                    AND sa.SkuPartNumber NOT LIKE N''%STUDIO%'');');
        EXEC (N'CREATE OR ALTER VIEW dash.CopilotSuiteHolder AS
SELECT DISTINCT su.UserPrincipalName AS Upn
FROM audit.UserLicenseSku AS su JOIN audit.LicenseSku AS ss ON ss.SkuId = su.SkuId
WHERE ss.CopilotInSuite = 1;');
    END
    ELSE
    BEGIN
        IF OBJECT_ID(N'dash.CopilotSuiteOnly', N'V') IS NULL
            EXEC (N'CREATE VIEW dash.CopilotSuiteOnly AS SELECT CAST(NULL AS NVARCHAR(512)) AS Upn WHERE 1 = 0;');
        IF OBJECT_ID(N'dash.CopilotSuiteHolder', N'V') IS NULL
            EXEC (N'CREATE VIEW dash.CopilotSuiteHolder AS SELECT CAST(NULL AS NVARCHAR(512)) AS Upn WHERE 1 = 0;');
    END;
END;
