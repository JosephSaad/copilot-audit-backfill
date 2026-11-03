<#
.SYNOPSIS
    Installs or updates the Copilot Insights dashboard on IIS (Windows Server 2022 or later).

.DESCRIPTION
    Idempotent: run it again to update the site files or change its settings.
    A setting not given again keeps what the site has (2.4.12): the app pool's
    account, the database and TrustServerCertificate in the connection string,
    the tenants, the administrators, the folder and the bindings.

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
      8. Schedules the daily pre-cache (2.5.1): Invoke-InsightsPrecache.ps1,
         copied into the site's folder, builds every report page ahead at
         -PrecacheAt in -PrecacheTimeZone, 07:00 Eastern Time unless given.
      9. With -GovernanceDatabase, the Governance Workbench (/workbench): checks
         the server with governance\deploy\Test-WorkbenchPrerequisites.ps1
         before the site goes offline (SQL Server 2022 or later), writes
         ConnectionStrings:Governance and Notifications:WorkbenchBaseUrl, and,
         with -GrantDatabaseAccess, creates or upgrades the governance database
         (governance\src\Governance.Database\Deploy-Database.ps1), the site's
         account in app_web and -WorkerAccount in app_worker. With -WorkerServer,
         on that one server only: installs the worker as the Windows service
         CopilotGovernanceWorker (governance\deploy\Install-WorkbenchWorker.ps1)
         and schedules the daily ledger digest (Save-LedgerDigest.ps1).

    The site is offline from step 3 to step 6, and back online whether they
    succeed or fail. Its inputs are checked before it goes offline. -WhatIf
    checks the inputs, lists what would change, and changes nothing.

    The dashboard reads the database the pipeline fills (Install-CopilotAudit.ps1).
    The top of the organization is found in the org chart; -OrgTopUpn or
    Admin > Access overrides it. Nothing needs setting by hand in SQL.

.PARAMETER PackagePath
    The app to install: a folder holding CopilotInsights.csproj (published with
    dotnet publish, which needs the .NET 10 SDK), a published folder, or a zip of one.

.PARAMETER Tenant
    For a group with several Microsoft 365 tenants (tools\Add-CopilotTenant.ps1):
    each as Name=Database, on -SqlServer, the first being -Database's own name,
    such as 'Contoso=CopilotAudit','Fabrikam=CopilotAudit_Fabrikam'. Written to
    Insights:Tenants; the dashboard shows a tenant switcher and a group overview.
    With -GrantDatabaseAccess, the site's account is given access in each.

.PARAMETER AdminPrincipal
    Bootstrap administrators: UPNs, DOMAIN\user or, better, DOMAIN\Group. They are
    administrators whatever the database says; keep the list short.

.PARAMETER AppPoolIdentity
    The account the site runs as, and signs in to SQL Server as. A group managed
    service account (DOMAIN\name$, no password) is best. Omitted: the account the
    app pool runs as already, or for a new pool its own identity, which reaches a
    remote SQL Server as the machine account (DOMAIN\HOST$). An empty value
    (-AppPoolIdentity '') goes back to the pool's own identity.

.PARAMETER PrecacheAt
    When the scheduled task 'Copilot Insights pre-cache' builds every report page ahead each day, as HH:mm
    in -PrecacheTimeZone (2.5.1). Omitted: a task already scheduled is kept as it is, and a new one runs at
    07:00. An empty value (-PrecacheAt '') removes the task.

.PARAMETER PrecacheTimeZone
    The Windows time zone of -PrecacheAt. Default 'Eastern Standard Time': Eastern Time, EST in winter
    and EDT in summer, whatever zone the server's clock is in.

.PARAMETER ApplicationPath
    Installs the dashboard as an application under an existing site rather than as a site of its own: its virtual
    directory, such as /CopilotDashboard for https://apps.contoso.com/CopilotDashboard. -SiteName then names the site
    that holds it (which must exist, with its own bindings and certificate, left as they are), and the app pool is
    -AppPoolName, CopilotInsights unless given. Every address the dashboard writes starts with the path.

.PARAMETER AppPoolName
    The app pool, with -ApplicationPath: CopilotInsights unless given. Without -ApplicationPath, the pool is named after the site.

.PARAMETER Authentication
    How people sign in: Windows (IIS Windows authentication) or Entra (Microsoft Entra ID, docs\ENTRA-SIGN-IN.md).
    With Entra, IIS lets requests through anonymously and the site signs people in itself; it needs HTTPS, and
    -EntraTenantId, -EntraClientId and -EntraCertificateThumbprint the first time. Omitted: the site keeps the way it
    has, and a new site uses Windows.

.PARAMETER EntraTenantId
    The Entra ID directory (tenant) ID, with -Authentication Entra, or for grants to Entra groups with Windows sign-in.

.PARAMETER EntraClientId
    The app registration's application (client) ID.

.PARAMETER EntraCertificateThumbprint
    The thumbprint of the app registration's certificate, in Local Computer > Personal on every web server; the
    app-pool account is given read access to its private key.

.PARAMETER GovernanceDatabase
    Turns the Governance Workbench on: its database on -SqlServer, such as CopilotGovernance (SQL Server 2022 or
    later). Needs PowerShell 7 (pwsh) and sqlcmd on the server. Run again without it, the Workbench keeps its settings.

.PARAMETER SiteUrl
    The address people open the site at, such as https://insights.contoso.com or, behind a load balancer, its
    address; with -ApplicationPath, the path too. The links in Workbench notices start with it. Omitted: the address
    the site has already, else this server's binding.

.PARAMETER WorkerServer
    Installs the Workbench worker (a Windows service) and the daily ledger digest on this server. Give it on one
    server only; the other web servers run the site alone.

