<#
.SYNOPSIS
    Keeps the product in American spelling (2.4.0): License, organization, catalog, honor and the rest,
    in the code, the SQL, the Power BI model, the pages and the guides.

.DESCRIPTION
    Fails on any British form it knows, in any text file of the repository. Two exceptions:
    CHANGELOG entries before 2.4.0 keep the words they were released with (and a rename line, old → new, names the old one), and values that are
    Microsoft's own (Graph's "cancelled" status) or HTML's (aria-labelledby) are left as they are.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-Spelling.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$british = '(?i)\b\w*(licence|organis|honour|recognis|catalogu|colour|behaviour|favour|summaris|prioritis|centre|optimis|normalis|pseudonymis|anonymis|authoris|modelled|modelling|labelled|labelling|fulfilment|artefact|programme|judgement|travelled|synchronis|analys(e|ing)\b)\w*'
$allowed = @('aria-labelledby', 'status == "cancelled"')
$text = '\.(cs|cshtml|ps1|psd1|psm1|sql|md|py|json|tmdl|pbir|pbism|yml|css|js|html|txt|csproj)$'

# Breakthis.txt is a copy of the files changed from 3.0.1 to 3.0.3, kept for review: the whole CHANGELOG is in it.
$files = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](\.git|bin|obj|node_modules|Modules|out|ui-audit|edge-audit)[\\/]' -and $_.Name -match $text -and $_.Name -ne 'Test-Spelling.ps1' -and $_.Name -ne 'Breakthis.txt' }

$found = [System.Collections.Generic.List[string]]::new()
foreach ($f in $files) {
    $content = [System.IO.File]::ReadAllText($f.FullName)
    if ($f.Name -eq 'CHANGELOG.md') {
        $at = $content.IndexOf('## [2.3.0]')
        if ($at -ge 0) { $content = $content.Substring(0, $at) }   # released entries keep their words
    }
    $lines = $content -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($f.Name -eq 'CHANGELOG.md' -and $line.Contains([char] 0x2192)) { continue }   # a rename names the old spelling
        foreach ($a in $allowed) { $line = $line.Replace($a, '') }
        foreach ($m in [regex]::Matches($line, $british)) {
            $found.Add(('{0}:{1}  {2}' -f [System.IO.Path]::GetRelativePath($root, $f.FullName), ($i + 1), $m.Value))
        }
    }
}

Write-Host "Checked $($files.Count) file(s)."
if ($found.Count) {
    $found | ForEach-Object { Write-Host "  FAIL  $_" -ForegroundColor Red }
    Write-Host "$($found.Count) British spelling(s): use the American form (license, organization, catalog, honor, ...)." -ForegroundColor Red
    exit 1
}
Write-Host 'American spelling throughout.' -ForegroundColor Green
