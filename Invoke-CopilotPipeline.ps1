<#
.SYNOPSIS
    Runs the pipeline from the job server's settings: the nightly run, or,
    with -History, the first load of everything.

.DESCRIPTION
    Reads copilot-audit.settings.psd1 (written by tools\Install-CopilotJobServer.ps1),
    so nothing here needs a parameter. Each step runs in its own PowerShell
    process and writes to a log of its own for the run (2.5.6) in the settings' LogPath. It stops at
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

    With ActivityDatabase in the settings (2.6.9), the nightly run also loads
    AI app calls outside Copilot, Power BI and Power Apps activity into it and
    rolls them up into the Copilot database; -History loads their history
    too. Both steps are optional: a failure is recorded and the run goes on.

    -ActivityHistory (once, for a database already running): only the
    activity history for the last HistoryDays days, finalized, and its rollup.
    Copilot interactions are not loaded or built again.

.PARAMETER History
    Load the history instead of the nightly run.

.PARAMETER ActivityHistory
    Load only the activity database's history (2.6.9), and roll it up.

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

    One run at a time on a database (2.4.12): each tenant's run holds its
    database while it works, as Install-CopilotAudit.ps1 and the maintenance
    rebuilds do. A run that finds another holding it stops at once, saying so.

.EXAMPLE
    .\Invoke-CopilotPipeline.ps1 -History
