<#
.SYNOPSIS
    Takes tests/schema-baseline.tsv again: the shape of the schema this checkout installs, which the next release
    must stay compatible with. Run it when cutting a release, after the changelog names the release.

.DESCRIPTION
    From 2.6.3 a release may add to the schema and may not take away from it (tests/SchemaCompatibility.ps1 has the
    rules). This installs the checkout into two new databases as a database in use is made (the dashboard, the first
    load's tables, a reporting account: Install-SchemaForSnapshot), reads their shape, and compares it with the baseline in place. When nothing in the baseline is broken it writes the new one,
    under the release CHANGELOG.md names last, and drops the two databases. When something is broken it lists what
    and writes nothing: the schema change has to be made compatible, not the baseline made to fit.

    tests/Test-SchemaCompatibility.ps1 fails a release whose baseline was not taken again, and section 20 of
    tests/Test-SqlIntegration.ps1 fails one whose baseline is not what the release installs.

.PARAMETER Check
    Compare and report; write nothing.

.EXAMPLE
    pwsh tools/dev/schema/Update-SchemaBaseline.ps1 -SqlServer localhost

.EXAMPLE
    pwsh tools/dev/schema/Update-SchemaBaseline.ps1 -SqlServer localhost -Check
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $SqlServer = 'localhost',
    [pscredential] $SqlCredential,
    [switch] $TrustServerCertificate,
    [string] $Database = 'CopilotSchemaBaseline',
    [switch] $Check
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
. (Join-Path $root 'tests' 'SchemaCompatibility.ps1')
$baselinePath = Join-Path $root 'tests' 'schema-baseline.tsv'
$governancePath = Join-Path $root 'tests' 'schema-baseline-governance.tsv'
$release = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md')), '(?m)^## \[(\d+\.\d+\.\d+)\]').Groups[1].Value
if (-not $release) { throw 'CHANGELOG.md names no release.' }

$licenseDatabase = "${Database}Lic"
function New-ConnString([string] $Catalog) {
    $csb = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $csb['Data Source'] = $SqlServer; $csb['Initial Catalog'] = $Catalog
    if ($SqlCredential) { $csb['User ID'] = $SqlCredential.UserName; $csb['Password'] = $SqlCredential.GetNetworkCredential().Password }
    else { $csb['Integrated Security'] = $true }
    if ($TrustServerCertificate -or -not $SqlCredential) { $csb['TrustServerCertificate'] = $true }
    $csb.ConnectionString
}
function Remove-Database([string] $Name) {
    $conn = [System.Data.SqlClient.SqlConnection]::new((New-ConnString 'master'))
    try {
        $conn.Open(); $cmd = $conn.CreateCommand()
        $cmd.CommandText = "IF DB_ID(N'$Name') IS NOT NULL BEGIN ALTER DATABASE [$Name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Name]; END;"
        [void] $cmd.ExecuteNonQuery()
    }
    finally { $conn.Dispose() }
}

$install = @{ SqlServer = $SqlServer; Database = $Database; LicenseDatabase = $licenseDatabase; TrustServerCertificate = $true }
if ($SqlCredential) { $install.SqlCredential = $SqlCredential }
Remove-Database $Database; Remove-Database $licenseDatabase
try {
    Write-Host "Installing this checkout into $Database and $licenseDatabase on $SqlServer..."
    Install-SchemaForSnapshot -Root $root -Install $install -ConnectionString (New-ConnString $Database)
    [string[]] $now = @(Get-SchemaSnapshot -ConnectionString (New-ConnString $Database) -Database copilot) + @(Get-SchemaSnapshot -ConnectionString (New-ConnString $licenseDatabase) -Database license)
    [array]::Sort($now, [StringComparer]::Ordinal)
}
finally { Remove-Database $Database; Remove-Database $licenseDatabase }

if (Test-Path -LiteralPath $baselinePath) {
    $old = Read-SchemaBaseline -Path $baselinePath
    $breaks = @(Compare-SchemaSnapshot -Baseline $old.Lines -Current $now)
    if ($breaks.Count) {
        Write-Host "This checkout breaks the schema of $($old.Release) in $($breaks.Count) place(s):" -ForegroundColor Red
        $breaks | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host 'Nothing was written. Keep what was there and add beside it.' -ForegroundColor Red
        exit 1
    }
    $added = @($now | Where-Object { $_ -cnotin $old.Lines }).Count
    Write-Host "Compatible with $($old.Release): $added line(s) added or widened, nothing taken away."
}

<# The Governance Workbench's database, a baseline of its own: installed by its Deploy-Database.ps1 (Windows sign-in to
   SQL Server only), read the same way, held to the same rules. #>
$governanceDatabase = "${Database}Gov"
$gov = $null
if ($SqlCredential) { Write-Warning 'The Workbench database is installed with Windows sign-in only: its baseline was not taken. Run this again without -SqlCredential.' }
else {
    Remove-Database $governanceDatabase
    try {
        Write-Host "Installing the Workbench's database into $governanceDatabase..."
        $deploy = & pwsh -NoProfile -File (Join-Path $root 'governance' 'src' 'Governance.Database' 'Deploy-Database.ps1') -SqlServer $SqlServer -Database $governanceDatabase -TrustServerCertificate 2>&1
        if ($LASTEXITCODE) { $deploy | Select-Object -Last 10 | ForEach-Object { Write-Host "  $_" }; throw 'The Workbench database did not install.' }
        [string[]] $gov = @(Get-SchemaSnapshot -ConnectionString (New-ConnString $governanceDatabase) -Database governance)
        [array]::Sort($gov, [StringComparer]::Ordinal)
    }
    finally { Remove-Database $governanceDatabase }
    if (Test-Path -LiteralPath $governancePath) {
        $oldGov = Read-SchemaBaseline -Path $governancePath
        $govBreaks = @(Compare-SchemaSnapshot -Baseline $oldGov.Lines -Current $gov)
        if ($govBreaks.Count) {
            Write-Host "This checkout breaks the Workbench database of $($oldGov.Release) in $($govBreaks.Count) place(s):" -ForegroundColor Red
            $govBreaks | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            Write-Host 'Nothing was written. Keep what was there and add beside it.' -ForegroundColor Red
            exit 1
        }
        Write-Host "Workbench database compatible with $($oldGov.Release): $(@($gov | Where-Object { $_ -cnotin $oldGov.Lines }).Count) line(s) added or widened, nothing taken away."
    }
}
if ($Check) { return }

$header = @(
    "# release $release"
    '# The shape of the schema this release installs: with the dashboard, the first load''s tables and a reporting account.'
    '# The next release must stay compatible with it; tests/SchemaCompatibility.ps1 has the rules and the columns.'
    '# Written by tools/dev/schema/Update-SchemaBaseline.ps1. Do not edit it by hand.'
)
[System.IO.File]::WriteAllText($baselinePath, (($header + $now) -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($now.Count) lines to tests/schema-baseline.tsv for release $release." -ForegroundColor Green
if ($gov) {
    $govHeader = @(
        "# release $release"
        '# The shape of the Governance Workbench''s database (its own, beside the Copilot one) as this release installs it.'
        '# The next release must stay compatible with it; tests/SchemaCompatibility.ps1 has the rules and the columns.'
        '# Written by tools/dev/schema/Update-SchemaBaseline.ps1. Do not edit it by hand.'
    )
    [System.IO.File]::WriteAllText($governancePath, (($govHeader + $gov) -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote $($gov.Count) lines to tests/schema-baseline-governance.tsv for release $release." -ForegroundColor Green
}
