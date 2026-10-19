<#
.SYNOPSIS
    Loads who used Power Apps, and when, from the Purview audit log into audit.PowerPlatformUserActivity (2.6.9):
    the Power Apps rows Leadership > Costs and savings reads to find Power Apps licenses nobody uses.

.DESCRIPTION
    Reads the audit log through Microsoft Graph's audit search (security/auditLog/queries), as the Copilot load
    does, with the same app registration and certificate: its application permission AuditLogsQuery.Read.All
    covers every record type, so nothing new is granted. Only Power Apps' own records are asked for (record type
    powerAppsApp: an app launched, created, edited, shared, deleted), for -Days complete UTC days ending yesterday,
    and nothing else is searched or stored.

    Each person who used Power Apps in those days is one row, product 'Power Apps':
      - LastActiveUtc  the last time a record names them, in the whole range;
      - ActiveDays30   on how many of the last 30 of those days they used it (all of them, with -Days under 30).
    Nothing but the person's sign-in name, the product and those two figures is kept: not the apps, the
    environments or the records.

    The Power Apps rows are replaced whole each run, and only they: the Power BI or Power Automate rows a CSV put
    in the table (tools\Import-CostCsv.ps1) are left as they are. The delete guard (2.6.5) holds a run that finds
    more than PowerAppsActivityMaxDropPercent (15 by default) fewer people than the table holds, so a search that
    came back short leaves the last good load in place; -AllowDrop lets a real drop through once. A load the guard
    refused ends with exit code 3, which the nightly run records and goes on from.

    THE SEARCHES. The range is searched as one window first. Where Graph says a window passed its record cap
    (the beta status's isRecordCountLimitExceeded), the window is split in two and each half searched again, down
    to an hour; so a quiet tenant costs one search a run and a busy one a few more. A run stops after -MaxSearches
    searches, so it can never eat the tenant's daily budget of audit searches, which the Copilot load shares.
    Without the beta status endpoint a window over the cap cannot be told apart: its records load and the run
    says the cap could not be checked.

    The run is recorded in audit.ExternalUsageRun (Source PowerApps), with the days, the people and why it ended
    as it did.

    With nothing given, everything comes from copilot-audit.settings.psd1: SqlServer, Database,
    TrustServerCertificate, TenantId, ClientId and CertificateThumbprint, and PowerAppsActivityDays for -Days.

.PARAMETER Days
    How many complete UTC days to search, ending yesterday. Default: PowerAppsActivityDays in the settings, else
    30. Microsoft keeps audit records for 180 days unless a retention policy kept more; days older than that come
    back empty. A run with fewer days than the last finds fewer people, which the delete guard holds: keep the
    nightly run's days and a run by hand the same, or pass -AllowDrop.

.PARAMETER MaxSearches
    The most audit searches one run may submit (20). A window over the record cap costs two more.

.PARAMETER PollSeconds
    How often a search's status is asked for while it runs (30).

.PARAMETER TimeoutMinutes
    How long one search may take before the run stops, saying so (240). Searches of a busy tenant can take an hour.

.PARAMETER MaxDropPercent
    The delete guard: the most the people with Power Apps activity may fall from the last load, in percent (15,
    or PowerAppsActivityMaxDropPercent in the settings file). 0 turns it off.

.PARAMETER AllowDrop
    Replace the Power Apps rows even when the guard would refuse, once.

.EXAMPLE
    .\tools\Import-PowerAppsActivity.ps1 -Days 180      # the history, as far back as the audit log keeps it
.EXAMPLE
    .\tools\Import-PowerAppsActivity.ps1                # as the nightly run does, from the settings file
.EXAMPLE
    .\Invoke-CopilotPipeline.ps1 -PowerAppsActivity -Days 90   # only this load, for every tenant in the settings
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateRange(1, 365)][int] $Days = 30,
    [string] $TenantId,
    [string] $ClientId,
    [string] $CertificateThumbprint,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    [ValidateRange(1, 100)][int] $MaxSearches = 20,
    [ValidateRange(1, 3600)][int] $PollSeconds = 30,
    [ValidateRange(1, 1440)][int] $TimeoutMinutes = 240,
    # A token already in hand, in place of signing in (the tests); and the endpoints, for a sovereign cloud or a mock.
    [securestring] $AccessToken,
    [string] $GraphEndpoint = 'https://graph.microsoft.com',
    [string] $LoginEndpoint = 'https://login.microsoftonline.com',
    [ValidateRange(0, 100)][double] $MaxDropPercent = 15,
    [switch] $AllowDrop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExternalUsage.Common.ps1')
