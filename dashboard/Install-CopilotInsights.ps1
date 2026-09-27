<#
.SYNOPSIS
    Installs or updates the Copilot Insights dashboard on IIS (Windows Server 2022 or later).

.DESCRIPTION
    Idempotent: run it again to update the site files or change its settings.

      1. Installs the IIS role services the site needs (Windows authentication,
         static content, logging, request filtering, the management console).
      2. Checks for the ASP.NET Core 10 Hosting Bundle, installing it from
         -HostingBundlePath when given.
      3. Publishes the app (from source, when -PackagePath is a folder holding
         CopilotInsights.csproj) or copies a published folder or zip into
         -PhysicalPath, keeping appsettings.Production.json.
      4. Writes appsettings.Production.json: the connection string and the
         bootstrap administrators (-AdminPrincipal), the first people who can
         open Admin and grant everyone else.
      5. Creates the app pool (no managed code, the given identity) and the
         site, with an HTTPS binding when -CertificateThumbprint is given.
      6. Turns Windows authentication on and anonymous authentication off.
      7. With -GrantDatabaseAccess (you need to be able to create logins):
         installs or upgrades the dashboard's dash schema in the database,
         builds its org chart and rollups, and grants the app-pool account
         what it needs. Without it, writes the grants for a DBA instead.

    The dashboard reads the database the pipeline fills (Install-CopilotAudit.ps1).
    The top of the organisation is found in the org chart; -OrgTopUpn or
    Admin > Access overrides it. Nothing needs setting by hand in SQL.

.PARAMETER PackagePath
    The app to install: a folder holding CopilotInsights.csproj (published with
    dotnet publish, which needs the .NET 10 SDK), a published folder, or a zip of one.

.PARAMETER AdminPrincipal
    Bootstrap administrators: UPNs, DOMAIN\user or, better, DOMAIN\Group. They are
    administrators whatever the database says; keep the list short.

.PARAMETER AppPoolIdentity
    The account the site runs as, and signs in to SQL Server as. A group managed
    service account (DOMAIN\name$, no password) is best. Omitted: the app pool's
    own identity, which reaches a remote SQL Server as the machine account (DOMAIN\HOST$).

.EXAMPLE
    .\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer sql01 -Database CopilotAudit `
        -AdminPrincipal 'CONTOSO\Copilot-Insights-Admins' -AppPoolIdentity 'CONTOSO\gmsa-insights$' `
        -HostName insights.contoso.com -CertificateThumbprint 3F2A... -GrantDatabaseAccess
#>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [string[]] $AdminPrincipal = @(),
    [string] $SiteName = 'CopilotInsights',
    [string] $PhysicalPath = 'C:\inetpub\CopilotInsights',
    [string] $HostName = '',
    [int] $Port = 0,
    [string] $CertificateThumbprint,
    [string] $AppPoolIdentity,
    [pscredential] $AppPoolCredential,
    [string] $HostingBundlePath,
    [switch] $TrustServerCertificate,
    [switch] $GrantDatabaseAccess,
    [string] $OrgTopUpn,
    [switch] $SkipFeatureInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

# ---- 0. Where we are ------------------------------------------------------------------------
# The IIS:\ drive exists only in Windows PowerShell; PowerShell 7 loads WebAdministration without it.
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this in Windows PowerShell 5.1 (powershell.exe), not PowerShell 7: the IIS provider it uses is not available there.' }
$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -eq 1) { throw 'This installs on Windows Server (2022 or later); for a workstation, run the app with dotnet run.' }
if ([int] $os.BuildNumber -lt 20348) { Write-Warning "Windows Server 2022 (build 20348) or later is supported; this is build $($os.BuildNumber)." }
if ($AppPoolIdentity -and -not $AppPoolIdentity.EndsWith('$') -and -not $AppPoolCredential) {
    throw "-AppPoolIdentity $AppPoolIdentity is not a managed service account (DOMAIN\name$): give its password with -AppPoolCredential."
}

