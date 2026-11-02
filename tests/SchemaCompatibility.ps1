<#
    Shared by tests/Test-SchemaCompatibility.ps1, section 20 of tests/Test-SqlIntegration.ps1 and
    tools/dev/schema/Update-SchemaBaseline.ps1: reads the shape of an installed database as lines of text, and says
    what in one set of lines would break someone who was written against another.

    From 2.6.3 a release may add to the schema and may not take away from it. tests/schema-baseline.tsv holds the
    shape of the last release; what CI installs is compared with it. A line is tab separated:

      C  database  schema.object  kind  ordinal  column      type  nullable  flags    a column of a table, view or function
      P  database  schema.object  kind  ordinal  @parameter  type  output    default  a parameter of a procedure or function
      O  database  schema.object  kind                                                an object
      G  database  role           permission  SCHEMA::name or schema.object           what a database role is granted

    database is 'copilot' or 'license'. kind is sys.objects.type (U, V, P, FN, IF, TF). flags: D a default, I an
    identity, X computed. A change is compatible when everything in the baseline is still there and still takes and
    gives what it did:

      - no object, column, parameter or grant goes away, and no object changes kind
      - a column or parameter keeps its type, or widens it (a longer string, a larger integer, a decimal with as many
        digits on both sides of the point)
      - a table's column that took NULL still does
      - a view's or function's columns keep their places: new ones go after them
      - a parameter keeps its place and its direction, and a new one has a default
      - a column added to a table takes NULL, or has a default, or fills itself (identity, computed)
#>

Set-StrictMode -Version Latest

$script:SchemaIntegerRank = @{ tinyint = 1; smallint = 2; int = 3; bigint = 4 }

function ConvertFrom-SchemaLine {
    <# The lines of a baseline or a snapshot as objects. Blank lines and # comments are passed over. #>
    param([AllowEmptyCollection()][AllowEmptyString()][string[]] $Line)
    foreach ($l in $Line) {
        if ([string]::IsNullOrWhiteSpace($l) -or $l.StartsWith('#')) { continue }
        $p = $l.Split("`t")
        switch ($p[0]) {
            'C' { [pscustomobject]@{ Section = 'C'; Database = $p[1]; Object = $p[2]; Kind = $p[3]; Ordinal = [int] $p[4]; Name = $p[5]; Type = $p[6]; Nullable = $p[7] -eq '1'; Flags = $p[8] } }
            'P' { [pscustomobject]@{ Section = 'P'; Database = $p[1]; Object = $p[2]; Kind = $p[3]; Ordinal = [int] $p[4]; Name = $p[5]; Type = $p[6]; Output = $p[7] -eq '1'; HasDefault = $p[8] -eq '1' } }
            'O' { [pscustomobject]@{ Section = 'O'; Database = $p[1]; Object = $p[2]; Kind = $p[3] } }
            'G' { [pscustomobject]@{ Section = 'G'; Database = $p[1]; Role = $p[2]; Permission = $p[3]; On = $p[4] } }
            default { throw "Not a schema line: $l" }
        }
    }
}

function Test-SchemaTypeCompatible {
    <# True when a value that fitted -Old fits -New and reads the same: the same type, or a wider one of its family. #>
    param([Parameter(Mandatory)][string] $Old, [Parameter(Mandatory)][string] $New)
    if ($Old -eq $New) { return $true }
    $rx = '^(?<n>[a-z0-9_ ]+?)(?:\((?<a>max|\d+)(?:,(?<b>\d+))?\))?$'
    $o = [regex]::Match($Old.ToLowerInvariant(), $rx); $n = [regex]::Match($New.ToLowerInvariant(), $rx)
    if (-not $o.Success -or -not $n.Success) { return $false }
    $on = $o.Groups['n'].Value; $nn = $n.Groups['n'].Value
    if ($script:SchemaIntegerRank.ContainsKey($on) -and $script:SchemaIntegerRank.ContainsKey($nn)) {
        return $script:SchemaIntegerRank[$nn] -ge $script:SchemaIntegerRank[$on]
    }
    if ($on -ne $nn) { return $false }
    if ($on -in 'varchar', 'nvarchar', 'varbinary') {
        $ol = $o.Groups['a'].Value; $nl = $n.Groups['a'].Value
        if ($nl -eq 'max') { return $true }
        if ($ol -eq 'max') { return $false }
        return [int] $nl -ge [int] $ol
    }
    if ($on -in 'decimal', 'numeric') {
        $op = [int] $o.Groups['a'].Value; $os = [int] $o.Groups['b'].Value; $np = [int] $n.Groups['a'].Value; $ns = [int] $n.Groups['b'].Value
        return $ns -ge $os -and ($np - $ns) -ge ($op - $os)
    }
    return $false
}

