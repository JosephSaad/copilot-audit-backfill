<#
    Unit tests for tools/Import-CopilotAuditCsv.ps1's CSV reader: how a
    Purview audit log export is parsed and mapped, with no SQL Server.
    Test-SqlIntegration.ps1 section 13 runs the whole import against one.

      - the current export's columns and the older one's (UserIds, Operations)
      - quoted fields holding commas, doubled quotes and line breaks
      - the record ID from the column, or from AuditData.Id when it is blank
      - the creation time from AuditData.CreationTime, with or without Z
      - the Graph backfill's mapping: object ID, UPN, workload, UserType name
      - AuditDataGz is gzip of the UTF-16 text, which SQL's DECOMPRESS reads
      - rejected rows (cut-off JSON, no ID, no time) with their line numbers
      - rows of another operation counted and left out, matched as the Graph
        filter matches ('Change user license.' for 'Change user license')
      - a file with no AuditData column refused
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')
. (Join-Path $root 'tools' 'Import-CopilotAuditCsv.ps1')
Import-CsvStageReader

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("csvimport-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

function New-Audit([string] $Id, [string] $Time, [hashtable] $Extra = @{}) {
    $a = [ordered]@{
        CreationTime = $Time; Id = $Id; Operation = 'CopilotInteraction'; OrganizationId = 'org-1'
        RecordType = 261; UserKey = "key-$Id"; UserType = 0; Workload = 'Copilot'; UserId = 'ann@contoso.com'
        ClientIP = '203.0.113.7'; CopilotEventData = @{ AppHost = 'Word' }
    }
    foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] }
    return ($a | ConvertTo-Json -Depth 10 -Compress)
}
function Read-All([string] $File, [string[]] $Ops = @('CopilotInteraction')) {
    $r = [CopilotAudit.CsvStageReader]::new($File, $Ops)
    try {
        $t = [CopilotAudit.CsvStageReader]::NewTable()
        while (-not $r.EndOfData) { [void] $r.Fill($t, 7, 2) }   # small batches: rows span Fill calls
        return [pscustomobject]@{ Table = $t; Reader = $r }
    }
    finally { $r.Dispose() }
}
function Unzip([byte[]] $Gz) {
    $in = [System.IO.MemoryStream]::new($Gz)
    $gzs = [System.IO.Compression.GZipStream]::new($in, [System.IO.Compression.CompressionMode]::Decompress)
    $out = [System.IO.MemoryStream]::new(); $gzs.CopyTo($out); $gzs.Dispose()
    return [System.Text.Encoding]::Unicode.GetString($out.ToArray())
}

