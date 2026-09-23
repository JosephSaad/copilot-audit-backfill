<#
.SYNOPSIS
    Fills the directory facts the Copilot audit records cannot carry: who holds
    a Copilot licence, what the administrative unit GUIDs mean, and the user
    attributes adoption reporting is sliced by.

.DESCRIPTION
    Reads Microsoft Graph and writes ONLY to audit.CopilotLicense,
    audit.AdministrativeUnit and audit.UserProfile. It never touches the
    backfill tables, so it can run on its own schedule and a mistake here
    cannot corrupt the audit archive.

    Only the users that actually appear in audit.CopilotInteraction are looked
    up, not the whole directory - at most a few thousand lookups rather than
    every account in the tenant. Pass -AllLicensedUsers to also pull in users
    who hold a licence but have never interacted, which is what the shelfware
    report needs.

    Two things Graph cannot tell you, and which this records honestly rather
    than guessing:

      * Licence history. assignedPlans carries the date the CURRENT assignment
        began, so "licensed since March" is answerable. A licence granted and
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
    User.Read.All and Organization.Read.All for licences and profiles,
    AdministrativeUnit.Read.All for the unit names. App-only uses the
    equivalent application permissions instead.

.PARAMETER AllLicensedUsers
    Also enumerate every user holding a Copilot licence, including those with
    no interactions. Needed for "licensed but never used".

