<#
    Tests the delete guard (2.6.5) without SQL Server: a load that empties a table and fills it again is refused
    when it would leave the table more than its percentage smaller than the last load did.

      1  tools\DropGuard.ps1: the rule, the message, -AllowDrop, and where the percentage comes from
      2  Every loader that replaces a whole table counts before it deletes, with its own setting
      3  Invoke-CopilotPipeline.ps1: each step gets its own percentage from the settings file

    The guard runs end to end against a database, for the agent workbook, the user directory, the licenses held
    and GitHub's seats, in Test-SqlIntegration.ps1 (sections 8 and 22).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$script:Failures = [System.Collections.Generic.List[string]]::new()
function Assert-That {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor Green }
    else            { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:Failures.Add($Message) }
}

# ---- 1. The rule ---------------------------------------------------------------------------------
Write-Host "`n1. tools\DropGuard.ps1" -ForegroundColor Cyan
. (Join-Path $root 'tools' 'DropGuard.ps1')
function Refused([long] $Before, [long] $After, [double] $Pct) { [bool] (Get-DropRefusal -What 'T' -Before $Before -After $After -MaxDropPercent $Pct -Setting 'S') }

Assert-That (Refused 10000 8000 15) '10,000 rows before and 8,000 after is 20% fewer: refused at 15%'
Assert-That (-not (Refused 10000 8500 15)) 'exactly 15% fewer is allowed at 15%'
Assert-That (Refused 10000 8499 15) 'a row more than that is refused'
Assert-That (-not (Refused 10000 8000 20) -and (Refused 10000 7999 20)) 'the percentage is the line: 20% fewer allowed at 20, a row more refused'
Assert-That (-not (Refused 0 0 15) -and -not (Refused 0 5 15)) 'a first load (nothing before) always goes ahead'
Assert-That (-not (Refused 100 100 15) -and -not (Refused 100 250 15)) 'a load that stays level or grows goes ahead'
Assert-That (Refused 100 0 15) 'an empty load over a full table is refused'
Assert-That (-not (Refused 100 0 0)) 'a percentage of 0 turns the guard off'
Assert-That (-not (Refused 100 1 100) -and -not (Refused 100 0 100)) 'and 100 lets any drop through'

