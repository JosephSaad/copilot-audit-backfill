<#
.SYNOPSIS
    Runs the pipeline from the job server's settings: the nightly run, or,
    with -History, the first load of everything.

.DESCRIPTION
    Reads copilot-audit.settings.psd1 (written by tools\Install-CopilotJobServer.ps1),
    so nothing here needs a parameter. Each step runs in its own PowerShell
    process and appends to a dated log in the settings' LogPath. It stops at
    the first failure and exits 1, so Task Scheduler's history shows it; the
    next run resumes the same load.

    Nightly (the scheduled task), in this order:
      1. User directory from AD, so tonight's builds see today's organization.
      2. Copilot licenses from Graph.
      3. License events: -Incremental into the license database.
      4. Copilot interactions: -Incremental, which then refreshes the
         flattened table, sessions, bi and the dashboard.

    Then, after a successful night, the retention the dashboard's
    administrators set (dash.usp_ApplyRetention, where the dashboard is
    installed).

    Every run and step is recorded in audit.PipelineRun and
    audit.PipelineRunStep, which Admin > Health shows and the dashboard's
    pipeline alert watches. If SQL cannot be reached to record it, the run
    goes on and the log still has everything.

    -History (once, from a console; hours):
      1. Copilot interactions for the last HistoryDays days, finalized.
      2. License events for the same days, finalized.
      3. User directory from AD.
      4. Copilot licenses from Graph.
      5. Install-CopilotAudit.ps1: shreds the license events and builds
         everything in full.
    Every step resumes where it stopped, so after Ctrl+C or a reboot the same
    command carries on.

.PARAMETER History
    Load the history instead of the nightly run.

.PARAMETER Days
    How many days of history (-History). Default: HistoryDays in the settings, 180.

.PARAMETER Tenant
    With several tenants (Tenants in the settings), run only this one, by
    its Name: for example to load a newly added tenant's history.

.PARAMETER SettingsPath
    Default: copilot-audit.settings.psd1 next to this script.

.NOTES
    Several Microsoft 365 tenants: each has its own app registration and its
    own pair of databases, listed under Tenants in the settings (each entry
    overrides the top-level values it names; tools\Add-CopilotTenant.ps1 adds
    one). The top-level values are the first tenant. Each tenant runs in turn,
    with its own run record; one tenant's failure does not stop the next, and
    the exit code is 1 if any failed.

.EXAMPLE
    .\Invoke-CopilotPipeline.ps1 -History
.EXAMPLE
    .\Invoke-CopilotPipeline.ps1          # what the nightly task runs
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [switch] $History,
    [ValidateRange(1, 3650)][int] $Days,
    [string] $Tenant,
    [string] $SettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The license-event load's operations. Copilot Studio's authoring operations ride along in the same
# searches (no extra search), for Agents > Build and publish (audit.usp_ShredAgentEvents).
$script:AgentOperations = 'BotCreate', 'BotDelete', 'BotDeleteCleanup', 'BotUpdateOperation-BotPublish', 'BotUpdateOperation-BotShare',
                          'BotUpdateOperation-BotNameUpdate', 'BotUpdateOperation-BotAuthUpdate', 'BotUpdateOperation-BotIconUpdate',
                          'BotAppInsightsUpdate', 'BotComponentCreate', 'BotComponentUpdate', 'BotComponentDelete',
                          'BotComponentCollectionCreate', 'BotComponentCollectionDelete'
$script:LicenseOperations = @('Change user license', 'Update user', 'Add user', 'Delete user') + $script:AgentOperations
if (-not $SettingsPath) { $SettingsPath = Join-Path $PSScriptRoot 'copilot-audit.settings.psd1' }

function Get-PipelineSetting {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "No settings at $Path. Run tools\Install-CopilotJobServer.ps1 first." }
    $s = Import-PowerShellDataFile -LiteralPath $Path
    foreach ($k in 'SqlServer', 'Database', 'TenantId', 'ClientId', 'CertificateThumbprint') {
        if (-not ($s.Contains($k) -and $s[$k])) { throw "$k is not set in $Path. Run tools\Install-CopilotJobServer.ps1 again (with -RegisterApp, or -TenantId and -ClientId)." }
    }
    $s
}