# ---- 1. IIS -----------------------------------------------------------------------------------
if (-not $SkipFeatureInstall) {
    Write-Step 'IIS role services'
    $features = 'Web-Server', 'Web-Default-Doc', 'Web-Static-Content', 'Web-Http-Errors', 'Web-Http-Logging',
                'Web-Filtering', 'Web-Windows-Auth', 'Web-Mgmt-Console'
    $r = Install-WindowsFeature -Name $features
    if ($r.RestartNeeded -eq 'Yes') { Write-Warning 'Windows asks for a restart to finish installing IIS. Restart, then run this again.' }
}
Import-Module WebAdministration

# ---- 2. ASP.NET Core Hosting Bundle -------------------------------------------------------------
Write-Step 'ASP.NET Core 10 Hosting Bundle'
function Test-HostingBundle {
    $module = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    $fw = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.AspNetCore.App'
    (Test-Path $module) -and (Test-Path $fw) -and [bool] (Get-ChildItem $fw -Directory | Where-Object Name -like '10.*')
}
if (-not (Test-HostingBundle)) {
    if (-not $HostingBundlePath) {
        throw 'The ASP.NET Core 10 Hosting Bundle is not installed. Download "Hosting Bundle" for .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0 and pass it with -HostingBundlePath, or install it and run this again.'
    }
    $p = Start-Process -FilePath $HostingBundlePath -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "The Hosting Bundle installer failed with exit code $($p.ExitCode)." }
    if (-not (Test-HostingBundle)) { throw 'The Hosting Bundle installed, but the ASP.NET Core module or the 10.x runtime is still missing.' }
    & net stop was /y | Out-Null; & net start w3svc | Out-Null
}

# ---- 3. The files -----------------------------------------------------------------------------
Write-Step "Site files in $PhysicalPath"
$staging = Join-Path ([IO.Path]::GetTempPath()) ("CopilotInsights-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $staging | Out-Null
try {
    $src = (Resolve-Path $PackagePath).Path
    if ((Get-Item $src).PSIsContainer -and (Test-Path (Join-Path $src 'CopilotInsights.csproj'))) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Publishing from source needs the .NET 10 SDK; or pass a published folder or zip.' }
        & dotnet publish (Join-Path $src 'CopilotInsights.csproj') -c Release -o $staging --nologo
        if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
    }
    elseif ($src -like '*.zip') { Expand-Archive -Path $src -DestinationPath $staging -Force }
    else { Copy-Item -Path (Join-Path $src '*') -Destination $staging -Recurse -Force }
    if (-not (Test-Path (Join-Path $staging 'CopilotInsights.dll'))) { throw "No CopilotInsights.dll in ${PackagePath}: is it the published app?" }

    New-Item -ItemType Directory -Force $PhysicalPath, (Join-Path $PhysicalPath 'logs') | Out-Null
    # Stop the site while its files change: app_offline.htm makes the module unload the app.
    Set-Content (Join-Path $PhysicalPath 'app_offline.htm') '<p>Copilot Insights is being updated. Try again in a minute.</p>'
    Start-Sleep -Seconds 2
    & robocopy $staging $PhysicalPath /MIR /XF appsettings.Production.json app_offline.htm /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the site files failed (robocopy $LASTEXITCODE)." }
}
finally { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }

# ---- 4. appsettings.Production.json -----------------------------------------------------------
Write-Step 'Settings'
$settingsPath = Join-Path $PhysicalPath 'appsettings.Production.json'
function Set-Prop($Object, [string] $Name, $Value) {
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value } else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject] @{} }
if (-not $settings.PSObject.Properties['Insights']) { Set-Prop $settings 'Insights' ([pscustomobject] @{}) }
$trust = if ($TrustServerCertificate) { 'true' } else { 'false' }
Set-Prop $settings.Insights 'ConnectionString' "Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
if ($AdminPrincipal.Count -gt 0) { Set-Prop $settings.Insights 'BootstrapAdmins' @($AdminPrincipal) }
elseif (-not $settings.Insights.PSObject.Properties['BootstrapAdmins'] -or @($settings.Insights.BootstrapAdmins).Count -eq 0) {
    Write-Warning 'No -AdminPrincipal: nobody can open Admin until a row is added to dash.RoleGrant (see the RUNBOOK).'
}
Set-Prop $settings.Insights 'AllowViewAs' $false
$settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding utf8