$why = Get-DropRefusal -What 'User directory (audit.UserDirectory)' -Before 10000 -After 8000 -MaxDropPercent 15 -Setting 'UserDirectoryMaxDropPercent'
Assert-That ($why -like 'User directory (audit.UserDirectory): the new load has 8,000 row(s) where the last load left 10,000, 20% fewer; the delete guard allows at most 15% fewer, so nothing was replaced.*') `
    "the refusal names what, both counts, the drop and the limit ($why)"
Assert-That ($why -like '*run the load again with -AllowDrop, or raise UserDirectoryMaxDropPercent in copilot-audit.settings.psd1.') 'and says how to let a real drop through'
$odd = Get-DropRefusal -What 'T' -Before 3 -After 2 -MaxDropPercent 12.5 -Setting 'S'
Assert-That ($odd -like '*33.3% fewer*at most 12.5% fewer*') "a drop and a limit that are not whole numbers are said to one place ($odd)"

$thrown = $null
try { Assert-DropGuard -What 'T' -Before 10 -After 1 -MaxDropPercent 15 -Setting 'S' } catch { $thrown = $_ }
Assert-That ($thrown -and (Test-DropRefusal $thrown) -and "$thrown" -like 'T: the new load has 1 row(s)*') 'Assert-DropGuard stops the load, with an error Test-DropRefusal knows'
Assert-That (-not (Test-DropRefusal ([System.Management.Automation.ErrorRecord]::new([System.Exception]::new('other'), 'x', 'NotSpecified', $null)))) 'and another error is not taken for a refusal'
$warned = Assert-DropGuard -What 'T' -Before 10 -After 1 -MaxDropPercent 15 -Setting 'S' -AllowDrop 3>&1
Assert-That ("$warned" -like 'T: 1 row(s) where the last load left 10; let through by -AllowDrop.') "-AllowDrop lets it through, and warns ($warned)"
$quiet = @(Assert-DropGuard -What 'T' -Before 10 -After 9 -MaxDropPercent 15 -Setting 'S' 3>&1)
Assert-That ($quiet.Count -eq 0) 'a load the guard allows says nothing'

Assert-That ((Resolve-MaxDropPercent -What 'Agent workbook' -Given 40 -WasGiven $true -Settings @{ AgentWorkbookMaxDropPercent = 5 }) -eq 40) 'a percentage on the command line wins over the settings file'
Assert-That ((Resolve-MaxDropPercent -What 'Agent workbook' -Given 15 -WasGiven $false -Settings @{ AgentWorkbookMaxDropPercent = 5 }) -eq 5) 'else the loader''s own setting'
Assert-That ((Resolve-MaxDropPercent -What 'Agent workbook' -Given 15 -WasGiven $false -Settings @{ AgentWorkbookMaxDropPercent = 0 }) -eq 0) 'a setting of 0 turns it off'
Assert-That ((Resolve-MaxDropPercent -What 'Agent workbook' -Given 15 -WasGiven $false -Settings @{ AgentWorkbookMaxDropPercent = '7.5' }) -eq 7.5) 'a setting written as text is read as a number'
Assert-That ((Resolve-MaxDropPercent -What 'Agent workbook' -Given 15 -WasGiven $false -Settings @{ UserDirectoryMaxDropPercent = 5 }) -eq 15) 'another loader''s setting is not this one''s: the default, 15'
Assert-That ((Resolve-MaxDropPercent -What 'GitHub Copilot seats' -Given 15 -WasGiven $false -Settings $null) -eq 15) 'no settings file: 15'
$bad = $null
try { $null = Resolve-MaxDropPercent -What 'Foundry deployments' -Given 15 -WasGiven $false -Settings @{ FoundryDeploymentMaxDropPercent = '150' } } catch { $bad = "$_" }
Assert-That ($bad -like 'FoundryDeploymentMaxDropPercent in the settings file is ''150''*from 0 to 100*') "a setting that is not a percentage stops the load, saying so ($bad)"
Assert-That (@($script:DropGuardSetting.Values) -join ',' -eq 'AgentWorkbookMaxDropPercent,UserDirectoryMaxDropPercent,LicenseMaxDropPercent,GitHubSeatMaxDropPercent,FoundryDeploymentMaxDropPercent,CostImportMaxDropPercent,PowerAppsActivityMaxDropPercent') `
    'each of the five loaders, the cost import (2.6.6) and the Power Apps activity load (2.6.9) has a setting of its own'

# ---- 2. The loaders -------------------------------------------------------------------------------
Write-Host "`n2. The loaders that replace a whole table" -ForegroundColor Cyan
$src = @{}
foreach ($f in 'Import-AgentWorkbook.ps1', 'Export-UsersWithHierarchy-ToSql.ps1', 'Update-CopilotAuditEnrichment.ps1', 'Import-GitHubCopilotUsage.ps1', 'Import-FoundryUsage.ps1', 'ExternalUsage.Common.ps1', 'Import-PowerAppsActivity.ps1') {
    $src[$f] = Get-Content -Raw -LiteralPath (Join-Path $root 'tools' $f)
}
foreach ($f in 'Import-AgentWorkbook.ps1', 'Export-UsersWithHierarchy-ToSql.ps1', 'Update-CopilotAuditEnrichment.ps1', 'Import-GitHubCopilotUsage.ps1', 'Import-FoundryUsage.ps1', 'Import-PowerAppsActivity.ps1') {
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($src[$f], [ref] $null, [ref] $null)
    $params = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
    $max = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'MaxDropPercent' }
    Assert-That ($params -contains 'MaxDropPercent' -and $params -contains 'AllowDrop' -and "$($max.DefaultValue)" -eq '15') "$f takes -MaxDropPercent (15 by default) and -AllowDrop"
}
$wb = $src['Import-AgentWorkbook.ps1']
Assert-That ($wb.IndexOf('Assert-DropGuard -What "Sheet $sheet') -gt 0 -and $wb.IndexOf('Assert-DropGuard -What "Sheet $sheet') -lt $wb.IndexOf('Sql "DELETE FROM $q;" $tx')) `
    'the workbook checks every sheet before it empties any table'
Assert-That ($wb -match "Resolve-MaxDropPercent -What 'Agent workbook'") 'and reads AgentWorkbookMaxDropPercent from the settings file'
$dir = $src['Export-UsersWithHierarchy-ToSql.ps1']
Assert-That ($dir.IndexOf("Assert-DropGuard -What 'User directory") -gt 0 -and $dir.IndexOf("Assert-DropGuard -What 'User directory") -lt $dir.IndexOf("'TRUNCATE TABLE audit.UserDirectory;'")) `
    'the directory counts its rows before it empties the table'