function Get-PipelineTenant {
    <#
        Every tenant to run, as its own complete settings: the top-level
        values (the first tenant, named by TenantName or 'Default'), then each
        entry of Tenants overlaid on them. Pure, for the tests.
    #>
    param([System.Collections.IDictionary] $Setting, [string] $Only)
    $first = [ordered]@{}
    foreach ($k in $Setting.Keys) { if ($k -ne 'Tenants') { $first[$k] = $Setting[$k] } }
    $first.Name = if ($Setting.Contains('TenantName') -and $Setting.TenantName) { [string] $Setting.TenantName } else { 'Default' }
    $all = [System.Collections.Generic.List[object]]::new()
    $all.Add($first)
    if ($Setting.Contains('Tenants')) {
        foreach ($t in @($Setting.Tenants)) {
            if (-not $t) { continue }
            $m = [ordered]@{}
            foreach ($k in $first.Keys) { $m[$k] = $first[$k] }
            foreach ($k in $t.Keys) { $m[$k] = $t[$k] }
            foreach ($need in 'Name', 'TenantId', 'ClientId', 'Database') {
                if (-not ($t.Contains($need) -and $t[$need])) { throw "A tenant under Tenants has no $need. Each needs Name, TenantId, ClientId and its own Database." }
            }
            if ($m.Database -eq $first.Database) { throw "Tenant $($m.Name) uses the first tenant's database ($($m.Database)): each tenant needs its own." }
            $all.Add($m)
        }
    }
    $names = @($all | ForEach-Object { $_.Name })
    if (@($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Two tenants have the same Name.' }
    if ($Only) {
        $pick = @($all | Where-Object { $_.Name -eq $Only })
        if (-not $pick) { throw "No tenant '$Only'. The tenants are: $($names -join ', ')." }
        return , $pick
    }
    , $all.ToArray()
}

function Get-PipelineStep {
    <#
        The steps to run, as (Name, Script, Arguments). Pure: the settings in,
        the plan out, so the plan can be tested without running anything.
    #>
    param([System.Collections.IDictionary] $Setting, [switch] $History, [int] $Days, [datetime] $Today = [datetime]::UtcNow.Date,
          [string] $Root = $PSScriptRoot)
    function Has($k) { $Setting.Contains($k) -and $Setting[$k] }
    $app = @{ TenantId = $Setting.TenantId; ClientId = $Setting.ClientId; CertificateThumbprint = $Setting.CertificateThumbprint }
    $sql = @{ SqlServer = $Setting.SqlServer }
    if (Has 'TrustServerCertificate') { $sql.TrustServerCertificate = $true }
    $lic = if (Has 'LicenseDatabase') { [string] $Setting.LicenseDatabase } else { $null }
    $steps = [System.Collections.Generic.List[object]]::new()
    function Add($name, $script, $argsTable) { $steps.Add([pscustomobject]@{ Name = $name; Script = [System.IO.Path]::Combine($Root, $script); Arguments = $argsTable }) }

    $directory = {
        if (Has 'TargetDomain') {
            $a = $sql + @{ Database = $Setting.Database; TargetDomain = $Setting.TargetDomain }
            if ($Setting.Contains('EmployeeTypes')) { $a.EmployeeTypes = @($Setting.EmployeeTypes) }
            Add 'User directory from AD' 'tools/Export-UsersWithHierarchy-ToSql.ps1' $a
        }
    }
    $licenses = { Add 'Copilot licenses from Graph' 'tools/Update-CopilotAuditEnrichment.ps1' ($sql + $app + @{ Database = $Setting.Database; AllLicensedUsers = $true }) }

    if ($History) {
        $n = if ($Days) { $Days } elseif (Has 'HistoryDays') { [int] $Setting.HistoryDays } else { 180 }
        $start = $Today.AddDays(-$n)
        Add 'Copilot interactions (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $Setting.Database; StartDate = $start; Finalize = $true })
        if ($lic) { Add 'License events (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; StartDate = $start; Finalize = $true; Operations = $script:LicenseOperations }) }
        & $directory
        & $licenses
        $build = $sql + @{ Database = $Setting.Database }
        if ($lic) { $build.LicenseDatabase = $lic; $build.LicenseEventDatabases = @($lic) } else { $build.SkipLicenseDatabase = $true }
        if (Has 'Dashboard') { $build.Dashboard = $true }
        Add 'Build everything' 'Install-CopilotAudit.ps1' $build
    }
    else {
        & $directory
        & $licenses
        if ($lic) { Add 'License events' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; Incremental = $true; Operations = $script:LicenseOperations }) }
        $inter = $sql + $app + @{ Database = $Setting.Database; Incremental = $true }
        if ($lic) { $inter.SessionLicenseDatabase = $lic }
        Add 'Copilot interactions' 'Invoke-CopilotAuditBackfill.ps1' $inter
    }
    , $steps.ToArray()
}

function Invoke-PipelineStep {
    <# Runs one step in its own process, teeing its output to the log. Returns its exit code. #>
    param([pscustomobject] $Step, [string] $Log)
    "$(Get-Date -Format s) == $($Step.Name)" | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
    # A separate process per step, so one step's modules and state cannot affect the next.
    & pwsh -NoProfile -NonInteractive -Command { param($s, $a) $global:LASTEXITCODE = 0; & $s @a; exit $LASTEXITCODE } -args $Step.Script, $Step.Arguments 2>&1 |
        Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
    $LASTEXITCODE
}

function Invoke-PipelineSql {
    <# One command against the Copilot database: a scalar back, or nothing. #>
    param([System.Collections.IDictionary] $Setting, [string] $Sql, [hashtable] $Parameters = @{}, [int] $Timeout = 60)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $Setting.SqlServer; $b['Initial Catalog'] = $Setting.Database; $b['Integrated Security'] = $true
    $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] ($Setting.Contains('TrustServerCertificate') -and $Setting.TrustServerCertificate)
    # A whole connection string in the settings wins (the tests, or SQL authentication).
    $cs = if ($Setting.Contains('ConnectionString') -and $Setting.ConnectionString) { [string] $Setting.ConnectionString } else { $b.ConnectionString }
    $c = [System.Data.SqlClient.SqlConnection]::new($cs)
    $c.Open()
    try {
        $cmd = $c.CreateCommand()
        $cmd.CommandText = $Sql
        $cmd.CommandTimeout = $Timeout
        foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
        $cmd.ExecuteScalar()
    }
    finally { $c.Dispose() }
}