.PARAMETER WorkerAccount
    The gMSA the worker runs as (DOMAIN\name$), made a member of app_worker in the governance database and given read
    on dash.UserDay and bi.DimUser in the Copilot database (with -GrantDatabaseAccess; otherwise the grant is written
    for a DBA to Grant-WorkbenchWorkerAccess.sql in the site's folder).

.PARAMETER WorkerPackagePath
    The worker to install: a folder holding Governance.Worker.csproj (published with the .NET 10 SDK), a published
    folder, or the release's copilot-governance-worker zip. Omitted: governance\src\Governance.Worker beside this installer.

.PARAMETER WorkerSettingsFile
    The worker's settings, made from governance\deploy\appsettings.Worker.Production.sample.json. Needed the first
    time; omitted, the installed worker's own file is kept. Its connection strings (GovernanceWorker, and Insights for
    the seat read's Copilot activity) are written from -SqlServer, -GovernanceDatabase and -Database on every run; the
    site's address is filled in when the file leaves it as <...>, or when -SiteUrl is given.

.PARAMETER WorkerPath
    The worker's folder. Default C:\CopilotGovernance\Worker.

.PARAMETER LedgerDigestPath
    A share on write-once storage the worker's account can write to. With it, the scheduled task 'Copilot
    Governance ledger digest' saves a digest of the governance database's ledger there each day.

.PARAMETER LedgerDigestAt
    When the ledger digest runs each day, as HH:mm on this server's clock. Default 02:00.

.PARAMETER ProxyUrl
    The outbound proxy, such as http://proxy.contoso.com:8080: for the prerequisite check's HTTPS tests and the worker's calls.

.EXAMPLE
    .\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer sql01 -SiteName 'Apps' -ApplicationPath /CopilotDashboard `
        -AdminPrincipal 'CONTOSO\Copilot-Insights-Admins' -AppPoolIdentity 'CONTOSO\gmsa-insights$' -GrantDatabaseAccess
.EXAMPLE
    .\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer sql01 -Database CopilotAudit `
        -AdminPrincipal 'CONTOSO\Copilot-Insights-Admins' -AppPoolIdentity 'CONTOSO\gmsa-insights$' `
        -HostName insights.contoso.com -CertificateThumbprint 3F2A... -GrantDatabaseAccess
.EXAMPLE
    .\Install-CopilotInsights.ps1 -PackagePath .\copilot-insights-v2.5.4.zip -SqlServer sql01   # an update: the rest is kept
.EXAMPLE
    .\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer sql01 -GrantDatabaseAccess `
        -GovernanceDatabase CopilotGovernance -SiteUrl https://insights.contoso.com -WorkerServer -WorkerAccount 'CONTOSO\gmsa-cgw-worker$' `
        -WorkerSettingsFile D:\Drop\appsettings.Worker.Production.json -LedgerDigestPath '\\worm01\CopilotGovernanceLedger$'
#>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
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
    [string[]] $Tenant = @(),
    [switch] $SkipFeatureInstall,
    [string] $PrecacheAt = '07:00',
    [string] $PrecacheTimeZone = 'Eastern Standard Time',
    [string] $ApplicationPath,
    [string] $AppPoolName,
    [ValidateSet('Windows', 'Entra')][string] $Authentication,
    [string] $EntraTenantId,
    [string] $EntraClientId,
    [string] $EntraCertificateThumbprint,
    [string] $GovernanceDatabase,
    [string] $SiteUrl,
    [switch] $WorkerServer,
    [string] $WorkerAccount,
    [string] $WorkerPackagePath,
    [string] $WorkerSettingsFile,
    [string] $WorkerPath = 'C:\CopilotGovernance\Worker',
    [string] $LedgerDigestPath,
    [string] $LedgerDigestAt = '02:00',
    [string] $ProxyUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function ConvertTo-Thumbprint {
    <#
        A certificate thumbprint as the store names it: hex digits only, in capitals. The certificate dialog
        copies it with spaces and an invisible mark in front, which made the lookup fail. Pure, for the tests.
    #>
    param([string] $Value)
    ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

function ConvertTo-TenantList {
    <# -Tenant's Name=Database pairs as Insights:Tenants entries and their databases, or an error naming the one that is not. Pure, for the tests. #>
    param([string[]] $Tenant, [string] $SqlServer, [string] $Trust)
    $entries = @(); $databases = @()
    foreach ($t in $Tenant) {
        if ($t -notmatch '^\s*([^=]+?)\s*=\s*([A-Za-z0-9_\-]+)\s*$') { throw "-Tenant '$t' is not Name=Database." }
        $databases += $Matches[2]
        $entries += [pscustomobject] @{ Name = $Matches[1]; ConnectionString = "Server=$SqlServer;Database=$($Matches[2]);Integrated Security=true;Encrypt=true;TrustServerCertificate=$Trust" }
    }
    if (@($entries | ForEach-Object { $_.Name } | Sort-Object -Unique).Count -ne $entries.Count) { throw '-Tenant names must be different.' }
    [pscustomobject] @{ Entries = $entries; Databases = $databases }
}

function Resolve-SiteSetting {
    <#
        What this run installs with: each setting given now, else what the site has already, else the
        default. A re-run without every first-run parameter reset them, though the documents said the settings were
        kept: the app pool went back to its own identity (DOMAIN\HOST$ at a remote SQL Server, which had no grants, or
        was given the site's), the connection string to CopilotAudit without TrustServerCertificate, Kerberos off, and
        a plain HTTP binding on port 80 appeared beside the HTTPS one. Pure, for the tests.
    #>
    param(
        [string[]] $Given,                 # the parameters given this time
        [string] $ConnectionString,        # the site's, from appsettings.Production.json; empty for a new site
        [string] $PoolIdentityType,        # the app pool's processModel.identityType; empty for a new pool
        [string] $PoolUserName,
        [bool] $SiteExists,
        [string] $Database, [bool] $TrustServerCertificate, [string] $AppPoolIdentity
    )
    $r = [ordered] @{ Database = $Database; TrustServerCertificate = $TrustServerCertificate; AppPoolIdentity = $AppPoolIdentity
                      KeepPoolIdentity = $false; NewBinding = $true }
    if ($ConnectionString) {
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
        if ($Given -notcontains 'Database' -and $b.InitialCatalog) { $r.Database = $b.InitialCatalog }
        if ($Given -notcontains 'TrustServerCertificate') { $r.TrustServerCertificate = [bool] $b.TrustServerCertificate }
    }
    # The pool's own account when none is given; -AppPoolIdentity '' asks for the pool's own identity.
    if ($Given -notcontains 'AppPoolIdentity' -and "$PoolIdentityType" -in 'SpecificUser', '3' -and $PoolUserName) {
        $r.AppPoolIdentity = $PoolUserName
        $r.KeepPoolIdentity = $true
    }
    # A binding for a new site, or when one is asked for: a certificate, a host name or a port.
    $r.NewBinding = -not $SiteExists -or $Given -contains 'CertificateThumbprint' -or $Given -contains 'HostName' -or $Given -contains 'Port'
    [pscustomobject] $r
}

function Assert-WorkbenchInput {
    <# The Workbench's parameters, checked before anything changes: an error naming the one that is wrong. Pure, for the tests. #>
    param(
        [string[]] $Given,                 # the parameters given this time
        [string] $GovernanceDatabase,
        [string[]] $CopilotDatabases,      # -Database and every tenant's
        [bool] $WorkerServer, [string] $WorkerAccount, [string] $LedgerDigestAt, [string] $ProxyUrl
    )
    if (-not $GovernanceDatabase) {
        $for = @($Given | Where-Object { $_ -in 'SiteUrl', 'WorkerServer', 'WorkerAccount', 'WorkerPackagePath', 'WorkerSettingsFile', 'WorkerPath', 'LedgerDigestPath', 'LedgerDigestAt', 'ProxyUrl' })
        if ($for.Count) { throw "-$($for[0]) is for the Governance Workbench: give -GovernanceDatabase with it. Nothing was changed." }
        return
    }
    if ($GovernanceDatabase -notmatch '^[A-Za-z][A-Za-z0-9_]{0,100}$') { throw "-GovernanceDatabase '$GovernanceDatabase' must be letters, digits and underscores, starting with a letter. Nothing was changed." }
    if ($CopilotDatabases -contains $GovernanceDatabase) { throw "-GovernanceDatabase '$GovernanceDatabase' is a Copilot database: the Workbench has a database of its own, such as CopilotGovernance. Nothing was changed." }
    if ($WorkerAccount -and $WorkerAccount -notmatch '^[A-Za-z0-9][A-Za-z0-9.\-]{0,62}\\[A-Za-z0-9][A-Za-z0-9.\-_]{0,62}\$$') {
        throw "-WorkerAccount '$WorkerAccount' is not a group managed service account (DOMAIN\name`$). Nothing was changed."
    }
    if ($WorkerServer -and -not $WorkerAccount) { throw '-WorkerServer needs -WorkerAccount, the gMSA the worker runs as. Nothing was changed.' }
    foreach ($p in 'WorkerPackagePath', 'WorkerSettingsFile', 'WorkerPath', 'LedgerDigestPath', 'LedgerDigestAt') {
        if ($Given -contains $p -and -not $WorkerServer) { throw "-$p is for the server that runs the worker: give -WorkerServer with it there. Nothing was changed." }
    }
    if ($LedgerDigestAt -notmatch '^([01]?\d|2[0-3]):[0-5]\d$') { throw "-LedgerDigestAt '$LedgerDigestAt' is not a time of day: give it as HH:mm, such as 02:00. Nothing was changed." }
    if ($ProxyUrl -and $ProxyUrl -notmatch '^https?://[^\s/]+(:\d+)?/?$') { throw "-ProxyUrl '$ProxyUrl' is not an address such as http://proxy.contoso.com:8080. Nothing was changed." }
}

function Resolve-WorkbenchBaseUrl {
    <#
        The site's root address, which the links in Workbench notices start with (Notifications:WorkbenchBaseUrl):
        -SiteUrl, else what the site has, else its binding's. Never .../workbench: the notices add that. Pure, for the tests.
    #>
    param([string] $SiteUrl, [string] $Existing, [string] $Protocol, [string] $HostName, [int] $Port, [string] $AppName)
    if ($SiteUrl) {
        $u = $SiteUrl.Trim().TrimEnd('/')
        if ($u -notmatch '^https?://[^/\s?#]+(/[^\s?#]*)?$') { throw "-SiteUrl '$SiteUrl' is not an address such as https://insights.contoso.com. Nothing was changed." }
        if ($u -match '/workbench$') { throw "-SiteUrl '$SiteUrl' ends with /workbench: give the site's own address, $($u -replace '/workbench$', ''). Nothing was changed." }
        return $u
    }
    if ($Existing -and $Existing -notmatch '<[^>]+>') { return $Existing.Trim().TrimEnd('/') }
    $plain = -not $Port -or ($Protocol -eq 'https' -and $Port -eq 443) -or ($Protocol -eq 'http' -and $Port -eq 80)
    '{0}://{1}{2}{3}' -f $Protocol, $HostName, $(if ($plain) { '' } else { ":$Port" }), $(if ($AppName) { "/$AppName" } else { '' })
}

function Merge-WorkerSetting {
    <#
        The worker's settings with its two connection strings written from this run's -SqlServer, -GovernanceDatabase
        and Copilot database every time (a rerun against another server or database must not keep the old ones):
        GovernanceWorker, and Insights, from which the nightly seat read takes Copilot activity as the site's manual
        read does. The site's address is filled in where the file leaves it out or as <...>, and also when -SiteUrl is
        given. Pure, for the tests.
    #>
    param($Settings, [string] $ConnectionString, [string] $InsightsConnectionString, [string] $BaseUrl, [bool] $BaseUrlGiven)
    foreach ($s in 'ConnectionStrings', 'Notifications') {
        if (-not $Settings.PSObject.Properties[$s]) { $Settings | Add-Member -NotePropertyName $s -NotePropertyValue ([pscustomobject] @{}) }
    }
    $fill = @(@($Settings.ConnectionStrings, 'GovernanceWorker', $ConnectionString, $true), @($Settings.Notifications, 'WorkbenchBaseUrl', $BaseUrl, $BaseUrlGiven))
    if ($InsightsConnectionString) { $fill += , @($Settings.ConnectionStrings, 'Insights', $InsightsConnectionString, $true) }
    foreach ($f in $fill) {
        $o, $name, $value, $always = $f
        $p = $o.PSObject.Properties[$name]
        if (-not $p) { $o | Add-Member -NotePropertyName $name -NotePropertyValue $value }
        elseif ($always -or -not "$($p.Value)" -or "$($p.Value)" -match '<[^>]+>') { $p.Value = $value }
    }
    $Settings
}

function Get-WorkerInsightsGrant {
    <#
        The worker's read on the Copilot database, for the nightly seat read's Copilot activity (dash.UserDay, each
        person's interactions per day, and bi.DimUser, their UPN): the gMSA's login and user where missing, then SELECT
        on those two tables only, where they exist. Safe to rerun. Pure, for the tests.
    #>
    param([string] $Database, [string] $Account)
    $a = $Account.Replace("'", "''")
    @"
-- The Workbench worker ($Account) reads Copilot activity for the nightly seat read: two tables, read only.
USE [master];
IF SUSER_ID(N'$a') IS NULL CREATE LOGIN [$Account] FROM WINDOWS;
USE [$Database];
DECLARE @u sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(N'$a'));
IF @u IS NULL BEGIN CREATE USER [$Account] FOR LOGIN [$Account]; SET @u = N'$a'; END;
IF @u <> N'dbo'
BEGIN
    DECLARE @g nvarchar(max) = N'';
    IF OBJECT_ID(N'dash.UserDay', N'U') IS NOT NULL SET @g += N'GRANT SELECT ON dash.UserDay TO ' + QUOTENAME(@u) + N';';
    IF OBJECT_ID(N'bi.DimUser', N'U') IS NOT NULL SET @g += N'GRANT SELECT ON bi.DimUser TO ' + QUOTENAME(@u) + N';';
    EXEC (@g);
END;
"@
}

function Get-LedgerDigestArgument {
    <# The daily ledger digest task's command line for pwsh. Pure, for the tests. #>
    param([string] $Script, [string] $SqlServer, [string] $Database, [string] $DigestPath, [bool] $TrustServerCertificate)
    # A trailing \ would escape the closing quote.
    $a = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Script`" -SqlServer `"$SqlServer`" -Database $Database -DigestPath `"$($DigestPath.TrimEnd('\'))`""
    if ($TrustServerCertificate) { $a += ' -TrustServerCertificate' }
    $a
}

# ---- 0. Where we are ------------------------------------------------------------------------
# The IIS:\ drive exists only in Windows PowerShell; PowerShell 7 loads WebAdministration without it.
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this in Windows PowerShell 5.1 (powershell.exe), not PowerShell 7: the IIS provider it uses is not available there.' }
$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -eq 1) { throw 'This installs on Windows Server (2022 or later); for a workstation, run the app with dotnet run.' }
if ([int] $os.BuildNumber -lt 20348) { Write-Warning "Windows Server 2022 (build 20348) or later is supported; this is build $($os.BuildNumber)." }
if ($AppPoolIdentity -and -not $AppPoolIdentity.EndsWith('$') -and -not $AppPoolCredential) {
    throw "-AppPoolIdentity $AppPoolIdentity is not a managed service account (DOMAIN\name$): give its password with -AppPoolCredential."
}
# The Workbench's own scripts run in PowerShell 7, and its database scripts through sqlcmd.
$governanceKit = Join-Path (Split-Path $PSScriptRoot -Parent) 'governance'
if ($GovernanceDatabase) {
    if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) { throw '-GovernanceDatabase needs PowerShell 7 (pwsh) on this server: the Workbench''s scripts run in it. Install it, then run this again. Nothing was changed.' }
    if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw '-GovernanceDatabase needs the SQL Server command-line tools (sqlcmd) on this server. Install them, then run this again. Nothing was changed.' }
    if (-not (Test-Path (Join-Path $governanceKit 'deploy\Test-WorkbenchPrerequisites.ps1'))) { throw "-GovernanceDatabase needs the governance folder of the source package beside the dashboard folder ($governanceKit). Run this from the source package. Nothing was changed." }
}

