<#
.SYNOPSIS
    Checks how the release is built (2.4.12): least-privilege tokens, actions and images pinned
    so they cannot change under it, the same tests as CI, and the signature check on the modules
    tools/Save-CopilotAuditModules.ps1 downloads. From 2.6.5 the release ships no bundled
    modules: no offline zip.

.DESCRIPTION
    Before 2.4.12 every job of release.yml had a token that could write to the repository and its
    releases, every action was taken by a tag its owner can move, the bundled module was loaded
    and shipped with no check of where it came from, and the release ran fewer tests than CI.
    This reads the workflows, and runs tools/Save-CopilotAuditModules.ps1's check on files of
    known signature; it downloads nothing.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-Workflows.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}

function Read-Workflow([string] $Name) { [System.IO.File]::ReadAllText((Join-Path $root '.github' 'workflows' $Name)).Replace("`r`n", "`n") }

# Each job's own text, by name: from its line under jobs: to the next.
function Get-Job([string] $Text) {
    $jobs = [ordered]@{}
    $body = $Text.Substring($Text.IndexOf("`njobs:`n") + 7)
    $marks = [regex]::Matches($body, '(?m)^  ([A-Za-z][\w-]*):\s*$')
    for ($i = 0; $i -lt $marks.Count; $i++) {
        $end = if ($i + 1 -lt $marks.Count) { $marks[$i + 1].Index } else { $body.Length }
        $jobs[$marks[$i].Groups[1].Value] = $body.Substring($marks[$i].Index, $end - $marks[$i].Index)
    }
    $jobs
}

$release = Read-Workflow 'release.yml'
$ci = Read-Workflow 'ci.yml'
$jobs = Get-Job $release

# ---------------------------------------------------------------------------
Write-Host "`n1. Tokens: read for every job, write for publish alone" -ForegroundColor Cyan
$top = $release.Substring(0, $release.IndexOf("`njobs:`n"))
Assert-That ($top -match '(?m)^permissions:\n  contents: read\s*$') 'the workflow''s token reads only'
Assert-That ($jobs.Contains('publish') -and $jobs['publish'] -match '(?m)^    permissions:\n      contents: write\s*$') 'publish, which creates the release, may write'
$writers = @($jobs.Keys | Where-Object { $_ -ne 'publish' -and $jobs[$_] -cmatch '(?m)^\s+[\w-]+: write\s*$' })
Assert-That ($writers.Count -eq 0) "no other job is given write ($($writers -join ', '))"
$checkouts = [regex]::Matches($release, '(?m)^( +)- uses: actions/checkout@.*\n\1  with:\n\1    persist-credentials: false$').Count
$allCheckouts = [regex]::Matches($release, 'actions/checkout@').Count
Assert-That ($checkouts -eq $allCheckouts -and $allCheckouts -gt 0) "every checkout leaves no token behind for later steps ($checkouts of $allCheckouts)"

# ---------------------------------------------------------------------------
Write-Host "`n2. Actions and images pinned, with their release named" -ForegroundColor Cyan
$uses = [regex]::Matches($release, '(?m)uses:\s*(\S+)(.*)$')
$loose = @($uses | Where-Object { $_.Groups[1].Value -notmatch '^[\w.-]+/[\w.-]+@[0-9a-f]{40}$' -or $_.Groups[2].Value -notmatch '^\s+# v\d+(\.\d+)*\s*$' })
Assert-That ($uses.Count -gt 0 -and $loose.Count -eq 0) "every action is taken by its full commit, with its version in a comment ($($uses.Count); loose: $(($loose | ForEach-Object { $_.Groups[1].Value }) -join ', '))"
$images = [regex]::Matches($release, '(?m)^\s+image:\s*(\S+)')
$floating = @($images | Where-Object { $_.Groups[1].Value -notmatch '@sha256:[0-9a-f]{64}$' })
Assert-That ($images.Count -gt 0 -and $floating.Count -eq 0) "every container image is taken by its digest ($($images.Count))"