.EXAMPLE
    .\Invoke-CopilotPipeline.ps1          # what the nightly task runs
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [switch] $History,
    [switch] $ActivityHistory,
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
# The activity database's record types (2.6.9): AI apps outside Copilot, Power BI and Power Apps. ActivityRecordTypes in
# the settings replaces the list.
$script:ActivityRecordTypes = 'ConnectedAIAppInteraction', 'PowerBIAudit', 'PowerAppsApp'
# The delete guard's exit code (2.6.5): a loader that kept its table's last good load, which the night goes on from.
. (Join-Path $PSScriptRoot 'tools' 'DropGuard.ps1')
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
          [string] $Root = $PSScriptRoot, [switch] $ActivityHistory)
    function Has($k) { $Setting.Contains($k) -and $Setting[$k] }
    $app = @{ TenantId = $Setting.TenantId; ClientId = $Setting.ClientId; CertificateThumbprint = $Setting.CertificateThumbprint }
    $sql = @{ SqlServer = $Setting.SqlServer }
    if (Has 'TrustServerCertificate') { $sql.TrustServerCertificate = $true }
    $lic = if (Has 'LicenseDatabase') { [string] $Setting.LicenseDatabase } else { $null }
    # The license databases sessions and bi read (2.4.12): SessionLicenseDatabases, a list added to the settings by
    # hand when an older license database is read beside the one loaded each night, else that one. Only that one
    # could be passed, so a database built with several (as Install-CopilotAudit.ps1 suggests) failed every night.
    $readLic = @(@(if (Has 'SessionLicenseDatabases') { $Setting.SessionLicenseDatabases } elseif ($lic) { $lic }) |
                 ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $steps = [System.Collections.Generic.List[object]]::new()
    # The delete guard's percentage for a loader (2.6.5), where the settings set one: <Loader>MaxDropPercent, 0 to turn it off.
    # Without one, the loader's own default (15) applies.
    function Drop($argsTable, $key) {
        if ($Setting.Contains($key) -and $null -ne $Setting[$key] -and "$($Setting[$key])" -ne '') { $argsTable.MaxDropPercent = [double] $Setting[$key] }
        $argsTable
    }
    function Add($name, $script, $argsTable, [switch] $Optional) {
        $steps.Add([pscustomobject]@{ Name = $name; Script = [System.IO.Path]::Combine($Root, $script); Arguments = $argsTable; Optional = [bool] $Optional })
    }
    # The activity database (2.6.9), where the settings name one: its load, then its rollup into the Copilot database.
    $act = if (Has 'ActivityDatabase') { [string] $Setting.ActivityDatabase } else { $null }
    $actTypes = if (Has 'ActivityRecordTypes') { @($Setting.ActivityRecordTypes) } else { $script:ActivityRecordTypes }
    $rollup = $sql + @{ Database = $Setting.Database; ActivityDatabase = $act }
    $n = if ($Days) { $Days } elseif (Has 'HistoryDays') { [int] $Setting.HistoryDays } else { 180 }
    if ($ActivityHistory) {
        if (-not $act) { throw 'ActivityDatabase is not set in the settings file: there is no activity database to load.' }
        Add 'AI apps, Power BI and Power Apps (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $act; StartDate = $Today.AddDays(-$n); Finalize = $true; RecordTypes = $actTypes })
        Add 'AI apps, Power BI and Power Apps rollup' 'tools/Update-ActivityRollup.ps1' ($rollup + @{ Full = $true })
        return , $steps.ToArray()
    }

    $directory = {
        if (Has 'TargetDomain') {
            $a = $sql + @{ Database = $Setting.Database; TargetDomain = $Setting.TargetDomain }
            if ($Setting.Contains('EmployeeTypes')) { $a.EmployeeTypes = @($Setting.EmployeeTypes) }
            if (Has 'DepartmentAttribute') { $a.DepartmentAttribute = @($Setting.DepartmentAttribute) }   # 2.5.0: where the department is
            if (Has 'CountryNames') { $a.CountryNames = $Setting.CountryNames }   # the administrator's own names for countries
            Add 'User directory from AD' 'tools/Export-UsersWithHierarchy-ToSql.ps1' (Drop $a 'UserDirectoryMaxDropPercent')
        }
    }
    $licenses = {
        $l = $sql + $app + @{ Database = $Setting.Database; AllLicensedUsers = $true }
        if (Has 'IncludeLeaveDates') { $l.IncludeLeaveDates = $true }   # 2.4.3: needs User-LifeCycleInfo.Read.All
        Add 'Copilot licenses from Graph' 'tools/Update-CopilotAuditEnrichment.ps1' (Drop $l 'LicenseMaxDropPercent')
    }

    if ($History) {
        $start = $Today.AddDays(-$n)
        Add 'Copilot interactions (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $Setting.Database; StartDate = $start; Finalize = $true })
        if ($lic) { Add 'License events (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; StartDate = $start; Finalize = $true; Operations = $script:LicenseOperations }) }
        & $directory
        & $licenses
        $build = $sql + @{ Database = $Setting.Database }
        if ($lic) { $build.LicenseDatabase = $lic } else { $build.SkipLicenseDatabase = $true }
        if ($readLic.Count) { $build.LicenseEventDatabases = $readLic }
        if (Has 'Dashboard') { $build.Dashboard = $true }
        Add 'Build everything' 'Install-CopilotAudit.ps1' $build
        if ($act) {
            Add 'AI apps, Power BI and Power Apps (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $act; StartDate = $start; Finalize = $true; RecordTypes = $actTypes }) -Optional
            Add 'AI apps, Power BI and Power Apps rollup' 'tools/Update-ActivityRollup.ps1' ($rollup + @{ Full = $true }) -Optional
        }
    }
    else {
        & $directory
        & $licenses
        # An Excel workbook of agents, reloaded each night when the settings name it.
        # From SharePoint Online (the newest file in a library, as the pipeline's app) or from an address or a share.
        if (Has 'AgentWorkbookSiteUrl') {
            # Its own app registration and certificate (2.5.6), in the tenant the workbook's site is in.
            $w = $sql + @{ Database = $Setting.Database; SiteUrl = $Setting.AgentWorkbookSiteUrl
                           TenantId = $(if (Has 'AgentWorkbookTenantId') { $Setting.AgentWorkbookTenantId } else { $Setting.TenantId })
                           ClientId = $Setting.AgentWorkbookClientId; CertificateThumbprint = $Setting.AgentWorkbookCertificateThumbprint }
            if (Has 'AgentWorkbookFolder') { $w.Folder = $Setting.AgentWorkbookFolder }
            if (Has 'AgentWorkbookFilter') { $w.FileFilter = $Setting.AgentWorkbookFilter }
            Add 'Agent workbook from Excel' 'tools/Import-AgentWorkbook.ps1' (Drop $w 'AgentWorkbookMaxDropPercent')
        }
        elseif (Has 'AgentWorkbookUrl') { Add 'Agent workbook from Excel' 'tools/Import-AgentWorkbook.ps1' (Drop ($sql + @{ Database = $Setting.Database; Url = $Setting.AgentWorkbookUrl }) 'AgentWorkbookMaxDropPercent') }
        if ($lic) { Add 'License events' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; Incremental = $true; Operations = $script:LicenseOperations }) }
        $inter = $sql + $app + @{ Database = $Setting.Database; Incremental = $true }
        if ($readLic.Count) { $inter.SessionLicenseDatabase = $readLic -join ',' }
        Add 'Copilot interactions' 'Invoke-CopilotAuditBackfill.ps1' $inter
        if ($act) {
            Add 'AI apps, Power BI and Power Apps' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $act; Incremental = $true; RecordTypes = $actTypes }) -Optional
            Add 'AI apps, Power BI and Power Apps rollup' 'tools/Update-ActivityRollup.ps1' $rollup -Optional
        }
        # Usage and cost of AI outside Microsoft 365 Copilot (2.6.2), where the settings name the source. Last, and
        # optional: a failure of one is recorded with its step and in audit.ExternalUsageRun, and fails nothing else.
        if ((Has 'GitHubEnterprise') -or (Has 'GitHubOrganization')) {
            $g = $sql + @{ Database = $Setting.Database }
            if (Has 'GitHubEnterprise') { $g.Enterprise = [string] $Setting.GitHubEnterprise } else { $g.Organization = [string] $Setting.GitHubOrganization }
            if (Has 'GitHubTokenPath') { $g.TokenPath = [string] $Setting.GitHubTokenPath }
            if (Has 'GitHubApiBase') { $g.ApiBase = [string] $Setting.GitHubApiBase }
            Add 'GitHub Copilot usage' 'tools/Import-GitHubCopilotUsage.ps1' (Drop $g 'GitHubSeatMaxDropPercent') -Optional
        }
        if ((Has 'FoundrySubscriptions') -and ((Has 'FoundryCertificateThumbprint') -or (Has 'CertificateThumbprint'))) {
            $f = $sql + @{ Database = $Setting.Database; SubscriptionId = @($Setting.FoundrySubscriptions)
                           TenantId = $(if (Has 'FoundryTenantId') { $Setting.FoundryTenantId } else { $Setting.TenantId })
                           ClientId = $(if (Has 'FoundryClientId') { $Setting.FoundryClientId } else { $Setting.ClientId })
                           CertificateThumbprint = $(if (Has 'FoundryCertificateThumbprint') { $Setting.FoundryCertificateThumbprint } else { $Setting.CertificateThumbprint }) }
            Add 'Foundry usage and cost' 'tools/Import-FoundryUsage.ps1' (Drop $f 'FoundryDeploymentMaxDropPercent') -Optional
        }
    }
    , $steps.ToArray()
}

