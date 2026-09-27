<#
.SYNOPSIS
    Dumps the table and view metadata of an installed Copilot database and license-event
    database to out/meta.json, the first step of regenerating docs/SCHEMA.md.
.DESCRIPTION
    Run against a fresh install of the release being documented (Install-CopilotAudit.ps1
    with -Dashboard, so the dash and bi objects exist). Then scripts.py, then build_md.py.
    See tools/dev/README.md.
#>
[CmdletBinding()]
param(
    [string] $SqlServer = 'localhost',
    [string] $CopilotDatabase = 'CopilotAudit',
    [string] $LicenseDatabase = 'CopilotLicenseEvents'
)
$ErrorActionPreference = 'Stop'
$out = @{}
foreach ($db in 'CopilotAudit', 'CopilotLicenseEvents') {
    $src = if ($db -eq 'CopilotAudit') { $CopilotDatabase } else { $LicenseDatabase }
    $cn = [System.Data.SqlClient.SqlConnection]::new("Server=$SqlServer;Database=$src;Integrated Security=true;TrustServerCertificate=true")
    $cn.Open()
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
    $out[$db] = $rows
    $cn.Close()
}
$out | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path ([System.IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'out')).FullName) 'meta.json') -Encoding utf8
Write-Output ("columns: " + ($out.Values | ForEach-Object { $_.columns.Count } | Measure-Object -Sum).Sum)
