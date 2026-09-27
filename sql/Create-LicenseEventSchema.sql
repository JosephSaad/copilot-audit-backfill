/*
    License lifecycle events
    ------------------------------------------------------------------------
    Requirement 3 asks for license type, license CHANGE dates, and when access
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

    Which property names appear varies by how the change was made. Shredding
    ModifiedProperties into rows rather than guessing at column names survives
    that: whatever Microsoft records is queryable.

    ---- Chunked events -------------------------------------------------------

    Most license changes do not carry their detail in ModifiedProperties at
    all. When the change is too large for one audit record - any user holding
    a few SKUs, because AssignedPlan lists every service plan - Entra splits it
    across several records. Each has empty ModifiedProperties and an
    ExtendedProperties 'additionalDetails' entry of the form

        {"id": "<group>", "seq": "1".."n", "b": "<10,000-character fragment>"}

    and the fragments, joined in seq order, form one JSON document whose
    targetUpdatedProperties holds the real Name/OldValue/NewValue list. On a
    live tenant 775 of 1,042 directory records were chunks, in groups of up to
    seven, and 400 of 422 'Change user license' records had no detail of their
    own. audit.LicenseEventChunk keeps the fragments, because one group's
    records can land in different shredder batches; usp_ShredLicenseEvents
    reassembles each complete group and files its properties against the
    seq = 1 record with Source = 'chunks'.

    A 'b' fragment is longer than 4,000 characters, so it must be read with
    OPENJSON ... WITH (... NVARCHAR(MAX)). JSON_VALUE returns NULL for it.

    The earlier model - a detail-free 'Change user license' paired with an
    'Update user' sibling on InterSystemsId - did not match live data (every
    record in a chunk group shares one InterSystemsId, and no sibling carried
    the lists). The pairing is kept in vLicenseChange as a fallback, but it
    never borrows from a record in the same chunk group.

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
        TargetUpn        NVARCHAR(512)   NULL,       -- whose license changed
        TargetObjectId   NVARCHAR(200)   NULL,
        /*
            Records raised for the same underlying change share this, which is
            what pairs a detail-free 'Change user license' with the
            'Update user' that carries the license lists.
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

/* Chunk membership. NULL for an ordinary, self-contained record. */
IF COL_LENGTH(N'audit.LicenseEvent', N'ChunkGroupId') IS NULL
    ALTER TABLE audit.LicenseEvent ADD ChunkGroupId NVARCHAR(100) NULL;
IF COL_LENGTH(N'audit.LicenseEvent', N'ChunkSeq') IS NULL
    ALTER TABLE audit.LicenseEvent ADD ChunkSeq INT NULL;

IF OBJECT_ID(N'audit.LicenseEvent', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'audit.LicenseEvent') AND name = N'IX_LicenseEvent_Chunk')
    EXEC (N'CREATE INDEX IX_LicenseEvent_Chunk ON audit.LicenseEvent (ChunkGroupId, ChunkSeq);');

/* One row per chunk fragment, kept until its group can be reassembled. */
IF OBJECT_ID(N'audit.LicenseEventChunk', N'U') IS NULL
    CREATE TABLE audit.LicenseEventChunk
    (
        ChunkGroupId NVARCHAR(100)  NOT NULL,
        Seq          INT            NOT NULL,
        RecordId     VARCHAR(64)    NOT NULL,
        Body         NVARCHAR(MAX)  NULL,
        CONSTRAINT PK_LicenseEventChunk PRIMARY KEY (ChunkGroupId, Seq)
    );

/*
    One row per ModifiedProperties entry - or, for a chunked event, per entry
    of its reassembled targetUpdatedProperties (Source = 'chunks', Ordinal
    from 10000). Names are whatever the service wrote.
*/
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

IF COL_LENGTH(N'audit.LicenseEventProperty', N'Source') IS NULL
    ALTER TABLE audit.LicenseEventProperty
        ADD Source VARCHAR(10) NOT NULL CONSTRAINT DF_LicenseEventProperty_Source DEFAULT ('record');

