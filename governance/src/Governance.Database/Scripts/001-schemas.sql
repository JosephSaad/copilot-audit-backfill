-- Schemas and database roles. Safe to rerun.
-- ref reference data; core registry; sec people, roles, delegations, RACI; wf requests, tasks, decisions;
-- evd evidence; ntf notifications; intg integration snapshots; ops events and findings; aud audit; rpt reporting views.
SET NOCOUNT ON;
DECLARE @s sysname;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT v FROM (VALUES ('ref'),('core'),('sec'),('wf'),('evd'),('ntf'),('intg'),('ops'),('aud'),('rpt')) AS t(v);
OPEN c; FETCH NEXT FROM c INTO @s;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF SCHEMA_ID(@s) IS NULL EXEC (N'CREATE SCHEMA ' + @s + N' AUTHORIZATION dbo;');
    FETCH NEXT FROM c INTO @s;
END
CLOSE c; DEALLOCATE c;

-- app_web and app_worker hold EXECUTE and table permissions only (granted in 900-grants.sql).
-- gov_reader is read-only reporting and audit export. No application identity holds db_owner.
IF DATABASE_PRINCIPAL_ID('app_web')    IS NULL CREATE ROLE app_web;
IF DATABASE_PRINCIPAL_ID('app_worker') IS NULL CREATE ROLE app_worker;
IF DATABASE_PRINCIPAL_ID('gov_reader') IS NULL CREATE ROLE gov_reader;
GO