function Get-PipelineSkipNote {
    <#
        A line for the log for each optional loader the nightly run leaves out because the settings do not
        configure it (2.6.2): which one, and the setting that would turn it on. Nothing for one that is
        configured. Skipping is never a failure. Pure, for the tests.
    #>
    param([System.Collections.IDictionary] $Setting)
    function Has($k) { $Setting.Contains($k) -and $Setting[$k] }
    $notes = [System.Collections.Generic.List[string]]::new()
    if (-not ((Has 'GitHubEnterprise') -or (Has 'GitHubOrganization'))) {
        $notes.Add('GitHub Copilot usage: skipped, not configured (set GitHubEnterprise, or GitHubOrganization, in the settings file to load it).')
    }
    if (-not (Has 'FoundrySubscriptions')) {
        $notes.Add('Foundry usage and cost: skipped, not configured (set FoundrySubscriptions in the settings file to load it).')
    }
    elseif (-not ((Has 'FoundryCertificateThumbprint') -or (Has 'CertificateThumbprint'))) {
        $notes.Add('Foundry usage and cost: skipped, FoundrySubscriptions is set but there is no certificate to sign in with (FoundryCertificateThumbprint, or CertificateThumbprint).')
    }
    if (-not (Has 'ActivityDatabase')) {
        $notes.Add('AI apps, Power BI and Power Apps activity: skipped, not configured (set ActivityDatabase in the settings file, install it with Install-CopilotAudit.ps1 -ActivityDatabase, then load its history with Invoke-CopilotPipeline.ps1 -ActivityHistory).')
    }
    , $notes.ToArray()
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

function Get-DropGuardDetail {
    <#
        The run's detail when the delete guard refused a load (2.6.5): which steps, and the guard's own lines from the
        log, the last five, so Admin > Health says why without opening the log. At most 4,000 characters.
    #>
    param([string[]] $Step, [string] $Log)
    $why = @(if ($Log -and (Test-Path -LiteralPath $Log)) { Get-Content -LiteralPath $Log | Where-Object { $_ -match 'the delete guard allows at most' } | Select-Object -Last 5 })
    $text = "Refused by the delete guard: $($Step -join ', '). Each table kept its last good load; the rest of the run went on." + $(if ($why.Count) { "`n" + ($why -join "`n") } else { '' })
    $text.Substring(0, [Math]::Min(4000, $text.Length))
}

function Get-PipelineConnectionString {
    <# The Copilot database's connection string, from the settings. #>
    param([System.Collections.IDictionary] $Setting)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $Setting.SqlServer; $b['Initial Catalog'] = $Setting.Database; $b['Integrated Security'] = $true
    $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] ($Setting.Contains('TrustServerCertificate') -and $Setting.TrustServerCertificate)
    # A whole connection string in the settings wins (the tests, or SQL authentication).
    if ($Setting.Contains('ConnectionString') -and $Setting.ConnectionString) { [string] $Setting.ConnectionString } else { $b.ConnectionString }
}

