<#
.SYNOPSIS
    Keeps the two reporting guides true to the scripts (2.5.9): the full reference carries the column
    reference and the text of the scripts that create the schema, and docs/REPORTING.md is its brief version.
    The repository keeps the reference's guide only (2.7.6); the release attaches the full reference.

.DESCRIPTION
    Reads the sources, not a list kept by hand:

      1. The committed reference is the guide only, and tools/dev/schema/Build-ReportingReference.ps1 makes the
         full one from today's scripts and docs/SCHEMA.md, as the release workflow does.
      2. Every view the scripts create in bi is described in the reference and named in the brief guide,
         and neither names a bi view the scripts do not create.
      3. The brief guide stays brief, and each guide points at the other.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-ReportingReference.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$script:Failures = 0
function Assert-That([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  PASS  $Message" } else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures++ }
}
function Read-Text([string] $Path) { [System.IO.File]::ReadAllText((Join-Path $root $Path)).Replace("`r`n", "`n") }

$brief = Read-Text 'docs/REPORTING.md'
$reference = Read-Text 'docs/REPORTING-REFERENCE.md'
$cut = $reference.IndexOf("`n# Part B.")
Assert-That ($cut -gt 0) 'docs/REPORTING-REFERENCE.md has its Part B'
$guide = if ($cut -gt 0) { $reference.Substring(0, $cut) } else { $reference }

Write-Host "`n1. The repository keeps the guide; the full reference is made from the scripts" -ForegroundColor Cyan
$builder = Join-Path $root 'tools' 'dev' 'schema' 'Build-ReportingReference.ps1'
& pwsh -NoProfile -File $builder -Check | ForEach-Object { Write-Host "        $_" }
Assert-That ($LASTEXITCODE -eq 0) 'docs/REPORTING-REFERENCE.md is the guide only, the marks of its generated parts empty'
Assert-That (($reference -split "`n").Count -lt 3000) "and short ($(($reference -split "`n").Count) lines)"
$full = Join-Path ([System.IO.Path]::GetTempPath()) "reporting-reference-$PID.md"
try {
    & pwsh -NoProfile -File $builder -OutFile $full | ForEach-Object { Write-Host "        $_" }
    Assert-That ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $full)) 'the full reference is made, as the release workflow makes it'
    $made = if (Test-Path -LiteralPath $full) { [System.IO.File]::ReadAllText($full) } else { '' }
    foreach ($s in Get-ChildItem -LiteralPath (Join-Path $root 'sql') -Filter '*.sql') {
        if (-not $made.Contains("``sql/$($s.Name)``")) { Assert-That $false "sql/$($s.Name) has its transcript in Part C" }
    }
    Assert-That ($made.Contains('`Install-CopilotAudit.ps1`') -and $made.Contains('`Invoke-CopilotPipeline.ps1`') -and $made.Contains('### Loading and state')) 'with the installer, the nightly pipeline and the column reference'
}
finally { Remove-Item -LiteralPath $full -ErrorAction SilentlyContinue }
$release = Read-Text '.github/workflows/release.yml'
Assert-That ($release.Contains('Build-ReportingReference.ps1 -OutFile') -and $release.Contains('build_docx.py') -and $release.Contains('REPORTING-REFERENCE.docx')) 'the release workflow makes the full reference and the Word guides and attaches them'

Write-Host "`n2. Every bi view is documented, and only those" -ForegroundColor Cyan
$views = @(Get-ChildItem -LiteralPath (Join-Path $root 'sql') -Filter '*.sql' | ForEach-Object {
        [regex]::Matches([System.IO.File]::ReadAllText($_.FullName), 'CREATE OR ALTER VIEW bi\.(\w+)') | ForEach-Object { $_.Groups[1].Value }
    } | Sort-Object -Unique)
Assert-That ($views.Count -ge 26) "the scripts create $($views.Count) views in bi"
$section6 = $guide.Substring($guide.IndexOf("`n## 6. "), $guide.IndexOf("`n## 7. ") - $guide.IndexOf("`n## 6. "))
$undescribed = @($views | Where-Object { $section6 -notmatch "(?m)^#### .*\bbi\.$_\b" })
Assert-That ($undescribed.Count -eq 0) "each has its own heading in the reference's section 6 ($($undescribed -join ', '))"
$unnamed = @($views | Where-Object { -not $brief.Contains("``bi.$_``") })
Assert-That ($unnamed.Count -eq 0) "and each is named in the brief guide ($($unnamed -join ', '))"
# A name in bi that is none of the scripts' views, tables, procedures or functions is a view that was renamed or removed.
$known = @(Get-ChildItem -LiteralPath (Join-Path $root 'sql') -Filter '*.sql' | ForEach-Object {
        [regex]::Matches([System.IO.File]::ReadAllText($_.FullName), '(?i)(?:TABLE|VIEW|PROCEDURE|FUNCTION)\s+bi\.(\w+)') | ForEach-Object { $_.Groups[1].Value }
    } | Sort-Object -Unique)
foreach ($doc in @{ Name = 'the brief guide'; Text = $brief }, @{ Name = 'the reference'; Text = $guide }) {
    $named = @([regex]::Matches($doc.Text, '\bbi\.([A-Z]\w+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $stale = @($named | Where-Object { $_ -notin $known })
    Assert-That ($stale.Count -eq 0) "$($doc.Name) names nothing in bi that the scripts do not create ($($stale -join ', '))"
}

Write-Host "`n3. The brief guide is brief, and the two point at each other" -ForegroundColor Cyan
$words = @($brief -split '\s+' | Where-Object { $_ }).Count
# Under 20 pages at 500 words a page.
Assert-That ($words -lt 10000) "the brief guide is under 20 pages ($words words, about $([Math]::Ceiling($words / 500)) pages)"
Assert-That ($brief.Contains('](REPORTING-REFERENCE.md)')) 'the brief guide links to the reference'
Assert-That ($guide.Contains('](REPORTING.md)')) 'and the reference to the brief guide'
foreach ($doc in @{ Name = 'docs/REPORTING.md'; Text = $brief }, @{ Name = 'docs/REPORTING-REFERENCE.md'; Text = $guide }) {
    $broken = @([regex]::Matches($doc.Text, '\]\(((?!https?:|#)[^)#]+)(?:#[^)]*)?\)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $root 'docs' $_)) })
    Assert-That ($broken.Count -eq 0) "every file $($doc.Name) links to exists ($($broken -join ', '))"
}

Write-Host ''
if ($script:Failures) { Write-Host "$script:Failures assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'The reporting guides match the scripts.' -ForegroundColor Green
