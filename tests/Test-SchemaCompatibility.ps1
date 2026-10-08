<#
.SYNOPSIS
    The rules of schema compatibility, without a SQL Server: what Compare-SchemaSnapshot calls a break and what it
    lets through, and that tests/schema-baseline.tsv is the baseline of the release the changelog names last.

.DESCRIPTION
    From 2.6.3 a release may add to the schema and may not take away from it. Section 20 of
    tests/Test-SqlIntegration.ps1 installs the schema and compares it with tests/schema-baseline.tsv; this proves the
    comparison itself, and catches a release cut without the baseline being taken again
    (tools/dev/schema/Update-SchemaBaseline.ps1).

.EXAMPLE
    pwsh -NoProfile -File tests/Test-SchemaCompatibility.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SchemaCompatibility.ps1')

$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}
function New-Line { ($args | ForEach-Object { "$_" }) -join "`t" }

Write-Host "`n1. What is compatible and what is not" -ForegroundColor Cyan
$base = @(
    New-Line O copilot audit.Person U
    New-Line C copilot audit.Person U 1 PersonId int 0 I
    New-Line C copilot audit.Person U 2 Name 'nvarchar(100)' 1 ''
    New-Line C copilot audit.Person U 3 Cost 'decimal(18,4)' 1 ''
    New-Line C copilot audit.Person U 4 Seen 'datetime2(0)' 0 ''
    New-Line O copilot bi.People V
    New-Line C copilot bi.People V 1 PersonId int 0 ''
    New-Line C copilot bi.People V 2 Name 'nvarchar(100)' 1 ''
    New-Line O copilot audit.usp_Load P
    New-Line P copilot audit.usp_Load P 1 '@From' date 0 0
    New-Line P copilot audit.usp_Load P 2 '@Rows' int 1 0
    New-Line O copilot audit.usp_Plain P
    New-Line O license audit.Person U
    New-Line C license audit.Person U 1 PersonId int 0 ''
    New-Line G copilot CopilotReporting SELECT bi.People
    New-Line G copilot CopilotDashboard SELECT SCHEMA::dash
)
function Get-Break([scriptblock] $Change) { , @(Compare-SchemaSnapshot -Baseline $base -Current @(& $Change $base)) }

Assert-That ((Get-Break { param($b) $b }).Count -eq 0) 'the baseline is compatible with itself'
$more = Get-Break { param($b) $b + @(
    (New-Line O copilot audit.Agent U), (New-Line C copilot audit.Agent U 1 AgentId int 0 ''),
    (New-Line C copilot audit.Person U 5 Country 'nvarchar(60)' 1 ''), (New-Line C copilot audit.Person U 6 Kind tinyint 0 D),
    (New-Line C copilot bi.People V 3 Country 'nvarchar(60)' 1 ''), (New-Line P copilot audit.usp_Load P 3 '@Days' int 0 1),
    (New-Line P copilot audit.usp_Plain P 1 '@Quiet' bit 0 1), (New-Line G copilot CopilotReporting SELECT audit.Agent)) }
Assert-That ($more.Count -eq 0) "additions pass: a table, a column that takes NULL, one with a default, a view column at the end, a parameter with a default, a grant ($($more -join '; '))"
$wider = Get-Break { param($b) $b -replace 'nvarchar\(100\)', 'nvarchar(max)' -replace "PersonId`tint", "PersonId`tbigint" -replace 'decimal\(18,4\)', 'decimal(22,6)' }
Assert-That ($wider.Count -eq 0) "and so does a wider type: a longer string, a larger integer, a decimal with more digits on both sides ($($wider -join '; '))"
$loosened = Get-Break { param($b) $b -replace "Seen`tdatetime2\(0\)`t0", "Seen`tdatetime2(0)`t1" }
Assert-That ($loosened.Count -eq 0) 'and a table column that starts to take NULL'

