/*
    Trace-CopilotNightly.sql
    ------------------------------------------------------------------------
    For the database administrator: what the nightly task is running in SQL
    Server, and a recording of every statement it runs that takes a second or
    longer, to find where a slow night's time goes. The companion of
    tools\Measure-CopilotNightly.ps1, which shows each stage's time and needs
    no recording; this shows the statements inside a stage.

    In SSMS, set @Do below and run the whole script. With sqlcmd, leave the
    file as it is and name the action (sqlcmd stops when -v Do is left out):

        sqlcmd -S SQLPROD01 -E -v Do=start -i tools\Trace-CopilotNightly.sql

      N'now'    What the task is running at this moment: each statement with
                its time, CPU, reads, wait and who blocks it. Reads only.
      N'start'  Start recording: an Extended Events session, CopilotNightly,
                that keeps every statement the task's connections run for
                @MinSeconds or longer, in files in SQL Server's log folder
                (4 files of 50 MB at most, the oldest overwritten).
      N'read'   The recording so far: the time by procedure, then the
                statements by total time. Run it during or after the night.
      N'stop'   Stop recording and remove the session. The .xel files stay
                in the log folder; delete them when they are no longer wanted.

    The task's connections are known by their application name:
    CopilotAuditBackfill (the two loads and every refresh after them),
    CopilotAuditUserDirectory (the directory) and CopilotAuditEnrichment (the
    licenses from Graph). Nothing else is recorded.

    'now' and 'read' need VIEW SERVER STATE; 'start' and 'stop' need ALTER ANY
    EVENT SESSION. The recording costs little: a statement under @MinSeconds is
    dropped where it is raised, and no plan is collected. Requires SQL Server
    2017 or later.
*/
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;   -- reading the recording's XML needs it, and sqlcmd leaves it off unless given -I

DECLARE @Do         NVARCHAR(10) = N'now',   -- now | start | read | stop
        @MinSeconds INT          = 1;        -- with 'start': the shortest statement kept

DECLARE @session SYSNAME = N'CopilotNightly', @sql NVARCHAR(MAX);

/* sqlcmd -v Do=start puts the action here; SSMS leaves the text as it is, and @Do above decides. */
IF LEFT(N'$(Do)', 1) <> N'$' SET @Do = N'$(Do)';

IF @Do NOT IN (N'now', N'start', N'read', N'stop')
    THROW 50000, N'@Do must be now, start, read or stop.', 1;

/* ---- now ---------------------------------------------------------------- */
IF @Do = N'now'
BEGIN
    SELECT r.session_id                                   AS Spid,
           s.program_name                                 AS Program,
           DB_NAME(r.database_id)                         AS [Database],
           r.status                                       AS [Status],
           r.total_elapsed_time / 1000                    AS ElapsedSeconds,
           r.cpu_time / 1000                              AS CpuSeconds,
           r.logical_reads                                AS PagesRead,
           r.writes                                       AS PagesWritten,
           r.dop                                          AS Dop,
           r.granted_query_memory * 8 / 1024              AS MemoryGrantedMb,
           ISNULL(r.wait_type, N'')                       AS WaitingOn,
           r.wait_time / 1000                             AS WaitSeconds,
           NULLIF(r.blocking_session_id, 0)               AS BlockedBySpid,
           ISNULL(OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + N'.' + OBJECT_NAME(t.objectid, t.dbid), N'') AS [Procedure],
           SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
                     (CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END
                      - r.statement_start_offset) / 2 + 1)  AS [Statement],
           /* The plan with the rows each operator has produced so far: open it in SSMS. */
           (SELECT TOP (1) x.query_plan FROM sys.dm_exec_query_statistics_xml(r.session_id) AS x) AS PlanSoFar
    FROM sys.dm_exec_requests AS r
    JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
    WHERE s.program_name LIKE N'CopilotAudit%' AND r.session_id <> @@SPID
    ORDER BY r.total_elapsed_time DESC;

    IF @@ROWCOUNT = 0
        RAISERROR('No statement of the task is running at this moment: it is between statements, downloading from Purview or reading the directory.', 0, 1) WITH NOWAIT;

    /* What each of its sessions has waited on since it connected: where the time is not CPU. */
    SELECT w.session_id AS Spid, w.wait_type AS WaitedOn, w.wait_time_ms / 1000 AS Seconds, w.waiting_tasks_count AS Waits
    FROM sys.dm_exec_session_wait_stats AS w
    JOIN sys.dm_exec_sessions AS s ON s.session_id = w.session_id
    WHERE s.program_name LIKE N'CopilotAudit%' AND w.wait_time_ms >= 1000
    ORDER BY w.wait_time_ms DESC;
