<#
.SYNOPSIS
    Downloads the PowerShell modules Invoke-CopilotAuditBackfill.ps1 needs into a
    'Modules' folder, so the tool can run on a server with no PSGallery access.

.DESCRIPTION
    Run this ONCE on any machine that can reach the PowerShell Gallery, then copy
    the whole tool folder - including the 'Modules' folder this creates - to the
    locked server. Invoke-CopilotAuditBackfill.ps1 puts that folder at the front
    of PSModulePath automatically, so nothing has to be installed on the server
    and nothing is written outside the folder.

    Microsoft.Graph.Authentication is the only external module. Everything else
    the tool uses - including System.Data.SqlClient - ships inside PowerShell 7.

    The package is platform-agnostic: it carries the Windows native broker
    binaries (msalruntime.dll for x64, plus arm64 and x86) whatever OS you run
    this on. Running it on Windows x64 is still the closest match to the target
    and is what -Verify checks against.

.PARAMETER Path
    Where to create the Modules folder. Defaults to the tool's own folder, which
    is what the script expects at run time.

.PARAMETER RequiredVersion
    Module version to pin. Defaults to the version this release was tested with.

.PARAMETER Force
    Replace an existing Modules folder instead of stopping.

.EXAMPLE
    .\tools\Save-CopilotAuditModules.ps1
    Creates .\Modules\Microsoft.Graph.Authentication\<version>\...

.EXAMPLE
    .\tools\Save-CopilotAuditModules.ps1 -RequiredVersion 2.40.0 -Force
#>
[CmdletBinding()]
param(
    [string] $Path,
    [string] $RequiredVersion = '2.40.0',
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ModuleNames = @('Microsoft.Graph.Authentication')

if (-not $Path) { $Path = Split-Path $PSScriptRoot -Parent }
$modulesDir = Join-Path $Path 'Modules'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "This needs PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}

if (Test-Path -LiteralPath $modulesDir) {
    if (-not $Force) {
        throw "$modulesDir already exists. Re-run with -Force to replace it."
    }
    Write-Host "Removing the existing $modulesDir ..." -ForegroundColor Yellow
    Remove-Item -LiteralPath $modulesDir -Recurse -Force
}

New-Item -ItemType Directory -Path $modulesDir -Force | Out-Null

foreach ($name in $ModuleNames) {
    Write-Host "Saving $name $RequiredVersion ..." -ForegroundColor Cyan
    Save-Module -Name $name -RequiredVersion $RequiredVersion -Path $modulesDir `
                -Repository PSGallery -ErrorAction Stop
}

# ---------------------------------------------------------------------------
# Prove the saved copy actually resolves, with the machine's own module paths
# taken out of the picture - which is the situation on the locked server.
# ---------------------------------------------------------------------------

Write-Host 'Verifying the saved modules resolve on their own ...' -ForegroundColor Cyan

# Point PSModulePath at the bundle alone - the situation on the locked server -
# and confirm the cmdlets resolve AND come from this folder rather than from a
# copy already installed on this machine.
$savedModulePath = $env:PSModulePath
try {
    $env:PSModulePath = $modulesDir

    foreach ($name in $ModuleNames) {
        $available = Get-Module -ListAvailable -Name $name |
                     Where-Object { $_.Version.ToString() -eq $RequiredVersion }
        if (-not $available) {
            throw "$name $RequiredVersion is not discoverable under $modulesDir after saving it."
        }
        Import-Module -Name $name -RequiredVersion $RequiredVersion -Force -ErrorAction Stop
    }

    foreach ($c in 'Connect-MgGraph', 'Invoke-MgGraphRequest', 'Get-MgContext', 'Disconnect-MgGraph') {
        $cmd = Get-Command -Name $c -ErrorAction SilentlyContinue
        if (-not $cmd) { throw "$c did not resolve from the saved modules." }
        $base = $cmd.Module.ModuleBase
        if (-not $base -or -not $base.StartsWith($modulesDir, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$c resolved from '$base', not from the bundle at '$modulesDir'."
        }
    }
}
finally { $env:PSModulePath = $savedModulePath }

# ---------------------------------------------------------------------------
# Manifest, so the copy that reaches the server can be checked against this one.
# ---------------------------------------------------------------------------

$files = Get-ChildItem -LiteralPath $modulesDir -Recurse -File | Sort-Object FullName
$manifest = foreach ($f in $files) {
    [pscustomobject]@{
        Path   = $f.FullName.Substring($modulesDir.Length).TrimStart('\', '/') -replace '\\', '/'
        Sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        Bytes  = $f.Length
    }
}
$manifestPath = Join-Path $modulesDir 'MODULES-MANIFEST.csv'
$manifest | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8

$totalMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)

Write-Host ''
Write-Host "Saved to : $modulesDir"          -ForegroundColor Green
Write-Host "Modules  : $($ModuleNames -join ', ') $RequiredVersion"
Write-Host "Files    : $($files.Count) ($totalMb MB)"
Write-Host "Manifest : $manifestPath"
Write-Host "Verified : Connect-MgGraph resolves with PSModulePath set to the bundle alone." -ForegroundColor Green
Write-Host ''
Write-Host 'Now copy the whole tool folder, including Modules, to the server.'
Write-Host 'Invoke-CopilotAuditBackfill.ps1 picks it up automatically - nothing to install.'
