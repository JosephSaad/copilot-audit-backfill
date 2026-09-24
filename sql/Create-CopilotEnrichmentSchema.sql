/*
    Copilot audit backfill - enrichment lookup tables
    ------------------------------------------------------------------------
    Directory facts the audit records cannot carry, kept in their own tables so
    nothing here touches the backfill path. Populated by
    tools/Update-CopilotAuditEnrichment.ps1.

      audit.CopilotLicense      who holds a Copilot licence, and since when
      audit.AdministrativeUnit  administrative unit id -> display name
      audit.UserProfile         department, office, manager and so on

    audit.CopilotLicense is also created by Create-CopilotFlattenedSchema.sql,
    which shipped before this file existed and made only three columns. The
    ALTERs below add the rest, so a database created by either script ends up
    the same shape.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* ---- Licences ---------------------------------------------------------- */

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

/* The Copilot SKU the licence came from, e.g. Microsoft_365_Copilot. */
IF COL_LENGTH(N'audit.CopilotLicense', N'SkuPartNumber') IS NULL
    ALTER TABLE audit.CopilotLicense ADD SkuPartNumber NVARCHAR(100) NULL;

/*
    assignedPlans[].assignedDateTime for the Copilot service plan. This is the
    only history Graph offers: it says when the CURRENT assignment began, so
    "licensed since March" is answerable but "was this user licensed last
    March" is not - a licence granted and later removed leaves no trace.
*/
IF COL_LENGTH(N'audit.CopilotLicense', N'LicensedSinceUtc') IS NULL
    ALTER TABLE audit.CopilotLicense ADD LicensedSinceUtc DATETIME2(0) NULL;

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

/* ---- Administrative units ---------------------------------------------- */

IF OBJECT_ID(N'audit.AdministrativeUnit', N'U') IS NULL
    CREATE TABLE audit.AdministrativeUnit
    (
        Id              NVARCHAR(100)   NOT NULL
                            CONSTRAINT PK_AdministrativeUnit PRIMARY KEY,
        DisplayName     NVARCHAR(400)   NULL,
        [Description]   NVARCHAR(1000)  NULL,
        UpdatedUtc      DATETIME2(0)    NOT NULL
                            CONSTRAINT DF_AdministrativeUnit_Updated DEFAULT (SYSUTCDATETIME())
    );

/* ---- User profiles ------------------------------------------------------ */

IF OBJECT_ID(N'audit.UserProfile', N'U') IS NULL
    CREATE TABLE audit.UserProfile
    (
        UserPrincipalName   NVARCHAR(512)   NOT NULL
                                CONSTRAINT PK_UserProfile PRIMARY KEY,
        DisplayName         NVARCHAR(400)   NULL,
        Department          NVARCHAR(200)   NULL,
        JobTitle            NVARCHAR(200)   NULL,
        OfficeLocation      NVARCHAR(200)   NULL,
        City                NVARCHAR(200)   NULL,
        Country             NVARCHAR(200)   NULL,
        CompanyName         NVARCHAR(200)   NULL,
        UsageLocation       NVARCHAR(10)    NULL,
        UserType            NVARCHAR(20)    NULL,   -- Member | Guest
        AccountEnabled      BIT             NULL,
        ManagerUpn          NVARCHAR(512)   NULL,
        CreatedDateTimeUtc  DATETIME2(0)    NULL,
        DirectoryStatus     NVARCHAR(20)    NULL,   -- Found | NotFound
        UpdatedUtc          DATETIME2(0)    NOT NULL
                                CONSTRAINT DF_UserProfile_Updated DEFAULT (SYSUTCDATETIME())
    );

/* Division is Entra's employeeOrgData.division - the org level above
   department that reporting by business line is sliced by. Added after 1.2.0,
   so existing databases are altered in place. */
IF OBJECT_ID(N'audit.UserProfile', N'U') IS NOT NULL
   AND COL_LENGTH(N'audit.UserProfile', N'Division') IS NULL
    ALTER TABLE audit.UserProfile ADD Division NVARCHAR(200) NULL;

IF OBJECT_ID(N'audit.UserProfile', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.UserProfile') AND name = N'IX_UserProfile_Department')
    EXEC (N'CREATE INDEX IX_UserProfile_Department ON audit.UserProfile (Department) INCLUDE (OfficeLocation, Country);');

/* ---- Reporting views ---------------------------------------------------- */

/*
    Adoption: licensed users against users who actually interacted. The LEFT
    JOIN direction matters - a licensed user with no interactions is the
    shelfware this is meant to surface.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotAdoption
AS
SELECT l.UserPrincipalName,
       l.IsCopilotLic,
       l.SkuPartNumber,
       l.LicensedSinceUtc,
       p.Department,
       p.OfficeLocation,
       p.Country,
       p.ManagerUpn,
       p.UserType,
       u.Interactions,
       u.FirstInteractionUtc,
       u.LastInteractionUtc,
       CASE WHEN u.Interactions IS NULL THEN 1 ELSE 0 END AS NeverUsed,
       DATEDIFF(DAY, l.LicensedSinceUtc, u.FirstInteractionUtc) AS DaysToFirstUse
FROM audit.CopilotLicense AS l
LEFT JOIN audit.UserProfile AS p
       ON p.UserPrincipalName = l.UserPrincipalName
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
EXEC (N'
CREATE OR ALTER VIEW audit.vCopilotUsageByDepartment
AS
SELECT ISNULL(p.Department, N''(unknown)'') AS Department,
       i.ActivityDate,
       i.AppHost,
       COUNT_BIG(*)                          AS Interactions,
       COUNT(DISTINCT i.UserPrincipalName)   AS ActiveUsers
FROM audit.CopilotInteraction AS i
LEFT JOIN audit.UserProfile AS p
       ON p.UserPrincipalName = i.UserPrincipalName
GROUP BY ISNULL(p.Department, N''(unknown)''), i.ActivityDate, i.AppHost;');