# ---- 5. App pool and site ---------------------------------------------------------------------
Write-Step "App pool and site '$SiteName'"
$poolPath = "IIS:\AppPools\$SiteName"
if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $SiteName | Out-Null }
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''
Set-ItemProperty $poolPath -Name managedPipelineMode -Value 'Integrated'
Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
if ($AppPoolIdentity) {
    $pw = if ($AppPoolCredential) { $AppPoolCredential.GetNetworkCredential().Password } else { '' }
    Set-ItemProperty $poolPath -Name processModel -Value @{ userName = $AppPoolIdentity; password = $pw; identityType = 3 }
    $poolAccount = $AppPoolIdentity
}
else {
    Set-ItemProperty $poolPath -Name processModel.identityType -Value 4   # ApplicationPoolIdentity
    $poolAccount = "IIS APPPOOL\$SiteName"
}

$https = [bool] $CertificateThumbprint
if (-not $Port) { $Port = if ($https) { 443 } else { 80 } }
if (-not (Get-Website -Name $SiteName)) {
    New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $SiteName -Port $Port -HostHeader $HostName -Ssl:$https | Out-Null
}
else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $SiteName
}
if ($https) {
    $proto = 'https'
    if (-not (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName)) {
        New-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName -SslFlags $(if ($HostName) { 1 } else { 0 })
    }
    $cert = Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "No certificate $CertificateThumbprint in LocalMachine\My." }
    (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName).AddSslCertificate($CertificateThumbprint, 'My')
}
else {
    $proto = 'http'
    Write-Warning 'No -CertificateThumbprint: the site answers on HTTP only. Windows authentication still protects it, but use HTTPS in production.'
}

# ---- 6. Authentication ------------------------------------------------------------------------
Write-Step 'Windows authentication on, anonymous off'
Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $false
Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name enabled -Value $true
# Kerberos for a host name registered on the app-pool account (the SPN below) needs the pool's own credentials.
Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name useAppPoolCredentials -Value ([bool] $AppPoolIdentity)

# The app pool writes its logs; everyone else only reads the site.
$acl = Get-Acl (Join-Path $PhysicalPath 'logs')
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $poolAccount, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl (Join-Path $PhysicalPath 'logs') $acl
$acl = Get-Acl $PhysicalPath
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $poolAccount, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl $PhysicalPath $acl

Remove-Item (Join-Path $PhysicalPath 'app_offline.htm') -ErrorAction SilentlyContinue
Start-WebAppPool -Name $SiteName -ErrorAction SilentlyContinue
Restart-WebAppPool -Name $SiteName -ErrorAction SilentlyContinue

# ---- 7. Database access -----------------------------------------------------------------------
Write-Step 'Database access'
$sqlLogin = if ($AppPoolIdentity) { $AppPoolIdentity }
            elseif ($SqlServer -match '^(\.|localhost|\(local\)|' + [regex]::Escape($env:COMPUTERNAME) + ')(\\|,|$)') { $poolAccount }
            else { "$env:USERDOMAIN\$env:COMPUTERNAME`$" }
