-- Permissions for the application roles. No application identity holds db_owner.
-- Ledger tables accept INSERT only (append-only); granting UPDATE or DELETE on them would fail anyway.
SET NOCOUNT ON;
DECLARE @sql nvarchar(max) = N'';

-- Read and write on working schemas.
SELECT @sql += N'GRANT SELECT, INSERT, UPDATE ON SCHEMA::' + s + N' TO app_web; GRANT SELECT, INSERT, UPDATE ON SCHEMA::' + s + N' TO app_worker; '
FROM (VALUES ('ref'),('core'),('sec'),('wf'),('evd'),('ntf'),('intg'),('ops')) AS t(s);
EXEC (@sql);

-- Audit: insert and read only.
GRANT SELECT, INSERT ON SCHEMA::aud TO app_web;
GRANT SELECT, INSERT ON SCHEMA::aud TO app_worker;

-- Reporting.
GRANT SELECT ON SCHEMA::rpt TO app_web;
GRANT SELECT ON SCHEMA::rpt TO app_worker;
GRANT SELECT ON SCHEMA::rpt TO gov_reader;
SELECT @sql = N'';
SELECT @sql += N'GRANT SELECT ON SCHEMA::' + s + N' TO gov_reader; '
FROM (VALUES ('ref'),('core'),('sec'),('wf'),('evd'),('ntf'),('intg'),('ops'),('aud')) AS t(s);
EXEC (@sql);

-- Narrow deletes: token cache rows, sync staging, task exclusions recomputed, outbox cleanup.
GRANT DELETE ON sec.TokenCache        TO app_web;
GRANT DELETE ON sec.TokenCache        TO app_worker;
GRANT DELETE ON intg.GroupMember      TO app_worker;
GRANT DELETE ON intg.SeatHolder       TO app_worker;
GRANT DELETE ON intg.CopilotActivity  TO app_worker;
GRANT DELETE ON core.SeatChargeSnapshot TO app_worker;   -- draft snapshots are retaken; the trigger refuses certified ones
GRANT DELETE ON wf.TaskExclusion      TO app_web;
GRANT DELETE ON wf.TaskExclusion      TO app_worker;
GRANT DELETE ON ntf.Outbox            TO app_worker;
GRANT DELETE ON ntf.DigestEntry       TO app_worker;
GRANT DELETE ON ntf.Delivery          TO app_worker;   -- the Retention job: delivery records past Retention.WorkerRecordDays
GRANT DELETE ON intg.JobRun           TO app_worker;   -- the Retention job: job runs past Retention.WorkerRecordDays
GRANT DELETE ON sec.DelegationEnvelope TO app_web;

-- Sequences.
GRANT UPDATE ON wf.RequestNumber TO app_web;
GRANT UPDATE ON wf.RequestNumber TO app_worker;

-- Singleton jobs use sp_getapplock; the evidence purge is the only update to content.
GRANT EXECUTE ON SCHEMA::sec TO app_web;
GRANT EXECUTE ON SCHEMA::sec TO app_worker;
GRANT VIEW DATABASE STATE TO app_worker;   -- ledger digest checks
-- The daily ledger digest (Save-LedgerDigest.ps1) runs as the worker's account. Microsoft Learn's digest management
-- page names a GENERATE LEDGER DIGEST permission; SQL Server 2022 and 2025 have none (sys.fn_builtin_permissions) and
-- let any user run sys.sp_generate_database_ledger_digest. Where the engine has it, it is granted, once.
IF EXISTS (SELECT 1 FROM sys.fn_builtin_permissions('DATABASE') WHERE permission_name = N'GENERATE LEDGER DIGEST')
   AND NOT EXISTS (SELECT 1 FROM sys.database_permissions
                   WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'app_worker') AND class = 0
                     AND permission_name = N'GENERATE LEDGER DIGEST' AND state IN ('G', 'W'))
    EXEC (N'GRANT GENERATE LEDGER DIGEST TO app_worker;');
GO