$gone = Get-Break { param($b) $b | Where-Object { $_ -notlike '*bi.People*' } }
Assert-That ($gone.Count -eq 2 -and $gone[0] -eq 'copilot: bi.People is gone' -and $gone[1] -like '*CopilotReporting is no longer granted SELECT on bi.People') "a dropped view is named once, and so is the grant that went with it ($($gone -join '; '))"
$renamed = Get-Break { param($b) $b -replace "`tName`t", "`tFullName`t" }
Assert-That ($renamed.Count -eq 2 -and $renamed[0] -eq 'copilot: audit.Person has lost its column Name' -and $renamed[1] -eq 'copilot: bi.People has lost its column Name') "a renamed column is a lost one, in the table and in the view ($($renamed -join '; '))"
$oneDb = Get-Break { param($b) $b | Where-Object { $_ -notlike "*license`taudit.Person*" } }
Assert-That ($oneDb.Count -eq 1 -and $oneDb[0] -eq 'license: audit.Person is gone') 'the two databases are told apart: a table gone from one is not hidden by its namesake in the other'
$narrow = Get-Break { param($b) $b -replace 'nvarchar\(100\)', 'nvarchar(50)' -replace 'decimal\(18,4\)', 'decimal(18,6)' -replace "Seen`tdatetime2\(0\)", "Seen`tdate" }
Assert-That ($narrow.Count -eq 4 -and ($narrow -join ';') -like '*audit.Person.Name was nvarchar(100) and is now nvarchar(50)*audit.Person.Cost was decimal(18,4) and is now decimal(18,6)*audit.Person.Seen was datetime2(0) and is now date*') `
    "a shorter string, a decimal with fewer digits before the point and another type are breaks ($($narrow.Count))"
$notNull = Get-Break { param($b) $b -replace "Cost`tdecimal\(18,4\)`t1", "Cost`tdecimal(18,4)`t0" }
Assert-That ($notNull.Count -eq 1 -and $notNull[0] -eq 'copilot: audit.Person.Cost took NULL and no longer does') 'a table column that stops taking NULL is a break'
$moved = Get-Break { param($b) ($b -replace "bi.People`tV`t1`tPersonId", "bi.People`tV`t2`tPersonId" -replace "bi.People`tV`t2`tName", "bi.People`tV`t1`tName") }
Assert-That ($moved.Count -eq 2 -and $moved[0] -like '*bi.People.PersonId was column 1 and is now column 2') 'a view''s columns keep their places'
$tableOrder = Get-Break { param($b) ($b -replace "audit.Person`tU`t3`tCost", "audit.Person`tU`t9`tCost") }
Assert-That ($tableOrder.Count -eq 0) 'a table''s need not: a rebuilt table numbers its columns again'
$kind = Get-Break { param($b) $b -replace "bi.People`tV", "bi.People`tU" }
Assert-That ($kind.Count -eq 1 -and $kind[0] -eq 'copilot: bi.People was of kind V and is now U') 'an object that changes kind is named once'
$mustFill = Get-Break { param($b) $b + (New-Line C copilot audit.Person U 5 Tenant 'nvarchar(40)' 0 '') }
Assert-That ($mustFill.Count -eq 1 -and $mustFill[0] -like '*new column audit.Person.Tenant does not take NULL and has no default') 'a new column a writer would have to fill is a break'
$mustPass = Get-Break { param($b) $b + (New-Line P copilot audit.usp_Load P 3 '@Days' int 0 0) }
Assert-That ($mustPass.Count -eq 1 -and $mustPass[0] -like '*new parameter @Days of audit.usp_Load has no default') 'and so is a new parameter a caller would have to pass'
$parm = Get-Break { param($b) $b -replace "1`t@From`tdate`t0", "2`t@From`tdatetime`t1" | Where-Object { $_ -notlike '*@Rows*' } }
Assert-That ($parm.Count -eq 4) "a parameter that changes type, place or direction, and one that is gone, are four breaks ($($parm.Count))"
$revoked = Get-Break { param($b) $b | Where-Object { $_ -notlike '*SCHEMA::dash' } }
Assert-That ($revoked.Count -eq 1 -and $revoked[0] -eq 'copilot: the role CopilotDashboard is no longer granted SELECT on SCHEMA::dash') 'a grant taken from a role is a break'

Write-Host "`n2. Types and parameter defaults" -ForegroundColor Cyan
$ok = @('int|bigint', 'tinyint|int', 'varchar(10)|varchar(max)', 'nvarchar(max)|nvarchar(max)', 'varbinary(16)|varbinary(32)', 'numeric(10,2)|numeric(12,2)', 'uniqueidentifier|uniqueidentifier')
$bad = @('bigint|int', 'nvarchar(max)|nvarchar(4000)', 'varchar(10)|nvarchar(10)', 'char(2)|char(3)', 'decimal(18,4)|decimal(18,2)', 'datetime2(0)|datetime2(3)', 'int|decimal(18,0)', 'bit|tinyint')
$okWrong = @($ok | Where-Object { $a, $b = $_.Split('|'); -not (Test-SchemaTypeCompatible -Old $a -New $b) })
$badWrong = @($bad | Where-Object { $a, $b = $_.Split('|'); Test-SchemaTypeCompatible -Old $a -New $b })
Assert-That ($okWrong.Count -eq 0) "a type may widen within its family ($($okWrong -join ', '))"
Assert-That ($badWrong.Count -eq 0) "and may not narrow, change family, or change what is read back ($($badWrong -join ', '))"
$proc = @'
CREATE OR ALTER PROCEDURE audit.usp_Load
    @From DATE,                         -- the first day = inclusive
    @Cost DECIMAL(18, 4) = 0,
    @Name NVARCHAR(100) = N'a, b',
    @Rows INT OUTPUT,
    @Last INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @Last = 1 SET @Rows = 0;