try {
    Write-Host "`nCurrent export format" -ForegroundColor Cyan
    $multi = (New-Audit 'r-3' '2026-09-01T12:00:00Z' @{ Note = 'has "quotes", commas' }) -replace ',"Workload"', ",`n  `"Workload`""
    $rows = @(
        [pscustomobject]@{ RecordId = 'r-1'; CreationDate = '2026-09-01T10:00:00'; RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'; UserId = 'ann@contoso.com'; AuditData = (New-Audit 'r-1' '2026-09-01T10:00:00'); AssociatedAdminUnits = 'au-1'; AssociatedAdminUnitsNames = 'Sales' }
        [pscustomobject]@{ RecordId = '';    CreationDate = '';                    RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'; UserId = 'ann@contoso.com'; AuditData = (New-Audit 'r-2' '2026-09-01T11:00:00Z'); AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' }
        [pscustomobject]@{ RecordId = 'r-3'; CreationDate = '2026-09-01T12:00:00'; RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'; UserId = 'ann@contoso.com'; AuditData = $multi; AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' }
        [pscustomobject]@{ RecordId = 'r-4'; CreationDate = '2026-09-01T13:00:00'; RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'; UserId = 'ann@contoso.com'; AuditData = (New-Audit 'r-4' '2026-09-01T13:00:00').Substring(0, 40); AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' }
        [pscustomobject]@{ RecordId = 'r-5'; CreationDate = '2026-09-01T14:00:00'; RecordType = 'AzureActiveDirectory'; Operation = 'UserLoggedIn'; UserId = 'ann@contoso.com'; AuditData = (New-Audit 'r-5' '2026-09-01T14:00:00' @{ Operation = 'UserLoggedIn' }); AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' }
        [pscustomobject]@{ RecordId = '';    CreationDate = '2026-09-01T15:00:00'; RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'; UserId = 'ann@contoso.com'; AuditData = '{"CreationTime":"2026-09-01T15:00:00"}'; AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' }
        [pscustomobject]@{ RecordId = 'r-7'; CreationDate = 'not a date';          RecordType = 'CopilotInteraction'; Operation = 'CopilotInteraction'; UserId = 'ann@contoso.com'; AuditData = '{"Id":"r-7"}'; AssociatedAdminUnits = ''; AssociatedAdminUnitsNames = '' }
    )
    $f1 = Join-Path $tmp 'current.csv'
    $rows | Export-Csv $f1 -NoTypeInformation -Encoding utf8BOM
    $res = Read-All $f1
    $t = $res.Table; $r = $res.Reader
    $byId = @{}; foreach ($row in $t.Rows) { $byId[$row.RecordId] = $row }

    Assert-That ($r.RowsRead -eq 7) "every data row is read, a multi-line one as one row ($($r.RowsRead))"
    Assert-That ($r.RowsStaged -eq 3 -and $byId.ContainsKey('r-1') -and $byId.ContainsKey('r-2') -and $byId.ContainsKey('r-3')) "three rows staged: r-1, r-2, r-3 ($($r.RowsStaged))"
    Assert-That ($r.RowsOtherOperation -eq 1) "the other operation is counted and left out ($($r.RowsOtherOperation))"
    Assert-That ($r.RowsRejected -eq 3) "cut-off JSON, no ID and no time are rejected ($($r.RowsRejected))"
    $reasons = @($r.Rejects | ForEach-Object { '{0}:{1}' -f $_.LineNumber, $_.Reason })
    Assert-That (@($r.Rejects | Where-Object { $_.RecordId -eq 'r-4' -and $_.Reason -like '*not valid JSON*Excel*' }).Count -eq 1) 'the cut-off row is named, with the Excel hint'
    Assert-That (@($r.Rejects | Where-Object { $_.Reason -like 'No RecordId*' }).Count -eq 1) 'a row with no ID anywhere is rejected'
    Assert-That (@($r.Rejects | Where-Object { $_.RecordId -eq 'r-7' -and $_.Reason -like 'No readable creation time*' }).Count -eq 1) 'a row with no readable time is rejected'
    Assert-That (@($r.Rejects | Where-Object { $_.LineNumber -lt 2 }).Count -eq 0) "rejects carry the line they start on ($($reasons -join '; '))"

    $a = $byId['r-1']
    Assert-That ($a.CreatedDateTime -eq [datetime]::new(2026, 9, 1, 10, 0, 0) -and $a.FileId -eq 7) 'a CreationTime without Z is taken as UTC'
    Assert-That ($a.UserId -eq 'key-r-1' -and $a.UserPrincipalName -eq 'ann@contoso.com') 'UserId is the object ID (UserKey), UserPrincipalName the sign-in name'
    Assert-That ($a.UserType -eq 'Regular' -and $a.Service -eq 'Copilot' -and $a.OrganizationId -eq 'org-1') 'UserType 0 becomes Regular; Service is the workload'
    Assert-That ($a.ClientIp -eq '203.0.113.7' -and $a.AdministrativeUnits -eq 'au-1' -and $a.RecordType -eq 'CopilotInteraction') 'client IP, admin units and record type carried'
    Assert-That ((Unzip $a.AuditDataGz) -eq $rows[0].AuditData) 'AuditDataGz decompresses to the exact AuditData text'
    Assert-That ($byId['r-2'].CreatedDateTime -eq [datetime]::new(2026, 9, 1, 11, 0, 0)) 'a blank RecordId falls back to AuditData.Id; a Z time parses the same'
    Assert-That ((Unzip $byId['r-3'].AuditDataGz) -eq $multi) 'a quoted field with line breaks, commas and doubled quotes survives intact'
    Assert-That ($r.Earliest -eq [datetime]::new(2026, 9, 1, 10, 0, 0) -and $r.Latest -eq [datetime]::new(2026, 9, 1, 12, 0, 0)) 'earliest and latest staged times tracked'

    Write-Host "`nOlder export format, and licence operations" -ForegroundColor Cyan
    $f2 = Join-Path $tmp 'classic.csv'
    @(
        [pscustomobject]@{ CreationDate = '2026-08-01T09:00:00'; UserIds = 'bob@contoso.com'; Operations = 'Change user license.'; AuditData = (New-Audit 'c-1' '2026-08-01T09:00:00' @{ Operation = 'Change user license.'; Workload = 'AzureActiveDirectory'; UserType = 4 }) }
        [pscustomobject]@{ CreationDate = '2026-08-01T09:05:00'; UserIds = 'bob@contoso.com'; Operations = 'Hard Delete user.';    AuditData = (New-Audit 'c-2' '2026-08-01T09:05:00' @{ Operation = 'Hard Delete user.' }) }
        [pscustomobject]@{ CreationDate = '2026-08-01T09:10:00'; UserIds = 'bob@contoso.com'; Operations = 'UserLoggedIn';         AuditData = (New-Audit 'c-3' '2026-08-01T09:10:00' @{ Operation = 'UserLoggedIn' }) }
    ) | Export-Csv $f2 -NoTypeInformation -Encoding utf8BOM
    $res = Read-All $f2 @('Add user', 'Change user license', 'Delete user', 'Update user')
    $ids = @($res.Table.Rows | ForEach-Object { $_.RecordId })
    Assert-That ($res.Reader.RowsStaged -eq 2 -and $ids -contains 'c-1' -and $ids -contains 'c-2') "UserIds/Operations columns read; 'Change user license.' and 'Hard Delete user.' match as the Graph filter would ($($ids -join ', '))"
    Assert-That ($res.Reader.RowsOtherOperation -eq 1) 'UserLoggedIn is not a licence operation'
    $c1 = @($res.Table.Rows | Where-Object { $_.RecordId -eq 'c-1' })[0]
    Assert-That ($c1.UserPrincipalName -eq 'bob@contoso.com' -and $c1.UserType -eq 'System' -and $c1.Service -eq 'AzureActiveDirectory') 'the UserIds column gives the UPN; UserType 4 becomes System'

    Write-Host "`nNot an export" -ForegroundColor Cyan
    $f3 = Join-Path $tmp 'other.csv'
    'Name,Value', 'a,1' | Set-Content $f3
    $msg = try { [void] [CopilotAudit.CsvStageReader]::new($f3, @('CopilotInteraction')); '' } catch { $_.Exception.GetBaseException().Message }
    Assert-That ($msg -like '*No AuditData column*') "a CSV with no AuditData column is refused ($msg)"

    Assert-That ((Get-OperationSet @('Update user', ' Add user', 'Change user license', 'Delete user')) -eq 'Add user,Change user license,Delete user,Update user') 'the operation set is written as the backfill writes it'
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:Failures.Count) { Write-Host "$($script:Failures.Count) assertion(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All CSV import tests passed.' -ForegroundColor Green