# ---- 1. IIS -----------------------------------------------------------------------------------
if (-not $SkipFeatureInstall) {
    Write-Step 'IIS role services'
    # Web-AppInit starts the site with IIS, so email alerts and digests go out with nobody signed in.
    $features = 'Web-Server', 'Web-Default-Doc', 'Web-Static-Content', 'Web-Http-Errors', 'Web-Http-Logging',
                'Web-Filtering', 'Web-Windows-Auth', 'Web-Mgmt-Console', 'Web-AppInit'
    $r = Install-WindowsFeature -Name $features
    if ($r.RestartNeeded -eq 'Yes') { Write-Warning 'Windows asks for a restart to finish installing IIS. Restart, then run this again.' }
}
Import-Module WebAdministration

# ---- What the site has, and the inputs, before anything changes --------------------------------
# A setting not given again keeps what the site has (Resolve-SiteSetting). Every input is checked here, before step 3
# takes the site offline: a thumbprint the store does not hold, a -Tenant that is not Name=Database, a
# settings file that does not read, threw after the files were copied, with the site left offline.
# As a site of its own, or as an application (a virtual directory) under an existing site.
$appName = $null
if ($ApplicationPath) {
    $appName = $ApplicationPath.Trim().Trim('/')
    if ($appName -notmatch '^[A-Za-z0-9][A-Za-z0-9._\-]*(/[A-Za-z0-9][A-Za-z0-9._\-]*)*$') { throw "-ApplicationPath '$ApplicationPath' is not a path such as /CopilotDashboard. Nothing was changed." }
    # A name the dashboard uses for its own addresses (/admin, /agents ...) could not be told from them.
    $own = 'leadership', 'adoption', 'licensing', 'security', 'compliance', 'agents', 'enablement', 'forecasting', 'regions', 'workforce', 'apps', 'governance', 'group', 'admin', 'api', 'healthz', 'not-found', 'error', 'me', 'workbench'
    if (($appName -split '/')[0] -in $own) { throw "-ApplicationPath '$ApplicationPath' has the name of one of the dashboard's own addresses (/$(($appName -split '/')[0])): choose another, such as /CopilotDashboard. Nothing was changed." }
    if (-not (Get-Website -Name $SiteName)) { throw "-ApplicationPath needs an existing site to hold it: there is no site '$SiteName'. Give the site's name with -SiteName. Nothing was changed." }
}
$location = if ($appName) { "$SiteName/$appName" } else { $SiteName }
$appItem = if ($appName) { "IIS:\Sites\$SiteName\$appName" } else { "IIS:\Sites\$SiteName" }
$siteBefore = if ($appName) { Get-WebApplication -Site $SiteName -Name $appName } else { Get-Website -Name $SiteName }
# An application's own pool and folder, not the ones a site of its own would have: named after its path, so it never
# shares them with a dashboard installed as a site (one pool cannot hold two of them). Run again, it keeps the pool
# and the folder it has.
$leaf = if ($appName) { $appName -replace '/', '-' } else { $null }
$poolName = if ($AppPoolName) { $AppPoolName } elseif ($appName -and $siteBefore) { [string] $siteBefore.applicationPool } elseif ($appName) { $leaf } else { $SiteName }
$poolPath = "IIS:\AppPools\$poolName"
if ($siteBefore -and -not $PSBoundParameters.ContainsKey('PhysicalPath')) { $PhysicalPath = [Environment]::ExpandEnvironmentVariables($siteBefore.physicalPath) }
elseif ($appName -and -not $PSBoundParameters.ContainsKey('PhysicalPath')) { $PhysicalPath = Join-Path (Split-Path $PhysicalPath -Parent) $leaf }
# The dashboard already an application in this site, run again without -ApplicationPath, would have copied it over
# the site's own folder.
if (-not $appName -and $siteBefore) {
    $inside = @(Get-WebApplication -Site $SiteName | Where-Object { Test-Path (Join-Path ([Environment]::ExpandEnvironmentVariables($_.PhysicalPath)) 'CopilotInsights.dll') })
    if ($inside.Count -and -not (Test-Path (Join-Path $PhysicalPath 'CopilotInsights.dll'))) {
        throw "The dashboard is an application in site '$SiteName', at $($inside[0].path): give -ApplicationPath $($inside[0].path) to update it. Nothing was changed."
    }
}
# The files are mirrored into the folder, which removes what else it holds: never a folder of something else.
$kept = 'logs', 'precache', 'appsettings.Production.json', 'app_offline.htm', 'Grant-CopilotInsightsAccess.sql', 'Invoke-InsightsPrecache.ps1'
if ((Test-Path $PhysicalPath) -and -not (Test-Path (Join-Path $PhysicalPath 'CopilotInsights.dll')) -and
    @(Get-ChildItem -LiteralPath $PhysicalPath -Force | Where-Object Name -notin $kept).Count) {
    throw "$PhysicalPath holds files that are not the dashboard's, which installing would remove: give -PhysicalPath an empty folder or the dashboard's own. Nothing was changed."
}
# An app pool holds one dashboard: refused when another site or application already runs in the one named.
$others = @(Get-WebConfiguration "/system.applicationHost/sites/site/application[@applicationPool='$poolName']" -ErrorAction SilentlyContinue |
    ForEach-Object { "$($_.ItemXPath -replace '.*@name=''([^'']+)''.*', '$1')$($_.path)" } |
    Where-Object { $_ -ne "$SiteName$(if ($appName) { "/$appName" } else { '/' })" })