$grants = @"
-- Copilot Insights: the app-pool account $sqlLogin reads the reports and keeps the dash tables.
USE [master];
IF SUSER_ID(N'$sqlLogin') IS NULL CREATE LOGIN [$sqlLogin] FROM WINDOWS;
USE [$Database];
IF USER_ID(N'$sqlLogin') IS NULL CREATE USER [$sqlLogin] FOR LOGIN [$sqlLogin];
GRANT SELECT ON SCHEMA::bi TO [$sqlLogin];
GRANT SELECT ON SCHEMA::dash TO [$sqlLogin];
GRANT INSERT, UPDATE, DELETE ON dash.Setting TO [$sqlLogin];
GRANT INSERT, UPDATE, DELETE ON dash.RoleGrant TO [$sqlLogin];
GRANT INSERT, UPDATE, DELETE ON dash.AgentAlias TO [$sqlLogin];
GRANT INSERT, UPDATE, DELETE ON dash.SensitivityLabel TO [$sqlLogin];
GRANT INSERT ON dash.AuditLog TO [$sqlLogin];
GRANT EXECUTE ON dash.usp_RefreshDashboard TO [$sqlLogin];
GRANT SELECT, INSERT, UPDATE ON audit.SessionAppCategory TO [$sqlLogin];
-- Admin > Health reads the pipeline's run logs, where they exist.
DECLARE @t sysname, @sql nvarchar(400);
DECLARE t CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM (VALUES (N'CopilotLoad'), (N'CopilotCsvImport'), (N'CopilotSessionRun'), (N'UserDirectory')) AS v(name)
    WHERE OBJECT_ID(N'audit.' + name, N'U') IS NOT NULL;
OPEN t; FETCH NEXT FROM t INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'GRANT SELECT ON audit.' + QUOTENAME(@t) + N' TO ' + QUOTENAME(N'$sqlLogin') + N';';
    EXEC (@sql);
    FETCH NEXT FROM t INTO @t;
END;
"@
$grantsFile = Join-Path $PhysicalPath 'Grant-CopilotInsightsAccess.sql'
Set-Content $grantsFile $grants -Encoding utf8
if ($GrantDatabaseAccess) {
    # The dash schema first: the grants below name its tables.
    $repoSql = Join-Path (Split-Path $PSScriptRoot -Parent) 'sql'
    $dbConn = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    $dbConn.Open()
    try {
        foreach ($f in 'Create-CopilotDashboardSchema.sql', 'Create-CopilotDashboardProcedure.sql') {
            $text = Get-Content -LiteralPath (Join-Path $repoSql $f) -Raw
            foreach ($batch in [regex]::Split($text, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
                if ($batch.Trim()) { $c = $dbConn.CreateCommand(); $c.CommandText = $batch; $c.CommandTimeout = 0; [void] $c.ExecuteNonQuery() }
            }
        }
        if ($OrgTopUpn) {
            $c = $dbConn.CreateCommand(); $c.CommandText = "UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = 'OrgTopUpn';"
            [void] $c.Parameters.AddWithValue('@v', $OrgTopUpn.Trim()); [void] $c.Parameters.AddWithValue('@by', "$env:USERDOMAIN\$env:USERNAME"); [void] $c.ExecuteNonQuery()
        }
        $c = $dbConn.CreateCommand(); $c.CommandText = "IF OBJECT_ID(N'bi.FactInteraction', N'U') IS NOT NULL EXEC dash.usp_RefreshDashboard;"; $c.CommandTimeout = 0; [void] $c.ExecuteNonQuery()
        Write-Host "Dashboard schema installed in $Database and its rollups built."
    }
    finally { $dbConn.Dispose() }
    $conn = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServer;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    $conn.Open()
    try { $cmd = $conn.CreateCommand(); $cmd.CommandText = $grants; $cmd.CommandTimeout = 120; [void] $cmd.ExecuteNonQuery() }
    finally { $conn.Dispose() }
    Write-Host "Granted $sqlLogin access to $Database."
}
else {
    Write-Host "Database grants written to $grantsFile. Have a DBA run them, or run this again with -GrantDatabaseAccess."
}

$url = "{0}://{1}{2}/" -f $proto, $(if ($HostName) { $HostName } else { $env:COMPUTERNAME }), $(if (($https -and $Port -eq 443) -or (-not $https -and $Port -eq 80)) { '' } else { ":$Port" })
Write-Host ''
Write-Host "Copilot Insights is at $url" -ForegroundColor Green
Write-Host 'Next: sign in as a bootstrap administrator, open Admin > Access, set the top of the organisation, and grant roles.'
if ($HostName) { Write-Host "For Kerberos sign-in on $HostName, register its SPN on the app-pool account: setspn -S HTTP/$HostName $(if ($AppPoolIdentity) { $AppPoolIdentity } else { "$env:USERDOMAIN\$env:COMPUTERNAME`$" })" }