# Read here: in Invoke-Main, $PSBoundParameters is its own.
$script:MaxDropGiven = $PSBoundParameters.ContainsKey('MaxDropPercent')
$script:DaysGiven = $PSBoundParameters.ContainsKey('Days')
$script:DatabaseGiven = $PSBoundParameters.ContainsKey('Database')

$script:RecordType = 'powerAppsApp'   # Graph's auditLogRecordType for Power Apps' app records
$script:Product = 'Power Apps'        # the table's name for it, as the cost pages read it
$script:UseBeta = $true               # the beta status says whether a search passed its record cap; v1.0 does not
$script:Inv = [cultureinfo]::InvariantCulture

function Write-Line([string] $Text) { Write-Information ("{0:u} {1}" -f [datetime]::UtcNow, $Text) -InformationAction Continue }

function Get-ActivityRange {
    <# The UTC range a load searches: -Days whole days, ending at the start of today. Pure, for the tests. #>
    param([ValidateRange(1, 365)][int] $Days, [datetime] $Today = [datetime]::UtcNow.Date)
    $to = [datetime]::SpecifyKind($Today.Date, [System.DateTimeKind]::Utc)
    [pscustomobject]@{ From = $to.AddDays(-$Days); To = $to }
}

function Get-ActivityQueryBody {
    <# The audit search for one window: Power Apps' records only. Pure, for the tests. #>
    param([datetime] $From, [datetime] $To)
    [ordered]@{
        '@odata.type'       = '#microsoft.graph.security.auditLogQuery'
        displayName         = 'PowerAppsActivity {0} {1}' -f $From.ToString('yyyyMMdd-HHmm', $script:Inv), $To.ToString('yyyyMMdd-HHmm', $script:Inv)
        filterStartDateTime = $From.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $script:Inv)
        filterEndDateTime   = $To.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $script:Inv)
        recordTypeFilters   = @($script:RecordType)
    }
}

function Split-ActivityWindow {
    <# A window over the record cap as its two halves, on the hour; nothing for a window of an hour or less. Pure, for the tests. #>
    param([datetime] $From, [datetime] $To)
    $hours = [Math]::Floor(($To - $From).TotalHours)
    if ($hours -lt 2) { return , @() }
    $mid = $From.AddHours([Math]::Floor($hours / 2))
    , @([pscustomobject]@{ From = $From; To = $mid }, [pscustomobject]@{ From = $mid; To = $To })
}

function ConvertTo-UtcTime {
    <# A record's time as UTC: Graph sends it as text, which ConvertFrom-Json may already have read as a date. Pure. #>
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) {
        if ($Value.Kind -eq [System.DateTimeKind]::Local) { return $Value.ToUniversalTime() }
        return [datetime]::SpecifyKind($Value, [System.DateTimeKind]::Utc)
    }
    if ($Value -is [datetimeoffset]) { return $Value.UtcDateTime }
    $parsed = [datetimeoffset]::MinValue
    if ([datetimeoffset]::TryParse("$Value", $script:Inv, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref] $parsed)) { return $parsed.UtcDateTime }
    $null
}