function Test-HistoryLoaded {
    param([System.Collections.IDictionary] $Setting)
    [bool] (Invoke-PipelineSql $Setting "SELECT CASE WHEN OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM audit.CopilotAudit) THEN 1 ELSE 0 END")
}

function Write-PipelineRecord {
    <#
        Records the run in audit.PipelineRun / PipelineRunStep. Never fails the
        run: a database that cannot be reached, or one without the tables yet,
        leaves only a warning in the log.
    #>
    param([System.Collections.IDictionary] $Setting, [string] $Sql, [hashtable] $Parameters, [string] $Log)
    try { Invoke-PipelineSql $Setting ("IF OBJECT_ID(N'audit.PipelineRun', N'U') IS NOT NULL BEGIN " + $Sql + " END") $Parameters }
    catch { "$(Get-Date -Format s) (not recorded in audit.PipelineRun: $($_.Exception.Message))" | Out-File -FilePath $Log -Append }
}

function Get-LogTail {
    <# The last lines of the log, for the failed run's detail. #>
    param([string] $Log, [int] $Lines = 25)
    if (-not (Test-Path -LiteralPath $Log)) { return $null }
    $t = (Get-Content -LiteralPath $Log -Tail $Lines) -join "`n"
    if ($t.Length -gt 3900) { $t = $t.Substring($t.Length - 3900) }
    $t
}

function Invoke-Retention {
    <#
        The retention the dashboard's administrators set (Admin > Privacy and
        retention): old audit-log and notification rows, and the stored JSON of
        old records. A failure is logged, not fatal: the data is loaded either way.
    #>
    param([System.Collections.IDictionary] $Setting, [string] $Log)
    try {
        $r = Invoke-PipelineSql $Setting -Timeout 3600 -Sql "IF OBJECT_ID(N'dash.usp_ApplyRetention', N'P') IS NOT NULL EXEC dash.usp_ApplyRetention;"
        if ($r) { "$(Get-Date -Format s) Retention: $r" | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue }
    }
    catch { "$(Get-Date -Format s) Retention was not applied: $($_.Exception.Message)" | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue }
}

function Get-PipelineCertificate {
    <# The app certificate in LocalMachine\My (or CurrentUser\My), or nothing. #>
    param([System.Collections.IDictionary] $T)
    if (-not $T.Contains('CertificateThumbprint') -or -not $T.CertificateThumbprint) { return }
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My') {
        $c = Get-Item -LiteralPath "$store\$($T.CertificateThumbprint)" -ErrorAction SilentlyContinue
        if ($c) { return $c }
    }
}

