<#
.SYNOPSIS
    Records who holds a Copilot license, from Microsoft Graph: the one fact
    about users that neither the audit records nor on-premises Active
    Directory can supply.

.DESCRIPTION
    Reads Microsoft Graph and writes ONLY to audit.CopilotLicense. It never
    touches the backfill tables, so it can run on its own schedule and a
    mistake here cannot corrupt the audit archive.

    Licenses only, since 2.0.0. Title, department, division, office, manager
    and the management chain come from on-premises Active Directory instead,
    by tools/Export-UsersWithHierarchy-ToSql.ps1 - so this needs just two
    Graph permissions, and none for profiles, managers or administrative
    units. Before 2.0.0 it also filled audit.UserProfile and
    audit.AdministrativeUnit; nothing reads those now.

    Only the users that actually appear in audit.CopilotInteraction are looked
    up, not the whole directory - at most a few thousand lookups rather than
    every account in the tenant - and everyone already recorded as licensed,
    so a license taken back is recorded too (2.4.12). Pass -AllLicensedUsers
    to also pull in users who hold a license but have never interacted, which
    is what the shelfware report needs.

    Two things Graph cannot tell you, and which this records honestly rather
    than guessing (the license-event databases fill in the first):

      * License history. assignedPlans carries the date the CURRENT assignment
        began, so "licensed since March" is answerable. A license granted and
        later removed leaves no trace at all.
      * Users who have left. Audit records outlive directory objects, so a
        departed user resolves to nothing. Those rows get
        DirectoryStatus = 'NotFound' rather than being reported as unlicensed,
        which would be a different claim.

.PARAMETER SqlServer / -Database / -ConnectionString / -SqlCredential / -TrustServerCertificate
    Where the audit database lives. Same shape as Invoke-CopilotAuditBackfill.ps1.

.PARAMETER TenantId / -ClientId / -CertificateThumbprint
    Graph sign-in. With -CertificateThumbprint the connection is app-only,
    which needs no interactive prompt; without it, delegated sign-in is used.

.PARAMETER Scopes
    Delegated scopes to request. The defaults are the least that works:
    User.Read.All for each user's assigned licenses and plans, account state
    and hire date, and LicenseAssignment.Read.All for the tenant's SKUs and
    their seat counts (/subscribedSkus). App-only uses the same two as
    application permissions.

.PARAMETER AllLicensedUsers
    Also enumerate every user holding a Copilot license, including those with
    no interactions. Needed for "licensed but never used".

.PARAMETER IncludeLeaveDates
    Also read each person's employeeLeaveDateTime, the day HR says they leave, for
    Licensing > Departures ahead (2.4.3). It needs the Graph application permission
    User-LifeCycleInfo.Read.All on top of the others; without it Graph refuses.

.PARAMETER SkipProfiles
    No effect since 2.0.0, which reads licenses only. Accepted so existing
    scheduled tasks keep working.

.PARAMETER CopilotSuiteSku
    The suites that carry Microsoft 365 Copilot inside them (2.6.6), as SKU
    part number patterns (-like, case-insensitive): Microsoft 365 E7 by default.
    A person who holds one is licensed for Copilot, as with the Copilot add-on,
    and the license pool marks the suite (audit.LicenseSku.CopilotInSuite), so
    the dashboard never offers to revoke a Copilot that cannot be taken off
    alone, nor prices it as an add-on. Check the part number License pool shows
    for your E7 and pass it here if it differs; @() turns it off.

.PARAMETER MaxDropPercent
    The delete guard (2.6.5). audit.UserLicenseSku, every license each person
    looked up holds, is replaced whole each run. When the new run has more than
    this percentage fewer rows than the table holds (LicenseMaxDropPercent in
    the settings file, 15 by default), nothing about the people is written: the
    licenses stay as the last good run left them. 0 turns the guard off.

.PARAMETER AllowDrop
    Write a run the guard would refuse, once, when the drop is real.

.EXAMPLE
    .\tools\Update-CopilotAuditEnrichment.ps1 -SqlServer SQLPROD01 -Database CopilotAudit `
        -TenantId contoso.onmicrosoft.com -AllLicensedUsers

.EXAMPLE
    .\tools\Update-CopilotAuditEnrichment.ps1 -SqlServer SQLPROD01 -Database CopilotAudit `
        -TenantId contoso.onmicrosoft.com -ClientId <app-id> -CertificateThumbprint <thumbprint>
