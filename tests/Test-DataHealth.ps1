<#
.SYNOPSIS
    Keeps tools/Test-CopilotDataHealth.ps1 honest (2.5.0): what it says when it ends, how it tells a license
    record from another operation, how it finds a gap in coverage, and that it only reads.

.DESCRIPTION
    The health check needs two SQL Server databases, so these tests run its own pieces without them. The
    functions and the closing block are taken from the script itself, by its syntax tree, so a change to
    the script is a change to what is tested:

      1. The closing, in five situations: it says every check passed only when no section stopped and
         nothing warned. Before 2.5.0 it said so after a stopped section, a license database that could
         not be opened, and warnings.
      2. A section that throws is caught, counted and named, and the next section still runs.
      3. A gap in coverage: ranges that leave more than the given hours between them, whatever order they
         come in, a range inside another, and rows with no dates.
      4. A license record is one whatever its trailing period ('Update user.', as Entra records it), as
         audit.usp_ShredLicenseEvents counts it. The script's license operations are the pipeline's, and
         every Copilot Studio operation the pipeline asks for starts with Bot or (2.7.0, the administrators'
         actions on agents) is named, as the script counts them.
      5. It only reads: a read-only connection, uncommitted reads, and nothing that writes.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-DataHealth.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')

$healthPath = Join-Path (Join-Path $root 'tools') 'Test-CopilotDataHealth.ps1'
$health = [System.IO.File]::ReadAllText($healthPath)

$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($healthPath, [ref] $tokens, [ref] $parseErrors)
Assert-That ($parseErrors.Count -eq 0) "the health check parses ($($parseErrors.Count) error(s))"

function Get-HealthFunction([string] $Name) {
    $found = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true)
    if (-not $found) { throw "function $Name is not in $healthPath" }
    $found.Extent.Text
}

# The helpers as the script defines them, in this script's scope, where the counters they raise are.
foreach ($name in 'Write-Head', 'Write-Ok', 'Write-Info', 'Write-Warn', 'Write-Act', 'Invoke-Section', 'Get-CoverageGap') {
    . ([scriptblock]::Create((Get-HealthFunction $name)))
}

# ---------------------------------------------------------------------------
Write-Host "`n1. What it says at the end" -ForegroundColor Cyan

$closing = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if ($script:Actions.Count -eq 0)') }, $true)
Assert-That ($null -ne $closing) 'the closing block is found'

function Invoke-Scenario {
    param([string] $Name, [scriptblock] $Setup, [string[]] $Says, [string[]] $DoesNotSay)
    $script:Actions = [System.Collections.Generic.List[string]]::new(); $script:Warnings = 0; $script:NotRun = 0
    $out = & { & $Setup; . ([scriptblock]::Create($closing.Extent.Text)) } 6>&1 | Out-String
    $missing = @($Says | Where-Object { $out -notmatch [regex]::Escape($_) })
    $unexpected = @($DoesNotSay | Where-Object { $out -match [regex]::Escape($_) })
    Assert-That ($missing.Count -eq 0 -and $unexpected.Count -eq 0) ($Name + $(if ($missing) { " (missing: $($missing -join ' | '))" }) + $(if ($unexpected) { " (unexpected: $($unexpected -join ' | '))" }))
}

Invoke-Scenario 'nothing wrong: every check passed' { } @('Nothing to do: every check passed.') @('did not run', 'warning(s)')
Invoke-Scenario 'a warning and no command is not an all clear' { Write-Warn 'x' } @('No command to run, but 1 warning(s) above need a look.') @('every check passed')
Invoke-Scenario 'a section that stopped is not an all clear' { Invoke-Section 'S' { throw 'boom' } } `
    @('This section stopped: boom', '1 check(s) did not run to the end', 'not an all clear') @('every check passed')
Invoke-Scenario 'a command and a stopped section: the list, and that there may be more' { Write-Act 'a' 'do-this'; Invoke-Section 'S' { throw 'boom' } } `
    @('1. do-this', '1 check(s) did not run to the end (see the warnings above), so there may be more to do.') @('every check passed', 'not an all clear')
Invoke-Scenario 'a command alone: the list and no note' { Write-Act 'a' 'do-this' } @('1. do-this') @('every check passed', 'did not run')

