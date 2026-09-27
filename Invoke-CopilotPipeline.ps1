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
      1. User directory from AD, so tonight's builds see today's organisation.
      2. Copilot licences from Graph.
      3. Licence events: -Incremental into the licence database.
      4. Copilot interactions: -Incremental, which then refreshes the
         flattened table, sessions, bi and the dashboard.

    -History (once, from a console; hours):
      1. Copilot interactions for the last HistoryDays days, finalized.
      2. Licence events for the same days, finalized.
      3. User directory from AD.
      4. Copilot licences from Graph.
      5. Install-CopilotAudit.ps1: shreds the licence events and builds
         everything in full.
    Every step resumes where it stopped, so after Ctrl+C or a reboot the same
    command carries on.

.PARAMETER History
    Load the history instead of the nightly run.

.PARAMETER Days
    How many days of history (-History). Default: HistoryDays in the settings, 180.

.PARAMETER SettingsPath
    Default: copilot-audit.settings.psd1 next to this script.

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
    [string] $SettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:LicenceOperations = 'Change user license', 'Update user', 'Add user', 'Delete user'
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
    $lic = if (Has 'LicenceDatabase') { [string] $Setting.LicenceDatabase } else { $null }
    $steps = [System.Collections.Generic.List[object]]::new()
    function Add($name, $script, $argsTable) { $steps.Add([pscustomobject]@{ Name = $name; Script = [System.IO.Path]::Combine($Root, $script); Arguments = $argsTable }) }

    $directory = {
        if (Has 'TargetDomain') {
            $a = $sql + @{ Database = $Setting.Database; TargetDomain = $Setting.TargetDomain }
            if ($Setting.Contains('EmployeeTypes')) { $a.EmployeeTypes = @($Setting.EmployeeTypes) }
            Add 'User directory from AD' 'tools/Export-UsersWithHierarchy-ToSql.ps1' $a
        }
    }
    $licences = { Add 'Copilot licences from Graph' 'tools/Update-CopilotAuditEnrichment.ps1' ($sql + $app + @{ Database = $Setting.Database; AllLicensedUsers = $true }) }

    if ($History) {
        $n = if ($Days) { $Days } elseif (Has 'HistoryDays') { [int] $Setting.HistoryDays } else { 180 }
        $start = $Today.AddDays(-$n)
        Add 'Copilot interactions (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $Setting.Database; StartDate = $start; Finalize = $true })
        if ($lic) { Add 'Licence events (history)' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; StartDate = $start; Finalize = $true; Operations = $script:LicenceOperations }) }
        & $directory
        & $licences
        $build = $sql + @{ Database = $Setting.Database }
        if ($lic) { $build.LicenceDatabase = $lic; $build.LicenceEventDatabases = @($lic) } else { $build.SkipLicenceDatabase = $true }
        if (Has 'Dashboard') { $build.Dashboard = $true }
        Add 'Build everything' 'Install-CopilotAudit.ps1' $build
    }
    else {
        & $directory
        & $licences
        if ($lic) { Add 'Licence events' 'Invoke-CopilotAuditBackfill.ps1' ($sql + $app + @{ Database = $lic; Incremental = $true; Operations = $script:LicenceOperations }) }
        $inter = $sql + $app + @{ Database = $Setting.Database; Incremental = $true }
        if ($lic) { $inter.SessionLicenceDatabase = $lic }
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

function Test-HistoryLoaded {
    param([System.Collections.IDictionary] $Setting)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $Setting.SqlServer; $b['Initial Catalog'] = $Setting.Database; $b['Integrated Security'] = $true
    $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] ($Setting.Contains('TrustServerCertificate') -and $Setting.TrustServerCertificate)
    $c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    $c.Open()
    try {
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'audit.CopilotAudit', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM audit.CopilotAudit) THEN 1 ELSE 0 END"
        [bool] $cmd.ExecuteScalar()
    }
    finally { $c.Dispose() }
}

function Invoke-Main {
    $s = Get-PipelineSetting $SettingsPath
    $logDir = if ($s.Contains('LogPath') -and $s.LogPath) { $s.LogPath } else { Join-Path $PSScriptRoot 'logs' }
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $log = Join-Path $logDir ("{0}-{1:yyyy-MM-dd}.log" -f $(if ($History) { 'history' } else { 'nightly' }), (Get-Date))
    $keep = if ($s.Contains('LogRetentionDays') -and $s.LogRetentionDays) { [int] $s.LogRetentionDays } else { 90 }
    Get-ChildItem -LiteralPath $logDir -Filter '*.log' | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$keep) | Remove-Item -ErrorAction SilentlyContinue

    if (-not $History -and -not (Test-HistoryLoaded $s)) {
        "$(Get-Date -Format s) No Copilot records in $($s.Database) yet. Load the history first: .\Invoke-CopilotPipeline.ps1 -History" |
            Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
        exit 1
    }
    foreach ($step in (Get-PipelineStep -Setting $s -History:$History -Days $Days)) {
        $code = Invoke-PipelineStep -Step $step -Log $log
        if ($code -ne 0) {
            "$(Get-Date -Format s) $($step.Name) failed (exit code $code). Run the same command again to resume." |
                Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
            exit 1
        }
    }
    "$(Get-Date -Format s) Done." | Tee-Object -FilePath $log -Append | Write-Information -InformationAction Continue
    if ($History) { Write-Information 'History loaded and built. The nightly task keeps it current from tonight.' -InformationAction Continue }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