#>
[CmdletBinding()]
param(
    [string]   $SqlServer,
    [string]   $Database,
    [string]   $ConnectionString,
    [pscredential] $SqlCredential,
    [switch]   $TrustServerCertificate,

    [string]   $TenantId,
    [string]   $ClientId,
    [string]   $CertificateThumbprint,
    [string[]] $Scopes = @('User.Read.All', 'LicenseAssignment.Read.All'),

    [switch]   $AllLicensedUsers,
    [switch]   $IncludeLeaveDates,
    [switch]   $SkipProfiles,

    [ValidateRange(1, 20)] [int] $BatchSize = 20,
    [string]   $CopilotSkuMatch = 'COPILOT',
    [string[]] $CopilotSuiteSku = @('Microsoft_365_E7*', 'M365_E7*', 'SPE_E7*'),

    [ValidateRange(0, 100)] [double] $MaxDropPercent = 15,
    [switch]   $AllowDrop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DropGuard.ps1')

# A Modules folder beside the tool wins, exactly as the backfill script does,
# so this works on a server with no gallery access too.
$script:ToolRoot = Split-Path $PSScriptRoot -Parent
$script:BundledModules = Join-Path $script:ToolRoot 'Modules'
if (Test-Path -LiteralPath $script:BundledModules -PathType Container) {
    $sep = [System.IO.Path]::PathSeparator
    $existing = ($env:PSModulePath -split [regex]::Escape($sep)) | Where-Object { $_ -ne $script:BundledModules }
    $env:PSModulePath = (@($script:BundledModules) + $existing) -join $sep
}

function Write-Log {
    param([Parameter(Mandatory)][string] $Message,
          [ValidateSet('INFO', 'WARN', 'ERROR')][string] $Level = 'INFO')
    $line = '{0:yyyy-MM-dd HH:mm:ss}Z [{1,-5}] {2}' -f [datetime]::UtcNow, $Level, $Message
    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        default { Write-Host $line }
    }
}

# ===========================================================================
# SQL
# ===========================================================================

function New-EnrichmentConnectionString {
    # See Invoke-CopilotAuditBackfill.ps1: assigning .ConnectionString goes
    # through the builder's keyword indexer and throws.
    $csb = if ($ConnectionString) {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    else {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    }
    if (-not $ConnectionString) {
        if (-not $SqlServer -or -not $Database) { throw 'Specify -SqlServer and -Database (or -ConnectionString).' }
        $csb['Data Source'] = $SqlServer
        $csb['Initial Catalog'] = $Database
        if ($SqlCredential) {
            $csb['User ID'] = $SqlCredential.UserName
            $csb['Password'] = $SqlCredential.GetNetworkCredential().Password
        }
        else { $csb['Integrated Security'] = $true }
        $csb['Encrypt'] = $true
        $csb['TrustServerCertificate'] = [bool] $TrustServerCertificate
    }
    $csb['Application Name'] = 'CopilotAuditEnrichment'
    return $csb.ConnectionString
}

function Invoke-EnrichmentSql {
    param(
        [Parameter(Mandatory)][string] $Sql,
        [ValidateSet('NonQuery', 'Scalar', 'Table')][string] $As = 'NonQuery',
        [hashtable] $Parameters = @{},
        [int] $TimeoutSeconds = 300
    )
    $conn = [System.Data.SqlClient.SqlConnection]::new($script:SqlConnStr)
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        $cmd.CommandTimeout = $TimeoutSeconds
        foreach ($k in $Parameters.Keys) {
            $v = $Parameters[$k]
            [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $v) { [DBNull]::Value } else { $v }))
        }
        switch ($As) {
            'Scalar' { return $cmd.ExecuteScalar() }
            'Table'  {
                $dt = [System.Data.DataTable]::new()
                $reader = $cmd.ExecuteReader()
                try { $dt.Load($reader) } finally { $reader.Dispose() }
                return ,$dt        # the comma stops PowerShell unrolling it into DataRows
            }
            default  { [void] $cmd.ExecuteNonQuery() }
        }
    }
    finally { $conn.Dispose() }
}

