<#
.SYNOPSIS
    Loads GitHub Copilot's seats, daily usage and cost for an enterprise (or an organization) into the Copilot
    database. First plumbing (2.6.2): the tables and the load; no report reads them yet.

.DESCRIPTION
    Reads GitHub's REST API with a token and writes four tables (sql\Create-CopilotExternalUsageSchema.sql,
    which Install-CopilotAudit.ps1 runs):

      1. Seats (audit.GitHubCopilotSeat): every seat as it stands, who holds it, the plan (business or
         enterprise), when it was assigned and when it was last used. Replaced whole.
      2. Daily usage (audit.GitHubCopilotDay): the enterprise-1-day (or organization-1-day) usage metrics
         report of each day asked for: active users, interactions, code generated and accepted, lines of code.
         The whole record is kept as JSON too, for the breakdowns that have no columns yet.
      3. Usage by person (audit.GitHubCopilotUserDay): the users-1-day report of each day: what each person did,
         and the AI credits they used.
      4. Cost (audit.GitHubCopilotCost): GitHub's billing usage for this month and the last, the lines whose
         product is Copilot: quantity, price and the net amount charged.

    A seat's cost is its plan's price a month (19 USD for Copilot Business, 39 USD for Copilot Enterprise at
    GitHub's list price, 2026): the seats table has the plan, and the cost table has what GitHub billed, the
    seats and the AI credits beyond the ones included.

    Each part is read on its own. A part the token may not read (billing needs an administrator) is left out
    and named in the run's detail, and the run ends Partial; the others are loaded. Days GitHub has no report
    for yet are skipped: its usage metrics can be three days behind. Loading the same days again replaces
    them, so the nightly run reads a trailing window (-Days, 10 by default).

    The run is recorded in audit.ExternalUsageRun, which Admin > Health shows, with the day the token expires.

    THE TOKEN. A GitHub token with read access to the enterprise's (or organization's) Copilot metrics, seats
    and billing: for an enterprise, a classic personal access token with manage_billing:copilot and
    read:enterprise held by an enterprise owner or billing manager, or a GitHub App installation token with
    the matching permissions. It is read, in this order, from -Token, from the environment variable
    GITHUB_COPILOT_TOKEN, or from the file -TokenPath names. Save it to that file once with -SaveToken: it is
    encrypted for this computer (Windows data protection, machine scope), so the nightly task's account can
    read it here and the file is useless anywhere else. Keep the file readable by administrators and the
    task's account only. The token is sent to GitHub's API and to nothing else: a report's file is fetched
    from the address GitHub returns, without it.

    With no -Enterprise or -Organization, everything comes from copilot-audit.settings.psd1: GitHubEnterprise
    or GitHubOrganization, GitHubTokenPath, and SqlServer, Database and TrustServerCertificate.

.PARAMETER Enterprise
    The enterprise's slug, as in github.com/enterprises/<slug>. For GitHub Copilot Enterprise bought at the
    enterprise level, this is the one to give.

.PARAMETER Organization
    An organization's name, for Copilot bought by one organization. Give one of the two.

.PARAMETER Days
    How many complete UTC days to read, ending yesterday. Default 10. GitHub keeps up to a year.

.PARAMETER SaveToken
    Ask for the token and save it, encrypted for this computer, to -TokenPath. Nothing is loaded.

.PARAMETER MaxDropPercent
    The delete guard (2.6.5). The seats are replaced whole for the enterprise or organization each run. When
    GitHub lists more than this percentage fewer seats than the table holds (GitHubSeatMaxDropPercent in the
    settings file, 15 by default), the seats are not replaced and the run ends Partial, saying why; usage and
    cost load as usual. 0 turns the guard off.

.PARAMETER AllowDrop
    Replace the seats even when the guard would refuse, once: after seats were really taken back.

.EXAMPLE
    .\tools\Import-GitHubCopilotUsage.ps1 -SaveToken
.EXAMPLE
    .\tools\Import-GitHubCopilotUsage.ps1 -Enterprise contoso -SqlServer SQLPROD01 -Days 90
