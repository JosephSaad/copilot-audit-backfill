<#
.SYNOPSIS
    Creates or upgrades the CopilotGovernance database: runs every script in Scripts\ in name order
    (each is safe to rerun) and maps the application principals to app_web and app_worker.

.DESCRIPTION
    Production: the web and worker gMSAs connect with Windows authentication. Pass them with
    -WebPrincipal 'DOMAIN\gmsa-cgw-web$' -WorkerPrincipal 'DOMAIN\gmsa-cgw-worker$'.
    Local test: -CreateSqlLogins makes SQL logins cgw_web_<db> and cgw_worker_<db> with random passwords
    and writes the two connection strings to -LocalSettingsPath (git-ignored). Run as a deployment
    principal (db_owner on the database); no application identity gets db_owner.

.EXAMPLE
    pwsh -File src\Governance.Database\Deploy-Database.ps1 -SqlServer localhost -Database CopilotGovernanceTest -CreateSqlLogins -LocalSettingsPath config\appsettings.Local.json
#>
[CmdletBinding()]
param(
    [string] $SqlServer = 'localhost',
    [Parameter(Mandatory)] [string] $Database,
    [string] $WebPrincipal = '',
    [string] $WorkerPrincipal = '',
    [string] $ReaderPrincipal = '',
    [switch] $CreateSqlLogins,
    [string] $LocalSettingsPath = '',
    [switch] $TrustServerCertificate = $true
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Database -notmatch '^[A-Za-z][A-Za-z0-9_]{0,100}$') { throw "Database name '$Database' must be letters, digits and underscores." }
$trust = [string[]]@()
if ($TrustServerCertificate) { $trust = [string[]]@('-C') }

function Invoke-SqlFile([string] $db, [string] $file) {
    $out = & sqlcmd -S $SqlServer -E @trust -d $db -b -I -i $file 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed on $(Split-Path $file -Leaf):`n$($out -join "`n")" }
}
function Invoke-SqlText([string] $db, [string] $text) {
    $tmp = [IO.Path]::Combine([IO.Path]::GetTempPath(), "cgw-$([guid]::NewGuid().ToString('n')).sql")
    try {
        [IO.File]::WriteAllText($tmp, $text)
        Invoke-SqlFile $db $tmp
    }
    finally { [IO.File]::Delete($tmp) }
}
function New-Password {
    $bytes = [byte[]]::new(24)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return 'Cgw!' + ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x')
}
function Quote([string] $name) { return '[' + $name.Replace(']', ']]') + ']' }
function Literal([string] $s) { return "N'" + $s.Replace("'", "''") + "'" }

Write-Host "== Database $Database on $SqlServer"
Invoke-SqlText 'master' "IF DB_ID($(Literal $Database)) IS NULL CREATE DATABASE $(Quote $Database);"
Invoke-SqlText 'master' "ALTER DATABASE $(Quote $Database) SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;"

$scripts = Get-ChildItem (Join-Path $PSScriptRoot 'Scripts') -Filter '*.sql' | Sort-Object Name
foreach ($s in $scripts) {
    Write-Host "   $($s.Name)"
    Invoke-SqlFile $Database $s.FullName
}

function Grant-Principal([string] $login, [string] $role, [bool] $windows) {
    $user = $login
    $create = if ($windows) { "CREATE LOGIN $(Quote $login) FROM WINDOWS" } else { $null }
    $sql = @"
IF SUSER_ID($(Literal $login)) IS NULL $(if ($create) { $create } else { "THROW 50000, 'Login missing', 1" });
IF DATABASE_PRINCIPAL_ID($(Literal $user)) IS NULL CREATE USER $(Quote $user) FOR LOGIN $(Quote $login);
ALTER ROLE $role ADD MEMBER $(Quote $user);
"@
    Invoke-SqlText $Database $sql
    Write-Host "   $login -> $role"
}

if ($WebPrincipal) { Grant-Principal $WebPrincipal 'app_web' $true }
if ($WorkerPrincipal) { Grant-Principal $WorkerPrincipal 'app_worker' $true }
if ($ReaderPrincipal) { Grant-Principal $ReaderPrincipal 'gov_reader' $true }

if ($CreateSqlLogins) {
    if (-not $LocalSettingsPath) { throw '-CreateSqlLogins needs -LocalSettingsPath for the connection strings.' }
    $suffix = $Database.ToLowerInvariant()
    $conn = @{}
    foreach ($pair in @(@("cgw_web_$suffix", 'app_web', 'Governance'), @("cgw_worker_$suffix", 'app_worker', 'GovernanceWorker'))) {
        $login, $role, $key = $pair
        $pw = New-Password
        $sql = @"
IF SUSER_ID($(Literal $login)) IS NULL CREATE LOGIN $(Quote $login) WITH PASSWORD = $(Literal $pw), CHECK_POLICY = ON, DEFAULT_DATABASE = $(Quote $Database);
ELSE ALTER LOGIN $(Quote $login) WITH PASSWORD = $(Literal $pw);
"@
        Invoke-SqlText 'master' $sql
        Invoke-SqlText $Database @"
IF DATABASE_PRINCIPAL_ID($(Literal $login)) IS NULL CREATE USER $(Quote $login) FOR LOGIN $(Quote $login);
ALTER ROLE $role ADD MEMBER $(Quote $login);
"@
        $conn[$key] = "Server=$SqlServer;Database=$Database;User ID=$login;Password=$pw;TrustServerCertificate=True;Encrypt=True;Application Name=CopilotGovernance-$role"
        Write-Host "   $login -> $role (SQL login, local test only)"
    }
    $full = if ([IO.Path]::IsPathRooted($LocalSettingsPath)) { $LocalSettingsPath } else { Join-Path (Get-Location) $LocalSettingsPath }
    $settings = if (Test-Path $full) { Get-Content $full -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
    if (-not $settings.ContainsKey('ConnectionStrings')) { $settings['ConnectionStrings'] = @{} }
    $settings['ConnectionStrings']['Governance'] = $conn['Governance']
    $settings['ConnectionStrings']['GovernanceWorker'] = $conn['GovernanceWorker']
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    $settings | ConvertTo-Json -Depth 6 | Set-Content $full -Encoding utf8
    Write-Host "   connection strings written to $full"
}
Write-Host '== Done'