function Compare-SchemaSnapshot {
    <#
        What in -Current breaks someone written against -Baseline: one sentence each, none when it is compatible.
        Both are lines as Get-SchemaSnapshot writes them. What -Current adds is not reported unless it has to be
        (a new column a writer must now fill, a new parameter a caller must now pass).
    #>
    param([AllowEmptyCollection()][string[]] $Baseline, [AllowEmptyCollection()][string[]] $Current)
    $base = @(ConvertFrom-SchemaLine $Baseline); $now = @(ConvertFrom-SchemaLine $Current)
    $breaks = [System.Collections.Generic.List[string]]::new()

    $kinds = @{}; $members = @{}; $grants = @{}; $owned = @{}
    foreach ($r in $now) {
        if ($r.Section -eq 'G') { $grants["$($r.Database)|$($r.Role)|$($r.Permission)|$($r.On)"] = $true; continue }
        $kinds["$($r.Database)|$($r.Object)"] = $r.Kind
        if ($r.Section -in 'C', 'P') { $members["$($r.Database)|$($r.Object)|$($r.Section)|$($r.Name)"] = $r }
    }
    foreach ($r in $base) {
        if ($r.Section -in 'C', 'P') { $owned["$($r.Database)|$($r.Object)|$($r.Section)|$($r.Name)"] = $true }
    }

    $reported = @{}
    foreach ($r in $base) {
        if ($r.Section -eq 'G') {
            if (-not $grants.ContainsKey("$($r.Database)|$($r.Role)|$($r.Permission)|$($r.On)")) {
                $breaks.Add("$($r.Database): the role $($r.Role) is no longer granted $($r.Permission) on $($r.On)")
            }
            continue
        }
        $objectKey = "$($r.Database)|$($r.Object)"
        if (-not $kinds.ContainsKey($objectKey)) {
            if (-not $reported.ContainsKey($objectKey)) { $reported[$objectKey] = $true; $breaks.Add("$($r.Database): $($r.Object) is gone") }
            continue
        }
        if ($kinds[$objectKey] -ne $r.Kind) {
            if (-not $reported.ContainsKey($objectKey)) { $reported[$objectKey] = $true; $breaks.Add("$($r.Database): $($r.Object) was of kind $($r.Kind) and is now $($kinds[$objectKey])") }
            continue
        }
        if ($r.Section -eq 'O') { continue }
        $what = if ($r.Section -eq 'C') { 'column' } else { 'parameter' }
        $c = $members["$objectKey|$($r.Section)|$($r.Name)"]
        if ($null -eq $c) { $breaks.Add("$($r.Database): $($r.Object) has lost its $what $($r.Name)"); continue }
        if (-not (Test-SchemaTypeCompatible -Old $r.Type -New $c.Type)) {
            $breaks.Add("$($r.Database): $($r.Object).$($r.Name) was $($r.Type) and is now $($c.Type)")
        }
        if ($r.Section -eq 'C') {
            if ($r.Kind -eq 'U' -and $r.Nullable -and -not $c.Nullable) { $breaks.Add("$($r.Database): $($r.Object).$($r.Name) took NULL and no longer does") }
            if ($r.Kind -ne 'U' -and $r.Ordinal -ne $c.Ordinal) { $breaks.Add("$($r.Database): $($r.Object).$($r.Name) was column $($r.Ordinal) and is now column $($c.Ordinal)") }
        }
        else {
            if ($r.Ordinal -ne $c.Ordinal) { $breaks.Add("$($r.Database): $($r.Object) takes $($r.Name) in place $($c.Ordinal), not $($r.Ordinal) as before") }
            if ($r.Output -ne $c.Output) { $breaks.Add("$($r.Database): $($r.Object) $($r.Name) changed between input and output") }
        }
    }

    # What is new in an object the baseline knows, and that someone written against the baseline cannot know to give.
    $known = @{}
    foreach ($r in $base) { if ($r.Section -ne 'G') { $known["$($r.Database)|$($r.Object)"] = $true } }
    foreach ($r in $now) {
        if ($r.Section -notin 'C', 'P' -or -not $known.ContainsKey("$($r.Database)|$($r.Object)")) { continue }
        if ($owned.ContainsKey("$($r.Database)|$($r.Object)|$($r.Section)|$($r.Name)")) { continue }
        if ($r.Section -eq 'C' -and $r.Kind -eq 'U' -and -not $r.Nullable -and -not $r.Flags) {
            $breaks.Add("$($r.Database): the new column $($r.Object).$($r.Name) does not take NULL and has no default")
        }
        if ($r.Section -eq 'P' -and -not $r.HasDefault -and -not $r.Output) {
            $breaks.Add("$($r.Database): the new parameter $($r.Name) of $($r.Object) has no default")
        }
    }
    return $breaks.ToArray()
}