$script:Actions = [System.Collections.Generic.List[string]]::new(); $script:Warnings = 0; $script:NotRun = 0
$ran = [System.Collections.Generic.List[string]]::new()
$null = & { Invoke-Section 'first' { throw 'stops here' }; Invoke-Section 'second' { $ran.Add('second') } } 6>&1
Assert-That ($script:NotRun -eq 1 -and $script:Warnings -eq 1 -and $ran.Count -eq 1) `
    "a section that throws is counted and warned, and the next still runs (not run $($script:NotRun), warnings $($script:Warnings), ran $($ran.Count))"
$script:Actions = [System.Collections.Generic.List[string]]::new()
$null = & { Write-Act 'one' 'same command'; Write-Act 'two' 'same command' } 6>&1
Assert-That ($script:Actions.Count -eq 1) 'two findings that need the same command list it once'

# ---------------------------------------------------------------------------
Write-Host "`n2. A gap in coverage" -ForegroundColor Cyan

# As the script calls it: the gaps come back as an array, empty when there are none.
function Get-Gap($Ranges, [double] $MinHours) { $found = Get-CoverageGap $Ranges $MinHours; , @($found) }
function New-Range([string] $From, [string] $To) {
    [pscustomobject]@{ From = $(if ($From) { [datetime]::Parse($From, [cultureinfo]::InvariantCulture) }); To = $(if ($To) { [datetime]::Parse($To, [cultureinfo]::InvariantCulture) }) }
}
$gaps = Get-Gap @((New-Range '2026-01-01 00:00' '2026-01-10 00:00'), (New-Range '2026-01-10 13:00' '2026-01-20 00:00')) 12
Assert-That ($gaps.Count -eq 1 -and $gaps[0].From -eq [datetime]'2026-01-10 00:00' -and $gaps[0].To -eq [datetime]'2026-01-10 13:00') 'thirteen hours between two ranges is a gap of twelve'
Assert-That ((Get-Gap @((New-Range '2026-01-01 00:00' '2026-01-10 00:00'), (New-Range '2026-01-10 11:00' '2026-01-20 00:00')) 12).Count -eq 0) 'eleven hours is not'
Assert-That ((Get-Gap @((New-Range '2026-01-01' '2026-01-12'), (New-Range '2026-01-10' '2026-01-20')) 12).Count -eq 0) 'ranges that overlap leave no gap'
$gaps = Get-Gap @((New-Range '2026-02-01' '2026-02-05'), (New-Range '2026-01-01' '2026-01-30'), (New-Range '2026-01-05' '2026-01-10')) 12
Assert-That ($gaps.Count -eq 1 -and $gaps[0].From -eq [datetime]'2026-01-30' -and $gaps[0].To -eq [datetime]'2026-02-01') `
    'ranges in any order, one inside another: the gap is measured from the latest end so far (30 Jan to 1 Feb)'
Assert-That ((Get-Gap @((New-Range '' '2026-01-10'), (New-Range '2026-01-01' ''), (New-Range '2026-03-01' '2026-03-02')) 12).Count -eq 0) 'a range with no start or no end is left out'
Assert-That ((Get-Gap @() 12).Count -eq 0) 'no ranges, no gaps'

# ---------------------------------------------------------------------------
Write-Host "`n3. A license record, and the operations the pipeline loads" -ForegroundColor Cyan

function Get-QuotedItem([string] $Text) { @([regex]::Matches($Text, "N?'([^']*)'") | ForEach-Object { $_.Groups[1].Value }) }

$pipeline = [System.IO.File]::ReadAllText((Join-Path $root 'Invoke-CopilotPipeline.ps1'))
$pipelineLicense = [regex]::Match($pipeline, '\$script:LicenseOperations\s*=\s*@\((?<l>[^)]*)\)')
$pipelineAgent = [regex]::Match($pipeline, '\$script:AgentOperations\s*=\s*(?<l>[^$]*?)\r?\n\$script:LicenseOperations')
Assert-That ($pipelineLicense.Success -and $pipelineAgent.Success) "the pipeline's operation lists are found"
$licenseOperations = Get-QuotedItem $pipelineLicense.Groups['l'].Value
# Comment lines dropped first: an apostrophe in one (2.7.0) would pair the quotes wrongly.
$agentOperations = Get-QuotedItem ($pipelineAgent.Groups['l'].Value -replace '(?m)^\s*#.*$', '')