function New-ActivityAggregate {
    <# Who was seen, by sign-in name whatever its case: when last, and on which of the last 30 days. #>
    [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
}

function Add-ActivityRecord {
    <#
        Counts one audit record in the aggregate. A record names its person by userPrincipalName (else userId); one
        that names nobody with an @ (a system or service action) is left out. Returns whether it was counted. Pure.
    #>
    param([System.Collections.Generic.Dictionary[string, object]] $Aggregate, $Record, [datetime] $Since30)
    $upn = $null
    foreach ($p in 'userPrincipalName', 'userId') {
        $prop = $Record.PSObject.Properties[$p]
        if ($prop -and "$($prop.Value)" -like '*@*') { $upn = "$($prop.Value)".Trim().ToLowerInvariant(); break }
    }
    if (-not $upn) { return $false }
    $at = $Record.PSObject.Properties['createdDateTime']
    $when = if ($at) { ConvertTo-UtcTime $at.Value } else { $null }
    if ($null -eq $when) { return $false }
    $entry = $null
    if (-not $Aggregate.TryGetValue($upn, [ref] $entry)) {
        $entry = [pscustomobject]@{ Last = $when; Days = [System.Collections.Generic.HashSet[datetime]]::new() }
        $Aggregate[$upn] = $entry
    }
    elseif ($when -gt $entry.Last) { $entry.Last = $when }
    if ($when -ge $Since30) { [void] $entry.Days.Add($when.Date) }
    $true
}

function ConvertTo-ActivityRow {
    <# The aggregate as the table's rows, by sign-in name: product, last activity to the second, days active in 30. Pure. #>
    param([System.Collections.Generic.Dictionary[string, object]] $Aggregate)
    , @(foreach ($k in ($Aggregate.Keys | Sort-Object)) {
            $e = $Aggregate[$k]
            [pscustomobject]@{
                UserPrincipalName = $k
                Product           = $script:Product
                LastActiveUtc     = [datetime]::SpecifyKind($e.Last.AddTicks(-($e.Last.Ticks % [TimeSpan]::TicksPerSecond)), [System.DateTimeKind]::Utc)
                ActiveDays30      = $e.Days.Count
            }
        })
}

function Invoke-GraphRequest {
    <#
        One call of Microsoft Graph with the token: its status and its answer, read as JSON. Throttling (429) and a
        service that is briefly away (502, 503, 504) are waited out, for as long as Graph says or a growing pause,
        up to five times. Anything else is the caller's to judge.
    #>
    param([string] $Uri, [string] $BearerToken, [string] $Method = 'Get', $Body)
    for ($try = 1; ; $try++) {
        $p = @{ Uri = $Uri; Method = $Method; Headers = @{ Authorization = "Bearer $BearerToken" }; SkipHttpErrorCheck = $true; TimeoutSec = 300 }
        if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 10 -Compress); $p.ContentType = 'application/json' }
        $r = Invoke-WebRequest @p
        $status = [int] $r.StatusCode
        if ($status -in 429, 502, 503, 504 -and $try -le 5) {
            $wait = [Math]::Min(120, 5 * [Math]::Pow(2, $try - 1))
            if ($r.Headers.ContainsKey('Retry-After')) { $n = 0; if ([int]::TryParse("$(@($r.Headers['Retry-After'])[0])", [ref] $n) -and $n -gt 0) { $wait = [Math]::Min($n, 600) } }
            Write-Line "Graph answered ${status}: waiting $wait s before asking again."
            Start-Sleep -Seconds $wait
            continue
        }
        $parsed = $null
        if ($r.Content) { try { $parsed = $r.Content | ConvertFrom-Json -Depth 64 } catch { $parsed = $null } }
        return [pscustomobject]@{ Status = $status; Body = $parsed }
    }
}

function Get-GraphError {
    <# Graph's own reason for a failed call, with what to check for the two that are about access. Pure, for the tests. #>
    param([int] $Status, $Body, [string] $What)
    $message = "HTTP $Status"
    if ($Body -and $Body.PSObject.Properties['error'] -and $Body.error) { $message = "$($Body.error.code): $($Body.error.message)" }
    $hint = switch ($Status) {
        401 { ' The token was refused: check the app registration (ClientId, TenantId) and its certificate.' }
        403 { ' The app needs the application permission AuditLogsQuery.Read.All with admin consent, as the Copilot load does.' }
        default { '' }
    }
    "Graph answered $Status for $What`: $message.$hint"
}

