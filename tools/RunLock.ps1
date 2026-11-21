<#
.SYNOPSIS
    Who holds the run lock (3.1.3): one sentence for the message of a run that found the Copilot database in use.

.DESCRIPTION
    Dot-sourced by every script that takes the run lock (the exclusive application lock CopilotAudit.Run):
    Invoke-CopilotPipeline.ps1, Install-CopilotAudit.ps1, Invoke-CopilotAuditBackfill.ps1,
    tools\Invoke-CopilotMaintenance.ps1 and tools\Import-CopilotAuditCsv.ps1. When one is refused, it says which
    session holds the lock: the program, the machine, the account, and whether it is working or idle and since when.
    Before, the message said only that another run was using the database, and a query window left open in SQL
    Server Management Studio after a manual step stopped every nightly run with nothing to show what held it.

    Seeing another session's locks needs VIEW SERVER PERFORMANCE STATE (SQL Server 2022 and later) or VIEW SERVER
    STATE (before 2022). Without it the sentence says so, names the account and the permission, and the run is
    refused as before. It never throws: a refused run's message is what matters, and this only adds to it.

    Works in Windows PowerShell 5.1 as well as PowerShell 7, as the installer and the maintenance tool do.
#>

function Get-RunLockHolder {
    <# One sentence naming the session that holds CopilotAudit.Run in the database the connection string names. #>
    param([Parameter(Mandatory)] [string] $ConnectionString)

    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    $b['Pooling'] = $false
    $b['Application Name'] = 'Copilot run lock check'
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $who = ''; $permission = 'VIEW SERVER STATE'
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandTimeout = 30
        $cmd.CommandText = "SELECT SUSER_SNAME(), CAST(SERVERPROPERTY('ProductMajorVersion') AS INT);"
        $r = $cmd.ExecuteReader()
        try { if ($r.Read()) { $who = [string] $r.GetValue(0); if (-not $r.IsDBNull(1) -and $r.GetInt32(1) -ge 16) { $permission = 'VIEW SERVER PERFORMANCE STATE' } } }
        finally { $r.Dispose() }

        # The lock's resource_description is "<db>:[CopilotAudit.Run]:(<hash>)". Idle time is worked out on the
        # server, whose clock last_request_end_time is in.
        $cmd.CommandText = @"
SELECT TOP (1) l.request_session_id, s.program_name, s.host_name, s.login_name, s.status, r.command,
       CONVERT(VARCHAR(16), s.login_time, 120),
       DATEDIFF(MINUTE, s.last_request_end_time, SYSDATETIME())
FROM sys.dm_tran_locks AS l
JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id
LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
WHERE l.resource_type = N'APPLICATION'
  AND l.resource_database_id = DB_ID()
  AND l.request_mode = N'X'
  AND l.request_status = N'GRANT'
  AND l.resource_description LIKE N'%\[CopilotAudit.Run\]%' ESCAPE N'\'
  AND l.request_session_id <> @@SPID;
"@
        $r = $cmd.ExecuteReader()
        try {
            if (-not $r.Read()) { return 'No session holds it now: run this again.' }
            $spid = $r.GetValue(0)
            $text = { param([int] $i) if ($r.IsDBNull($i)) { '' } else { ([string] $r.GetValue($i)).Trim() } }
            $program = & $text 1; $hostName = & $text 2; $login = & $text 3; $status = & $text 4; $command = & $text 5
            $since = & $text 6
            $idle = if ($r.IsDBNull(7)) { -1 } else { [int] $r.GetValue(7) }
        }
        finally { $r.Dispose() }

        if (-not $program) { $program = 'a program that gave no name' }
        $where = ''
        if ($hostName) { $where += " on $hostName" }
        if ($login) { $where += " as $login" }
        if ($command -or $status -eq 'running') {
            $doing = if ($command) { "running $command now" } else { 'running now' }
            return "It is held by session $spid, $program$where, $doing (signed in $since): let it finish."
        }
        $howLong = if ($idle -lt 0) { 'idle' }
                   elseif ($idle -lt 60) { "idle for $idle min" }
                   elseif ($idle -lt 2880) { 'idle for {0} h {1} min' -f [math]::Floor($idle / 60), ($idle % 60) }
                   else { 'idle for {0} days' -f [math]::Floor($idle / 1440) }
        return "It is held by session $spid, $program$where, $howLong (signed in $since). If that is not a run of this product (a query window left open in SQL Server Management Studio, say), close it, or have a DBA end it: KILL $spid;"
    }
    catch [System.Data.SqlClient.SqlException] {
        $denied = $false
        foreach ($e in $_.Exception.Errors) { if ($e.Number -in 297, 300) { $denied = $true } }
        if ($denied) {
            $account = if ($who) { $who } else { 'this account' }
            return "Which session holds it cannot be shown: $account needs $permission on the SQL Server to see other sessions (a DBA can grant it: GRANT $permission TO [$account];), or a DBA can look it up."
        }
        return "Which session holds it could not be read: $($_.Exception.Message)"
    }
    catch { return "Which session holds it could not be read: $($_.Exception.Message)" }
    finally { $c.Dispose() }
}
