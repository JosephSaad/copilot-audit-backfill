<#
.SYNOPSIS
    Dot-source: Get-EdgeToken signs in through Microsoft Edge on this Windows PC (whatever the default
    browser is) with the authorization code flow and a localhost redirect, using the MSAL library that
    a loaded Microsoft module already brought in. No device code, no Windows account broker window.
#>
Set-StrictMode -Version Latest

$script:EdgePcas = @{}

function Import-MsalFromModule {
    # Load the MSAL copy of whichever Microsoft module this process imported (only one per process).
    $candidates = @()
    $exo = Get-Module ExchangeOnlineManagement
    if ($exo) { $candidates += Join-Path $exo.ModuleBase 'netCore' }
    $mg = Get-Module Microsoft.Graph.Authentication
    if (-not $mg -and -not $exo) { $mg = Get-Module -ListAvailable Microsoft.Graph.Authentication | Sort-Object Version -Descending | Select-Object -First 1 }
    if ($mg) { $candidates += Join-Path $mg.ModuleBase 'Dependencies\Core' }
    foreach ($dir in $candidates) {
        $msal = Join-Path $dir 'Microsoft.Identity.Client.dll'
        if (-not (Test-Path $msal)) { continue }
        foreach ($dep in 'Microsoft.IdentityModel.Abstractions.dll') {
            $p = Join-Path $dir $dep
            if (-not (Test-Path $p)) { $p = Join-Path (Split-Path $dir) $dep }
            if (Test-Path $p) { try { [void][Reflection.Assembly]::LoadFrom($p) } catch { } }
        }
        [void][Reflection.Assembly]::LoadFrom($msal)
        return
    }
    throw 'MSAL not found in the Microsoft.Graph.Authentication or ExchangeOnlineManagement module.'
}

function Get-EdgeToken {
    param(
        [Parameter(Mandatory)] [string]   $ClientId,
        [Parameter(Mandatory)] [string]   $Tenant,
        [Parameter(Mandatory)] [string[]] $Scopes,
        [string] $LoginHint = ''
    )
    if (-not ('Microsoft.Identity.Client.PublicClientApplicationBuilder' -as [type])) { Import-MsalFromModule }
    $key = "$ClientId|$Tenant"
    if (-not $script:EdgePcas.ContainsKey($key)) {
        $script:EdgePcas[$key] = [Microsoft.Identity.Client.PublicClientApplicationBuilder]::Create($ClientId).
            WithAuthority("https://login.microsoftonline.com/$Tenant").
            WithRedirectUri('http://localhost').
            Build()
    }
    $pca = $script:EdgePcas[$key]
    $scopeList = [System.Collections.Generic.List[string]]::new()
    foreach ($s in $Scopes) { $scopeList.Add($s) }

    $accounts = @($pca.GetAccountsAsync().GetAwaiter().GetResult())
    if ($accounts.Count -gt 0) {
        try { return $pca.AcquireTokenSilent($scopeList, $accounts[0]).ExecuteAsync().GetAwaiter().GetResult() }
        catch [Microsoft.Identity.Client.MsalUiRequiredException] { }
        catch { if ($_.Exception.InnerException -isnot [Microsoft.Identity.Client.MsalUiRequiredException]) { throw } }
    }

    $opts = [Microsoft.Identity.Client.SystemWebViewOptions]::new()
    $edge = [Microsoft.Identity.Client.SystemWebViewOptions].GetMethod('OpenWithEdgeBrowserAsync', [type[]]@([uri]))
    $opts.OpenBrowserAsync = $edge.CreateDelegate([System.Func[uri, System.Threading.Tasks.Task]])
    $opts.HtmlMessageSuccess = 'Signed in. You can close this tab and go back to the setup script.'

    $builder = $pca.AcquireTokenInteractive($scopeList).WithSystemWebViewOptions($opts).WithUseEmbeddedWebView($false)
    if ($LoginHint) { $builder = $builder.WithLoginHint($LoginHint) }
    Write-Host '   --  Microsoft Edge opens for sign-in.' -ForegroundColor Yellow
    return $builder.ExecuteAsync().GetAwaiter().GetResult()
}

function Open-InEdge([string] $url) {
    # Microsoft Edge on this Windows PC, whatever the default browser is.
    Start-Process 'msedge.exe' -ArgumentList @('--new-window', $url)
}
