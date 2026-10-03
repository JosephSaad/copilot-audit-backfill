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

    Every file of the module that can run must carry a valid Authenticode
    signature from its publisher (Microsoft, and Json.NET for its one library)
    before the module is loaded here: the signature holds each file's hash, so a
    changed file fails. That check needs Windows (2.4.12).

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

function Get-UnsignedModuleFile {
    <#
        The files under a folder that can run (scripts, manifests, formats,
        libraries, catalogs) without a valid Authenticode signature from one of
        the module's publishers. 2.4.12: Save-Module checks no signature
        (PowerShellGet checks one only when it installs), and the module is
        imported below and shipped in the release's bundle, so a package changed
        on the gallery or on the way ran here and reached the server unnoticed.
    #>
    param(
        [Parameter(Mandatory)][string] $Path,
        [string[]] $Publisher = @('O=Microsoft Corporation', 'O=Json.NET (.NET Foundation)')
    )
    $kinds = '.ps1', '.psm1', '.psd1', '.ps1xml', '.dll', '.exe', '.cat'
    foreach ($f in Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object { $_.Extension -in $kinds }) {
        $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName
        $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }
        # The organization exactly, one part of the subject: 'O=Microsoft Corporation Ltd' is someone else.
        $parts = @($subject -split ',\s*')
        if ($sig.Status -ne 'Valid' -or -not @($Publisher | Where-Object { $_ -in $parts }).Count) {
            [pscustomobject]@{ Path = $f.FullName; Status = [string] $sig.Status; Signer = $subject }
        }
    }
}

# Dot-sourcing (tests) loads the function without saving anything.
if ($MyInvocation.InvocationName -eq '.') { return }

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

# Absolute from here on. -Path may be relative (the release workflow passes a
# relative stage folder), and the verification below compares it against a
# module's ModuleBase, which is always absolute.
$modulesDir = (Resolve-Path -LiteralPath $modulesDir).ProviderPath

foreach ($name in $ModuleNames) {
    Write-Host "Saving $name $RequiredVersion ..." -ForegroundColor Cyan
    Save-Module -Name $name -RequiredVersion $RequiredVersion -Path $modulesDir `
                -Repository PSGallery -ErrorAction Stop
}

# Before anything from the download runs: every file that can run is signed by its publisher, unchanged.
Write-Host 'Checking the saved files'' signatures ...' -ForegroundColor Cyan
if (-not (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $modulesDir -Recurse -Force
    throw 'Checking the modules'' signatures needs Windows (Get-AuthenticodeSignature). Run this on Windows.'
}
$unsigned = @(Get-UnsignedModuleFile -Path $modulesDir)
if ($unsigned.Count) {
    $unsigned | ForEach-Object { Write-Host ('  {0}: {1} {2}' -f $_.Path.Substring($modulesDir.Length), $_.Status, $_.Signer) -ForegroundColor Red }
    Remove-Item -LiteralPath $modulesDir -Recurse -Force
    throw "$($unsigned.Count) saved file(s) are not signed by the module's publisher, or were changed after signing. Nothing was kept."
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