# ---------------------------------------------------------------------------
Write-Host "`n3. The release runs the tests CI runs" -ForegroundColor Cyan
$ciJobs = Get-Job $ci
function Get-TestFile([string] $Job) { @([regex]::Matches($Job, 'tests/(Test-[\w-]+\.ps1)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) }
$ciTests = Get-TestFile $ciJobs['test']
$relTests = Get-TestFile $jobs['test']
$missing = @($ciTests | Where-Object { $_ -notin $relTests })
Assert-That ($ciTests.Count -ge 8 -and $missing.Count -eq 0) "the release's test job runs every test CI's does ($($relTests.Count); missing: $($missing -join ', '))"
$onDisk = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter 'Test-*.ps1' | ForEach-Object Name)
$unrun = @($onDisk | Where-Object { $_ -ne 'Test-SqlIntegration.ps1' -and $_ -notin $ciTests })
Assert-That ($unrun.Count -eq 0) "every test file runs in CI ($($unrun -join ', '))"
Assert-That ($jobs['sql-integration'] -match 'tests/Test-SqlIntegration\.ps1' -and $jobs['dashboard'] -match 'dotnet test') 'and the SQL integration and dashboard tests too'
Assert-That ($ciJobs.Contains('analyze') -and $jobs.Contains('analyze') -and $jobs['analyze'] -match 'Invoke-ScriptAnalyzer' -and $jobs['analyze'] -match 'PSScriptAnalyzerSettings\.psd1') 'and PSScriptAnalyzer, as CI does'
$analyzeCheck = $jobs['analyze'].IndexOf('Get-UnsignedModuleFile')
Assert-That ($jobs['analyze'] -match 'Save-Module PSScriptAnalyzer -RequiredVersion [\d.]+' -and $analyzeCheck -gt 0 -and $jobs['analyze'].IndexOf('Import-Module') -gt $analyzeCheck) 'which takes the analyzer at a fixed version and checks its signatures before loading it'
Assert-That ($jobs['publish'] -match 'needs: \[test, sql-integration, analyze, dashboard\]') 'and publishes only when all of them pass'
# Two slips an edit can make that GitHub only reports as a workflow that never ran. A step joined to the line
# before it ("run: ...ps1      - name: Next"), and a blank line inside a command continued with "\", which ends the
# command there: the release would have been created without the files listed after it.
foreach ($wf in 'ci.yml', 'release.yml') {
    $lines = Get-Content -LiteralPath (Join-Path $root '.github' 'workflows' $wf)
    $joined = @($lines | Where-Object { $_ -match '\S\s+- (name|uses):\s' -and $_ -notmatch '^\s*#' })
    $broken = @(for ($i = 0; $i -lt $lines.Count - 1; $i++) { if ($lines[$i] -match '\\\s*$' -and $lines[$i + 1].Trim() -eq '') { $i + 1 } })
    Assert-That ($joined.Count -eq 0 -and $broken.Count -eq 0) "$wf has every step on a line of its own, and no command continued onto a blank line ($($joined -join ' | ')$($broken -join ', '))"
}
# The modules are the operator's to install; the release carries none of them.
Assert-That (-not $jobs.Contains('bundle') -and (Get-Content -Raw -LiteralPath (Join-Path $root '.github' 'workflows' 'release.yml')) -notmatch 'offline-win-x64') `
    'and builds no offline zip of bundled modules (2.6.5)'

# ---------------------------------------------------------------------------
Write-Host "`n4. A downloaded module is checked before it runs" -ForegroundColor Cyan
$save = Join-Path $root 'tools' 'Save-CopilotAuditModules.ps1'
$saveText = [System.IO.File]::ReadAllText($save)
$checkAt = $saveText.IndexOf('Get-UnsignedModuleFile -Path $modulesDir')
$importAt = $saveText.IndexOf('Import-Module -Name $name')
Assert-That ($checkAt -gt 0 -and $importAt -gt $checkAt) 'Save-CopilotAuditModules.ps1 checks the signatures before it imports the module'
Assert-That ($jobs['analyze'] -match 'Save-CopilotAuditModules\.ps1') 'and the release checks the analyzer it downloads with the same check'
if (-not (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue)) {
    Write-Host '  SKIP  the signature check itself needs Windows' -ForegroundColor Yellow
}
else {
    . $save
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "copilot-sig-$PID"
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        # PowerShell's own files are signed by Microsoft: a copy keeps its signature, an edited copy loses it.
        $signed = Get-ChildItem -LiteralPath (Join-Path $PSHOME 'Modules') -Recurse -Filter *.psd1 |
                  Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -eq 'Valid' } | Select-Object -First 1
        $dll = Join-Path $PSHOME 'System.Management.Automation.dll'
        Assert-That ([bool] $signed -and (Get-AuthenticodeSignature -LiteralPath $dll).Status -eq 'Valid') "found files signed by Microsoft to check against ($($signed.Name))"
        $good = Join-Path $tmp 'good'
        New-Item -ItemType Directory -Path $good -Force | Out-Null
        Copy-Item -LiteralPath $signed.FullName, $dll -Destination $good
        Set-Content -LiteralPath (Join-Path $good 'readme.txt') -Value 'not code'
        Assert-That (@(Get-UnsignedModuleFile -Path $good).Count -eq 0) 'a library and a manifest signed by Microsoft pass, and a file that cannot run is not asked for a signature'
        $bad = Join-Path $tmp 'bad'
        New-Item -ItemType Directory -Path $bad -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $bad 'Unsigned.psm1') -Value 'function Get-Thing { 1 }'
        $edited = Join-Path $bad $signed.Name
        $text = [System.IO.File]::ReadAllText($signed.FullName)
        [System.IO.File]::WriteAllText($edited, $text.Replace('@{', "@{`r`n# changed"), [System.Text.UTF8Encoding]::new($true))
        $found = @(Get-UnsignedModuleFile -Path $bad)
        Assert-That ($found.Count -eq 2) "an unsigned module and a signed manifest changed afterwards are both refused ($(($found | ForEach-Object { "$([System.IO.Path]::GetFileName($_.Path)) $($_.Status)" }) -join ', '))"
        $other = @(Get-UnsignedModuleFile -Path $good -Publisher 'O=Someone Else')
        Assert-That ($other.Count -eq 2) 'and a valid signature from anyone but the module''s publishers is refused'
    }
    finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All workflow tests passed.' -ForegroundColor Green