.EXAMPLE
    .\tools\Import-GitHubCopilotUsage.ps1          # as the nightly run does, from the settings file
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $Enterprise,
    [string] $Organization,
    [ValidateRange(1, 366)][int] $Days = 10,
    [string] $SqlServer,
    [string] $Database = 'CopilotAudit',
    [switch] $TrustServerCertificate,
    [string] $ConnectionString,
    [securestring] $Token,
    [string] $TokenPath,
    [switch] $SaveToken,
    [string] $SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'copilot-audit.settings.psd1'),
    # GitHub Enterprise Cloud with data residency has its own API host (https://api.<tenant>.ghe.com); the tests point this at a mock.
    [string] $ApiBase = 'https://api.github.com',
    [string] $ApiVersion = '2026-03-10',
    [ValidateRange(0, 100)][double] $MaxDropPercent = 15,
    [switch] $AllowDrop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ExternalUsage.Common.ps1')
$script:MaxDropGiven = $PSBoundParameters.ContainsKey('MaxDropPercent')   # read here: in Invoke-Main, $PSBoundParameters is its own

function Write-Line([string] $Text) { Write-Information ("{0:u} {1}" -f [datetime]::UtcNow, $Text) -InformationAction Continue }

function Protect-GitHubToken {
    <# The token as bytes only this computer can read back (Windows data protection, machine scope). #>
    param([securestring] $Secret)
    Add-Type -AssemblyName System.Security
    $plain = [System.Net.NetworkCredential]::new('', $Secret).Password
    [System.Security.Cryptography.ProtectedData]::Protect([System.Text.Encoding]::UTF8.GetBytes($plain), $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
}

function Unprotect-GitHubToken {
    param([byte[]] $Bytes)
    Add-Type -AssemblyName System.Security
    [System.Text.Encoding]::UTF8.GetString([System.Security.Cryptography.ProtectedData]::Unprotect($Bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
}

function Get-GitHubScope {
    <# What is loaded and where its endpoints are: an enterprise, or an organization. Pure, for the tests. #>
    param([string] $Enterprise, [string] $Organization)
    if ($Enterprise -and $Organization) { throw 'Give -Enterprise or -Organization, not both: each is loaded on its own.' }
    if ($Enterprise) {
        $e = [uri]::EscapeDataString($Enterprise)
        return [pscustomobject]@{ Name = "enterprise:$Enterprise"; Metrics = "/enterprises/$e/copilot/metrics/reports"; Report = 'enterprise-1-day'
                                  Seats = "/enterprises/$e/copilot/billing/seats"; Billing = "/enterprises/$e/settings/billing/usage" }
    }
    if ($Organization) {
        $o = [uri]::EscapeDataString($Organization)
        return [pscustomobject]@{ Name = "org:$Organization"; Metrics = "/orgs/$o/copilot/metrics/reports"; Report = 'organization-1-day'
                                  Seats = "/orgs/$o/copilot/billing/seats"; Billing = "/organizations/$o/settings/billing/usage" }
    }
    throw 'Give -Enterprise or -Organization, or set GitHubEnterprise or GitHubOrganization in the settings file.'
}

function ConvertFrom-GitHubReportFile {
    <#
        The records of a usage metrics report file: one JSON object a line (NDJSON), or one JSON array, or one
        object. Blank lines are skipped. Pure, for the tests.
    #>
    param([AllowNull()][AllowEmptyString()][string] $Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return , @() }
    $t = $Text.Trim().TrimStart([char] 0xFEFF)
    if ($t.StartsWith('[')) { return , @($t | ConvertFrom-Json -Depth 64) }
    $lines = @($t -split "`r?`n" | Where-Object { $_.Trim() })
    if ($lines.Count -gt 1 -or -not $t.Contains("`n")) {
        try { return , @($lines | ForEach-Object { $_ | ConvertFrom-Json -Depth 64 }) }
        catch { Write-Verbose "Not one object a line; read as a single document: $($_.Exception.Message)" }
    }
    , @($t | ConvertFrom-Json -Depth 64)
}

function Get-Field {
    <# A property of a record, or $null where it has none: the reports gain and lose fields. #>
    param($Record, [string] $Name)
    if ($null -eq $Record) { return $null }
    $p = $Record.PSObject.Properties[$Name]
    if ($p) { $p.Value } else { $null }
}

function ConvertTo-UtcTime {
    <# A time as GitHub writes it (ISO 8601, or "2026-12-31 00:00:00 UTC"), in UTC; $null where there is none or it cannot be read. #>
    param($Value)
    if ($null -eq $Value -or "$Value" -eq '') { return $null }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime() }
    $s = "$Value".Trim() -replace ' UTC$', 'Z'
    $d = [datetimeoffset]::MinValue
    if ([datetimeoffset]::TryParse($s, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref] $d)) { $d.UtcDateTime } else { $null }
}

function ConvertTo-GitHubSeatRow {
    <# A seat as GitHub lists it, as a row of audit.GitHubCopilotSeat. Pure, for the tests. #>
    param($Seat, [string] $Scope)
    $who = Get-Field $Seat 'assignee'
    $team = Get-Field $Seat 'assigning_team'
    $org = Get-Field $Seat 'organization'
    [pscustomobject]@{
        Scope = $Scope; Login = [string] (Get-Field $who 'login'); UserId = Get-Field $who 'id'
        Organization = $(if ($org) { [string] (Get-Field $org 'login') })
        AssigningTeam = $(if ($team) { [string] ((Get-Field $team 'slug'), (Get-Field $team 'name') | Where-Object { $_ } | Select-Object -First 1) })
        PlanType = [string] (Get-Field $Seat 'plan_type')
        CreatedUtc = ConvertTo-UtcTime (Get-Field $Seat 'created_at'); LastActivityUtc = ConvertTo-UtcTime (Get-Field $Seat 'last_activity_at')
        LastActivityEditor = [string] (Get-Field $Seat 'last_activity_editor'); LastAuthenticatedUtc = ConvertTo-UtcTime (Get-Field $Seat 'last_authenticated_at')
        PendingCancellationDate = $(if (Get-Field $Seat 'pending_cancellation_date') { ([datetime] (Get-Field $Seat 'pending_cancellation_date')).Date })
    }
}

function ConvertTo-GitHubDayRow {
    <# A day's record of the enterprise or organization report, as a row of audit.GitHubCopilotDay. Pure, for the tests. #>
    param($Record, [string] $Scope, [datetime] $Day)
    $d = Get-Field $Record 'day'
    [pscustomobject]@{
        Scope = $Scope; Day = $(if ($d) { ([datetime] $d).Date } else { $Day.Date })
        DailyActiveUsers = Get-Field $Record 'daily_active_users'; WeeklyActiveUsers = Get-Field $Record 'weekly_active_users'
        MonthlyActiveUsers = Get-Field $Record 'monthly_active_users'; MonthlyActiveChatUsers = Get-Field $Record 'monthly_active_chat_users'
        MonthlyActiveAgentUsers = Get-Field $Record 'monthly_active_agent_users'
        Interactions = Get-Field $Record 'user_initiated_interaction_count'; CodeGenerations = Get-Field $Record 'code_generation_activity_count'
        CodeAcceptances = Get-Field $Record 'code_acceptance_activity_count'
        LocSuggestedToAdd = Get-Field $Record 'loc_suggested_to_add_sum'; LocSuggestedToDelete = Get-Field $Record 'loc_suggested_to_delete_sum'
        LocAdded = Get-Field $Record 'loc_added_sum'; LocDeleted = Get-Field $Record 'loc_deleted_sum'
        Json = ($Record | ConvertTo-Json -Depth 64 -Compress)
    }
}

function ConvertTo-GitHubUserDayRow {
    <# A person's day in the users report, as a row of audit.GitHubCopilotUserDay; $null for a record with no user id. Pure, for the tests. #>
    param($Record, [string] $Scope, [datetime] $Day)
    $id = Get-Field $Record 'user_id'
    if ($null -eq $id) { return $null }
    $d = Get-Field $Record 'day'
    [pscustomobject]@{
        Scope = $Scope; Day = $(if ($d) { ([datetime] $d).Date } else { $Day.Date }); UserId = [long] $id; Login = [string] (Get-Field $Record 'user_login')
        Interactions = Get-Field $Record 'user_initiated_interaction_count'; CodeGenerations = Get-Field $Record 'code_generation_activity_count'
        CodeAcceptances = Get-Field $Record 'code_acceptance_activity_count'; LocAdded = Get-Field $Record 'loc_added_sum'; LocDeleted = Get-Field $Record 'loc_deleted_sum'
        AiCreditsUsed = Get-Field $Record 'ai_credits_used'
        UsedChat = Get-Field $Record 'used_chat'; UsedAgent = Get-Field $Record 'used_agent'; UsedCli = Get-Field $Record 'used_cli'
    }
}

function ConvertTo-GitHubCostRow {
    <#
        A line of the billing usage, as a row of audit.GitHubCopilotCost; $null for a line of another product
        (Actions, Packages ...) or one with no date. Pure, for the tests.
    #>
    param($Item, [string] $Scope)
    $product = [string] (Get-Field $Item 'product'); $sku = [string] (Get-Field $Item 'sku')
    if ($product -notmatch 'copilot' -and $sku -notmatch 'copilot') { return $null }
    $date = ConvertTo-UtcTime (Get-Field $Item 'date')
    if ($null -eq $date) { return $null }
    $quantity = Get-Field $Item 'quantity'; if ($null -eq $quantity) { $quantity = Get-Field $Item 'grossQuantity' }
    [pscustomobject]@{
        Scope = $Scope; UsageDate = $date.Date; Product = $product; Sku = $sku; Model = [string] (Get-Field $Item 'model')
        Organization = [string] (Get-Field $Item 'organizationName'); UnitType = [string] (Get-Field $Item 'unitType')
        PricePerUnit = Get-Field $Item 'pricePerUnit'; Quantity = $quantity
        GrossAmount = Get-Field $Item 'grossAmount'; DiscountAmount = Get-Field $Item 'discountAmount'; NetAmount = Get-Field $Item 'netAmount'
    }
}

function Invoke-GitHubApi {
    <#
        One GET of GitHub's API, with the token. Returns the status, the body as an object and the response's
        headers. 204 (nothing to give) and 404 come back as a status, not an error; anything else that is not
        a success throws with GitHub's own message. A rate limit is waited out once.
    #>
    param([string] $Uri, [string] $BearerToken, [string] $Version)
    $headers = @{ Accept = 'application/vnd.github+json'; Authorization = "Bearer $BearerToken"; 'X-GitHub-Api-Version' = $Version; 'User-Agent' = 'copilot-audit-backfill' }
    for ($try = 1; ; $try++) {
        $r = Invoke-WebRequest -Uri $Uri -Headers $headers -SkipHttpErrorCheck -TimeoutSec 120
        $status = [int] $r.StatusCode
        if ($status -in 403, 429 -and $try -eq 1) {
            $wait = 0
            if ($r.Headers.ContainsKey('Retry-After')) { $wait = [int] "$(@($r.Headers['Retry-After'])[0])" }
            elseif ($r.Headers.ContainsKey('X-RateLimit-Remaining') -and "$(@($r.Headers['X-RateLimit-Remaining'])[0])" -eq '0') { $wait = 60 }
            if ($wait -gt 0 -and $wait -le 300) { Write-Line "GitHub asked to wait $wait s (rate limit)."; Start-Sleep -Seconds $wait; continue }
        }
        $body = $null
        if ($status -ne 204 -and $r.Content) { try { $body = $r.Content | ConvertFrom-Json -Depth 64 } catch { $body = $null } }
        if ($status -ge 400 -and $status -ne 404) {
            $message = if ($body -and (Get-Field $body 'message')) { Get-Field $body 'message' } else { "HTTP $status" }
            throw "GitHub answered $status for $(([uri] $Uri).AbsolutePath): $message"
        }
        return [pscustomobject]@{ Status = $status; Body = $body; Headers = $r.Headers }
    }
}

function Get-GitHubReport {
    <#
        The records of one usage metrics report for one day: asks for the report, then fetches each file it
        links to. The token goes to the API only, never to a file's address. Nothing for a day GitHub has no
        report for (204 or 404).
    #>
    param([string] $Base, [string] $Path, [datetime] $Day, [string] $BearerToken, [string] $Version)
    $r = Invoke-GitHubApi -Uri ("{0}{1}?day={2:yyyy-MM-dd}" -f $Base, $Path, $Day) -BearerToken $BearerToken -Version $Version
    if ($r.Status -in 204, 404 -or $null -eq $r.Body) { return , @() }
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($link in @(Get-Field $r.Body 'download_links')) {
        if (-not $link) { continue }
        $file = Invoke-WebRequest -Uri $link -Headers @{ 'User-Agent' = 'copilot-audit-backfill' } -TimeoutSec 300
        $text = if ($file.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($file.Content) } else { [string] $file.Content }
        foreach ($rec in (ConvertFrom-GitHubReportFile $text)) { $records.Add($rec) }
    }
    , $records.ToArray()
}

function Get-GitHubSeat {
    <# Every seat, a page of 100 at a time, until GitHub has given as many as it says there are. #>
    param([string] $Base, [string] $Path, [string] $BearerToken, [string] $Version)
    $seats = [System.Collections.Generic.List[object]]::new()
    $headers = $null
    for ($page = 1; $page -le 5000; $page++) {
        $r = Invoke-GitHubApi -Uri ("{0}{1}?per_page=100&page={2}" -f $Base, $Path, $page) -BearerToken $BearerToken -Version $Version
        if ($r.Status -eq 404) { throw "GitHub has no Copilot seats at ${Path}: check the enterprise or organization name, and that the token may read it." }
        $headers = $r.Headers
        $got = @(Get-Field $r.Body 'seats')
        foreach ($s in $got) { if ($s) { $seats.Add($s) } }
        $total = Get-Field $r.Body 'total_seats'
        if ($got.Count -eq 0 -or ($null -ne $total -and $seats.Count -ge [int] $total)) { break }
    }
    [pscustomobject]@{ Seats = $seats.ToArray(); Headers = $headers }
}

function Invoke-Main {
    # What the command line does not give comes from the settings file.
    $setting = @{}
    if (Test-Path -LiteralPath $SettingsPath) { $setting = Import-PowerShellDataFile -LiteralPath $SettingsPath }
    function Setting($k) { if ($setting.Contains($k) -and $setting[$k]) { $setting[$k] } }
    $script:MaxDropPercent = Resolve-MaxDropPercent -What 'GitHub Copilot seats' -Given $MaxDropPercent -WasGiven $script:MaxDropGiven -Settings $setting
    if (-not $TokenPath) { $script:TokenPath = if (Setting 'GitHubTokenPath') { Setting 'GitHubTokenPath' } else { Join-Path (Split-Path $SettingsPath -Parent) 'github-copilot.token' } }

    if ($SaveToken) {
        $secret = if ($Token) { $Token } else { Read-Host -AsSecureString -Prompt 'GitHub token (it is not shown)' }
        [System.IO.File]::WriteAllBytes($TokenPath, (Protect-GitHubToken $secret))
        Write-Line "Saved to $TokenPath, encrypted for this computer. Let only administrators and the nightly task's account read the file."
        return
    }

    if (-not $Enterprise -and -not $Organization) { $script:Enterprise = Setting 'GitHubEnterprise'; $script:Organization = Setting 'GitHubOrganization' }
    $scope = Get-GitHubScope -Enterprise $Enterprise -Organization $Organization
    if (-not $PSBoundParameters.ContainsKey('ApiBase') -and (Setting 'GitHubApiBase')) { $script:ApiBase = [string] (Setting 'GitHubApiBase') }
    $script:ApiBase = $ApiBase.TrimEnd('/')
    if (-not $ConnectionString) {
        if (-not $SqlServer) { $script:SqlServer = Setting 'SqlServer' }
        if (-not $PSBoundParameters.ContainsKey('Database') -and (Setting 'Database')) { $script:Database = Setting 'Database' }
        if (Setting 'TrustServerCertificate') { $script:TrustServerCertificate = $true }
        if (-not $SqlServer) { throw 'Give -SqlServer, or SqlServer in the settings file.' }
    }

    $bearer = if ($Token) { [System.Net.NetworkCredential]::new('', $Token).Password }
              elseif ($env:GITHUB_COPILOT_TOKEN) { $env:GITHUB_COPILOT_TOKEN }
              elseif (Test-Path -LiteralPath $TokenPath) { Unprotect-GitHubToken ([System.IO.File]::ReadAllBytes($TokenPath)) }
              else { throw "No GitHub token: run this once with -SaveToken (it writes $TokenPath), or set GITHUB_COPILOT_TOKEN." }

    $conn = New-UsageConnection -SqlServer $SqlServer -Database $Database -TrustServerCertificate:$TrustServerCertificate -ConnectionString $ConnectionString
    try {
        if (-not (Test-UsageSchema $conn)) { throw "The tables are not in $($conn.Database) yet: run Install-CopilotAudit.ps1 from this release first." }
        $days = Get-UsageDay -Days $Days
        $runId = Start-UsageRun $conn -Source GitHub -Scope $scope.Name -FromDay $days[0] -ToDay $days[-1]
        $written = 0; $done = [System.Collections.Generic.List[string]]::new(); $skipped = [System.Collections.Generic.List[string]]::new(); $expires = $null
        try {
            # 1. Seats, whole.
            try {
                $s = Get-GitHubSeat -Base $ApiBase -Path $scope.Seats -BearerToken $bearer -Version $ApiVersion
                if ($s.Headers -and $s.Headers.ContainsKey('github-authentication-token-expiration')) { $expires = ConvertTo-UtcTime "$(@($s.Headers['github-authentication-token-expiration'])[0])" }
                $rows = @($s.Seats | ForEach-Object { ConvertTo-GitHubSeatRow $_ $scope.Name } | Where-Object { $_.Login } | Sort-Object Login -Unique)
                $n = Write-UsageTable $conn 'audit.GitHubCopilotSeat' (ConvertTo-UsageTable $rows ([ordered]@{
                        Scope = [string]; Login = [string]; UserId = [long]; Organization = [string]; AssigningTeam = [string]; PlanType = [string]; CreatedUtc = [datetime]
                        LastActivityUtc = [datetime]; LastActivityEditor = [string]; LastAuthenticatedUtc = [datetime]; PendingCancellationDate = [datetime] })) `
                    -ReplaceWhere 'Scope = @scope' -ReplaceParameters @{ '@scope' = $scope.Name } `
                    -MaxDropPercent $MaxDropPercent -What "GitHub Copilot seats of $($scope.Name)" -Setting 'GitHubSeatMaxDropPercent' -AllowDrop:$AllowDrop
                $written += $n; $done.Add("$n seat(s)"); Write-Line "Seats: $n."
            }
            catch { $skipped.Add("seats ($($_.Exception.Message))"); Write-Warning "Seats were not loaded: $($_.Exception.Message)" }

            # 2 and 3. The day's report, and each person's day.
            try {
                $dayRows = [System.Collections.Generic.List[object]]::new(); $userRows = [System.Collections.Generic.List[object]]::new(); $none = 0
                foreach ($day in $days) {
                    $records = Get-GitHubReport -Base $ApiBase -Path "$($scope.Metrics)/$($scope.Report)" -Day $day -BearerToken $bearer -Version $ApiVersion
                    if ($records.Count -eq 0) { $none++; continue }
                    foreach ($rec in $records) { $dayRows.Add((ConvertTo-GitHubDayRow $rec $scope.Name $day)) }
                    foreach ($rec in (Get-GitHubReport -Base $ApiBase -Path "$($scope.Metrics)/users-1-day" -Day $day -BearerToken $bearer -Version $ApiVersion)) {
                        $u = ConvertTo-GitHubUserDayRow $rec $scope.Name $day
                        if ($u) { $userRows.Add($u) }
                    }
                }
                # One row a day and one a person a day: a report in several files, or a person listed twice, is its last record.
                $dayTable = ConvertTo-UsageTable @($dayRows | Group-Object { $_.Day } | ForEach-Object { $_.Group[-1] }) ([ordered]@{
                        Scope = [string]; Day = [datetime]; DailyActiveUsers = [int]; WeeklyActiveUsers = [int]; MonthlyActiveUsers = [int]; MonthlyActiveChatUsers = [int]
                        MonthlyActiveAgentUsers = [int]; Interactions = [long]; CodeGenerations = [long]; CodeAcceptances = [long]; LocSuggestedToAdd = [long]
                        LocSuggestedToDelete = [long]; LocAdded = [long]; LocDeleted = [long]; Json = [string] })
                $userTable = ConvertTo-UsageTable @($userRows | Group-Object { "$($_.Day)|$($_.UserId)" } | ForEach-Object { $_.Group[-1] }) ([ordered]@{
                        Scope = [string]; Day = [datetime]; UserId = [long]; Login = [string]; Interactions = [long]; CodeGenerations = [long]; CodeAcceptances = [long]
                        LocAdded = [long]; LocDeleted = [long]; AiCreditsUsed = [decimal]; UsedChat = [bool]; UsedAgent = [bool]; UsedCli = [bool] })
                $n1 = Write-UsageTable $conn 'audit.GitHubCopilotDay' $dayTable -Key 'Scope', 'Day'
                $n2 = Write-UsageTable $conn 'audit.GitHubCopilotUserDay' $userTable -Key 'Scope', 'Day', 'UserId'
                $written += $n1 + $n2; $done.Add("$n1 day(s) of usage, $n2 person-day(s)")
                Write-Line "Usage: $n1 day(s), $n2 person-day(s); $none of $($days.Count) day(s) had no report yet."
            }
            catch { $skipped.Add("usage ($($_.Exception.Message))"); Write-Warning "Usage was not loaded: $($_.Exception.Message)" }

            # 4. Cost: this month and the last, each replaced whole.
            try {
                $today = [datetime]::UtcNow.Date; $n = 0
                foreach ($month in @($today.AddMonths(-1), $today)) {
                    $r = Invoke-GitHubApi -Uri ("{0}{1}?year={2}&month={3}" -f $ApiBase, $scope.Billing, $month.Year, $month.Month) -BearerToken $bearer -Version $ApiVersion
                    if ($r.Status -eq 404) { throw 'GitHub has no billing usage for it: the token needs an administrator of the enterprise or organization, on the enhanced billing platform.' }
                    $rows = @(@(Get-Field $r.Body 'usageItems') | ForEach-Object { if ($_) { ConvertTo-GitHubCostRow $_ $scope.Name } } | Where-Object { $_ })
                    $first = [datetime]::new($month.Year, $month.Month, 1)
                    $n += Write-UsageTable $conn 'audit.GitHubCopilotCost' (ConvertTo-UsageTable $rows ([ordered]@{
                            Scope = [string]; UsageDate = [datetime]; Product = [string]; Sku = [string]; Model = [string]; Organization = [string]; UnitType = [string]
                            PricePerUnit = [decimal]; Quantity = [decimal]; GrossAmount = [decimal]; DiscountAmount = [decimal]; NetAmount = [decimal] })) `
                        -ReplaceWhere 'Scope = @scope AND UsageDate >= @from AND UsageDate < @to' -ReplaceParameters @{ '@scope' = $scope.Name; '@from' = $first; '@to' = $first.AddMonths(1) }
                }
                $written += $n; $done.Add("$n cost line(s)"); Write-Line "Cost: $n line(s) for this month and the last."
            }
            catch { $skipped.Add("cost ($($_.Exception.Message))"); Write-Warning "Cost was not loaded: $($_.Exception.Message)" }

            $status = if ($skipped.Count -eq 0) { 'Succeeded' } elseif ($done.Count -gt 0) { 'Partial' } else { 'Failed' }
            $detail = (@($(if ($done.Count) { 'Loaded ' + ($done -join ', ') + '.' }), $(if ($skipped.Count) { 'Not loaded: ' + ($skipped -join '; ') + '.' })) | Where-Object { $_ }) -join ' '
            Complete-UsageRun $conn -RunId $runId -Status $status -Rows $written -Detail $detail -ExpiresUtc $expires
            Write-Line "$status. $detail"
            if ($expires) { Write-Line ("The token expires {0:yyyy-MM-dd}." -f $expires) }
            if ($status -eq 'Failed') { exit 1 }
        }
        catch {
            Complete-UsageRun $conn -RunId $runId -Status Failed -Rows $written -Detail $_.Exception.Message -ExpiresUtc $expires
            throw
        }
    }
    finally { $conn.Dispose() }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