function Invoke-PipelineSql {
    <# One command against the Copilot database: a scalar back, or nothing. #>
    param([System.Collections.IDictionary] $Setting, [string] $Sql, [hashtable] $Parameters = @{}, [int] $Timeout = 60)
    $c = [System.Data.SqlClient.SqlConnection]::new((Get-PipelineConnectionString $Setting))
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
    # The table only through EXEC (2.4.12): a statement naming a missing table fails when it runs, whatever its other
    # conditions, so a database whose history was not loaded yet threw here instead of saying to load it.
    [bool] (Invoke-PipelineSql $Setting "IF OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL SELECT 0; ELSE EXEC (N'SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.CopilotAudit) THEN 1 ELSE 0 END');")
}

function Enter-PipelineRun {
    <#
        Holds the Copilot database for this run alone (2.4.12): an exclusive application lock, on a connection of its
        own kept open until the run ends, that Install-CopilotAudit.ps1 and the maintenance rebuilds take too. Nothing
        kept runs apart: a run by hand during the nightly, or the nightly during an upgrade's full rebuild, loaded the
        same windows twice or counted interactions twice. Returns that connection (dispose it to let go), or nothing
        when another run holds the database. The steps this run starts know it holds the lock by CopilotAuditRunLock,
        so the history's build step does not wait on its own run.
    #>
    param([System.Collections.IDictionary] $Setting)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new((Get-PipelineConnectionString $Setting))
    # Not pooled: closing a pooled connection keeps its session, and the session's lock with it.
    $b['Pooling'] = $false
    $c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
        if ([int] $cmd.ExecuteScalar() -lt 0) { $c.Dispose(); return $null }
    }
    catch { $c.Dispose(); throw }
    $env:CopilotAuditRunLock = "$($Setting.SqlServer)/$($Setting.Database)"
    $c
}