if ($others.Count) { throw "App pool '$poolName' already runs $($others -join ', '): give -AppPoolName a pool of the dashboard's own. Nothing was changed." }
$settingsPath = Join-Path $PhysicalPath 'appsettings.Production.json'
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject] @{} }
$before = if ($settings.PSObject.Properties['Insights'] -and $settings.Insights.PSObject.Properties['ConnectionString']) { [string] $settings.Insights.ConnectionString } else { '' }
$poolType = ''; $poolUser = ''
if (Test-Path $poolPath) {
    $pm = Get-ItemProperty $poolPath -Name processModel
    $poolType = [string] $pm.identityType; $poolUser = [string] $pm.userName
}
$site = Resolve-SiteSetting -Given @($PSBoundParameters.Keys) -ConnectionString $before -PoolIdentityType $poolType -PoolUserName $poolUser `
    -SiteExists ([bool] $siteBefore) -Database $Database -TrustServerCertificate ([bool] $TrustServerCertificate) -AppPoolIdentity $AppPoolIdentity
$Database = $site.Database
$AppPoolIdentity = $site.AppPoolIdentity
$trust = if ($site.TrustServerCertificate) { 'true' } else { 'false' }
if ($siteBefore) {
    Write-Host ("    keeping what is not given again: database {0}, TrustServerCertificate {1}, app pool account {2}" -f $Database, $trust,
                $(if ($AppPoolIdentity) { $AppPoolIdentity } else { 'its own identity' }))
}
if ($AppPoolIdentity -and -not $site.KeepPoolIdentity) {
    try { [void] ([System.Security.Principal.NTAccount] $AppPoolIdentity).Translate([System.Security.Principal.SecurityIdentifier]) }
    catch { throw "-AppPoolIdentity ${AppPoolIdentity}: no such account here ($($_.Exception.Message)). Nothing was changed." }
}
if ($CertificateThumbprint) {
    $CertificateThumbprint = ConvertTo-Thumbprint $CertificateThumbprint
    if (-not (Test-Path "Cert:\LocalMachine\My\$CertificateThumbprint")) { throw "No certificate $CertificateThumbprint in LocalMachine\My. Nothing was changed." }
}
# The daily pre-cache: its time and zone, and the script it runs, which this copies into the site.
$precacheScript = Join-Path $PSScriptRoot 'Invoke-InsightsPrecache.ps1'
if ($PrecacheAt -and $PrecacheAt -notmatch '^([01]?\d|2[0-3]):[0-5]\d$') { throw "-PrecacheAt '$PrecacheAt' is not a time of day: give it as HH:mm, such as 07:00, or '' for no pre-cache. Nothing was changed." }
try { [void] [TimeZoneInfo]::FindSystemTimeZoneById($PrecacheTimeZone) }
catch { throw "-PrecacheTimeZone '$PrecacheTimeZone' is not a Windows time zone here (Get-TimeZone -ListAvailable lists them). Nothing was changed." }
if (-not (Test-Path $precacheScript)) { Write-Warning "No $precacheScript beside this installer: the daily pre-cache is not scheduled." }
# Several tenants: Name=Database on the same server. Without -Tenant, the list already there is kept, and so are the
# databases the grants go to.
$tenantList = $null
$tenantDbs = @()
if ($Tenant.Count -gt 0) {
    $tenantList = ConvertTo-TenantList -Tenant $Tenant -SqlServer $SqlServer -Trust $trust
    $tenantDbs = @($tenantList.Databases)
}
elseif ($settings.PSObject.Properties['Insights'] -and $settings.Insights.PSObject.Properties['Tenants']) {
    $tenantDbs = @(@($settings.Insights.Tenants) | Where-Object { $_ -and $_.PSObject.Properties['ConnectionString'] } |
                   ForEach-Object { (New-Object System.Data.SqlClient.SqlConnectionStringBuilder ([string] $_.ConnectionString)).InitialCatalog } | Where-Object { $_ })
}
# The Governance Workbench: its parameters, the address its notices link to, and on the worker's server the worker
# and its settings.
Assert-WorkbenchInput -Given @($PSBoundParameters.Keys) -GovernanceDatabase $GovernanceDatabase -CopilotDatabases (@($Database) + $tenantDbs) `
    -WorkerServer ([bool] $WorkerServer) -WorkerAccount $WorkerAccount -LedgerDigestAt $LedgerDigestAt -ProxyUrl $ProxyUrl
$workbenchUrl = $null
if ($GovernanceDatabase) {
    # The site's own address when -SiteUrl is not given and the site has none yet: its HTTPS binding's, or its HTTP one's.
    $wbProto = if ($CertificateThumbprint) { 'https' } else { 'http' }
    $wbHost = $HostName; $wbPort = $Port
    if (-not $CertificateThumbprint -and -not $HostName -and -not $Port -and (Get-Website -Name $SiteName)) {
        $b = @(Get-WebBinding -Name $SiteName | Where-Object { $_.protocol -in 'http', 'https' } | Sort-Object { if ($_.protocol -eq 'https') { 0 } else { 1 } })
        if ($b) { $parts = ([string] $b[0].bindingInformation).Split(':'); $wbProto = [string] $b[0].protocol; $wbPort = [int] $parts[1]; if ($parts[2]) { $wbHost = $parts[2] } }
    }
    if (-not $wbHost) { $wbHost = $env:COMPUTERNAME }
    $hadUrl = if ($settings.PSObject.Properties['Notifications'] -and $settings.Notifications.PSObject.Properties['WorkbenchBaseUrl']) { [string] $settings.Notifications.WorkbenchBaseUrl } else { '' }
    $workbenchUrl = Resolve-WorkbenchBaseUrl -SiteUrl $SiteUrl -Existing $hadUrl -Protocol $wbProto -HostName $wbHost -Port $wbPort -AppName $appName
    if (-not $SiteUrl -and -not $hadUrl) { Write-Warning "Workbench notices will link to $workbenchUrl. Behind a load balancer, or at another name, give -SiteUrl with the address people open." }
    if ($WorkerServer) {
        $workerSource = if ($WorkerPackagePath) { $WorkerPackagePath } else { Join-Path $governanceKit 'src\Governance.Worker' }
        if (-not (Test-Path $workerSource)) { throw "No worker at ${workerSource}: give -WorkerPackagePath the release's copilot-governance-worker zip, a published folder, or governance\src\Governance.Worker. Nothing was changed." }
        # The worker's settings: the file given, else the installed worker's own.
        $installedSettings = Join-Path $WorkerPath 'appsettings.Production.json'
        $workerSettingsSource = if ($WorkerSettingsFile) { $WorkerSettingsFile } elseif (Test-Path $installedSettings) { $installedSettings } else { '' }
        if (-not $workerSettingsSource -or -not (Test-Path $workerSettingsSource)) {
            throw "The worker needs its settings: give -WorkerSettingsFile, made from governance\deploy\appsettings.Worker.Production.sample.json. Nothing was changed."
        }
        try { $workerSettings = Get-Content $workerSettingsSource -Raw | ConvertFrom-Json }
        catch { throw "$workerSettingsSource does not read as JSON ($($_.Exception.Message)). Nothing was changed." }
    }
}