$lic = $src['Update-CopilotAuditEnrichment.ps1']
Assert-That ($lic.IndexOf("Assert-DropGuard -What 'Licenses held") -gt 0 -and $lic.IndexOf("Assert-DropGuard -What 'Licenses held") -lt $lic.IndexOf("Save-RowBatch -Table 'audit.CopilotLicense'")) `
    'the licenses are checked before anything about the people is written'
Assert-That ($src['Import-GitHubCopilotUsage.ps1'] -match "-Setting 'GitHubSeatMaxDropPercent'" -and $src['Import-FoundryUsage.ps1'] -match "-Setting 'FoundryDeploymentMaxDropPercent'") `
    'GitHub''s seats and Foundry''s deployments go through the guard, each with its own setting'
Assert-That ($src['Import-FoundryUsage.ps1'] -match 'if \(-not \(Test-DropRefusal \$_\)\) \{ continue \}') 'a refusal of Foundry''s deployments still loads that subscription''s usage and cost'
$pa = $src['Import-PowerAppsActivity.ps1']
Assert-That ($pa -match "-Setting 'PowerAppsActivityMaxDropPercent'" -and $pa -match "-ReplaceWhere 'Product = @p'" -and $pa -match "Resolve-MaxDropPercent -What 'Power Apps activity'") `
    'the Power Apps activity load (2.6.9) replaces only the Power Apps rows, through the guard, with its own setting'
$common = $src['ExternalUsage.Common.ps1']
Assert-That ($common.IndexOf('Assert-DropGuard') -gt 0 -and $common.IndexOf('Assert-DropGuard') -lt $common.IndexOf('BeginTransaction')) 'the shared writer counts before it deletes'

# ---- 3. The nightly run ---------------------------------------------------------------------------
Write-Host "`n3. Invoke-CopilotPipeline.ps1" -ForegroundColor Cyan
. (Join-Path $root 'Invoke-CopilotPipeline.ps1')
$s = @{ TenantId = 't'; ClientId = 'c'; CertificateThumbprint = 'AB'; SqlServer = 'SQL1'; Database = 'CopilotAudit'; TargetDomain = 'corp.contoso.com'
        AgentWorkbookUrl = '\\share\agents.xlsx'; GitHubEnterprise = 'contoso'; FoundrySubscriptions = @('sub') }
$today = [datetime] '2026-10-14'
function Arg($steps, [string] $Like) { $st = @($steps | Where-Object { $_.Name -like $Like })[0]; if ($st.Arguments.Contains('MaxDropPercent')) { $st.Arguments.MaxDropPercent } else { 'unset' } }
$plain = Get-PipelineStep -Setting $s -Today $today -Root 'R:'
Assert-That ((Arg $plain 'User directory*') -eq 'unset' -and (Arg $plain 'Agent workbook*') -eq 'unset' -and (Arg $plain 'GitHub*') -eq 'unset') `
    'with no percentage in the settings, no step is given one: each loader''s own default, 15, applies'
$set = $s + @{ UserDirectoryMaxDropPercent = 5; LicenseMaxDropPercent = 25; AgentWorkbookMaxDropPercent = 0; GitHubSeatMaxDropPercent = 40; FoundryDeploymentMaxDropPercent = 60
               PowerAppsActivity = $true; PowerAppsActivityMaxDropPercent = 30 }
$steps = Get-PipelineStep -Setting $set -Today $today -Root 'R:'
Assert-That ((Arg $steps 'User directory*') -eq 5 -and (Arg $steps 'Copilot licenses*') -eq 25 -and (Arg $steps 'Agent workbook*') -eq 0 -and
             (Arg $steps 'GitHub*') -eq 40 -and (Arg $steps 'Foundry*') -eq 60 -and (Arg $steps 'Power Apps*') -eq 30) `
    'each loader gets its own percentage from the settings file, 0 (off) included'