$healthList = [regex]::Match($health, "WHEN n\.Op IN \((?<l>[^)]*)\) THEN 'License'")
Assert-That $healthList.Success "the health check's license operations are found"
$healthOperations = Get-QuotedItem $healthList.Groups['l'].Value
Assert-That (@(Compare-Object ($healthOperations | Sort-Object) ($licenseOperations | Sort-Object)).Count -eq 0) `
    "the health check's license operations are the pipeline's: $($licenseOperations -join ', ')"
# 2.7.0: the administrators' actions on agents (CopilotAgentManagement) do not start with Bot; the health check names them.
$adminOps = @($agentOperations | Where-Object { -not $_.StartsWith('Bot') })
Assert-That ($agentOperations.Count -ge 14 -and $health.Contains("a.Operation LIKE N'Bot%'") -and
             @($adminOps | Where-Object { -not $health.Contains("N'$_'") }).Count -eq 0) `
    "every Copilot Studio operation the pipeline loads ($($agentOperations.Count)) starts with Bot or is named in the health check ($($adminOps.Count) named), as it counts them"

# What the check does to an operation before looking it up, and what the shred does: the same.
$normalize = [regex]::Match($health, 'RTRIM\(REPLACE\(a\.Operation, N''\.'', N''''\)\)')
$shred = [System.IO.File]::ReadAllText((Join-Path (Join-Path $root 'sql') 'Create-LicenseEventProcedure.sql'))
Assert-That ($normalize.Success -and $shred.Contains("RTRIM(REPLACE(COALESCE(t.Operation, a.Operation), N'.', N''))")) `
    "the check strips a period from an operation as audit.usp_ShredLicenseEvents does"
function Get-OperationKind([string] $Operation) {
    $op = $Operation.Replace('.', '').TrimEnd()
    if ($Operation -like 'Bot*') { 'Copilot Studio' } elseif ($op -in $healthOperations) { 'License' } else { 'Other' }
}
Assert-That ((Get-OperationKind 'Update user.') -eq 'License' -and (Get-OperationKind 'Update user') -eq 'License' -and (Get-OperationKind 'Change user license.') -eq 'License') `
    "'Update user.' is a license record, with or without its period"
Assert-That ((Get-OperationKind 'BotCreate') -eq 'Copilot Studio' -and (Get-OperationKind 'BotUpdateOperation-BotPublish') -eq 'Copilot Studio') 'a Copilot Studio operation is counted as one'
Assert-That ((Get-OperationKind 'Add member to group.') -eq 'Other' -and (Get-OperationKind 'CopilotInteraction') -eq 'Other') `
    'anything else is another operation: the warning that asks whether -LicenseDatabase is the right database'

# ---------------------------------------------------------------------------
Write-Host "`n4. It only reads" -ForegroundColor Cyan

Assert-That ($health.Contains("`$b['ApplicationIntent'] = 'ReadOnly'")) 'the connection says it is read only'
Assert-That ($health.Contains('SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED')) 'it reads uncommitted data, so it takes no row or page locks'
Assert-That (-not $health.Contains('ExecuteNonQuery')) 'it never executes a command that writes'
$writes = @([regex]::Matches($health, '(?i)\b(INSERT\s+INTO|UPDATE\s+[\w\.\[\]]+\s+SET|DELETE\s+FROM|TRUNCATE\s+TABLE|DROP\s+(TABLE|INDEX|VIEW|PROCEDURE)|ALTER\s+(TABLE|DATABASE)|CREATE\s+(TABLE|INDEX|VIEW|PROCEDURE)|MERGE\s+(INTO\s+)?[\w\.\[\]]+\s+(AS\s+\w+\s+)?USING)') | ForEach-Object { $_.Value })
Assert-That ($writes.Count -eq 0) "nothing in it writes ($($writes -join '; '))"
Assert-That ($health -match 'Read only\.' -and $health -match 'schema-stability lock') 'it says it reads, and that a query still holds a schema-stability lock while it runs'

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All data health check tests passed.' -ForegroundColor Green