function Invoke-WorkbenchPrerequisite {
    <# governance\deploy\Test-WorkbenchPrerequisites.ps1 for this server, in PowerShell 7: true when no check fails. #>
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $governanceKit 'deploy\Test-WorkbenchPrerequisites.ps1'),
           '-Role', $(if ($WorkerServer) { 'Both' } else { 'Web' }), '-SqlServer', $SqlServer, '-Database', $GovernanceDatabase)
    if ($trust -eq 'true') { $a += '-TrustServerCertificate' }
    if ($ProxyUrl) { $a += '-ProxyUrl', $ProxyUrl }
    if ($WorkerServer) {
        $a += '-GmsaAccount', $WorkerAccount
        $subject = if ($workerSettings.PSObject.Properties['Tenant'] -and $workerSettings.Tenant.PSObject.Properties['WorkerCertificateSubject']) { [string] $workerSettings.Tenant.WorkerCertificateSubject } else { '' }
        if ($subject -and $subject -notmatch '<[^>]+>') { $a += '-WorkerCertificateSubject', $subject }
    }
    # A Hosting Bundle installed by this run is not on this session's PATH yet.
    $dotnetDir = Join-Path $env:ProgramFiles 'dotnet'
    if ((Test-Path (Join-Path $dotnetDir 'dotnet.exe')) -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:PATH = "$dotnetDir;$env:PATH" }
    & pwsh @a | Out-Host
    $LASTEXITCODE -eq 0
}

# ---- -WhatIf: the inputs are checked above; what a run would change is listed, and nothing is changed -----------
if ($WhatIfPreference) {
    $where = if ($appName) { "application /$appName in site '$SiteName'" } else { "site '$SiteName'" }
    [void] $PSCmdlet.ShouldProcess($PhysicalPath, "Copy the site files and write appsettings.Production.json (database $Database on $SqlServer)")
    [void] $PSCmdlet.ShouldProcess($where, "Create or update the app pool '$poolName' and the $where, with its sign-in")
    [void] $PSCmdlet.ShouldProcess("$SqlServer $((@(@($Database) + $tenantDbs) | Select-Object -Unique) -join ', ')",
        $(if ($GrantDatabaseAccess) { "Install the dashboard schema and grant the site's account" } else { 'Write the grants for a DBA' }))
    if (Test-Path $precacheScript) { [void] $PSCmdlet.ShouldProcess('Copilot Insights pre-cache', $(if ($PrecacheAt) { "Schedule daily at $PrecacheAt $PrecacheTimeZone, or keep the task as scheduled" } else { 'Remove the task' })) }
    if ($GovernanceDatabase) {
        [void] $PSCmdlet.ShouldProcess($settingsPath, "Set ConnectionStrings:Governance to $GovernanceDatabase on $SqlServer and Notifications:WorkbenchBaseUrl to $workbenchUrl")
        [void] $PSCmdlet.ShouldProcess("$SqlServer $GovernanceDatabase",
            $(if ($GrantDatabaseAccess) { "Create or upgrade the governance database, with the site's account in app_web$(if ($WorkerAccount) { " and $WorkerAccount in app_worker" })" } else { 'Show the Deploy-Database.ps1 command for a DBA' }))
        if ($WorkerAccount) {
            [void] $PSCmdlet.ShouldProcess("$SqlServer $Database", $(if ($GrantDatabaseAccess) { "Grant $WorkerAccount read on dash.UserDay and bi.DimUser" } else { "Write the grant of read on dash.UserDay and bi.DimUser to $WorkerAccount for a DBA" }))
        }
        if ($WorkerServer) {
            [void] $PSCmdlet.ShouldProcess('CopilotGovernanceWorker', "Install the worker from $workerSource into $WorkerPath as a Windows service running as $WorkerAccount, its connection strings set to $GovernanceDatabase and $Database on $SqlServer")
            [void] $PSCmdlet.ShouldProcess('Copilot Governance ledger digest', $(if ($LedgerDigestPath) { "Schedule daily at $LedgerDigestAt as $WorkerAccount, saving to $LedgerDigestPath" } else { 'Leave as it is (no -LedgerDigestPath)' }))
        }
        Write-Step 'Governance Workbench prerequisites'
        if (-not (Invoke-WorkbenchPrerequisite)) { Write-Warning 'A run would stop here: fix the FAIL lines above first.' }
    }
    Write-Host 'Nothing was changed (-WhatIf).'
    return
}

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

# ---- 2b. The Governance Workbench's prerequisites, before the site changes -----------------------
if ($GovernanceDatabase) {
    Write-Step 'Governance Workbench prerequisites'
    if (-not (Invoke-WorkbenchPrerequisite)) {
        throw 'This server is not ready for the Governance Workbench: fix the FAIL lines above (its database needs SQL Server 2022 or later), then run this again. The site was not changed.'
    }
}

