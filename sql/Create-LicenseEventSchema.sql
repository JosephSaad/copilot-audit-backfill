/*
    Licence lifecycle events
    ------------------------------------------------------------------------
    Requirement 3 asks for licence type, licence CHANGE dates, and when access
    was provisioned and revoked. The Graph user object carries current state
    only - assignedPlans gives the date the CURRENT assignment began and
    nothing about one that was removed - so history has to come from the audit
    log instead.

    It is in the same unified audit log the Copilot backfill already reads,
    under the AzureActiveDirectory workload, with the same 180-day retention.
    Invoke-CopilotAuditBackfill.ps1 already takes -Operations and passes it
    straight to the service's operationFilters, so collecting these needs a
    separate database and a different -Operations list, not new ingestion code:

        .\Invoke-CopilotAuditBackfill.ps1 -SqlServer SQLPROD01 `
            -Database CopilotLicenseEvents -TenantId contoso.onmicrosoft.com `
            -Operations 'Change user license','Update user','Add user','Delete user' `
            -StartDate (Get-Date).ToUniversalTime().Date.AddDays(-180) -Finalize

    Then audit.usp_ShredLicenseEvents turns audit.CopilotAudit in THAT database
    into the two tables below.

    ---- Why the properties are a child table, not columns -------------------

    'Change user license' frequently records no licence detail at all: its
    ModifiedProperties can be empty, and the actual old and new licence lists
    arrive on a separate 'Update user' record raised for the same change.
    Which property names appear also varies by how the change was made.

    Shredding ModifiedProperties into rows rather than guessing at column
    names survives all of that: whatever Microsoft records is queryable, and
    audit.vLicenseChange pairs the two records back together.

    Idempotent. Single batch (no GO). Requires SQL Server 2016+.
*/
SET NOCOUNT ON;

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo;');

/* One row per audit record. */
IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NULL
    CREATE TABLE audit.LicenseEvent
    (
        RecordId         VARCHAR(64)     NOT NULL
                             CONSTRAINT PK_LicenseEvent PRIMARY KEY,
        CreatedDateTime  DATETIME2(0)    NOT NULL,   -- UTC
        ActivityDate     DATE            NOT NULL,
        Operation        NVARCHAR(200)   NULL,       -- 'Change user license', 'Update user', ...
        ResultStatus     NVARCHAR(50)    NULL,
        ActorUpn         NVARCHAR(512)   NULL,       -- who made the change
        TargetUpn        NVARCHAR(512)   NULL,       -- whose licence changed
        TargetObjectId   NVARCHAR(200)   NULL,
        /*
            Records raised for the same underlying change share this, which is
            what pairs a detail-free 'Change user license' with the
            'Update user' that carries the licence lists.
        */
        CorrelationId    NVARCHAR(200)   NULL,
        ShreddedUtc      DATETIME2(0)    NOT NULL
                             CONSTRAINT DF_LicenseEvent_Shredded DEFAULT (SYSUTCDATETIME())
    );

IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Target')
    EXEC (N'CREATE INDEX IX_LicenseEvent_Target ON audit.LicenseEvent (TargetUpn, CreatedDateTime)
            INCLUDE (Operation, CorrelationId);');

IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Correlation')
    EXEC (N'CREATE INDEX IX_LicenseEvent_Correlation ON audit.LicenseEvent (CorrelationId);');

/* One row per ModifiedProperties entry. Names are whatever the service wrote. */
IF OBJECT_ID(N'audit.LicenseEventProperty', N'U') IS NULL
    CREATE TABLE audit.LicenseEventProperty
    (
        RecordId     VARCHAR(64)    NOT NULL,
        Ordinal      INT            NOT NULL,
        [Name]       NVARCHAR(400)  NULL,
        OldValue     NVARCHAR(MAX)  NULL,
        NewValue     NVARCHAR(MAX)  NULL,
        CONSTRAINT PK_LicenseEventProperty PRIMARY KEY (RecordId, Ordinal)
    );

/* ---- Views ------------------------------------------------------------- */

/*
    Licence-bearing properties, with the paired record folded in. A
    'Change user license' event usually has the timing and the actor; the
    'Update user' event sharing its CorrelationId usually has the licence
    lists. Reading either alone gives half the story.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseChange
AS
SELECT e.RecordId,
       e.CreatedDateTime,
       e.ActivityDate,
       e.Operation,
       e.ActorUpn,
       COALESCE(e.TargetUpn, paired.TargetUpn)   AS TargetUpn,
       e.CorrelationId,
       p.[Name]                                  AS PropertyName,
       p.OldValue,
       p.NewValue,
       CASE WHEN p.RecordId <> e.RecordId THEN 1 ELSE 0 END AS FromPairedRecord
FROM audit.LicenseEvent AS e
OUTER APPLY (
    SELECT TOP (1) p2.RecordId, p2.[Name], p2.OldValue, p2.NewValue
    FROM audit.LicenseEventProperty AS p2
    JOIN audit.LicenseEvent AS e2 ON e2.RecordId = p2.RecordId
    WHERE (e2.RecordId = e.RecordId
           OR (e.CorrelationId IS NOT NULL AND e2.CorrelationId = e.CorrelationId))
      AND (p2.[Name] LIKE N''%License%'' OR p2.[Name] LIKE N''%Plan%'' OR p2.[Name] LIKE N''%Sku%'')
    ORDER BY CASE WHEN p2.RecordId = e.RecordId THEN 0 ELSE 1 END, p2.Ordinal
) AS p
OUTER APPLY (
    SELECT TOP (1) e3.TargetUpn
    FROM audit.LicenseEvent AS e3
    WHERE e.CorrelationId IS NOT NULL AND e3.CorrelationId = e.CorrelationId
      AND e3.TargetUpn IS NOT NULL
    ORDER BY e3.CreatedDateTime
) AS paired;');

/*
    Provisioning and revocation per user. "Revoked" is inferred from the
    licence disappearing or the account being deleted, so it is a reading of
    the events rather than a field the service provides - treat it as such.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseLifecycle
AS
SELECT TargetUpn,
       MIN(CASE WHEN Operation LIKE N''Add user%'' THEN CreatedDateTime END)    AS AccountCreatedUtc,
       MAX(CASE WHEN Operation LIKE N''Delete user%'' THEN CreatedDateTime END) AS AccountDeletedUtc,
       MIN(CASE WHEN Operation LIKE N''Change user license%'' THEN CreatedDateTime END) AS FirstLicenceChangeUtc,
       MAX(CASE WHEN Operation LIKE N''Change user license%'' THEN CreatedDateTime END) AS LastLicenceChangeUtc,
       COUNT(CASE WHEN Operation LIKE N''Change user license%'' THEN 1 END)     AS LicenceChangeCount,
       COUNT(*)                                                                AS EventCount
FROM audit.LicenseEvent
WHERE TargetUpn IS NOT NULL
GROUP BY TargetUpn;');