function Invoke-TenantRun {
    <# One tenant's run: its steps in order, recorded in its own database. Returns $true when every step succeeded. #>
    param([System.Collections.IDictionary] $T, [string] $Log, [string] $Label)
    if (-not $History -and -not (Test-HistoryLoaded $T)) {
        "$(Get-Date -Format s) $Label No Copilot records in $($T.Database) yet. Load the history first: .\Invoke-CopilotPipeline.ps1 -History$(if ($Label) { " -Tenant '$($T.Name)'" })" |
            Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
        return $false
    }
    $runId = Write-PipelineRecord $T -Log $Log -Parameters @{ '@k' = $(if ($History) { 'History' } else { 'Nightly' }); '@h' = [Environment]::MachineName; '@u' = "$env:USERDOMAIN\$env:USERNAME" } -Sql `
        "INSERT INTO audit.PipelineRun (Kind, Host, RunAs) VALUES (@k, @h, @u); SELECT CAST(SCOPE_IDENTITY() AS INT);"
    # The app's certificate and when it expires, for the dashboard's expiry alert.
    $cert = Get-PipelineCertificate $T
    if ($runId -and $cert) {
        [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@t' = $cert.Thumbprint; '@e' = $cert.NotAfter.ToUniversalTime(); '@s' = $cert.Subject } -Sql `
            "IF COL_LENGTH(N'audit.PipelineRun', N'CertificateExpiresUtc') IS NOT NULL EXEC sp_executesql N'UPDATE audit.PipelineRun SET CertificateThumbprint = @t, CertificateExpiresUtc = @e, CertificateSubject = LEFT(@s, 400) WHERE RunId = @r;', N'@r INT, @t VARCHAR(64), @e DATETIME2(0), @s NVARCHAR(4000)', @r = @r, @t = @t, @e = @e, @s = @s;")
    }
    $n = 0
    foreach ($step in (Get-PipelineStep -Setting $T -History:$History -Days $Days)) {
        $n++
        if ($Label) { $step.Name = "$Label $($step.Name)" }
        if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@n' = $n; '@s' = $step.Name } -Sql 'INSERT INTO audit.PipelineRunStep (RunId, StepNo, Name) VALUES (@r, @n, @s);') }
        $code = Invoke-PipelineStep -Step $step -Log $Log
        if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@n' = $n; '@c' = $code } -Sql 'UPDATE audit.PipelineRunStep SET EndedUtc = SYSUTCDATETIME(), ExitCode = @c WHERE RunId = @r AND StepNo = @n;') }
        if ($code -ne 0) {
            "$(Get-Date -Format s) $($step.Name) failed (exit code $code). Run the same command again to resume." |
                Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
            if ($runId) {
                [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@f' = $step.Name; '@d' = (Get-LogTail $Log) } `
                    -Sql "UPDATE audit.PipelineRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Failed', FailedStep = @f, Detail = @d WHERE RunId = @r;")
            }
            return $false
        }
    }
    if (-not $History) { Invoke-Retention -Setting $T -Log $Log }
    if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId } -Sql "UPDATE audit.PipelineRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Succeeded' WHERE RunId = @r;") }
    $true
}

function Invoke-Main {
    $s = Get-PipelineSetting $SettingsPath
    $logDir = if ($s.Contains('LogPath') -and $s.LogPath) { $s.LogPath } else { Join-Path $PSScriptRoot 'logs' }
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $log = Join-Path $logDir ("{0}-{1:yyyy-MM-dd}.log" -f $(if ($History) { 'history' } else { 'nightly' }), (Get-Date))
    $keep = if ($s.Contains('LogRetentionDays') -and $s.LogRetentionDays) { [int] $s.LogRetentionDays } else { 90 }
    Get-ChildItem -LiteralPath $logDir -Filter '*.log' | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$keep) | Remove-Item -ErrorAction SilentlyContinue

    $tenants = Get-PipelineTenant -Setting $s -Only $Tenant
    $several = $s.Contains('Tenants') -and @($s.Tenants).Count -gt 0
    $failed = @()
    foreach ($t in $tenants) {
        $label = if ($several) { "[$($t.Name)]" } else { '' }
        if (-not (Invoke-TenantRun -T $t -Log $log -Label $label)) { $failed += $t.Name }
    }
    if ($failed.Count) {
        "$(Get-Date -Format s) Finished with failures$(if ($several) { ": $($failed -join ', ')" })." | Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
        exit 1
    }
    "$(Get-Date -Format s) Done." | Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
    if ($History) { Write-Information 'History loaded and built. The nightly task keeps it current from tonight.' -InformationAction Continue }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