# ---- 3. The files -----------------------------------------------------------------------------
Write-Step "Site files in $PhysicalPath"
$staging = Join-Path ([IO.Path]::GetTempPath()) ("CopilotInsights-" + [guid]::NewGuid().ToString('N'))
$offline = Join-Path $PhysicalPath 'app_offline.htm'
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
    # The release's zip holds the app in a CopilotInsights folder: use the folder the app is in.
    if (-not (Test-Path (Join-Path $staging 'CopilotInsights.dll'))) {
        $inner = Get-ChildItem $staging -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'CopilotInsights.dll') } | Select-Object -First 1
        if ($inner) {
            Get-ChildItem $inner.FullName -Force | Move-Item -Destination $staging -Force
            Remove-Item $inner.FullName -Recurse -Force
        }
    }
    if (-not (Test-Path (Join-Path $staging 'CopilotInsights.dll'))) { throw "No CopilotInsights.dll in ${PackagePath}: is it the published app?" }
    # The daily pre-cache's script goes into the site's folder with the app, where the app pool only reads it.
    if (Test-Path $precacheScript) { Copy-Item -LiteralPath $precacheScript -Destination $staging -Force }

    New-Item -ItemType Directory -Force $PhysicalPath, (Join-Path $PhysicalPath 'logs'), (Join-Path $PhysicalPath 'precache') | Out-Null
    # Stop the site while its files change: app_offline.htm makes the module unload the app.
    Set-Content $offline '<p>Copilot Insights is being updated. Try again in a minute.</p>'
    Start-Sleep -Seconds 2
    & robocopy $staging $PhysicalPath /MIR /XF appsettings.Production.json app_offline.htm /XD logs precache /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the site files failed (robocopy $LASTEXITCODE)." }

    # ---- 4. appsettings.Production.json -------------------------------------------------------
    Write-Step 'Settings'
    function Set-Prop($Object, [string] $Name, $Value) {
        if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value } else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    }
    if (-not $settings.PSObject.Properties['Insights']) { Set-Prop $settings 'Insights' ([pscustomobject] @{}) }
    Set-Prop $settings.Insights 'ConnectionString' "Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    # The site connects the same way, so a certificate SQL Server's clients do not trust shows here, not as
    # a "Something went wrong" page later. Signed in as whoever runs this, so only the handshake is judged.
    $probe = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServer;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust;Connect Timeout=15"
    try { $probe.Open() }
    catch {
        if ($_.Exception.Message -match 'certificate|SSL Provider|TLS|principal name') {
            Write-Warning "The encrypted connection to $SqlServer failed: $($_.Exception.GetBaseException().Message) The site would fail the same way. If SQL Server uses its own self-signed certificate, run this again with -TrustServerCertificate."
        }
    }
    finally { $probe.Dispose() }
    if ($AdminPrincipal.Count -gt 0) { Set-Prop $settings.Insights 'BootstrapAdmins' @($AdminPrincipal) }
    elseif (-not $settings.Insights.PSObject.Properties['BootstrapAdmins'] -or @($settings.Insights.BootstrapAdmins).Count -eq 0) {
        Write-Warning 'No -AdminPrincipal: nobody can open Admin until a row is added to dash.RoleGrant (see the RUNBOOK).'
    }
    if ($tenantList) { Set-Prop $settings.Insights 'Tenants' @($tenantList.Entries) }
    # Sign-in: the way given, else the way the site has (Windows for a new one). The app registration, when given.
    if ($Authentication) { Set-Prop $settings.Insights 'Authentication' $Authentication }
    $signIn = if ($settings.Insights.PSObject.Properties['Authentication']) { [string] $settings.Insights.Authentication } else { 'Windows' }
    if ($EntraTenantId -or $EntraClientId -or $EntraCertificateThumbprint) {
        if (-not $settings.Insights.PSObject.Properties['Entra']) { Set-Prop $settings.Insights 'Entra' ([pscustomobject] @{}) }
        if ($EntraTenantId) { Set-Prop $settings.Insights.Entra 'TenantId' $EntraTenantId.Trim() }
        if ($EntraClientId) { Set-Prop $settings.Insights.Entra 'ClientId' $EntraClientId.Trim() }
        if ($EntraCertificateThumbprint) {
            Set-Prop $settings.Insights.Entra 'ClientCredential' 'Certificate'
            Set-Prop $settings.Insights.Entra 'CertificateThumbprint' ($EntraCertificateThumbprint -replace '\s', '')
        }
    }
    if ($signIn -eq 'Entra') {
        $e = if ($settings.Insights.PSObject.Properties['Entra']) { $settings.Insights.Entra } else { $null }
        foreach ($need in 'TenantId', 'ClientId') {
            if (-not $e -or -not $e.PSObject.Properties[$need] -or -not $e.$need) { throw "Sign-in through Entra ID needs -Entra$need the first time (docs\ENTRA-SIGN-IN.md)." }
        }
    }
    # Before 2.3.0 a Development-only setting let an administrator view as someone else; the site no longer has it.
    if ($settings.Insights.PSObject.Properties['AllowViewAs']) { $settings.Insights.PSObject.Properties.Remove('AllowViewAs') }
    # The Governance Workbench: its database, and the site's address its notices link to (+ /workbench/...).
    if ($GovernanceDatabase) {
        if (-not $settings.PSObject.Properties['ConnectionStrings']) { Set-Prop $settings 'ConnectionStrings' ([pscustomobject] @{}) }
        Set-Prop $settings.ConnectionStrings 'Governance' "Server=$SqlServer;Database=$GovernanceDatabase;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust;Application Name=CopilotInsights-Workbench"
        if (-not $settings.PSObject.Properties['Notifications']) { Set-Prop $settings 'Notifications' ([pscustomobject] @{}) }
        Set-Prop $settings.Notifications 'WorkbenchBaseUrl' $workbenchUrl
    }
    $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding utf8

    # ---- 5. App pool and site -----------------------------------------------------------------
    Write-Step $(if ($appName) { "App pool '$poolName' and application /$appName in site '$SiteName'" } else { "App pool and site '$SiteName'" })
    if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $poolName | Out-Null }
    Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''
    Set-ItemProperty $poolPath -Name managedPipelineMode -Value 'Integrated'
    Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true
    Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    if ($site.KeepPoolIdentity) {
        # Left as it is, its stored password with it.
        $poolAccount = $AppPoolIdentity
    }
    elseif ($AppPoolIdentity) {
        $pw = if ($AppPoolCredential) { $AppPoolCredential.GetNetworkCredential().Password } else { '' }
        Set-ItemProperty $poolPath -Name processModel -Value @{ userName = $AppPoolIdentity; password = $pw; identityType = 3 }
        $poolAccount = $AppPoolIdentity
    }
    else {
        Set-ItemProperty $poolPath -Name processModel.identityType -Value 4   # ApplicationPoolIdentity
        $poolAccount = "IIS APPPOOL\$poolName"
    }

    $https = [bool] $CertificateThumbprint
    if (-not $Port) { $Port = if ($https) { 443 } else { 80 } }
    if ($appName) {
        # An application in a site that is not ours: its bindings and certificate are the site's, left as they are.
        if (-not $siteBefore) { New-WebApplication -Site $SiteName -Name $appName -PhysicalPath $PhysicalPath -ApplicationPool $poolName | Out-Null }
        else {
            Set-ItemProperty $appItem -Name physicalPath -Value $PhysicalPath
            Set-ItemProperty $appItem -Name applicationPool -Value $poolName
        }
        # Start the app with IIS, not at the first visit: its background sender runs the alerts and digests.
        Set-ItemProperty $appItem -Name preloadEnabled -Value $true
    }
    elseif (-not $siteBefore) {
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $poolName -Port $Port -HostHeader $HostName -Ssl:$https | Out-Null
    }
    else {
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $poolName
    }
    # Start the app with IIS, not at the first visit: its background sender runs the alerts and digests.
    if (-not $appName) { Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true }

    # Alerts also go to the Application log under this source; creating a source needs an administrator.
    if (-not [System.Diagnostics.EventLog]::SourceExists('Copilot Insights')) {
        New-EventLog -LogName Application -Source 'Copilot Insights'
    }
    if ($appName) {
        # The holding site's own address: its HTTPS binding's, or its HTTP one's.
        $kept = @(Get-WebBinding -Name $SiteName | Sort-Object { if ($_.protocol -eq 'https') { 0 } else { 1 } })
        $proto = 'http'
        if ($kept) {
            $parts = ([string] $kept[0].bindingInformation).Split(':')
            $proto = [string] $kept[0].protocol; $Port = [int] $parts[1]; if (-not $HostName) { $HostName = $parts[2] }
        }
    }
    elseif ($https) {
        $proto = 'https'
        if (-not (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName)) {
            New-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName -SslFlags $(if ($HostName) { 1 } else { 0 })
        }
        elseif ($HostName -and (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName).sslFlags -ne 1) {
            # New-Website -Ssl makes the binding without SNI; a host-name binding needs it to share port 443.
            Set-WebBinding -Name $SiteName -BindingInformation "*:${Port}:$HostName" -PropertyName sslFlags -Value 1
        }
        (Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $HostName).AddSslCertificate($CertificateThumbprint, 'My')
    }
    elseif ($site.NewBinding) {
        $proto = 'http'
        # New-Website makes the first binding; a site first installed with HTTPS has none for HTTP.
        if (-not (Get-WebBinding -Name $SiteName -Protocol http -Port $Port -HostHeader $HostName)) {
            New-WebBinding -Name $SiteName -Protocol http -Port $Port -HostHeader $HostName
        }
        Write-Warning 'No -CertificateThumbprint: the site answers on HTTP only. Windows authentication still protects it, but use HTTPS in production.'
    }
    else {
        # No binding asked for: the site keeps its own (a re-run without -CertificateThumbprint added HTTP on
        # port 80 beside the HTTPS one). The address below is its HTTPS binding's, or its HTTP one's.
        $kept = @(Get-WebBinding -Name $SiteName | Sort-Object { if ($_.protocol -eq 'https') { 0 } else { 1 } })
        $proto = 'http'
        if ($kept) {
            $parts = ([string] $kept[0].bindingInformation).Split(':')
            $proto = [string] $kept[0].protocol; $Port = [int] $parts[1]; $HostName = $parts[2]
        }
        Write-Host "    bindings kept: $((@($kept) | ForEach-Object { "$($_.protocol) $($_.bindingInformation)" }) -join ', ')"
    }
    $https = $proto -eq 'https'

    # ---- 6. Authentication --------------------------------------------------------------------
    if ($signIn -eq 'Entra') {
        # The site signs people in through Entra ID itself: IIS lets every request reach it. Every page still needs a
        # signed-in person; only the sign-in's own addresses and the health probe answer without one.
        Write-Step 'Sign-in through Entra ID: anonymous on, Windows authentication off in IIS'
        Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $location -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name enabled -Value $false
        Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $location -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $true
        if (-not $https) { Write-Warning 'Sign-in through Entra ID needs HTTPS: give -CertificateThumbprint, or put the site behind a proxy that ends TLS.' }
        $thumb = if ($settings.Insights.Entra.PSObject.Properties['CertificateThumbprint']) { [string] $settings.Insights.Entra.CertificateThumbprint } else { '' }
        if ($thumb) {
            # The app pool reads the certificate's private key to prove itself to Entra ID.
            $cert = Get-Item "Cert:\LocalMachine\My\$thumb" -ErrorAction SilentlyContinue
            if (-not $cert -or -not $cert.HasPrivateKey) { Write-Warning "The certificate $thumb, with its private key, is not in Local Computer > Personal: sign-in will fail until it is." }
            else {
                $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
                $keyFile = Get-ChildItem "$env:ProgramData\Microsoft\Crypto" -Recurse -Filter $rsa.Key.UniqueName -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($keyFile) {
                    $keyAcl = Get-Acl $keyFile.FullName
                    $keyAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $poolAccount, 'Read', 'Allow'))
                    Set-Acl $keyFile.FullName $keyAcl
                }
                else { Write-Warning "Could not find the private key file of ${thumb}: give $poolAccount read access to it in certlm.msc (All Tasks > Manage Private Keys)." }
            }
        }
    }
    else {
    Write-Step 'Windows authentication on, anonymous off'
    Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $location -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $false
    Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $location -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name enabled -Value $true
    # Kerberos for a host name registered on the app-pool account (the SPN below) needs the pool's own credentials.
    Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $location -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name useAppPoolCredentials -Value ([bool] $AppPoolIdentity)
    }
    # The load balancer's probe answers without sign-in: it says only whether the server reaches its database.
    Set-WebConfigurationProperty -PSPath 'IIS:\' -Location "$location/healthz" -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $true

    # The app pool writes its logs; everyone else only reads the site.
    $acl = Get-Acl (Join-Path $PhysicalPath 'logs')
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $poolAccount, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl (Join-Path $PhysicalPath 'logs') $acl
    # The pre-cache folder: the scheduled task, as LOCAL SERVICE, leaves a request and its log there; the app
    # pool takes the request and writes its answer. Neither can change the script the task runs, in the site's folder.
    $acl = Get-Acl (Join-Path $PhysicalPath 'precache')
    foreach ($who in $poolAccount, 'NT AUTHORITY\LOCAL SERVICE') {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $who, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl (Join-Path $PhysicalPath 'precache') $acl
    $acl = Get-Acl $PhysicalPath
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $poolAccount, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl $PhysicalPath $acl
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    # Back online whether steps 3 to 6 finished or not: only the last step removed it, so a run that failed
    # after the files were copied left every user on "being updated" until a run succeeded.
    Remove-Item $offline -ErrorAction SilentlyContinue
}
Start-WebAppPool -Name $poolName -ErrorAction SilentlyContinue
Restart-WebAppPool -Name $poolName -ErrorAction SilentlyContinue

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
-- The login may already be in the database under another name: as dbo when it owns the
-- database, or as a user a DBA added. The grants go to that user; the owner needs none.
DECLARE @u sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(N'$sqlLogin'));
IF @u IS NULL BEGIN CREATE USER [$sqlLogin] FOR LOGIN [$sqlLogin]; SET @u = N'$sqlLogin'; END;
IF @u = N'dbo' PRINT N'$sqlLogin owns this database, so it already has every right the site needs.';
ELSE
BEGIN
    DECLARE @g nvarchar(max) = N'