END;

/* ---- start -------------------------------------------------------------- */
IF @Do = N'start'
BEGIN
    IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @session)
        THROW 50001, N'The session CopilotNightly exists already: read it, or stop it first.', 1;
    IF @MinSeconds IS NULL OR @MinSeconds < 0 SET @MinSeconds = 1;

    DECLARE @where NVARCHAR(400) = CONCAT(N'WHERE ([sqlserver].[like_i_sql_unicode_string]([sqlserver].[client_app_name], N''CopilotAudit%'') AND [duration] >= ',
                                          CAST(@MinSeconds AS BIGINT) * 1000000, N')');
    SET @sql = N'
CREATE EVENT SESSION ' + QUOTENAME(@session) + N' ON SERVER
ADD EVENT sqlserver.sp_statement_completed (
    SET collect_object_name = (1), collect_statement = (1)
    ACTION (sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id)
    ' + @where + N'),
ADD EVENT sqlserver.sql_statement_completed (
    SET collect_statement = (1)
    ACTION (sqlserver.client_app_name, sqlserver.database_name, sqlserver.session_id)
    ' + @where + N')
ADD TARGET package0.event_file (SET filename = N''' + @session + N'.xel'', max_file_size = (50), max_rollover_files = (4))
WITH (MAX_MEMORY = 8192 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS, MAX_DISPATCH_LATENCY = 15 SECONDS, STARTUP_STATE = OFF);
ALTER EVENT SESSION ' + QUOTENAME(@session) + N' ON SERVER STATE = START;';
    EXEC sys.sp_executesql @sql;
    RAISERROR('Recording. Run the nightly task, or wait for tonight''s; then run this with @Do = N''read'', and N''stop'' when done.', 0, 1) WITH NOWAIT;
END;

/* ---- read --------------------------------------------------------------- */
IF @Do = N'read'
BEGIN
    DROP TABLE IF EXISTS #ev;
    CREATE TABLE #ev
    (
        EndedUtc DATETIME2(3) NULL, Program NVARCHAR(128) NULL, [Database] NVARCHAR(128) NULL, Spid INT NULL,
        [Procedure] NVARCHAR(300) NOT NULL, Line INT NULL, Seconds DECIMAL(18, 6) NULL, CpuSeconds DECIMAL(18, 6) NULL,
        PagesRead BIGINT NULL, PagesFromDisk BIGINT NULL, PagesWritten BIGINT NULL, [Rows] BIGINT NULL, [Statement] NVARCHAR(MAX) NULL
    );
    BEGIN TRY
        INSERT INTO #ev (EndedUtc, Program, [Database], Spid, [Procedure], Line, Seconds, CpuSeconds, PagesRead, PagesFromDisk, PagesWritten, [Rows], [Statement])
        SELECT x.value('(/event/@timestamp)[1]', 'datetime2(3)'),
               x.value('(/event/action[@name="client_app_name"]/value)[1]', 'nvarchar(128)'),
               x.value('(/event/action[@name="database_name"]/value)[1]', 'nvarchar(128)'),
               x.value('(/event/action[@name="session_id"]/value)[1]', 'int'),
               ISNULL(NULLIF(x.value('(/event/data[@name="object_name"]/value)[1]', 'nvarchar(300)'), N''), N'(not in a procedure)'),
               x.value('(/event/data[@name="line_number"]/value)[1]', 'int'),
               x.value('(/event/data[@name="duration"]/value)[1]', 'bigint') / 1000000.0,
               x.value('(/event/data[@name="cpu_time"]/value)[1]', 'bigint') / 1000000.0,
               x.value('(/event/data[@name="logical_reads"]/value)[1]', 'bigint'),
               x.value('(/event/data[@name="physical_reads"]/value)[1]', 'bigint'),
               x.value('(/event/data[@name="writes"]/value)[1]', 'bigint'),
               x.value('(/event/data[@name="row_count"]/value)[1]', 'bigint'),
               x.value('(/event/data[@name="statement"]/value)[1]', 'nvarchar(max)')
        FROM (SELECT CAST(f.event_data AS XML) AS x
              FROM sys.fn_xe_file_target_read_file(@session + N'*.xel', NULL, NULL, NULL) AS f) AS e;
    END TRY
    BEGIN CATCH
        /* No file by that name (nothing was ever recorded), or no right to read it. */
        DECLARE @why NVARCHAR(2048) = ERROR_MESSAGE();
        RAISERROR('The recording could not be read: %s If none was started, run this with @Do = N''start'' first.', 16, 1, @why);
        RETURN;
    END CATCH;

    IF NOT EXISTS (SELECT 1 FROM #ev)
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @session)
            RAISERROR('Nothing recorded yet: no statement of the task has run for that long since the recording started (events reach the file within 15 seconds).', 0, 1) WITH NOWAIT;
        ELSE
            RAISERROR('There is no recording: no session CopilotNightly, and no file an earlier one left. Start one with @Do = N''start''.', 0, 1) WITH NOWAIT;
        RETURN;
    END;

    /* A statement that calls a procedure counts the procedure's time again: read the inner lines. */
    SELECT [Database], [Procedure],
           CAST(SUM(Seconds) / 60.0 AS DECIMAL(18, 1))      AS Minutes,
           CAST(SUM(Seconds) AS DECIMAL(18, 0))             AS Seconds,
           CAST(SUM(CpuSeconds) AS DECIMAL(18, 0))          AS CpuSeconds,
           COUNT(*)                                         AS Statements,
           SUM(PagesRead)                                   AS PagesRead,
           MIN(DATEADD(MILLISECOND, -CAST(Seconds * 1000 AS INT), EndedUtc)) AS FirstStartedUtc,
           MAX(EndedUtc)                                    AS LastEndedUtc
    FROM #ev
    GROUP BY [Database], [Procedure]
    ORDER BY SUM(Seconds) DESC;

    SELECT TOP (50) [Database], [Procedure], Line,
           CAST(SUM(Seconds) / 60.0 AS DECIMAL(18, 1))      AS Minutes,
           CAST(SUM(Seconds) AS DECIMAL(18, 0))             AS Seconds,
           CAST(SUM(CpuSeconds) AS DECIMAL(18, 0))          AS CpuSeconds,
           COUNT(*)                                         AS Runs,
           SUM(PagesRead)                                   AS PagesRead,
           SUM(PagesFromDisk)                               AS PagesFromDisk,
           SUM(PagesWritten)                                AS PagesWritten,
           SUM([Rows])                                      AS [Rows],
           MAX(EndedUtc)                                    AS LastEndedUtc,
           LEFT(MIN([Statement]), 4000)                     AS [Statement]
    FROM #ev
    GROUP BY [Database], [Procedure], Line, CHECKSUM([Statement])
    ORDER BY SUM(Seconds) DESC;
END;

/* ---- stop --------------------------------------------------------------- */
IF @Do = N'stop'
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @session)
        RAISERROR('There is no session CopilotNightly: nothing to stop.', 0, 1) WITH NOWAIT;
    ELSE
    BEGIN
        SET @sql = N'DROP EVENT SESSION ' + QUOTENAME(@session) + N' ON SERVER;';
        EXEC sys.sp_executesql @sql;
        RAISERROR('Stopped. The CopilotNightly*.xel files are still in SQL Server''s log folder.', 0, 1) WITH NOWAIT;
    END;
END;