END;
'@
$defaults = '@From', '@Cost', '@Name', '@Rows', '@Last' | ForEach-Object { [int] (Test-SqlParameterDefault -Definition $proc -Name $_) }
Assert-That (($defaults -join '') -eq '01100') "a parameter's default is read from the procedure's text: not from a comment, nor from the body after the last one ($($defaults -join ''))"
Assert-That (-not (Test-SqlParameterDefault -Definition 'CREATE FUNCTION audit.f (@Day DATE) RETURNS TABLE AS RETURN (SELECT 1 AS One WHERE 1 = 1);' -Name '@Day')) 'nor from an inline function''s query'

Write-Host "`n3. The baseline file" -ForegroundColor Cyan
$baselinePath = Join-Path $PSScriptRoot 'schema-baseline.tsv'
$baseline = Read-SchemaBaseline -Path $baselinePath
$rows = @(ConvertFrom-SchemaLine $baseline.Lines)
$tables = @($rows | Where-Object { $_.Section -eq 'O' -and $_.Kind -eq 'U' }).Count
$views = @($rows | Where-Object { $_.Section -eq 'O' -and $_.Kind -eq 'V' }).Count
$dbs = @($rows | ForEach-Object Database | Sort-Object -Unique) -join ','
Assert-That ($tables -ge 90 -and $views -ge 30 -and $dbs -eq 'copilot,license') "tests/schema-baseline.tsv holds both databases: $tables tables, $views views, $(@($rows | Where-Object Section -eq 'C').Count) columns"
Assert-That (@(Compare-SchemaSnapshot -Baseline $baseline.Lines -Current $baseline.Lines).Count -eq 0) 'and reads as compatible with itself'
$sorted = [string[]] $baseline.Lines.Clone(); [array]::Sort($sorted, [StringComparer]::Ordinal)
Assert-That (($sorted -join "`n") -ceq ($baseline.Lines -join "`n") -and @($sorted | Select-Object -Unique).Count -eq $sorted.Count) 'its lines are sorted and none is there twice, so a change to it reads as one in a pull request'
$released = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md')), '(?m)^## \[(\d+\.\d+\.\d+)\]').Groups[1].Value
Assert-That ($baseline.Release -eq $released) "it was taken at the release the changelog names last ($($baseline.Release); the changelog says $released). When cutting a release, run tools/dev/schema/Update-SchemaBaseline.ps1"

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All schema compatibility tests passed.' -ForegroundColor Green