function Save-RowBatch {
    <#
        Upserts rows into a lookup table with one MERGE per chunk.

        Two limits shape this. SQL Server allows 2,100 parameters per command,
        so the chunk size is derived from the column count rather than fixed.
        And AddWithValue infers NVARCHAR for DBNull, which clashes with BIT and
        DATETIME2 columns, so every parameter is declared with an explicit type.
    #>
    param(
        [Parameter(Mandatory)][string] $Table,
        [Parameter(Mandatory)][string] $KeyColumn,
        [Parameter(Mandatory)][hashtable] $ColumnTypes,   # ordered name -> SqlDbType
        [Parameter(Mandatory)][string[]] $ColumnOrder,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Rows
    )
    $all = @($Rows)
    if ($all.Count -eq 0) { return 0 }

    $colCount = $ColumnOrder.Count
    $chunkSize = [math]::Max(1, [math]::Floor(2000 / $colCount))

    $colList = ($ColumnOrder | ForEach-Object { "[$_]" }) -join ', '
    $setList = ($ColumnOrder | Where-Object { $_ -ne $KeyColumn } | ForEach-Object { "t.[$_] = s.[$_]" }) -join ', '
    $srcList = ($ColumnOrder | ForEach-Object { "s.[$_]" }) -join ', '

    $written = 0
    for ($offset = 0; $offset -lt $all.Count; $offset += $chunkSize) {
        $last = [math]::Min($offset + $chunkSize, $all.Count) - 1
        $chunk = @($all[$offset..$last])   # @() again: a one-row chunk is a bare object

        $conn = [System.Data.SqlClient.SqlConnection]::new($script:SqlConnStr)
        try {
            $conn.Open()
            $cmd = $conn.CreateCommand()
            $cmd.CommandTimeout = 300

            $tuples = New-Object System.Collections.Generic.List[string]
            for ($i = 0; $i -lt $chunk.Count; $i++) {
                $names = New-Object System.Collections.Generic.List[string]
                for ($c = 0; $c -lt $colCount; $c++) {
                    $col = $ColumnOrder[$c]
                    $pname = '@p{0}_{1}' -f $i, $c
                    $param = $cmd.Parameters.Add($pname, $ColumnTypes[$col])
                    $v = $chunk[$i].$col
                    $param.Value = $(if ($null -eq $v -or "$v" -eq '') { [DBNull]::Value } else { $v })
                    $names.Add($pname)
                }
                $tuples.Add('(' + ($names -join ', ') + ')')
            }

            $cmd.CommandText = @"
MERGE $Table WITH (HOLDLOCK) AS t
USING (VALUES $($tuples -join ', ')) AS s ($colList)
ON t.[$KeyColumn] = s.[$KeyColumn]
WHEN MATCHED THEN UPDATE SET $setList, t.UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN INSERT ($colList) VALUES ($srcList);
"@
            [void] $cmd.ExecuteNonQuery()
            $written += $chunk.Count
        }
        finally { $conn.Dispose() }
    }
    return $written
}

# ===========================================================================
# Graph
# ===========================================================================

function Connect-EnrichmentGraph {
    foreach ($cmd in 'Connect-MgGraph', 'Invoke-MgGraphRequest') {
        if (-not (Get-Command -Name $cmd -ErrorAction SilentlyContinue)) {
            throw "$cmd not found. Install Microsoft.Graph.Authentication, or save it into the Modules folder beside the scripts (tools\Save-CopilotAuditModules.ps1)."
        }
    }
    $connect = @{ NoWelcome = $true }
    if ($TenantId) { $connect['TenantId'] = $TenantId }
    if ($CertificateThumbprint) {
        if (-not $ClientId) { throw '-CertificateThumbprint needs -ClientId (app-only sign-in).' }
        $connect['ClientId'] = $ClientId
        $connect['CertificateThumbprint'] = $CertificateThumbprint
        Write-Log "Signing in app-only as $ClientId."
    }
    else {
        if ($ClientId) { $connect['ClientId'] = $ClientId }
        $connect['Scopes'] = $Scopes
        Write-Log ('Signing in delegated, scopes: ' + ($Scopes -join ', ') + '.')
    }
    Connect-MgGraph @connect
}

function Get-GraphPaged {
    <# Follows @odata.nextLink and returns every value across all pages. #>
    param([Parameter(Mandatory)][string] $Uri)
    $out = New-Object System.Collections.Generic.List[object]
    $next = $Uri
    while ($next) {
        $resp = Invoke-MgGraphRequest -Method GET -Uri $next -OutputType PSObject
        if ($resp.PSObject.Properties.Name -contains 'value') {
            foreach ($v in $resp.value) { $out.Add($v) }
        }
        $next = if ($resp.PSObject.Properties.Name -contains '@odata.nextLink') { $resp.'@odata.nextLink' } else { $null }
    }
    return ,$out
}