function New-AuditSearch {
    <# Submits the search for one window and returns its id. #>
    param([string] $Graph, [string] $BearerToken, [datetime] $From, [datetime] $To)
    $r = Invoke-GraphRequest -Uri "$Graph/v1.0/security/auditLog/queries" -BearerToken $BearerToken -Method Post -Body (Get-ActivityQueryBody -From $From -To $To)
    if ($r.Status -lt 200 -or $r.Status -gt 299 -or -not $r.Body -or -not $r.Body.PSObject.Properties['id']) {
        throw (Get-GraphError -Status $r.Status -Body $r.Body -What 'the audit search')
    }
    "$($r.Body.id)"
}

function Wait-AuditSearch {
    <#
        Waits for a search to finish and returns whether it passed its record cap: $true or $false where the beta
        status says, $null where only v1.0 answers. A search that fails, is cancelled or outlasts -TimeoutMinutes
        stops the run.
    #>
    param([string] $Graph, [string] $BearerToken, [string] $Id, [int] $PollSeconds, [int] $TimeoutMinutes)
    $deadline = [datetime]::UtcNow.AddMinutes($TimeoutMinutes)
    while ($true) {
        $state = $null
        if ($script:UseBeta) {
            $b = Invoke-GraphRequest -Uri "$Graph/beta/security/auditLog/queries/$Id" -BearerToken $BearerToken
            if ($b.Status -ge 200 -and $b.Status -le 299) { $state = $b.Body }
            else { $script:UseBeta = $false; Write-Line "The beta status of audit searches is not available here (HTTP $($b.Status)): using v1.0, which cannot say whether a search passed its record cap." }
        }
        if (-not $state) {
            $v = Invoke-GraphRequest -Uri "$Graph/v1.0/security/auditLog/queries/$Id" -BearerToken $BearerToken
            if ($v.Status -lt 200 -or $v.Status -gt 299) { throw (Get-GraphError -Status $v.Status -Body $v.Body -What "the status of audit search $Id") }
            $state = $v.Body
        }
        $status = "$($state.status)"
        if ($status -eq 'succeeded') {
            $cap = $state.PSObject.Properties['isRecordCountLimitExceeded']
            return $(if ($cap -and $cap.Value -is [bool]) { $cap.Value } else { $null })
        }
        if ($status -in 'failed', 'cancelled') { throw "Audit search $Id ended $status in Purview: nothing was loaded. Run again later." }
        if ([datetime]::UtcNow -gt $deadline) { throw "Audit search $Id was still $status after $TimeoutMinutes minutes: nothing was loaded. Run again, or give -TimeoutMinutes more." }
        Start-Sleep -Seconds $PollSeconds
    }
}

function Read-AuditSearch {
    <#
        Reads every record of a finished search, a page at a time, into the aggregate. Returns how many records it
        read. Asks for pages of 1,000; a service that refuses the page size is asked again without it.
    #>
    param([string] $Graph, [string] $BearerToken, [string] $Id, [System.Collections.Generic.Dictionary[string, object]] $Aggregate, [datetime] $Since30)
    $read = 0
    $first = "$Graph/v1.0/security/auditLog/queries/$Id/records"
    $next = "$first`?`$top=1000"
    while ($next) {
        $r = Invoke-GraphRequest -Uri $next -BearerToken $BearerToken
        if ($r.Status -eq 400 -and $next -like '*$top=1000') { $next = $first; continue }
        if ($r.Status -lt 200 -or $r.Status -gt 299) { throw (Get-GraphError -Status $r.Status -Body $r.Body -What "the records of audit search $Id") }
        $page = $r.Body
        if ($page -and $page.PSObject.Properties['value']) {
            foreach ($rec in @($page.value)) { if ($rec) { $read++; [void] (Add-ActivityRecord -Aggregate $Aggregate -Record $rec -Since30 $Since30) } }
        }
        $link = if ($page) { $page.PSObject.Properties['@odata.nextLink'] } else { $null }
        $next = if ($link) { "$($link.Value)" } else { $null }
    }
    $read
}

