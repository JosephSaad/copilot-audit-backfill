<#
.SYNOPSIS
    Builds a demo database for the Copilot Insights dashboard: a synthetic
    company of 5,000 people (-People) with six months of Copilot use, and its
    spend on Fabric, the Power Platform, GitHub Copilot and Foundry (2.6.6).

.DESCRIPTION
    Creates the database (replacing it with -Force), runs the repository's
    schema scripts, loads New-InsightsDemoData.sql, then builds sessions, the
    bi schema and the dashboard tables exactly as a real install would. It
    sets the CEO as the top of the organization, names the demo's agents and
    sensitivity labels, and grants a persona role to one person per persona,
    so every page and the security model can be tried as each of them. It also
    adds a license pool, pipeline runs (one failed), training records, targets
    and a digest, for the operations pages. From 2.6.6 it adds what a real tenant
    has beyond its staff (New-InsightsDemoTenant.sql: service and guest accounts,
    leave dates, the agent workbook and its agents' use) and loads the workbook
    as the nightly import would, with Copilot Studio's authoring records
    (New-InsightsDemoAgents.sql), so no page is empty.

    Point the dashboard's connection string at this database. To see it as one
    of its people, map your Windows account to their UPN under
    Insights:IdentityMap in appsettings.Development.json and restart the site:
    the demo's people have no SID on record, so the mapping is allowed.

.EXAMPLE
    .\dashboard\demo\New-InsightsDemo.ps1 -SqlServer localhost -Database CopilotInsightsDemo -TrustServerCertificate -Force
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $SqlServer = 'localhost',
    [string] $Database = 'CopilotInsightsDemo',
    [switch] $TrustServerCertificate,
    [switch] $Force,
    [string[]] $Admins = @(),  # UPNs or DOMAIN\user names to make Admin in the demo
    [ValidateRange(400, 5200)][int] $People = 5000   # 2,317 is the demo before 2.6.6; names stay unique up to 5,200
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sqlDir = Join-Path $repo 'sql'

function Get-Conn([string] $Catalog) {
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $b['Data Source'] = $SqlServer; $b['Initial Catalog'] = $Catalog; $b['Integrated Security'] = $true
    $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] $TrustServerCertificate
    $c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    $c.add_InfoMessage({ param($s, $e) foreach ($m in $e.Errors) { if ($m.Message -notlike 'Warning: Null value*') { Write-Host "    $($m.Message)" -ForegroundColor DarkGray } } })
    $c.Open(); return $c
}
function Invoke-Batch([System.Data.SqlClient.SqlConnection] $Conn, [string] $Text) {
    foreach ($b in [regex]::Split($Text, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
        if ([string]::IsNullOrWhiteSpace($b)) { continue }
        $cmd = $Conn.CreateCommand(); $cmd.CommandText = $b; $cmd.CommandTimeout = 0
        [void] $cmd.ExecuteNonQuery()
    }
}
function Step([string] $Name, [scriptblock] $Body) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Body
    Write-Host ("   {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

Step "Database $Database" {
    $m = Get-Conn 'master'
    try {
        $exists = $m.CreateCommand(); $exists.CommandText = "SELECT DB_ID(N'$Database')"
        if ($exists.ExecuteScalar() -isnot [DBNull]) {
            if (-not $Force) { throw "$Database already exists. Pass -Force to replace it." }
            Invoke-Batch $m "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database];"
        }
        Invoke-Batch $m "CREATE DATABASE [$Database]; ALTER DATABASE [$Database] SET RECOVERY SIMPLE;"
    }
    finally { $m.Dispose() }
}

$conn = Get-Conn $Database
try {
    Step 'Schema scripts' {
        foreach ($s in 'Create-CopilotAuditSchema.sql', 'Create-CopilotFlattenedSchema.sql', 'Create-CopilotEnrichmentSchema.sql',
                       'Create-LicenseEventSchema.sql', 'Create-LicenseEventProcedure.sql', 'Create-CopilotSessionSchema.sql', 'Create-CopilotSessionProcedure.sql',
                       'Create-CopilotBiSchema.sql', 'Create-CopilotBiProcedure.sql', 'Create-AgentEventProcedure.sql', 'Create-CopilotExternalUsageSchema.sql', 'Create-CopilotActivityRollup.sql',
                       'Create-CopilotDashboardSchema.sql', 'Create-CopilotDashboardProcedure.sql', 'Create-CopilotOperationsSchema.sql') {
            Write-Host "   $s"
            Invoke-Batch $conn (Get-Content -LiteralPath (Join-Path $sqlDir $s) -Raw)
        }
    }
    Step 'Demo company and six months of use' {
        $cmd = $conn.CreateCommand(); $cmd.CommandTimeout = 0
        $cmd.CommandText = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-InsightsDemoData.sql') -Raw).Replace('DECLARE @people INT = 5000;', "DECLARE @people INT = $People;")
        $t = [System.Data.DataTable]::new(); $t.Load($cmd.ExecuteReader())
        $r = $t.Rows[0]
        Write-Host ("   {0:N0} people, {1:N0} licensed today, {2:N0} license events, {3:N0} sessions, {4:N0} interactions by {5:N0} people" -f `
                    $r.People, $r.LicensedToday, $r.LicenseEvents, $r.Sessions, $r.Interactions, $r.ActiveEver)
    }
    Step 'Service accounts, guests, leave dates, the agent workbook and its agents'' use' {
        $cmd = $conn.CreateCommand(); $cmd.CommandTimeout = 0
        $cmd.CommandText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-InsightsDemoTenant.sql') -Raw
        $t = [System.Data.DataTable]::new(); $t.Load($cmd.ExecuteReader())
        $r = $t.Rows[0]
        Write-Host ("   {0:N0} accounts that are not people, {1:N0} guests, {2:N0} leave dates, {3:N0} agents in the workbook, {4:N0} people paying in credits, {5:N0} interactions" -f `
                    $r.Accounts, $r.Guests, $r.Leaving, $r.Agents, $r.CreditUsers, $r.Interactions)
    }
    $conn.Dispose(); $conn = Get-Conn $Database   # a fresh session: no temp tables left over from the seed
    Step 'Sessions' { Invoke-Batch $conn "EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = N'$Database';" }
    Step 'bi star schema' { Invoke-Batch $conn "EXEC bi.usp_RefreshBi @LicenseEventDatabase = N'$Database';" }
    Step 'License pool, pipeline runs, training and targets' {
        Invoke-Batch $conn (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-InsightsDemoOperations.sql') -Raw)
    }
    Step 'Fabric, Power Platform, GitHub Copilot and Foundry costs' {
        $cmd = $conn.CreateCommand(); $cmd.CommandTimeout = 0
        $cmd.CommandText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-InsightsDemoCosts.sql') -Raw
        $t = [System.Data.DataTable]::new(); $t.Load($cmd.ExecuteReader())
        $r = $t.Rows[0]
        Write-Host ("   {0:N0} Fabric capacity-days, {1:N0} flows, {2:N0} people's Power Platform activity, {3:N0} GitHub Copilot seats, {4:N0} Foundry cost-days" -f `
                    $r.FabricDays, $r.Flows, $r.Activity, $r.Seats, $r.FoundryCostDays)
    }
    Step 'Dashboard settings, names and demo roles' {
        $admins = ($Admins | ForEach-Object { "(N'$($_.Replace("'", "''"))', 'User', 'Admin', N'Demo administrator', N'demo')" }) -join ",`n"
        Invoke-Batch $conn @"
UPDATE dash.Setting SET Value = N'avery.morgan@contoso.com', UpdatedBy = N'demo' WHERE Name = 'OrgTopUpn';
MERGE dash.SensitivityLabel AS t
USING (VALUES (N'5e1f0c2a-0000-4000-8000-000000000000', N'Public', 1), (N'5e1f0c2a-0000-4000-8000-000000000001', N'General', 2),
              (N'5e1f0c2a-0000-4000-8000-000000000002', N'Confidential', 3), (N'5e1f0c2a-0000-4000-8000-000000000003', N'Highly Confidential', 4)) AS s (LabelId, Name, Rank)
ON t.LabelId = s.LabelId
WHEN NOT MATCHED THEN INSERT (LabelId, Name, Rank, UpdatedBy) VALUES (s.LabelId, s.Name, s.Rank, N'demo');
INSERT INTO dash.AgentAlias (AgentKey, FriendlyName, Owner, UpdatedBy)
SELECT a.AgentKey, v.FriendlyName, v.Owner, N'demo'
FROM bi.DimAgent AS a
JOIN (VALUES (N'crd20_expenseAssistant', N'Expense Assistant', N'Finance'), (N'crd20_itHelpdesk', N'IT Helpdesk', N'IT Operations'),
             (N'crd20_hrPolicyHelper', N'HR Policy Helper', N'People'), (N'crd20_salesProposalWriter', N'Sales Proposal Writer', N'Sales Operations'),
             (N'crd20_contractReviewer', N'Contract Reviewer', N'Legal')) AS v (Schema_, FriendlyName, Owner) ON a.AgentName = v.Schema_
WHERE NOT EXISTS (SELECT 1 FROM dash.AgentAlias AS x WHERE x.AgentKey = a.AgentKey);
/* One person per persona role: the director of the department that would own it. */
INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy)
SELECT d.UserPrincipalName, 'User', v.Role, CONCAT(N'Demo: director of ', v.Dept), N'demo'
FROM (VALUES ('Licensing', N'Procurement'), ('Security', N'Security'), ('Compliance', N'Compliance'), ('Agents', N'Data & Analytics'),
             ('Enablement', N'Learning'), ('Adoption', N'Employee Experience')) AS v (Role, Dept)
JOIN audit.UserDirectory AS d ON d.Department = v.Dept AND d.OrgDepth = 2
WHERE NOT EXISTS (SELECT 1 FROM dash.RoleGrant AS g WHERE g.Principal = d.UserPrincipalName AND g.Role = v.Role);
$(if ($admins) { "INSERT INTO dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) VALUES $admins;" })
"@
    }
    Step 'Agent workbook load and authoring records' {
        $cmd = $conn.CreateCommand(); $cmd.CommandTimeout = 0
        $cmd.CommandText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-InsightsDemoAgents.sql') -Raw
        $t = [System.Data.DataTable]::new(); $t.Load($cmd.ExecuteReader())
        $r = $t.Rows[0]
        Write-Host ("   {0:N0} agents ({1:N0} seen in the audit log, {2:N0} in production), {3:N0} parent and child links, {4:N0} authoring records" -f `
                    $r.Agents, $r.Matched, $r.InProduction, $r.Links, $r.AuthoringEvents)
    }
    Step 'Dashboard refresh' { Invoke-Batch $conn 'EXEC dash.usp_RefreshDashboard;' }
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @'
SELECT g.Role, g.Principal, p.DisplayName, p.Level FROM dash.RoleGrant AS g LEFT JOIN dash.Person AS p ON p.Upn = g.Principal
UNION ALL
SELECT 'Level ' + CAST(p.Level AS VARCHAR(2)), p.Upn, p.DisplayName, p.Level FROM dash.Person AS p WHERE p.Level <= 2
ORDER BY 4, 1;
'@
    $t = [System.Data.DataTable]::new(); $t.Load($cmd.ExecuteReader())
    Write-Host "`nPeople to see the demo as (map your account to one in Insights:IdentityMap):" -ForegroundColor Green
    $t | Format-Table Role, Principal, DisplayName, Level -AutoSize | Out-String | Write-Host
    Write-Host 'Any other address in audit.UserDirectory works too: a team manager sees their team, a staff member only themselves.'
}
finally { $conn.Dispose() }