GRANT SELECT ON SCHEMA::bi TO {u};
GRANT SELECT ON SCHEMA::dash TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.Setting TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.RoleGrant TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.AgentAlias TO {u};
GRANT INSERT, UPDATE, DELETE ON dash.SensitivityLabel TO {u};
GRANT INSERT ON dash.AuditLog TO {u};
GRANT EXECUTE ON dash.usp_RefreshDashboard TO {u};
GRANT SELECT, INSERT, UPDATE ON audit.SessionAppCategory TO {u};
-- Every grant the site needs, kept up to date by Create-CopilotOperationsSchema.sql.
IF DATABASE_PRINCIPAL_ID(N''CopilotInsightsApp'') IS NOT NULL ALTER ROLE CopilotInsightsApp ADD MEMBER {u};';
    SET @g = REPLACE(@g, N'{u}', QUOTENAME(@u));
    EXEC (@g);
    -- Admin > Health reads the pipeline's run logs, where they exist.
    DECLARE @t sysname, @sql nvarchar(400);
    DECLARE t CURSOR LOCAL FAST_FORWARD FOR
        SELECT name FROM (VALUES (N'CopilotLoad'), (N'CopilotCsvImport'), (N'CopilotSessionRun'), (N'UserDirectory')) AS v(name)
        WHERE OBJECT_ID(N'audit.' + name, N'U') IS NOT NULL;
    OPEN t; FETCH NEXT FROM t INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'GRANT SELECT ON audit.' + QUOTENAME(@t) + N' TO ' + QUOTENAME(@u) + N';';
        EXEC (@sql);
        FETCH NEXT FROM t INTO @t;
    END;