function Get-Prop {
    <# Reads a property that may be absent, from either a hashtable or an object. #>
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Get-CopilotPlanId {
    <#
        The service plans that belong to the Copilot SKUs alone, by id. A user's assignedPlans names each plan by
        this id and by the service behind it ("MicrosoftOffice", "exchange" ...), never by the plan's name, so the
        id is the only way to tell which of a user's plans came with Copilot (before, the service's name was
        searched for "COPILOT", which most Copilot plans' services do not contain, and the date was left empty).
        A plan another SKU in the tenant carries too is left out: its date says nothing about Copilot.
    #>
    param([object[]] $Sku, [hashtable] $CopilotSku)
    $mine = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $others = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($s in @($Sku)) {
        # Not "$into = if ...": an empty set through the pipeline comes out as nothing.
        $isCopilot = $CopilotSku.ContainsKey("$(Get-Prop $s 'skuId')")
        foreach ($p in @(Get-Prop $s 'servicePlans')) {
            if ($null -eq $p) { continue }
            $id = "$(Get-Prop $p 'servicePlanId')"
            if (-not $id) { continue }
            if ($isCopilot) { [void] $mine.Add($id) } else { [void] $others.Add($id) }
        }
    }
    $mine.ExceptWith($others)
    , $mine
}

function Get-LicensedSince {
    <#
        Since when a user has held Copilot: the earliest assignment, not deleted, among their plans that belong
        to a Copilot SKU (by plan id), or whose service is named for Copilot (the rule before 2.4.8, kept beside it).
    #>
    param([object[]] $AssignedPlan, [System.Collections.Generic.HashSet[string]] $CopilotPlan, [string] $ServiceMatch)
    $since = $null; $capability = $null
    foreach ($ap in @($AssignedPlan)) {
        if ($null -eq $ap) { continue }
        $svc = "$(Get-Prop $ap 'service')"
        $id = "$(Get-Prop $ap 'servicePlanId')"
        $isCopilot = ($id -and $CopilotPlan -and $CopilotPlan.Contains($id)) -or
            ($svc -and $ServiceMatch -and $svc.ToUpperInvariant().Contains($ServiceMatch.ToUpperInvariant()))
        if (-not $isCopilot) { continue }
        $cap = "$(Get-Prop $ap 'capabilityStatus')"
        if ($cap -eq 'Deleted') { continue }
        $d = ConvertTo-UtcOrNull (Get-Prop $ap 'assignedDateTime')
        if ($null -eq $capability) { $capability = $cap }
        if ($d -and ($null -eq $since -or $d -lt $since)) { $since = $d; $capability = $cap }
    }
    [pscustomobject]@{ Since = $since; Capability = $capability }
}

function ConvertTo-LicensePoolRow {
    <#
        The license pool from /subscribedSkus: one row per SKU with the seats
        bought (prepaidUnits) and assigned (consumedUnits). Pure, for the tests.
    #>
    param([object[]] $Sku, [hashtable] $CopilotSku, [hashtable] $SuiteSku = @{})
    foreach ($s in @($Sku)) {
        $id = "$(Get-Prop $s 'skuId')"
        $guid = [guid]::Empty
        if (-not [guid]::TryParse($id, [ref] $guid)) {
            if ($id) { Write-Log "License pool: skipped SKU '$id', which is not a GUID." -Level WARN }
            continue
        }
        $pre = Get-Prop $s 'prepaidUnits'
        [pscustomobject]@{
            SkuId            = $guid
            SkuPartNumber    = "$(Get-Prop $s 'skuPartNumber')"
            IsCopilot        = [bool] $CopilotSku.ContainsKey($id)
            CopilotInSuite   = [bool] $SuiteSku.ContainsKey($id)
            Enabled          = [int] (Get-Prop $pre 'enabled')
            Warning          = [int] (Get-Prop $pre 'warning')
            Suspended        = [int] (Get-Prop $pre 'suspended')
            LockedOut        = [int] (Get-Prop $pre 'lockedOut')
            Consumed         = [int] (Get-Prop $s 'consumedUnits')
            CapabilityStatus = $(if (Get-Prop $s 'capabilityStatus') { "$(Get-Prop $s 'capabilityStatus')" } else { $null })
        }
    }
}

function Save-LicensePool {
    <# Replaces audit.LicenseSku and today's rows of audit.LicenseSkuDay, in one transaction. #>
    param([object[]] $Rows)
    $all = @($Rows)
    if ($all.Count -eq 0) { return 0 }
    $conn = [System.Data.SqlClient.SqlConnection]::new($script:SqlConnStr)
    $conn.Open()
    $tx = $conn.BeginTransaction()
    try {
        $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx
        $cmd.CommandText = "IF OBJECT_ID(N'audit.LicenseSku', N'U') IS NOT NULL DELETE FROM audit.LicenseSku; " +
                           "IF OBJECT_ID(N'audit.LicenseSkuDay', N'U') IS NOT NULL DELETE FROM audit.LicenseSkuDay WHERE SnapshotDate = CAST(SYSUTCDATETIME() AS DATE);"
        [void] $cmd.ExecuteNonQuery()
        foreach ($r in $all) {
            $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx
            $cmd.CommandText = @'
IF OBJECT_ID(N'audit.LicenseSku', N'U') IS NOT NULL
    INSERT INTO audit.LicenseSku (SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed, CapabilityStatus, CopilotInSuite)
    VALUES (@id, @part, @cop, @en, @warn, @sus, @lock, @used, @cap, @suite);
IF OBJECT_ID(N'audit.LicenseSkuDay', N'U') IS NOT NULL
    INSERT INTO audit.LicenseSkuDay (SnapshotDate, SkuId, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, LockedOut, Consumed)
    VALUES (CAST(SYSUTCDATETIME() AS DATE), @id, @part, @cop, @en, @warn, @sus, @lock, @used);
'@
            $cmd.Parameters.Add('@id', [System.Data.SqlDbType]::UniqueIdentifier).Value = $r.SkuId
            $cmd.Parameters.Add('@part', [System.Data.SqlDbType]::NVarChar, 100).Value = $r.SkuPartNumber
            $cmd.Parameters.Add('@cop', [System.Data.SqlDbType]::Bit).Value = $r.IsCopilot
            $cmd.Parameters.Add('@suite', [System.Data.SqlDbType]::Bit).Value = [bool] $r.CopilotInSuite
            $cmd.Parameters.Add('@en', [System.Data.SqlDbType]::Int).Value = $r.Enabled
            $cmd.Parameters.Add('@warn', [System.Data.SqlDbType]::Int).Value = $r.Warning
            $cmd.Parameters.Add('@sus', [System.Data.SqlDbType]::Int).Value = $r.Suspended
            $cmd.Parameters.Add('@lock', [System.Data.SqlDbType]::Int).Value = $r.LockedOut
            $cmd.Parameters.Add('@used', [System.Data.SqlDbType]::Int).Value = $r.Consumed
            $cmd.Parameters.Add('@cap', [System.Data.SqlDbType]::NVarChar, 32).Value = $(if ($r.CapabilityStatus) { $r.CapabilityStatus } else { [DBNull]::Value })
            [void] $cmd.ExecuteNonQuery()
        }
        $tx.Commit()
    }
    catch { $tx.Rollback(); throw }
    finally { $conn.Dispose() }
    $all.Count
}

function Save-UserSkus {
    <# Replaces audit.UserLicenseSku with every SKU each looked-up person holds, in one transaction. #>
    param([System.Data.DataTable] $Table)
    $conn = [System.Data.SqlClient.SqlConnection]::new($script:SqlConnStr)
    $conn.Open()
    $tx = $conn.BeginTransaction()
    try {
        $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx
        $cmd.CommandText = 'DELETE FROM audit.UserLicenseSku;'
        $cmd.CommandTimeout = 600
        [void] $cmd.ExecuteNonQuery()
        $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($conn, [System.Data.SqlClient.SqlBulkCopyOptions]::Default, $tx)
        $bulk.DestinationTableName = 'audit.UserLicenseSku'
        $bulk.BulkCopyTimeout = 600
        [void] $bulk.ColumnMappings.Add('UserPrincipalName', 'UserPrincipalName')
        [void] $bulk.ColumnMappings.Add('SkuId', 'SkuId')
        $bulk.WriteToServer($Table)
        $tx.Commit()
    }
    catch { $tx.Rollback(); throw }
    finally { $conn.Dispose() }
    $Table.Rows.Count
}

function ConvertTo-UtcOrNull {
    param($Value)
    if ($null -eq $Value -or "$Value" -eq '') { return $null }
    if ($Value -is [datetime]) { return ([datetime]$Value).ToUniversalTime() }
    $parsed = [datetime]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal
    if ([datetime]::TryParse("$Value", [cultureinfo]::InvariantCulture, $styles, [ref] $parsed)) { return $parsed }
    return $null
}

# ===========================================================================
# Main
# ===========================================================================

function Invoke-Main {
    $script:SqlConnStr = New-EnrichmentConnectionString

    Invoke-EnrichmentSql -Sql 'SELECT 1' -As Scalar | Out-Null
    Write-Log "Connected to $(if ($Database) { "$SqlServer/$Database" } else { 'SQL Server' })."

    $schema = Get-Content -LiteralPath (Join-Path $script:ToolRoot 'sql' 'Create-CopilotEnrichmentSchema.sql') -Raw
    Invoke-EnrichmentSql -Sql $schema
    Write-Log 'Enrichment tables ready.'

    if ($SkipProfiles) { Write-Log '-SkipProfiles has no effect since 2.0.0: this reads licenses only.' -Level WARN }

    Connect-EnrichmentGraph

    # ---- Which Copilot SKUs exist in this tenant ---------------------------
    $skus = Get-GraphPaged -Uri '/v1.0/subscribedSkus'
    $copilotSkus = @{}
    # The suites with Copilot inside (E7): licensed like the add-on, and marked so the dashboard prices and
    # recommends them as part of the suite.
    $suiteSkus = @{}
    foreach ($s in $skus) {
        $part = "$(Get-Prop $s 'skuPartNumber')"
        if ($part -and $part.ToUpperInvariant().Contains($CopilotSkuMatch.ToUpperInvariant())) {
            $copilotSkus["$(Get-Prop $s 'skuId')"] = $part
        }
        elseif ($part -and @($CopilotSuiteSku | Where-Object { $_ -and $part -like $_ }).Count -gt 0) {
            $copilotSkus["$(Get-Prop $s 'skuId')"] = $part
            $suiteSkus["$(Get-Prop $s 'skuId')"] = $part
        }
    }
    if ($suiteSkus.Count -gt 0) {
        Write-Log ('Suites with Copilot inside: ' + (($suiteSkus.Values | Sort-Object -Unique) -join ', ') + '. Their holders are licensed for Copilot.')
    }
    if ($copilotSkus.Count -eq 0) {
        Write-Log "No subscribed SKU matched '$CopilotSkuMatch'. Every user will be recorded as unlicensed." -Level WARN
    }
    else {
        Write-Log ('Copilot SKUs in this tenant: ' + (($copilotSkus.Values | Sort-Object -Unique) -join ', ') + '.')
    }
    $copilotPlans = Get-CopilotPlanId -Sku $skus -CopilotSku $copilotSkus
    Write-Log "The Copilot SKUs carry $($copilotPlans.Count) service plan(s) of their own: a user's 'licensed since' is the earliest of them."
    # The same response carries the seat counts: bought against assigned, for the license pool report.
    $pool = @(ConvertTo-LicensePoolRow -Sku $skus -CopilotSku $copilotSkus -SuiteSku $suiteSkus)
    $poolSaved = Save-LicensePool -Rows $pool
    foreach ($p in @($pool | Where-Object IsCopilot)) {
        Write-Log ("License pool {0}: {1:N0} bought, {2:N0} assigned." -f $p.SkuPartNumber, ($p.Enabled + $p.Warning), $p.Consumed)
    }
    Write-Log "License pool written: $poolSaved SKU(s)."

    # ---- Which users to look up -------------------------------------------
    $upns = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $seen = Invoke-EnrichmentSql -As Table -Sql @'
SELECT DISTINCT UserPrincipalName
FROM audit.CopilotInteraction
WHERE UserPrincipalName IS NOT NULL;
'@
    foreach ($r in $seen.Rows) { [void] $upns.Add([string] $r.UserPrincipalName) }
    Write-Log ("{0:N0} distinct user(s) appear in the audit data." -f $upns.Count)

    # And everyone recorded as holding a license. A row is written only for someone looked up, so a license
    # taken back from a person who never used Copilot, or a person who left, was never looked up again: Licensed for
    # good, on the unused-license list and in the licensed counts, after every reclaim the page recommends.
    $recorded = Invoke-EnrichmentSql -As Table -Sql 'SELECT UserPrincipalName FROM audit.CopilotLicense WHERE IsCopilotLic = 1;'
    $inAudit = $upns.Count
    foreach ($r in $recorded.Rows) { [void] $upns.Add([string] $r.UserPrincipalName) }
    if ($upns.Count -gt $inAudit) { Write-Log ("{0:N0} more recorded as licensed, looked up again." -f ($upns.Count - $inAudit)) }

    if ($AllLicensedUsers) {
        # Everyone holding a Copilot license, including users who never
        # interacted - the population the shelfware report needs.
        foreach ($skuId in $copilotSkus.Keys) {
            $lic = Get-GraphPaged -Uri ("/v1.0/users?`$select=userPrincipalName&`$filter=assignedLicenses/any(x:x/skuId eq $skuId)&`$count=true")
            foreach ($u in $lic) {
                $upn = "$(Get-Prop $u 'userPrincipalName')"
                if ($upn) { [void] $upns.Add($upn) }
            }
        }
        Write-Log ("{0:N0} user(s) after adding license holders with no interactions." -f $upns.Count)
    }

    # ---- Look each one up --------------------------------------------------
    $select = 'userPrincipalName,assignedLicenses,assignedPlans,accountEnabled,employeeHireDate,userType' + $(if ($IncludeLeaveDates) { ',employeeLeaveDateTime' } else { '' })
    $licRows = New-Object System.Collections.Generic.List[object]
    $skuRows = [System.Data.DataTable]::new()
    [void] $skuRows.Columns.Add('UserPrincipalName', [string]); [void] $skuRows.Columns.Add('SkuId', [guid])
    $notFound = 0

    # Twenty users to a Graph $batch request: one request a user took hours
    # for a large tenant and showed nothing until the end.
    $list = @($upns)
    $done = 0
    for ($at = 0; $at -lt $list.Count; $at += 20) {
        $pending = @{}
        for ($j = $at; $j -lt [math]::Min($at + 20, $list.Count); $j++) { $pending["$($j - $at)"] = $list[$j] }
        $answers = @{}
        for ($try = 1; $pending.Count -gt 0; $try++) {
            $requests = @($pending.GetEnumerator() | ForEach-Object {
                @{ id = $_.Key; method = 'GET'; url = "/users/$([uri]::EscapeDataString($_.Value))?`$select=$select" } })
            # A dropped connection or a failed batch is retried like a throttle: a
            # run of 60,000 users makes thousands of requests, and one reset lost the lot.
            $resp = $null; $wait = 0
            try {
                $resp = Invoke-MgGraphRequest -Method POST -Uri '/v1.0/$batch' -OutputType PSObject `
                            -Body (@{ requests = $requests } | ConvertTo-Json -Depth 4) -ContentType 'application/json'
            }
            catch {
                if ($try -ge 8) { throw }
                Write-Log "Graph request failed, retrying ($try of 7): $($_.Exception.Message)" -Level WARN
            }
            foreach ($r in @(Get-Prop $resp 'responses')) {
                $id = "$(Get-Prop $r 'id')"; $status = [int] (Get-Prop $r 'status')
                if (-not $pending.ContainsKey($id)) { continue }
                if ($status -eq 200) { $answers[$pending[$id]] = Get-Prop $r 'body'; $pending.Remove($id) }
                elseif ($status -eq 404) { $answers[$pending[$id]] = $null; $pending.Remove($id) }   # a departed user is expected, not an error
                elseif ($status -eq 429 -or $status -ge 500) {
                    $after = 0; [void] [int]::TryParse("$(Get-Prop (Get-Prop $r 'headers') 'Retry-After')", [ref] $after)
                    $wait = [math]::Max($wait, $after)
                }
                else {
                    $err = Get-Prop (Get-Prop $r 'body') 'error'
                    $hint = if ($status -eq 403 -and $IncludeLeaveDates) { ' -IncludeLeaveDates needs the Graph application permission User-LifeCycleInfo.Read.All, with admin consent; run without it until then.' } else { '' }
                    throw "Graph returned $status for $($pending[$id]): $(Get-Prop $err 'message')$hint"
                }
            }
            if ($pending.Count -gt 0) {
                if ($try -ge 8) { throw "Graph kept throttling or failing the lookup of $($pending.Values -join ', ')." }
                Start-Sleep -Seconds ([math]::Max($wait, [math]::Min(60, [math]::Pow(2, $try))))
            }
        }
        foreach ($upn in $list[$at..([math]::Min($at + 20, $list.Count) - 1)]) {
            $user = $answers[$upn]

            if ($null -eq $user) {
                $notFound++
                $licRows.Add([pscustomobject]@{
                    UserPrincipalName = $upn; IsCopilotLic = $null; SkuPartNumber = $null
                    LicensedSinceUtc = $null; CapabilityStatus = $null; DirectoryStatus = 'NotFound'
                    EntraAccountEnabled = $null; EmployeeHireDate = $null; UserType = $null; EmployeeLeaveUtc = $null
                })
                continue
            }

            # License, and every SKU they hold, for the overlap report
            $matchedSku = $null; $matchedIsSuite = $false
            $held = @{}
            foreach ($al in @(Get-Prop $user 'assignedLicenses')) {
                $id = "$(Get-Prop $al 'skuId')"
                $g = [guid]::Empty
                if ($id -and [guid]::TryParse($id, [ref] $g) -and -not $held.ContainsKey($g)) {
                    $held[$g] = $true
                    [void] $skuRows.Rows.Add("$(Get-Prop $user 'userPrincipalName')", $g)
                }
                # The add-on before a suite: someone with both is recorded by the add-on, which can be taken back.
                if ($id -and $copilotSkus.ContainsKey($id) -and (-not $matchedSku -or ($matchedIsSuite -and -not $suiteSkus.ContainsKey($id)))) {
                    $matchedSku = $copilotSkus[$id]; $matchedIsSuite = $suiteSkus.ContainsKey($id)
                }
            }
            $since = $null; $capability = $null
            if ($matchedSku) {
                $found = Get-LicensedSince -AssignedPlan @(Get-Prop $user 'assignedPlans') -CopilotPlan $copilotPlans -ServiceMatch $CopilotSkuMatch
                $since = $found.Since; $capability = $found.Capability
            }
            $licRows.Add([pscustomobject]@{
                UserPrincipalName = "$(Get-Prop $user 'userPrincipalName')"
                IsCopilotLic      = [int] [bool] $matchedSku
                SkuPartNumber     = $matchedSku
                LicensedSinceUtc  = $since
                CapabilityStatus  = $capability
                DirectoryStatus   = 'Found'
                EntraAccountEnabled = $(if ($null -ne (Get-Prop $user 'accountEnabled')) { [int] [bool] (Get-Prop $user 'accountEnabled') } else { $null })
                EmployeeHireDate  = $(ConvertTo-UtcOrNull (Get-Prop $user 'employeeHireDate'))
                UserType          = $(if (Get-Prop $user 'userType') { "$(Get-Prop $user 'userType')" } else { $null })
                EmployeeLeaveUtc  = $(ConvertTo-UtcOrNull (Get-Prop $user 'employeeLeaveDateTime'))
            })
        }
        $before = $done; $done = [math]::Min($at + 20, $list.Count)
        if ([math]::Floor($done / 1000) -gt [math]::Floor($before / 1000) -or $done -eq $list.Count) {
            Write-Log ("Looked up {0:N0} of {1:N0} user(s)." -f $done, $list.Count)
        }
    }

    # The delete guard, before anything about the people is written: every SKU each one holds replaces the
    # table whole, so a lookup that came back short must not take most of the licenses with it.
    if ((Invoke-EnrichmentSql -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.UserLicenseSku', N'U') IS NULL THEN 0 ELSE 1 END") -eq 1) {
        Assert-DropGuard -What 'Licenses held (audit.UserLicenseSku)' -Before ([long] (Invoke-EnrichmentSql -As Scalar -Sql 'SELECT COUNT_BIG(*) FROM audit.UserLicenseSku;')) `
                         -After $skuRows.Rows.Count -MaxDropPercent $MaxDropPercent -Setting 'LicenseMaxDropPercent' -AllowDrop:$AllowDrop
    }

    # The leave date only when it was asked for: a run without -IncludeLeaveDates keeps the last one read.
    $columns = @('UserPrincipalName', 'IsCopilotLic', 'SkuPartNumber', 'LicensedSinceUtc', 'CapabilityStatus', 'DirectoryStatus',
                 'EntraAccountEnabled', 'EmployeeHireDate', 'UserType') + $(if ($IncludeLeaveDates) { @('EmployeeLeaveUtc') } else { @() })
    $n = Save-RowBatch -Table 'audit.CopilotLicense' -KeyColumn 'UserPrincipalName' -Rows $licRows `
                   -ColumnOrder $columns `
                   -ColumnTypes @{
                       UserPrincipalName = [System.Data.SqlDbType]::NVarChar
                       IsCopilotLic      = [System.Data.SqlDbType]::Bit
                       SkuPartNumber     = [System.Data.SqlDbType]::NVarChar
                       LicensedSinceUtc  = [System.Data.SqlDbType]::DateTime2
                       CapabilityStatus  = [System.Data.SqlDbType]::NVarChar
                       DirectoryStatus   = [System.Data.SqlDbType]::NVarChar
                       EntraAccountEnabled = [System.Data.SqlDbType]::Bit
                       EmployeeHireDate  = [System.Data.SqlDbType]::Date
                       UserType          = [System.Data.SqlDbType]::NVarChar
                       EmployeeLeaveUtc  = [System.Data.SqlDbType]::DateTime2
                   }
    Write-Log "Licenses written: $n."
    if ((Invoke-EnrichmentSql -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'audit.UserLicenseSku', N'U') IS NULL THEN 0 ELSE 1 END") -eq 1) {
        Write-Log ("Licenses held, every SKU: {0:N0} written." -f (Save-UserSkus -Table $skuRows))
    }

    if ($notFound -gt 0) {
        Write-Log "$notFound user(s) are in the audit data but no longer in the directory; recorded as DirectoryStatus = 'NotFound', not as unlicensed." -Level WARN
    }

    $licensed = [int] (Invoke-EnrichmentSql -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense WHERE IsCopilotLic = 1')
    Write-Log "Enrichment complete: $licensed licensed user(s) recorded."
    Write-Log 'Re-run audit.usp_FlattenCopilotAudit to carry IsCopilotLic into audit.RawLogFlattened.'
}

# Dot-sourcing (tests) loads the functions without running anything. Refused by the delete guard, nothing
# about the people was written: a code of its own, which the nightly run records and goes on from.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-Main }
    catch {
        if (-not (Test-DropRefusal $_)) { throw }
        Write-Log $_.Exception.Message -Level ERROR
        exit $script:DropRefusedExitCode
    }
}