function Test-SqlParameterDefault {
    <#
        True when a module's text gives -Name a default. sys.parameters.has_default_value is filled for CLR modules
        only, so the text is read: the parameter's declaration, before the next one, followed by an equals sign.
    #>
    param([AllowNull()][string] $Definition, [Parameter(Mandatory)][string] $Name)
    if (-not $Definition) { return $false }
    $text = [regex]::Replace($Definition, '--[^\r\n]*|/\*[\s\S]*?\*/', ' ')
    $m = [regex]::Match($text, '(?i)' + [regex]::Escape($Name) + '\b(?<rest>[^@;]*)')
    if (-not $m.Success) { return $false }
    $rest = [regex]::Split($m.Groups['rest'].Value, '(?i)\bAS\b')[0]   # the last parameter runs into the body
    return [regex]::IsMatch($rest, '^[^,=]*(\([^)]*\))?[^,=]*=')
}

function Get-SchemaSnapshot {
    <# The shape of one installed database as sorted lines. -Database is the name the lines carry: copilot, license or governance (the Workbench's). #>
    param([Parameter(Mandatory)][string] $ConnectionString, [Parameter(Mandatory)][ValidateSet('copilot', 'license', 'governance')][string] $Database)
    $type = @"
CASE WHEN t.name IN (N'nvarchar', N'nchar') THEN CONCAT(t.name, N'(', IIF(x.max_length = -1, N'max', CONVERT(NVARCHAR(10), x.max_length / 2)), N')')
     WHEN t.name IN (N'varchar', N'char', N'varbinary', N'binary') THEN CONCAT(t.name, N'(', IIF(x.max_length = -1, N'max', CONVERT(NVARCHAR(10), x.max_length)), N')')
     WHEN t.name IN (N'decimal', N'numeric') THEN CONCAT(t.name, N'(', x.precision, N',', x.scale, N')')
     WHEN t.name IN (N'datetime2', N'datetimeoffset', N'time') THEN CONCAT(t.name, N'(', x.scale, N')')
     ELSE t.name END
"@
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    $read = {
        param([string] $Sql)
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = 120
        $t = [System.Data.DataTable]::new(); $r = $cmd.ExecuteReader()
        try { $t.Load($r) } finally { $r.Dispose() }
        return , $t
    }
    $lines = [System.Collections.Generic.List[string]]::new()
    try {
        $conn.Open()
        foreach ($o in (& $read @"
SELECT CONCAT(s.name, N'.', o.name) AS Obj, RTRIM(o.type) AS Kind
FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0 AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA');
"@).Rows) { $lines.Add(('O', $Database, $o.Obj, $o.Kind) -join "`t") }
        foreach ($c in (& $read @"
SELECT CONCAT(s.name, N'.', o.name) AS Obj, RTRIM(o.type) AS Kind, x.column_id AS Ordinal, x.name AS Col, $type AS TypeName, x.is_nullable AS Nullable,
       CONCAT(IIF(x.default_object_id <> 0, N'D', N''), IIF(x.is_identity = 1, N'I', N''), IIF(x.is_computed = 1, N'X', N'')) AS Flags
FROM sys.columns AS x
JOIN sys.objects AS o ON o.object_id = x.object_id JOIN sys.schemas AS s ON s.schema_id = o.schema_id
JOIN sys.types AS t ON t.user_type_id = x.user_type_id
WHERE o.type IN ('U', 'V', 'IF', 'TF') AND o.is_ms_shipped = 0 AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA');
"@).Rows) { $lines.Add(('C', $Database, $c.Obj, $c.Kind, [int] $c.Ordinal, $c.Col, $c.TypeName, [int] [bool] $c.Nullable, $c.Flags) -join "`t") }
        foreach ($p in (& $read @"
SELECT CONCAT(s.name, N'.', o.name) AS Obj, RTRIM(o.type) AS Kind, x.parameter_id AS Ordinal, x.name AS Parm, $type AS TypeName, x.is_output AS IsOutput, m.definition AS Definition
FROM sys.parameters AS x
JOIN sys.objects AS o ON o.object_id = x.object_id JOIN sys.schemas AS s ON s.schema_id = o.schema_id
JOIN sys.types AS t ON t.user_type_id = x.user_type_id
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0 AND x.parameter_id > 0 AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA');
"@).Rows) {
            $default = Test-SqlParameterDefault -Definition $(if ($p.Definition -is [DBNull]) { $null } else { [string] $p.Definition }) -Name $p.Parm
            $lines.Add(('P', $Database, $p.Obj, $p.Kind, [int] $p.Ordinal, $p.Parm, $p.TypeName, [int] [bool] $p.IsOutput, [int] $default) -join "`t")
        }
        foreach ($g in (& $read @"
SELECT r.name AS RoleName, p.permission_name AS Permission,
       CASE p.class WHEN 3 THEN CONCAT(N'SCHEMA::', SCHEMA_NAME(p.major_id)) ELSE CONCAT(OBJECT_SCHEMA_NAME(p.major_id), N'.', OBJECT_NAME(p.major_id)) END AS OnWhat
FROM sys.database_permissions AS p JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
WHERE r.type = 'R' AND r.is_fixed_role = 0 AND r.name <> N'public' AND p.state IN ('G', 'W') AND p.minor_id = 0
  AND (p.class = 3 OR (p.class = 1 AND OBJECT_NAME(p.major_id) IS NOT NULL));
"@).Rows) { $lines.Add(('G', $Database, $g.RoleName, $g.Permission, $g.OnWhat) -join "`t") }
    }
    finally { $conn.Dispose() }
    $sorted = $lines.ToArray()
    [array]::Sort($sorted, [StringComparer]::Ordinal)
    return $sorted
}

function Install-SchemaForSnapshot {
    <#
        Installs a checkout into two databases the way the baseline is taken, so that everything a database in use
        has is there to be read:
          - with the dashboard;
          - with sql/Create-CopilotCsvImport.sql, which the first load of records runs: it makes audit.CopilotAudit
            and audit.CopilotInteraction and the views over them, and the installer makes more views once they exist;
          - with a reporting account, a user without a login, for the CopilotReporting role and what it is granted.
        -Install is splatted to the checkout's Install-CopilotAudit.ps1 (SqlServer, Database, LicenseDatabase and how
        to sign in); -ConnectionString reaches the Copilot database.
    #>
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][hashtable] $Install, [Parameter(Mandatory)][string] $ConnectionString)
    $installer = Join-Path $Root 'Install-CopilotAudit.ps1'
    & $installer @Install -Dashboard 6>$null 3>$null | Out-Null
    $batches = @("IF DATABASE_PRINCIPAL_ID(N'schema_baseline_reader') IS NULL CREATE USER schema_baseline_reader WITHOUT LOGIN;") +
        [regex]::Split([System.IO.File]::ReadAllText((Join-Path $Root 'sql' 'Create-CopilotCsvImport.sql')), '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')
    $conn = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $conn.Open()
        foreach ($b in $batches) {
            if ([string]::IsNullOrWhiteSpace($b)) { continue }
            $cmd = $conn.CreateCommand(); $cmd.CommandText = $b; $cmd.CommandTimeout = 600
            [void] $cmd.ExecuteNonQuery()
        }
    }
    finally { $conn.Dispose() }
    & $installer @Install -Dashboard -ReportingAccount 'schema_baseline_reader' 6>$null 3>$null | Out-Null
}
function Read-SchemaBaseline {
    <# The baseline file: the release it was taken at and its lines. #>
    param([Parameter(Mandatory)][string] $Path)
    $all = [System.IO.File]::ReadAllLines($Path)
    $m = $all | ForEach-Object { [regex]::Match($_, '^# release (\d+\.\d+\.\d+)\s*$') } | Where-Object Success | Select-Object -First 1
    if (-not $m) { throw "$Path does not say which release it was taken at (a line '# release x.y.z')." }
    [pscustomobject]@{ Release = $m.Groups[1].Value; Lines = @($all | Where-Object { $_ -and -not $_.StartsWith('#') }) }
}