$hist = Get-PipelineStep -Setting $set -History -Today $today -Root 'R:'
Assert-That ((Arg $hist 'User directory*') -eq 5 -and (Arg $hist 'Copilot licenses*') -eq 25) 'a history load passes them too'
$sp = @{} + $set; $sp.Remove('AgentWorkbookUrl'); $sp.AgentWorkbookMaxDropPercent = 12
$site = Get-PipelineStep -Setting ($sp + @{ AgentWorkbookSiteUrl = 'https://contoso.sharepoint.com/sites/x'; AgentWorkbookClientId = 'w'; AgentWorkbookCertificateThumbprint = 'CD' }) -Today $today -Root 'R:'
Assert-That ((Arg $site 'Agent workbook*') -eq 12) 'and the workbook from SharePoint Online gets its own'

# A refused load ends with its own exit code, which the night records and goes on from: the table kept its last
# good load, so the interactions and the rest still load. Any other failure still stops the run.
Assert-That ($script:DropRefusedExitCode -eq 3) 'a load the guard refused ends with exit code 3'
$pipe = Get-Content -Raw -LiteralPath (Join-Path $root 'Invoke-CopilotPipeline.ps1')
$at3 = $pipe.IndexOf('if ($code -eq $script:DropRefusedExitCode) {'); $atFail = $pipe.IndexOf('if ($code -ne 0) {')
Assert-That ($at3 -gt 0 -and $at3 -lt $atFail -and $pipe.Substring($at3, $atFail - $at3) -match 'continue') 'the nightly run takes exit code 3 as refused and goes on, before it takes any other code as a failure that stops it'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "dropguard-$PID"
$null = New-Item -ItemType Directory -Force -Path $tmp
try {
    $stepScript = Join-Path $tmp 'refused.ps1'
    Set-Content -LiteralPath $stepScript -Value ". '$(Join-Path $root 'tools' 'DropGuard.ps1')'; Write-Host (Get-DropRefusal -What 'Sheet X' -Before 10 -After 1 -MaxDropPercent 15 -Setting 'S'); exit `$script:DropRefusedExitCode"
    $log = Join-Path $tmp 'night.log'
    $code = Invoke-PipelineStep -Step ([pscustomobject]@{ Name = 'Agent workbook from Excel'; Script = $stepScript; Arguments = @{} }) -Log $log 6>$null
    Assert-That ($code -eq 3) "a step that ends with it is seen by the run as exit code 3 ($code)"
    $detail = Get-DropGuardDetail -Step 'Agent workbook from Excel', 'User directory from AD' -Log $log
    Assert-That ($detail -like 'Refused by the delete guard: Agent workbook from Excel, User directory from AD. Each table kept its last good load; the rest of the run went on.*Sheet X: the new load has 1 row(s)*') `
        "and the run's detail, on Admin > Health, names the refused steps and gives the guard's own reason ($($detail -replace "`n", ' | '))"
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
foreach ($f in 'Import-AgentWorkbook.ps1', 'Export-UsersWithHierarchy-ToSql.ps1', 'Update-CopilotAuditEnrichment.ps1', 'Import-PowerAppsActivity.ps1') {
    Assert-That ($src[$f] -match 'exit \$script:DropRefusedExitCode') "$f ends with it when refused"
}

# ---------------------------------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All delete guard tests passed.' -ForegroundColor Green