END;
"@
# The same grants in every tenant's database.
$databases = @(@($Database) + $tenantDbs | Select-Object -Unique)
$grantsFor = { param($db) $grants.Replace("USE [$Database];", "USE [$db];") }
$grantsFile = Join-Path $PhysicalPath 'Grant-CopilotInsightsAccess.sql'
# GO between databases: each one's grants declare the same variables.
Set-Content $grantsFile (($databases | ForEach-Object { & $grantsFor $_ }) -join "`r`nGO`r`n") -Encoding utf8
if ($GrantDatabaseAccess) {
  foreach ($db in $databases) {
    # The dash schema first: the grants below name its tables.
    $repoSql = Join-Path (Split-Path $PSScriptRoot -Parent) 'sql'
    $dbConnString = "Server=$SqlServer;Database=$db;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    # One run at a time, as the nightly pipeline, Install-CopilotAudit.ps1 and the maintenance rebuilds hold it:
    # the schema scripts and a full dashboard refresh below ran beside a nightly's refresh of the same tables.
    $runLock = $null
    if ($env:CopilotAuditRunLock -ne "$SqlServer/$db") {
        $lb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $dbConnString
        $lb['Pooling'] = $false   # closing a pooled connection keeps its session, and the session's lock with it
        $runLock = New-Object System.Data.SqlClient.SqlConnection $lb.ConnectionString
        try {
            $runLock.Open()
            $lc = $runLock.CreateCommand()
            $lc.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
            $granted = [int] $lc.ExecuteScalar() -ge 0
        }
        catch { $runLock.Dispose(); throw }
        if (-not $granted) {
            $runLock.Dispose()
            throw "Another run is using $db now: the nightly pipeline, Install-CopilotAudit.ps1 or a maintenance rebuild. The site is installed; $db's dashboard schema and grants were not changed. Let it finish (Admin > Health shows the pipeline's runs), then run this again with -GrantDatabaseAccess."
        }
    }
    $dbConn = New-Object System.Data.SqlClient.SqlConnection $dbConnString
    try {
        $dbConn.Open()
        foreach ($f in 'Create-CopilotDashboardSchema.sql', 'Create-CopilotDashboardProcedure.sql', 'Create-CopilotOperationsSchema.sql') {
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
        Write-Host "Dashboard schema installed in $db and its rollups built."
    }
    finally { $dbConn.Dispose(); if ($runLock) { $runLock.Dispose() } }
    $conn = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServer;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
    $conn.Open()
    try { $cmd = $conn.CreateCommand(); $cmd.CommandText = (& $grantsFor $db); $cmd.CommandTimeout = 120; [void] $cmd.ExecuteNonQuery() }
    finally { $conn.Dispose() }
    Write-Host "Granted $sqlLogin access to $db."
  }
}
else {
    Write-Host "Database grants written to $grantsFile. Have a DBA run them, or run this again with -GrantDatabaseAccess."
}

# ---- 8. The daily pre-cache -------------------------------------------------------------------
# Every report page built ahead each morning. A task already scheduled is kept unless -PrecacheAt or
# -PrecacheTimeZone is given, as the site's other settings are; -PrecacheAt '' removes it.
$sitePrecache = Join-Path $PhysicalPath 'Invoke-InsightsPrecache.ps1'
if (Test-Path $sitePrecache) {
    Write-Step 'Daily pre-cache'
    $task = Get-ScheduledTask -TaskName 'Copilot Insights pre-cache' -ErrorAction SilentlyContinue
    if (-not $PrecacheAt) { if ($task) { & $sitePrecache -Unregister } else { Write-Host '    not scheduled (-PrecacheAt is empty).' } }
    elseif ($task -and -not $PSBoundParameters.ContainsKey('PrecacheAt') -and -not $PSBoundParameters.ContainsKey('PrecacheTimeZone')) {
        Write-Host "    kept as scheduled: $(@($task.Triggers | ForEach-Object { ([DateTime] $_.StartBoundary).ToString('HH:mm') }) -join ' and ') on this server's clock."
    }
    else { & $sitePrecache -Register -PhysicalPath $PhysicalPath -At $PrecacheAt -TimeZone $PrecacheTimeZone }
}

# ---- 9. The Governance Workbench --------------------------------------------------------------
if ($GovernanceDatabase) {
    # Its own database, by its ordered scripts: the site's account in app_web, the worker's in app_worker.
    Write-Step "Governance Workbench database $GovernanceDatabase"
    $deployDb = Join-Path $governanceKit 'src\Governance.Database\Deploy-Database.ps1'
    $dbArgs = @('-SqlServer', $SqlServer, '-Database', $GovernanceDatabase, '-WebPrincipal', $sqlLogin)
    if ($WorkerAccount) { $dbArgs += '-WorkerPrincipal', $WorkerAccount }
    if ($trust -eq 'true') { $dbArgs += '-TrustServerCertificate' }
    if ($GrantDatabaseAccess) {
        & pwsh -NoProfile -ExecutionPolicy Bypass -File $deployDb @dbArgs | Out-Host
        if ($LASTEXITCODE) { throw "Deploy-Database.ps1 failed for $GovernanceDatabase (exit code $LASTEXITCODE). The site is installed; its Workbench pages fail until the database is there. Fix what it says, then run this again." }
    }
    else {
        Write-Host '    Have a DBA create or upgrade it from the source package''s folder, or run this again with -GrantDatabaseAccess:'
        Write-Host "    pwsh -File governance\src\Governance.Database\Deploy-Database.ps1 $(($dbArgs | ForEach-Object { if ($_ -match '[\s$\\]') { "'$_'" } else { $_ } }) -join ' ')"
    }
    # The worker's read on the Copilot database (its ConnectionStrings:Insights): the nightly seat read takes Copilot
    # activity from there, as the site's manual read does, not from Microsoft's usage report.
    if ($WorkerAccount) {
        $workerGrant = Get-WorkerInsightsGrant -Database $Database -Account $WorkerAccount
        if ($GrantDatabaseAccess) {
            $conn = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlServer;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust"
            $conn.Open()
            try { $cmd = $conn.CreateCommand(); $cmd.CommandText = $workerGrant; $cmd.CommandTimeout = 120; [void] $cmd.ExecuteNonQuery() }
            finally { $conn.Dispose() }
            Write-Host "    $WorkerAccount may read dash.UserDay and bi.DimUser in $Database (Copilot activity for the seat read)."
        }
        else {
            $workerGrantFile = Join-Path $PhysicalPath 'Grant-WorkbenchWorkerAccess.sql'
            Set-Content $workerGrantFile $workerGrant -Encoding utf8
            Write-Host "    The worker's read on $Database (dash.UserDay, bi.DimUser) is written to $workerGrantFile. Have a DBA run it, or run this again with -GrantDatabaseAccess."
        }
    }

    if ($WorkerServer) {
        # The worker, on this server only: the other web servers run the site alone. Its jobs take a SQL lock each
        # (sp_getapplock), so a second copy started by mistake waits rather than running them twice.
        Write-Step "Governance Workbench worker service, as $WorkerAccount"
        $wStage = Join-Path ([IO.Path]::GetTempPath()) ("CopilotGovernanceWorker-" + [guid]::NewGuid().ToString('N'))
        $wSettings = "$wStage.settings.json"
        New-Item -ItemType Directory -Force $wStage | Out-Null
        try {
            $wSrc = (Resolve-Path $workerSource).Path
            if ((Get-Item $wSrc).PSIsContainer -and (Test-Path (Join-Path $wSrc 'Governance.Worker.csproj'))) {
                if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Publishing the worker from source needs the .NET 10 SDK; or give -WorkerPackagePath the release''s copilot-governance-worker zip.' }
                & dotnet publish (Join-Path $wSrc 'Governance.Worker.csproj') -c Release -o $wStage --nologo | Out-Host
                if ($LASTEXITCODE) { throw 'dotnet publish of the worker failed.' }
            }
            elseif ($wSrc -like '*.zip') { Expand-Archive -Path $wSrc -DestinationPath $wStage -Force }
            else { Copy-Item -Path (Join-Path $wSrc '*') -Destination $wStage -Recurse -Force }
            # The release's zip holds the worker in a folder of its own.
            $wApp = $wStage
            if (-not (Test-Path (Join-Path $wApp 'Governance.Worker.exe'))) {
                $inner = Get-ChildItem $wStage -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'Governance.Worker.exe') } | Select-Object -First 1
                if ($inner) { $wApp = $inner.FullName }
            }
            if (-not (Test-Path (Join-Path $wApp 'Governance.Worker.exe'))) { throw "No Governance.Worker.exe in ${workerSource}: is it the published worker?" }
            # The ledger digest's script goes with the worker, where its scheduled task runs it.
            Copy-Item -LiteralPath (Join-Path $governanceKit 'deploy\Save-LedgerDigest.ps1') -Destination $wApp -Force
            $ws = Merge-WorkerSetting -Settings $workerSettings -BaseUrl $workbenchUrl -BaseUrlGiven ([bool] $SiteUrl) `
                -ConnectionString "Server=$SqlServer;Database=$GovernanceDatabase;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust;Application Name=CopilotGovernance-Worker" `
                -InsightsConnectionString "Server=$SqlServer;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=$trust;Application Name=CopilotGovernance-Worker"
            $ws | ConvertTo-Json -Depth 10 | Set-Content $wSettings -Encoding utf8
            $wArgs = @('-PublishPath', $wApp, '-InstallPath', $WorkerPath, '-SettingsFile', $wSettings, '-GmsaAccount', $WorkerAccount)
            if ($ProxyUrl) { $wArgs += '-ProxyUrl', $ProxyUrl }
            & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $governanceKit 'deploy\Install-WorkbenchWorker.ps1') @wArgs | Out-Host
            if ($LASTEXITCODE) { throw "Install-WorkbenchWorker.ps1 failed (exit code $LASTEXITCODE). The site is installed; fix what it says, then run this again with -WorkerServer." }
        }
        finally {
            Remove-Item $wStage -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item $wSettings -Force -ErrorAction SilentlyContinue
        }

        # The daily ledger digest, on the worker's server and as its account, to write-once storage. A task already
        # scheduled is kept unless -LedgerDigestPath is given again.
        $digestTask = 'Copilot Governance ledger digest'
        if ($LedgerDigestPath) {
            $digestArgs = Get-LedgerDigestArgument -Script (Join-Path $WorkerPath 'Save-LedgerDigest.ps1') -SqlServer $SqlServer -Database $GovernanceDatabase `
                -DigestPath $LedgerDigestPath -TrustServerCertificate ($trust -eq 'true')
            $action = New-ScheduledTaskAction -Execute (Get-Command pwsh).Source -Argument $digestArgs -WorkingDirectory $WorkerPath
            $trigger = New-ScheduledTaskTrigger -Daily -At $LedgerDigestAt
            $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
            # LogonType Password is how Task Scheduler is told to fetch a gMSA's password from AD.
            $principal = New-ScheduledTaskPrincipal -UserId $WorkerAccount -LogonType Password -RunLevel Limited
            Register-ScheduledTask -TaskName $digestTask -TaskPath '\' -Action $action -Trigger $trigger -Settings $taskSettings -Principal $principal `
                -Description "Saves a digest of $GovernanceDatabase's ledger to $LedgerDigestPath (governance\deploy\Save-LedgerDigest.ps1)." -Force | Out-Null
            Write-Host "    '$digestTask' runs daily at $LedgerDigestAt as $WorkerAccount, saving to $LedgerDigestPath."
        }
        elseif (Get-ScheduledTask -TaskName $digestTask -ErrorAction SilentlyContinue) { Write-Host "    '$digestTask' kept as scheduled." }
        else { Write-Warning "No -LedgerDigestPath: the ledger digest is not scheduled. Give a share on write-once storage that $WorkerAccount can write to." }
    }
}

$url = "{0}://{1}{2}/{3}" -f $proto, $(if ($HostName) { $HostName } else { $env:COMPUTERNAME }), $(if (($https -and $Port -eq 443) -or (-not $https -and $Port -eq 80)) { '' } else { ":$Port" }), $(if ($appName) { "$appName/" } else { '' })
Write-Host ''
Write-Host "Copilot Insights is at $url" -ForegroundColor Green
if ($GovernanceDatabase) { Write-Host "The Governance Workbench is at $workbenchUrl/workbench (the address its notices link to)." -ForegroundColor Green }
Write-Host 'Next: sign in as a bootstrap administrator, open Admin > Access, set the top of the organization, and grant roles.'
if ($HostName -and $signIn -ne 'Entra') { Write-Host "For Kerberos sign-in on $HostName, register its SPN on the app-pool account: setspn -S HTTP/$HostName $(if ($AppPoolIdentity) { $AppPoolIdentity } else { "$env:USERDOMAIN\$env:COMPUTERNAME`$" })" }