function Exit-PipelineRun {
    param($Lock)
    if ($Lock) { $Lock.Dispose() }
    Remove-Item Env:\CopilotAuditRunLock -ErrorAction SilentlyContinue
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

function Invoke-ReservationSnapshot {
    <#
        2.6.8: keeps last month's Copilot reservation position (Leadership >
        Copilot reservations) once, as it was, after the licenses are read:
        dash.usp_TakeReservationSnapshot does nothing until the register has
        rows, or when the month is already kept. A failure is logged, not fatal.
    #>
    param([System.Collections.IDictionary] $Setting, [string] $Log)
    try {
        $null = Invoke-PipelineSql $Setting -Timeout 600 -Sql "IF OBJECT_ID(N'dash.usp_TakeReservationSnapshot', N'P') IS NOT NULL EXEC dash.usp_TakeReservationSnapshot;"
    }
    catch { "$(Get-Date -Format s) The reservation snapshot was not taken: $($_.Exception.Message)" | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue }
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

function Get-PipelineCertificateUse {
    <#
        Every certificate the pipeline signs in with, from the settings: what it is for, its thumbprint, and the
        app registration and tenant it signs in as. Purview: the app that reads the audit log and the licenses
        through Microsoft Graph. AgentWorkbook: the app that fetches the agent workbook from SharePoint Online,
        where the settings name a site and a certificate for it (a workbook from a share or an address signs in as
        the task's account, with no certificate). Pure, for the tests.
    #>
    param([System.Collections.IDictionary] $T)
    function Has($k) { $T.Contains($k) -and $T[$k] }
    # A thumbprint as the certificate dialog copies it can carry spaces and an invisible mark: hexadecimal digits only.
    function Clean($v) { ("$v" -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() }
    $uses = [System.Collections.Generic.List[object]]::new()
    if (Has 'CertificateThumbprint') {
        $uses.Add([pscustomobject]@{ Purpose = 'Purview'; Thumbprint = (Clean $T.CertificateThumbprint)
                                     ClientId = $(if (Has 'ClientId') { "$($T.ClientId)" }); TenantId = $(if (Has 'TenantId') { "$($T.TenantId)" }) })
    }
    if ((Has 'AgentWorkbookSiteUrl') -and (Has 'AgentWorkbookCertificateThumbprint')) {
        $uses.Add([pscustomobject]@{ Purpose = 'AgentWorkbook'; Thumbprint = (Clean $T.AgentWorkbookCertificateThumbprint)
                                     ClientId = $(if (Has 'AgentWorkbookClientId') { "$($T.AgentWorkbookClientId)" })
                                     TenantId = $(if (Has 'AgentWorkbookTenantId') { "$($T.AgentWorkbookTenantId)" } elseif (Has 'TenantId') { "$($T.TenantId)" }) })
    }
    # Foundry (2.6.2): only where its loader signs in with a certificate of its own, not the audit app's.
    if ((Has 'FoundrySubscriptions') -and (Has 'FoundryCertificateThumbprint')) {
        $uses.Add([pscustomobject]@{ Purpose = 'Foundry'; Thumbprint = (Clean $T.FoundryCertificateThumbprint)
                                     ClientId = $(if (Has 'FoundryClientId') { "$($T.FoundryClientId)" } elseif (Has 'ClientId') { "$($T.ClientId)" })
                                     TenantId = $(if (Has 'FoundryTenantId') { "$($T.FoundryTenantId)" } elseif (Has 'TenantId') { "$($T.TenantId)" }) })
    }
    , $uses.ToArray()
}

function Write-PipelineCertificate {
    <#
        Records each of those certificates as the job server holds it now (audit.PipelineCertificate), for Admin >
        Health and the dashboard's expiry alerts: subject, issuer, validity, the store it is in, and the app it signs
        in as. One the settings name that is in neither store is recorded as not found. A certificate no longer in
        use (the workbook moved to a share) has its row removed. Never fails the run, as the run's own record.
    #>
    param([System.Collections.IDictionary] $T, [string] $Log)
    $uses = Get-PipelineCertificateUse $T   # an array already: wrapped again it would be one item holding them all
    foreach ($u in $uses) {
        $cert = $null; $where = $null
        foreach ($store in 'LocalMachine\My', 'CurrentUser\My') {
            $cert = Get-Item -LiteralPath "Cert:\$store\$($u.Thumbprint)" -ErrorAction SilentlyContinue
            if ($cert) { $where = $store; break }
        }
        [void] (Write-PipelineRecord $T -Log $Log -Parameters @{
                '@p' = $u.Purpose; '@t' = $u.Thumbprint; '@f' = [bool] $cert
                '@s' = $(if ($cert) { $cert.Subject }); '@i' = $(if ($cert) { $cert.Issuer })
                '@nb' = $(if ($cert) { $cert.NotBefore.ToUniversalTime() }); '@e' = $(if ($cert) { $cert.NotAfter.ToUniversalTime() })
                '@st' = $where; '@c' = $u.ClientId; '@tn' = $u.TenantId; '@h' = [Environment]::MachineName
            } -Sql @"
IF OBJECT_ID(N'audit.PipelineCertificate', N'U') IS NOT NULL EXEC sp_executesql N'
MERGE audit.PipelineCertificate AS t USING (SELECT @p AS Purpose) AS s ON t.Purpose = s.Purpose
WHEN MATCHED THEN UPDATE SET Thumbprint = @t, Found = @f, Subject = LEFT(@s, 400), Issuer = LEFT(@i, 400), NotBeforeUtc = @nb, ExpiresUtc = @e,
                             Store = @st, ClientId = LEFT(@c, 64), TenantId = LEFT(@tn, 256), Host = @h, SeenUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (Purpose, Thumbprint, Found, Subject, Issuer, NotBeforeUtc, ExpiresUtc, Store, ClientId, TenantId, Host)
                      VALUES (@p, @t, @f, LEFT(@s, 400), LEFT(@i, 400), @nb, @e, @st, LEFT(@c, 64), LEFT(@tn, 256), @h);',
    N'@p VARCHAR(32), @t VARCHAR(64), @f BIT, @s NVARCHAR(4000), @i NVARCHAR(4000), @nb DATETIME2(0), @e DATETIME2(0), @st VARCHAR(32), @c NVARCHAR(4000), @tn NVARCHAR(4000), @h NVARCHAR(128)',
    @p = @p, @t = @t, @f = @f, @s = @s, @i = @i, @nb = @nb, @e = @e, @st = @st, @c = @c, @tn = @tn, @h = @h;
"@)
    }
    # What is no longer used is no longer watched. The names are this script's own.
    $keep = (@($uses | ForEach-Object { "'$($_.Purpose)'" }) + "''") -join ', '
    [void] (Write-PipelineRecord $T -Log $Log -Parameters @{} -Sql "IF OBJECT_ID(N'audit.PipelineCertificate', N'U') IS NOT NULL EXEC (N'DELETE FROM audit.PipelineCertificate WHERE Purpose NOT IN ($($keep.Replace("'", "''")));');")
}

function Invoke-TenantRun {
    <# One tenant's run: its steps in order, recorded in its own database. Returns $true when every step succeeded. #>
    param([System.Collections.IDictionary] $T, [string] $Log, [string] $Label)
    if (-not $History -and -not (Test-HistoryLoaded $T)) {
        "$(Get-Date -Format s) $Label No Copilot records in $($T.Database) yet. Load the history first: .\Invoke-CopilotPipeline.ps1 -History$(if ($Label) { " -Tenant '$($T.Name)'" })" |
            Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
        return $false
    }
    if ($ActivityHistory -and -not ($T.Contains('ActivityDatabase') -and $T.ActivityDatabase)) {
        # Said before a run is recorded: a run row left Running would read as one that died.
        "$(Get-Date -Format s) $Label No ActivityDatabase in the settings for $($T.Name): there is no activity history to load." |
            Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
        return $false
    }
    $once = $History -or $ActivityHistory
    $runId = Write-PipelineRecord $T -Log $Log -Parameters @{ '@k' = $(if ($once) { 'History' } else { 'Nightly' }); '@h' = [Environment]::MachineName; '@u' = "$env:USERDOMAIN\$env:USERNAME" } -Sql `
        "INSERT INTO audit.PipelineRun (Kind, Host, RunAs) VALUES (@k, @h, @u); SELECT CAST(SCOPE_IDENTITY() AS INT);"
    # The app's certificate and when it expires, for the dashboard's expiry alert.
    $cert = Get-PipelineCertificate $T
    if ($runId -and $cert) {
        [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@t' = $cert.Thumbprint; '@e' = $cert.NotAfter.ToUniversalTime(); '@s' = $cert.Subject } -Sql `
            "IF COL_LENGTH(N'audit.PipelineRun', N'CertificateExpiresUtc') IS NOT NULL EXEC sp_executesql N'UPDATE audit.PipelineRun SET CertificateThumbprint = @t, CertificateExpiresUtc = @e, CertificateSubject = LEFT(@s, 400) WHERE RunId = @r;', N'@r INT, @t VARCHAR(64), @e DATETIME2(0), @s NVARCHAR(4000)', @r = @r, @t = @t, @e = @e, @s = @s;")
    }
    # Each certificate the run signs in with, for Admin > Health and the expiry alerts: the audit app's and the agent workbook's.
    Write-PipelineCertificate $T -Log $Log
    $n = 0
    $refused = [System.Collections.Generic.List[string]]::new()
    foreach ($step in (Get-PipelineStep -Setting $T -History:$History -ActivityHistory:$ActivityHistory -Days $Days)) {
        $n++
        if ($Label) { $step.Name = "$Label $($step.Name)" }
        if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@n' = $n; '@s' = $step.Name } -Sql 'INSERT INTO audit.PipelineRunStep (RunId, StepNo, Name) VALUES (@r, @n, @s);') }
        $code = Invoke-PipelineStep -Step $step -Log $Log
        if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@n' = $n; '@c' = $code } -Sql 'UPDATE audit.PipelineRunStep SET EndedUtc = SYSUTCDATETIME(), ExitCode = @c WHERE RunId = @r AND StepNo = @n;') }
        if ($code -eq $script:DropRefusedExitCode) {
            # Refused by the delete guard (2.6.5): the table kept its last good load, so the steps after it can run.
            "$(Get-Date -Format s) $($step.Name) was refused by the delete guard: its table keeps the last good load (the reason is above). The run goes on." |
                Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
            $refused.Add($step.Name)
            continue
        }
        if ($code -ne 0 -and $step.PSObject.Properties['Optional'] -and $step.Optional) {
            # An optional step (2.6.2): its failure is on its own row and in the log, and the run goes on.
            "$(Get-Date -Format s) $($step.Name) failed (exit code $code). It is optional: the run goes on. Its own log is audit.ExternalUsageRun (Admin > Health)." |
                Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue
            continue
        }
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
    # The optional loaders the settings do not configure: a note each, and no more.
    if (-not $once) {
        foreach ($note in (Get-PipelineSkipNote $T)) { "$(Get-Date -Format s) $Label $note".Replace('  ', ' ') | Tee-Object -FilePath $Log -Append | Write-Information -InformationAction Continue }
    }
    if (-not $once) { Invoke-Retention -Setting $T -Log $Log }
    # 2.7.0: not after a load the delete guard refused: a month end is kept once, and its licenses may be the night before's.
    if (-not $once -and $refused.Count -eq 0) { Invoke-ReservationSnapshot -Setting $T -Log $Log }
    # A load the delete guard refused is named in the run's detail, which Admin > Health shows, with its reason.
    $detail = if ($refused.Count) { Get-DropGuardDetail -Step $refused -Log $Log } else { $null }
    if ($runId) { [void] (Write-PipelineRecord $T -Log $Log -Parameters @{ '@r' = $runId; '@d' = $detail } -Sql "UPDATE audit.PipelineRun SET EndedUtc = SYSUTCDATETIME(), Status = 'Succeeded', Detail = @d WHERE RunId = @r;") }
    $true
}

function Invoke-Main {
    $s = Get-PipelineSetting $SettingsPath
    $logDir = if ($s.Contains('LogPath') -and $s.LogPath) { $s.LogPath } else { Join-Path $PSScriptRoot 'logs' }
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    # A log of its own for each run (2.5.6): a second run on one day appended to the first's.
    $log = Join-Path $logDir ("{0}-{1:yyyy-MM-dd-HHmmss}.log" -f $(if ($ActivityHistory) { 'activity-history' } elseif ($History) { 'history' } else { 'nightly' }), (Get-Date))
    $keep = if ($s.Contains('LogRetentionDays') -and $s.LogRetentionDays) { [int] $s.LogRetentionDays } else { 90 }
    Get-ChildItem -LiteralPath $logDir -Filter '*.log' | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$keep) | Remove-Item -ErrorAction SilentlyContinue

    $tenants = Get-PipelineTenant -Setting $s -Only $Tenant
    $several = $s.Contains('Tenants') -and @($s.Tenants).Count -gt 0
    $failed = @()
    foreach ($t in $tenants) {
        $label = if ($several) { "[$($t.Name)]" } else { '' }
        # A tenant that cannot run is a failure of that tenant, and the next one runs (2.4.12): one whose database was
        # offline, restoring or had lost the service account's login threw out of this loop, so the tenants after it
        # did not run, every night until it was fixed, and no "Finished with failures" line was written.
        $ok = $false; $lock = $null
        try {
            $lock = Enter-PipelineRun $t
            if ($lock) { $ok = Invoke-TenantRun -T $t -Log $log -Label $label }
            else {
                "$(Get-Date -Format s) $label Another run is using $($t.Database) now (the nightly task, an upgrade or a rebuild): nothing was run. Run this again when it has finished." |
                    Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
            }
        }
        catch {
            "$(Get-Date -Format s) $label Could not run against $($t.Database): $($_.Exception.Message)" |
                Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
        }
        finally { Exit-PipelineRun $lock }
        if (-not $ok) { $failed += $t.Name }
    }
    if ($failed.Count) {
        "$(Get-Date -Format s) Finished with failures$(if ($several) { ": $($failed -join ', ')" })." | Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
        exit 1
    }
    "$(Get-Date -Format s) Done." | Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
    if ($History) { Write-Information 'History loaded and built. The nightly task keeps it current from tonight.' -InformationAction Continue }
    if ($ActivityHistory) { Write-Information 'Activity history loaded and rolled up. The nightly task keeps it current from tonight.' -InformationAction Continue }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