/* ---- Views ------------------------------------------------------------- */

/*
    License changes only: one row per license-bearing property per change.

    A chunked change is one change however many records carried it, so only
    its seq = 1 record appears (the other records are the same event). And
    the 'Update user' that Entra raises alongside every license change, with
    the same detail and CorrelationId, is dropped in favour of the
    'Change user license'. An
    event with no license property of its own may still borrow one from a
    record sharing its CorrelationId, marked FromPairedRecord = 1 - but never
    from its own chunk group, whose records all share that id. Events with no
    license detail from either source are not license changes as far as the
    record can show, and are left out; audit.LicenseEvent still has them.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseChange
AS
SELECT e.RecordId,
       e.CreatedDateTime,
       e.ActivityDate,
       e.Operation,
       e.ActorUpn,
       COALESCE(e.TargetUpn, paired.TargetUpn)                  AS TargetUpn,
       e.CorrelationId,
       p.[Name]                                                 AS PropertyName,
       p.OldValue,
       p.NewValue,
       CASE WHEN p.RecordId <> e.RecordId THEN 1 ELSE 0 END     AS FromPairedRecord,
       CASE WHEN p.Source = ''chunks'' THEN 1 ELSE 0 END        AS FromChunks
FROM audit.LicenseEvent AS e
CROSS APPLY (
    SELECT own.RecordId, own.[Name], own.OldValue, own.NewValue, own.Source
    FROM audit.LicenseEventProperty AS own
    WHERE own.RecordId = e.RecordId
      AND (own.[Name] LIKE N''%License%'' OR own.[Name] LIKE N''%Plan%'' OR own.[Name] LIKE N''%Sku%'')
    UNION ALL
    SELECT borrowed.RecordId, borrowed.[Name], borrowed.OldValue, borrowed.NewValue, borrowed.Source
    FROM (
        SELECT TOP (1) WITH TIES p2.RecordId, p2.[Name], p2.OldValue, p2.NewValue, p2.Source
        FROM audit.LicenseEventProperty AS p2
        JOIN audit.LicenseEvent AS e2 ON e2.RecordId = p2.RecordId
        WHERE e.CorrelationId IS NOT NULL
          AND e2.CorrelationId = e.CorrelationId
          AND e2.RecordId <> e.RecordId
          AND (e.ChunkGroupId IS NULL OR e2.ChunkGroupId IS NULL OR e2.ChunkGroupId <> e.ChunkGroupId)
          AND (p2.[Name] LIKE N''%License%'' OR p2.[Name] LIKE N''%Plan%'' OR p2.[Name] LIKE N''%Sku%'')
        ORDER BY e2.CreatedDateTime, e2.RecordId
    ) AS borrowed
    WHERE NOT EXISTS (SELECT 1 FROM audit.LicenseEventProperty AS o
                      WHERE o.RecordId = e.RecordId
                        AND (o.[Name] LIKE N''%License%'' OR o.[Name] LIKE N''%Plan%'' OR o.[Name] LIKE N''%Sku%''))
) AS p
OUTER APPLY (
    SELECT TOP (1) e3.TargetUpn
    FROM audit.LicenseEvent AS e3
    WHERE e.CorrelationId IS NOT NULL AND e3.CorrelationId = e.CorrelationId
      AND e3.TargetUpn IS NOT NULL
    ORDER BY e3.CreatedDateTime
) AS paired
WHERE (e.ChunkSeq IS NULL OR e.ChunkSeq = 1)
  /* Entra raises the same change as a ''Change user license'' and an
     ''Update user'', each with the full detail, under one CorrelationId.
     Report it once, as the license change. */
  AND NOT (e.Operation = N''Update user''
           AND e.CorrelationId IS NOT NULL
           AND EXISTS (SELECT 1 FROM audit.LicenseEvent AS x
                       WHERE x.CorrelationId = e.CorrelationId
                         AND x.Operation = N''Change user license''
                         AND (x.ChunkSeq IS NULL OR x.ChunkSeq = 1)));');

