<#
.SYNOPSIS
    Dumps the table and view metadata of a Copilot database and a license-event database to
    out/meta.json, the first step of regenerating docs/SCHEMA.md.
.DESCRIPTION
    Some tables exist only once data has been loaded (audit.CopilotAudit, from the first
    finalize), once a CSV import has run (audit.CopilotCsv*), or after an upgrade from 1.x
    (audit.AdministrativeUnit, audit.UserProfile). So point -CopilotDatabase at a loaded
    database of the release being documented, such as the one tests/Test-SqlIntegration.ps1
    leaves behind, and name others in -Supplement for what it lacks. Then scripts.py, then
    build_md.py. See tools/dev/README.md.
.PARAMETER Supplement
    'Database' adds every table and view the main database lacks; 'Database=schema.Name,...'
    adds only those.
.EXAMPLE
    pwsh tools/dev/schema/extract.ps1 -CopilotDatabase CopilotAuditIntegration -LicenseDatabase CopilotLicenseEvents `
        -Supplement CopilotAuditIntegration_Load, 'CopilotUpgrade230=audit.AdministrativeUnit,audit.UserProfile'
#>
[CmdletBinding()]
param(
    [string] $SqlServer = 'localhost',
    [string] $CopilotDatabase = 'CopilotAudit',
    [string] $LicenseDatabase = 'CopilotLicenseEvents',
    [string[]] $Supplement = @()
)
$ErrorActionPreference = 'Stop'

function Get-SchemaMetadata {
    param([string] $Database)
    $cn = [System.Data.SqlClient.SqlConnection]::new("Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true")
    $cn.Open()
    try {
        $q = {
            param($sql)
            $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql
            $t = [System.Data.DataTable]::new(); $t.Load($cmd.ExecuteReader())
            return , $t
        }
        $cols = & $q @"
SELECT s.name AS sch, o.name AS obj, o.type AS otype, c.column_id, c.name AS col,
       TYPE_NAME(c.user_type_id) AS typ, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
       dc.definition AS dflt, cc.definition AS computed
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.columns c ON c.object_id = o.object_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = o.object_id AND dc.parent_column_id = c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = o.object_id AND cc.column_id = c.column_id
WHERE o.type IN ('U','V') AND s.name IN ('audit','bi','dash')
ORDER BY s.name, o.name, c.column_id
"@
        $idx = & $q @"
SELECT s.name AS sch, o.name AS obj, i.name AS idx, i.type_desc, i.is_primary_key, i.is_unique,
       STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name END, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS keycols,
       STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN c.name END, ', ') AS inccols,
       i.filter_definition
FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE o.type = 'U' AND s.name IN ('audit','bi','dash') AND i.type > 0
GROUP BY s.name, o.name, i.name, i.type_desc, i.is_primary_key, i.is_unique, i.filter_definition
"@
        $chk = & $q @"
SELECT s.name AS sch, o.name AS obj, k.name AS chk, k.definition
FROM sys.check_constraints k JOIN sys.objects o ON o.object_id = k.parent_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name IN ('audit','bi','dash')
"@
        $rows = @{ columns = @(); indexes = @(); checks = @() }
        foreach ($r in $cols.Rows) { $rows.columns += [ordered]@{ sch = $r.sch; obj = $r.obj; otype = $r.otype.Trim(); id = $r.column_id; col = $r.col; typ = $r.typ; len = $r.max_length; prec = $r.precision; scale = $r.scale; nullable = [bool] $r.is_nullable; identity = [bool] $r.is_identity; dflt = "$($r.dflt)"; computed = "$($r.computed)" } }
        foreach ($r in $idx.Rows) { $rows.indexes += [ordered]@{ sch = $r.sch; obj = $r.obj; idx = "$($r.idx)"; type = $r.type_desc; pk = [bool] $r.is_primary_key; unique = [bool] $r.is_unique; keys = "$($r.keycols)"; incl = "$($r.inccols)"; filter = "$($r.filter_definition)" } }
        foreach ($r in $chk.Rows) { $rows.checks += [ordered]@{ sch = $r.sch; obj = $r.obj; chk = $r.chk; def = $r.definition } }
        $rows
    }
    finally { $cn.Close() }
}

$out = @{
    CopilotAudit         = Get-SchemaMetadata $CopilotDatabase
    CopilotLicenseEvents = Get-SchemaMetadata $LicenseDatabase
}

# What the main Copilot database lacks, from the supplements, in the order given.
$main = $out.CopilotAudit
foreach ($s in $Supplement) {
    $db, $only = $s -split '=', 2
    $want = if ($only) { @($only -split ',' | ForEach-Object { $_.Trim() }) } else { $null }
    $have = [System.Collections.Generic.HashSet[string]]::new([string[]] @($main.columns | ForEach-Object { "$($_.sch).$($_.obj)" }))
    $meta = Get-SchemaMetadata $db
    $take = { param($r) $n = "$($r.sch).$($r.obj)"; -not $have.Contains($n) -and (-not $want -or $want -contains $n) }
    $added = @($meta.columns | Where-Object { & $take $_ })
    $main.columns += $added
    $main.indexes += @($meta.indexes | Where-Object { & $take $_ })
    $main.checks += @($meta.checks | Where-Object { & $take $_ })
    Write-Output ("{0}: {1}" -f $db, ((@($added | ForEach-Object { "$($_.sch).$($_.obj)" } | Select-Object -Unique)) -join ', '))
}

$out | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path ([System.IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'out')).FullName) 'meta.json') -Encoding utf8
Write-Output ("columns: " + ($out.Values | ForEach-Object { $_.columns.Count } | Measure-Object -Sum).Sum)