.PARAMETER SkipProfiles
    Licences and administrative units only.

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
    [string[]] $Scopes = @('User.Read.All', 'Organization.Read.All', 'AdministrativeUnit.Read.All'),

    [switch]   $AllLicensedUsers,
    [switch]   $SkipProfiles,

    [ValidateRange(1, 20)] [int] $BatchSize = 20,
    [string]   $CopilotSkuMatch = 'COPILOT'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
    $csb = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    if ($ConnectionString) { $csb.ConnectionString = $ConnectionString }
    else {
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

function Save-Rows {
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
        [Parameter(Mandatory)][System.Collections.IEnumerable] $Rows
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
        $chunk = $all[$offset..$last]

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
            throw "$cmd not found. Install Microsoft.Graph.Authentication, or use the offline bundle's Modules folder."
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

    Connect-EnrichmentGraph

    # ---- Which Copilot SKUs exist in this tenant ---------------------------
    $skus = Get-GraphPaged -Uri '/v1.0/subscribedSkus'
    $copilotSkus = @{}
    foreach ($s in $skus) {
        $part = "$(Get-Prop $s 'skuPartNumber')"
        if ($part -and $part.ToUpperInvariant().Contains($CopilotSkuMatch.ToUpperInvariant())) {
            $copilotSkus["$(Get-Prop $s 'skuId')"] = $part
        }
    }
    if ($copilotSkus.Count -eq 0) {
        Write-Log "No subscribed SKU matched '$CopilotSkuMatch'. Every user will be recorded as unlicensed." -Level WARN
    }
    else {
        Write-Log ('Copilot SKUs in this tenant: ' + (($copilotSkus.Values | Sort-Object -Unique) -join ', ') + '.')
    }

    # ---- Administrative units ---------------------------------------------
    $units = Get-GraphPaged -Uri '/v1.0/directory/administrativeUnits'
    $unitRows = foreach ($u in $units) {
        [pscustomobject]@{
            Id          = "$(Get-Prop $u 'id')"
            DisplayName = Get-Prop $u 'displayName'
            Description = Get-Prop $u 'description'
        }
    }
    $n = Save-Rows -Table 'audit.AdministrativeUnit' -KeyColumn 'Id' -Rows $unitRows `
                   -ColumnOrder @('Id', 'DisplayName', 'Description') `
                   -ColumnTypes @{
                       Id          = [System.Data.SqlDbType]::NVarChar
                       DisplayName = [System.Data.SqlDbType]::NVarChar
                       Description = [System.Data.SqlDbType]::NVarChar
                   }
    Write-Log "Administrative units: $n."

    # ---- Which users to look up -------------------------------------------
    $upns = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $seen = Invoke-EnrichmentSql -As Table -Sql @'
SELECT DISTINCT UserPrincipalName
FROM audit.CopilotInteraction
WHERE UserPrincipalName IS NOT NULL;
'@
    foreach ($r in $seen.Rows) { [void] $upns.Add([string] $r.UserPrincipalName) }
    Write-Log ("{0:N0} distinct user(s) appear in the audit data." -f $upns.Count)

    if ($AllLicensedUsers) {
        # Everyone holding a Copilot licence, including users who never
        # interacted - the population the shelfware report needs.
        foreach ($skuId in $copilotSkus.Keys) {
            $lic = Get-GraphPaged -Uri ("/v1.0/users?`$select=userPrincipalName&`$filter=assignedLicenses/any(x:x/skuId eq $skuId)&`$count=true")
            foreach ($u in $lic) {
                $upn = "$(Get-Prop $u 'userPrincipalName')"
                if ($upn) { [void] $upns.Add($upn) }
            }
        }
        Write-Log ("{0:N0} user(s) after adding licence holders with no interactions." -f $upns.Count)
    }

    # ---- Look each one up --------------------------------------------------
    $select = 'userPrincipalName,displayName,department,jobTitle,officeLocation,city,country,companyName,usageLocation,userType,accountEnabled,createdDateTime,assignedLicenses,assignedPlans'
    $licRows = New-Object System.Collections.Generic.List[object]
    $profRows = New-Object System.Collections.Generic.List[object]
    $notFound = 0

    foreach ($upn in $upns) {
        $user = $null
        try {
            $user = Invoke-MgGraphRequest -Method GET -OutputType PSObject `
                        -Uri ("/v1.0/users/$([uri]::EscapeDataString($upn))?`$select=$select")
        }
        catch {
            # A departed user is expected, not an error. Anything else is worth seeing.
            $msg = $_.Exception.Message
            if ($msg -notmatch '404|NotFound|Resource .* does not exist') { throw }
        }

        if ($null -eq $user) {
            $notFound++
            $licRows.Add([pscustomobject]@{
                UserPrincipalName = $upn; IsCopilotLic = $null; SkuPartNumber = $null
                LicensedSinceUtc = $null; CapabilityStatus = $null; DirectoryStatus = 'NotFound'
            })
            if (-not $SkipProfiles) {
                $profRows.Add([pscustomobject]@{
                    UserPrincipalName = $upn; DisplayName = $null; Department = $null; JobTitle = $null
                    OfficeLocation = $null; City = $null; Country = $null; CompanyName = $null
                    UsageLocation = $null; UserType = $null; AccountEnabled = $null; ManagerUpn = $null
                    CreatedDateTimeUtc = $null; DirectoryStatus = 'NotFound'
                })
            }
            continue
        }

        # Licence
        $matchedSku = $null
        foreach ($al in @(Get-Prop $user 'assignedLicenses')) {
            $id = "$(Get-Prop $al 'skuId')"
            if ($id -and $copilotSkus.ContainsKey($id)) { $matchedSku = $copilotSkus[$id]; break }
        }
        $since = $null; $capability = $null
        if ($matchedSku) {
            foreach ($ap in @(Get-Prop $user 'assignedPlans')) {
                $svc = "$(Get-Prop $ap 'service')"
                if ($svc -and $svc.ToUpperInvariant().Contains($CopilotSkuMatch.ToUpperInvariant())) {
                    $cap = "$(Get-Prop $ap 'capabilityStatus')"
                    $d = ConvertTo-UtcOrNull (Get-Prop $ap 'assignedDateTime')
                    # Keep the earliest enabled assignment, which is the one that
                    # answers "since when".
                    if ($cap -ne 'Deleted' -and ($null -eq $since -or ($d -and $d -lt $since))) {
                        $since = $d; $capability = $cap
                    }
                }
            }
        }
        $licRows.Add([pscustomobject]@{
            UserPrincipalName = "$(Get-Prop $user 'userPrincipalName')"
            IsCopilotLic      = [int] [bool] $matchedSku
            SkuPartNumber     = $matchedSku
            LicensedSinceUtc  = $since
            CapabilityStatus  = $capability
            DirectoryStatus   = 'Found'
        })

        # Profile
        if (-not $SkipProfiles) {
            $mgr = $null
            try {
                $m = Invoke-MgGraphRequest -Method GET -OutputType PSObject `
                        -Uri ("/v1.0/users/$([uri]::EscapeDataString($upn))/manager?`$select=userPrincipalName")
                $mgr = Get-Prop $m 'userPrincipalName'
            }
            catch { $mgr = $null }   # no manager set is the common case, not a failure

            $enabled = Get-Prop $user 'accountEnabled'
            $profRows.Add([pscustomobject]@{
                UserPrincipalName  = "$(Get-Prop $user 'userPrincipalName')"
                DisplayName        = Get-Prop $user 'displayName'
                Department         = Get-Prop $user 'department'
                JobTitle           = Get-Prop $user 'jobTitle'
                OfficeLocation     = Get-Prop $user 'officeLocation'
                City               = Get-Prop $user 'city'
                Country            = Get-Prop $user 'country'
                CompanyName        = Get-Prop $user 'companyName'
                UsageLocation      = Get-Prop $user 'usageLocation'
                UserType           = Get-Prop $user 'userType'
                AccountEnabled     = $(if ($null -eq $enabled) { $null } else { [int] [bool] $enabled })
                ManagerUpn         = $mgr
                CreatedDateTimeUtc = ConvertTo-UtcOrNull (Get-Prop $user 'createdDateTime')
                DirectoryStatus    = 'Found'
            })
        }
    }

    $n = Save-Rows -Table 'audit.CopilotLicense' -KeyColumn 'UserPrincipalName' -Rows $licRows `
                   -ColumnOrder @('UserPrincipalName', 'IsCopilotLic', 'SkuPartNumber',
                                  'LicensedSinceUtc', 'CapabilityStatus', 'DirectoryStatus') `
                   -ColumnTypes @{
                       UserPrincipalName = [System.Data.SqlDbType]::NVarChar
                       IsCopilotLic      = [System.Data.SqlDbType]::Bit
                       SkuPartNumber     = [System.Data.SqlDbType]::NVarChar
                       LicensedSinceUtc  = [System.Data.SqlDbType]::DateTime2
                       CapabilityStatus  = [System.Data.SqlDbType]::NVarChar
                       DirectoryStatus   = [System.Data.SqlDbType]::NVarChar
                   }
    Write-Log "Licences written: $n."

    if (-not $SkipProfiles) {
        $n = Save-Rows -Table 'audit.UserProfile' -KeyColumn 'UserPrincipalName' -Rows $profRows `
                       -ColumnOrder @('UserPrincipalName', 'DisplayName', 'Department', 'JobTitle',
                                      'OfficeLocation', 'City', 'Country', 'CompanyName', 'UsageLocation',
                                      'UserType', 'AccountEnabled', 'ManagerUpn', 'CreatedDateTimeUtc',
                                      'DirectoryStatus') `
                       -ColumnTypes @{
                           UserPrincipalName  = [System.Data.SqlDbType]::NVarChar
                           DisplayName        = [System.Data.SqlDbType]::NVarChar
                           Department         = [System.Data.SqlDbType]::NVarChar
                           JobTitle           = [System.Data.SqlDbType]::NVarChar
                           OfficeLocation     = [System.Data.SqlDbType]::NVarChar
                           City               = [System.Data.SqlDbType]::NVarChar
                           Country            = [System.Data.SqlDbType]::NVarChar
                           CompanyName        = [System.Data.SqlDbType]::NVarChar
                           UsageLocation      = [System.Data.SqlDbType]::NVarChar
                           UserType           = [System.Data.SqlDbType]::NVarChar
                           AccountEnabled     = [System.Data.SqlDbType]::Bit
                           ManagerUpn         = [System.Data.SqlDbType]::NVarChar
                           CreatedDateTimeUtc = [System.Data.SqlDbType]::DateTime2
                           DirectoryStatus    = [System.Data.SqlDbType]::NVarChar
                       }
        Write-Log "Profiles written: $n."
    }

    if ($notFound -gt 0) {
        Write-Log "$notFound user(s) are in the audit data but no longer in the directory; recorded as DirectoryStatus = 'NotFound', not as unlicensed." -Level WARN
    }

    $licensed = [int] (Invoke-EnrichmentSql -As Scalar -Sql 'SELECT COUNT(*) FROM audit.CopilotLicense WHERE IsCopilotLic = 1')
    Write-Log "Enrichment complete: $licensed licensed user(s) recorded."
    Write-Log 'Re-run audit.usp_FlattenCopilotAudit to carry IsCopilotLic into audit.RawLogFlattened.'
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