/*
    SKUs added and removed, per change: the AssignedLicense lists diffed.
    This answers "when was this user given Copilot, and when was it taken
    away" directly. Each list entry reads
    [SkuName=Microsoft_365_Copilot, AccountId=..., SkuId=..., DisabledPlans=[...]]
    and the lists arrive either as a JSON array (chunked events) or as a
    string holding one (ModifiedProperties); both parse the same way here.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseSkuChange
AS
WITH lists AS (
    SELECT c.RecordId, c.CreatedDateTime, c.ActivityDate, c.ActorUpn, c.TargetUpn, c.FromChunks,
           c.OldValue, c.NewValue
    FROM audit.vLicenseChange AS c
    WHERE c.PropertyName = N''AssignedLicense''
),
skus AS (
    SELECT l.RecordId, N''old'' AS Side, s.Sku
    FROM lists AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.OldValue) = 1 THEN l.OldValue ELSE N''[]'' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N''SkuName='', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N'','', j.[value] + N'','', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL
    UNION
    SELECT l.RecordId, N''new'', s.Sku
    FROM lists AS l
    CROSS APPLY OPENJSON(CASE WHEN ISJSON(l.NewValue) = 1 THEN l.NewValue ELSE N''[]'' END) AS j
    CROSS APPLY (SELECT CHARINDEX(N''SkuName='', j.[value]) AS At) AS a
    CROSS APPLY (SELECT CASE WHEN a.At > 0 THEN SUBSTRING(j.[value], a.At + 8,
                     CHARINDEX(N'','', j.[value] + N'','', a.At + 8) - a.At - 8) END AS Sku) AS s
    WHERE s.Sku IS NOT NULL
)
SELECT l.RecordId, l.CreatedDateTime, l.ActivityDate, l.ActorUpn, l.TargetUpn, l.FromChunks,
       d.Sku,
       d.Change
FROM lists AS l
CROSS APPLY (
    SELECT n.Sku, N''Added'' AS Change FROM skus AS n
    WHERE n.RecordId = l.RecordId AND n.Side = N''new''
      AND NOT EXISTS (SELECT 1 FROM skus AS o WHERE o.RecordId = l.RecordId AND o.Side = N''old'' AND o.Sku = n.Sku)
    UNION ALL
    SELECT o.Sku, N''Removed'' FROM skus AS o
    WHERE o.RecordId = l.RecordId AND o.Side = N''old''
      AND NOT EXISTS (SELECT 1 FROM skus AS n WHERE n.RecordId = l.RecordId AND n.Side = N''new'' AND n.Sku = o.Sku)
) AS d;');

/*
    Provisioning and revocation per user. "Revoked" is inferred from the
    license disappearing or the account being deleted, so it is a reading of
    the events rather than a field the service provides - treat it as such.
    A chunked change counts once: only its seq = 1 record is counted.
    Operations match exactly: the service's operation filter for 'Add user'
    also returns 'Add user sponsor', which is not an account being created.
*/
EXEC (N'
CREATE OR ALTER VIEW audit.vLicenseLifecycle
AS
SELECT TargetUpn,
       MIN(CASE WHEN Operation = N''Add user'' THEN CreatedDateTime END)        AS AccountCreatedUtc,
       MAX(CASE WHEN Operation = N''Delete user'' THEN CreatedDateTime END)     AS AccountDeletedUtc,
       MIN(CASE WHEN Operation LIKE N''Change user license%'' THEN CreatedDateTime END) AS FirstLicenseChangeUtc,
       MAX(CASE WHEN Operation LIKE N''Change user license%'' THEN CreatedDateTime END) AS LastLicenseChangeUtc,
       COUNT(CASE WHEN Operation LIKE N''Change user license%'' THEN 1 END)     AS LicenseChangeCount,
       COUNT(*)                                                                AS EventCount
FROM audit.LicenseEvent
WHERE TargetUpn IS NOT NULL
  AND (ChunkSeq IS NULL OR ChunkSeq = 1)
GROUP BY TargetUpn;');
