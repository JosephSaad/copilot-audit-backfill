<#
    Rebuilds the walkthrough database (CgwWalkthrough) from scratch so capture.py always starts from the same state:
    the demo institution, sample requests and the Copilot seat demo, with BSAM as a new LOB (no reservation, no seat admin).
    Keeps the existing SQL logins and the settings file written by the first deploy.
#>
param([string] $SqlServer = 'localhost', [string] $Database = 'CgwWalkthrough')
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
sqlcmd -S $SqlServer -E -C -b -Q "IF DB_ID('$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END" | Out-Null
$settings = Join-Path $env:TEMP 'claude\CgwWalk.settings.json'
& pwsh -NoProfile -File (Join-Path $repo 'src\Governance.Database\Deploy-Database.ps1') -SqlServer $SqlServer -Database $Database -CreateSqlLogins -LocalSettingsPath $settings -TrustServerCertificate | Select-Object -Last 1
& dotnet run --project (Join-Path $repo 'tools\Governance.Seed') -- --database $Database --sample-requests | Select-Object -Last 1
sqlcmd -S $SqlServer -E -C -I -b -d $Database -Q @"
SET NOCOUNT ON;
DECLARE @fo int = (SELECT FundingOwnerId FROM core.FundingOwner WHERE Code = 'BSAM');
UPDATE sec.RoleAssignment SET EffectiveTo = DATEADD(DAY, -1, CAST(SYSUTCDATETIME() AS date))
WHERE RoleCode = 'LOB_SEAT_ADMIN' AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @fo;
DELETE FROM core.SeatReservation WHERE FundingOwnerId = @fo;
"@
"walkthrough database ready"