function Invoke-ActivitySearch {
    <#
        Searches the range, splitting a window that passed the record cap, and reads every record into an
        aggregate. Returns the aggregate, the records read, the searches made and the notes for the run's detail.
    #>
    param([string] $Graph, [string] $BearerToken, [datetime] $From, [datetime] $To, [int] $MaxSearches, [int] $PollSeconds, [int] $TimeoutMinutes)
    $agg = New-ActivityAggregate
    $since30 = @($From, $To.AddDays(-30)) | Sort-Object | Select-Object -Last 1
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue([pscustomobject]@{ From = $From; To = $To })
    $searches = 0; $records = 0
    $notes = [System.Collections.Generic.List[string]]::new()
    $capUnknown = $false
    while ($queue.Count -gt 0) {
        $w = $queue.Dequeue()
        if ($searches -ge $MaxSearches) { throw "The range needed more than $MaxSearches audit searches (-MaxSearches): nothing was loaded. Search fewer days, or give -MaxSearches more." }
        $searches++
        $label = '{0:yyyy-MM-dd HH:mm} to {1:yyyy-MM-dd HH:mm} UTC' -f $w.From, $w.To
        $id = New-AuditSearch -Graph $Graph -BearerToken $BearerToken -From $w.From -To $w.To
        Write-Line "Searching $label (audit search $id)."
        $over = Wait-AuditSearch -Graph $Graph -BearerToken $BearerToken -Id $id -PollSeconds $PollSeconds -TimeoutMinutes $TimeoutMinutes
        if ($over -eq $true) {
            $halves = Split-ActivityWindow -From $w.From -To $w.To
            if ($halves.Count -eq 2) {
                Write-Line "$label passed the record cap: searching each half."
                foreach ($h in $halves) { $queue.Enqueue($h) }
                continue
            }
            $notes.Add("$label passed the record cap even as one hour; its first records were loaded")
        }
        if ($null -eq $over) { $capUnknown = $true }
        $n = Read-AuditSearch -Graph $Graph -BearerToken $BearerToken -Id $id -Aggregate $agg -Since30 $since30
        $records += $n
        Write-Line ("{0}: {1:N0} record(s)." -f $label, $n)
    }
    if ($capUnknown) { $notes.Add('the record cap could not be checked (no beta status)') }
    [pscustomobject]@{ Aggregate = $agg; Records = $records; Searches = $searches; Notes = $notes.ToArray(); Since30 = $since30 }
}

