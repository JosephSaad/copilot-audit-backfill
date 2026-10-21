<#
.SYNOPSIS
    Checks that each headline figure that is a link opens a page showing the same number (2.6.7's rule).

.DESCRIPTION
    Reads every report page in the menu (or those given), finds each figure drawn as a link, opens its target, and
    looks for the figure's value there: as written, or as its digits. Run it against a dashboard on this machine signed
    in with Windows authentication as someone who sees every page (an administrator), at more than one period.

    Some figures lead on purpose to a related page that shows the detail behind them in another month or unit (a
    month's credits to the people who used them). They are listed in $RelatedLinks below and reported, not failed.

.EXAMPLE
    pwsh tools/dev/ui/Test-FigureLinks.ps1 -Url http://localhost:5391 -Days 30,90,365
#>
param(
    [string] $Url = 'https://localhost:7291',
    [string[]] $Pages,
    [string[]] $Days = @('30'),
    [string] $Out
)
Add-Type -AssemblyName System.Web
# pwsh -File passes -Days 30,90,365 as one string: split it.
$Pages = @($Pages | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Days = @($Days | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { [int] $_ })

# Page|label start -> target: a related page, by design (2.7.3 review).
$RelatedLinks = @(
    '/leadership/credit-chargeback|Copilot Credits',            # a month's credits -> who used credits, the last 30 days
    '/licensing/license-for-credits|Credits a month',           # money -> credits per person
    '/licensing/credit-use|Billed by the admin center',         # credits -> what they cost
    '/forecasting/credit-commitment|Drawn so far',              # money since the start -> each month's credits
    '/forecasting/budget|Seats to renew'                        # right size less a suite's -> the right size
)

# HttpClient with the Windows sign-in: a dashboard on http://localhost answers Negotiate, which Invoke-WebRequest only sends
# there with a switch the analyzer forbids. A certificate on https://localhost is the developer's own.
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseDefaultCredentials = $true
$handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMinutes(5)
function Get-Page([string] $u) {
    try { $r = $client.GetAsync($u).GetAwaiter().GetResult(); if ($r.IsSuccessStatusCode) { $r.Content.ReadAsStringAsync().GetAwaiter().GetResult() } else { $null } } catch { $null }
}
$base = $Url.TrimEnd('/')
if (-not $Pages) {
    $start = Get-Page "$base/"
    if (-not $start) { throw "The dashboard at $base did not answer." }
    $Pages = [regex]::Matches($start, 'href="(/[a-z][a-z0-9\-/]*)"') | ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -notmatch '^/(admin|about|health|api|css|js|lib|favicon)' } | Select-Object -Unique
}
$results = foreach ($d in $Days) {
    foreach ($p in $Pages) {
        $html = Get-Page "$base$p$(if ($p.Contains('?')) { '&' } else { '?' })d=$d"
        if (-not $html) { [pscustomobject]@{ Days = $d; Page = $p; Label = '(page failed)'; Value = ''; Target = ''; Result = 'FAIL' }; continue }
        foreach ($k in [regex]::Matches($html, '(?s)class="klabel">((?:(?!class="klabel").)*?)<a class="kvalue klink" href="([^"]+)"[^>]*>([^<]*)<')) {
            $plain = ([regex]::Replace([regex]::Replace($k.Groups[1].Value, '(?s)<(button|span class="tip)[^>]*>.*?</\1>', ' '), '<[^>]+>', ' ')).Trim() -replace '\s+', ' '
            $label = [System.Web.HttpUtility]::HtmlDecode($plain)
            $href = [System.Web.HttpUtility]::HtmlDecode($k.Groups[2].Value)
            $value = [System.Web.HttpUtility]::HtmlDecode($k.Groups[3].Value).Trim()
            $target = Get-Page "$base$href"
            $text = if ($target) { [System.Web.HttpUtility]::HtmlDecode([regex]::Replace($target, '<[^>]+>', ' ')) } else { '' }
            $digits = $value -replace '[^\d\.%]', ''
            $found = $target -and ($text.Contains($value) -or ($digits.Length -gt 0 -and ($text -replace '[,\s]', '').Contains($digits)))
            $isRelated = $RelatedLinks | Where-Object { $parts = $_ -split '\|'; ($p -split '\?')[0] -eq $parts[0] -and $label.StartsWith($parts[1]) }
            [pscustomobject]@{ Days = $d; Page = $p; Label = $(if ($label.Length -gt 40) { $label.Substring(0, 40) } else { $label }); Value = $value; Target = $href
                               Result = $(if ($found) { 'ok' } elseif ($isRelated) { 'related' } else { 'FAIL' }) }
        }
    }
}
$fail = @($results | Where-Object Result -eq 'FAIL')
"{0} linked figures checked ({1} pages, {2} period(s)): {3} ok, {4} related by design, {5} not found on their target." -f
    $results.Count, $Pages.Count, $Days.Count, @($results | Where-Object Result -eq 'ok').Count, @($results | Where-Object Result -eq 'related').Count, $fail.Count
if ($fail.Count) { $fail | Format-Table -AutoSize | Out-String -Width 300 }
if ($Out) { $results | Export-Csv $Out -NoTypeInformation }
exit $(if ($fail.Count) { 1 } else { 0 })