function Invoke-Main {
    $setting = @{}
    if (Test-Path -LiteralPath $SettingsPath) { $setting = Import-PowerShellDataFile -LiteralPath $SettingsPath }
    function Setting($k) { if ($setting.Contains($k) -and $setting[$k]) { $setting[$k] } }
    $script:MaxDropPercent = Resolve-MaxDropPercent -What 'Power Apps activity' -Given $MaxDropPercent -WasGiven $script:MaxDropGiven -Settings $setting
    if (-not $script:DaysGiven -and (Setting 'PowerAppsActivityDays')) {
        $d = 0
        if (-not [int]::TryParse("$(Setting 'PowerAppsActivityDays')", [ref] $d) -or $d -lt 1 -or $d -gt 365) { throw "PowerAppsActivityDays in the settings file is '$(Setting 'PowerAppsActivityDays')': it is a number of days from 1 to 365." }
        $script:Days = $d
    }
    if (-not $TenantId) { $script:TenantId = Setting 'TenantId' }
    if (-not $ClientId) { $script:ClientId = Setting 'ClientId' }
    if (-not $CertificateThumbprint) { $script:CertificateThumbprint = Setting 'CertificateThumbprint' }
    if (-not $ConnectionString) {
        if (-not $SqlServer) { $script:SqlServer = Setting 'SqlServer' }
        if (-not $script:DatabaseGiven -and (Setting 'Database')) { $script:Database = Setting 'Database' }
        if (Setting 'TrustServerCertificate') { $script:TrustServerCertificate = $true }
        if (-not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
    }

    $range = Get-ActivityRange -Days $Days
    $conn = New-UsageConnection -SqlServer $SqlServer -Database $Database -TrustServerCertificate:$TrustServerCertificate -ConnectionString $ConnectionString
    try {
        if (-not (Test-UsageSchema $conn)) { throw "The tables are not in $($conn.Database) yet: run Install-CopilotAudit.ps1 from this release first." }
        $runId = Start-UsageRun $conn -Source PowerApps -Scope "Power Apps ($script:RecordType), $Days day(s)" -FromDay $range.From -ToDay $range.To.AddDays(-1)
        $written = 0
        try {
            $bearer = if ($AccessToken) { [System.Net.NetworkCredential]::new('', $AccessToken).Password }
                      else {
                          if (-not ($TenantId -and $ClientId -and $CertificateThumbprint)) { throw 'Signing in needs -TenantId, -ClientId and -CertificateThumbprint (or the settings file''s).' }
                          Get-AppOnlyToken -Tenant $TenantId -Client $ClientId -Thumbprint $CertificateThumbprint -Login $LoginEndpoint -Resource $GraphEndpoint
                      }
            $found = Invoke-ActivitySearch -Graph $GraphEndpoint.TrimEnd('/') -BearerToken $bearer -From $range.From -To $range.To `
                         -MaxSearches $MaxSearches -PollSeconds $PollSeconds -TimeoutMinutes $TimeoutMinutes
            $rows = ConvertTo-ActivityRow $found.Aggregate
            $recent = @($rows | Where-Object { $_.ActiveDays30 -gt 0 }).Count
            $written = Write-UsageTable $conn 'audit.PowerPlatformUserActivity' (ConvertTo-UsageTable $rows ([ordered]@{
                    UserPrincipalName = [string]; Product = [string]; LastActiveUtc = [datetime]; ActiveDays30 = [int] })) `
                -ReplaceWhere 'Product = @p' -ReplaceParameters @{ '@p' = $script:Product } `
                -MaxDropPercent $script:MaxDropPercent -What 'Power Apps activity (audit.PowerPlatformUserActivity)' -Setting 'PowerAppsActivityMaxDropPercent' -AllowDrop:$AllowDrop
            $status = if ($found.Notes.Count -and ($found.Notes | Where-Object { $_ -like '*record cap even as one hour*' })) { 'Partial' } else { 'Succeeded' }
            $detail = ("{0:N0} people used Power Apps from {1:yyyy-MM-dd} to {2:yyyy-MM-dd}, {3:N0} of them since {4:yyyy-MM-dd}; {5:N0} record(s), {6} search(es)." -f
                       $written, $range.From, $range.To.AddDays(-1), $recent, $found.Since30, $found.Records, $found.Searches) +
                      $(if ($found.Notes.Count) { ' Note: ' + ($found.Notes -join '; ') + '.' } else { '' })
            Complete-UsageRun $conn -RunId $runId -Status $status -Rows $written -Detail $detail
            Write-Line "$status. $detail"
        }
        catch {
            if (Test-DropRefusal $_) {
                Complete-UsageRun $conn -RunId $runId -Status Partial -Rows 0 -Detail $_.Exception.Message
                Write-Line $_.Exception.Message
                $script:Refused = $true
                return
            }
            Complete-UsageRun $conn -RunId $runId -Status Failed -Rows $written -Detail $_.Exception.Message
            throw
        }
    }
    finally { $conn.Dispose() }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') {
    $script:Refused = $false
    Invoke-Main
    # Refused by the delete guard: the Power Apps rows kept the last good load, and the nightly run goes on.
    if ($script:Refused) { exit $script:DropRefusedExitCode }
}
