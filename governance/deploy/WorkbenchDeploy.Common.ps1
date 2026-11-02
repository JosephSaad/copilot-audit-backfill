<#
.SYNOPSIS
    Shared helpers for the Copilot Governance Workbench deployment scripts. Dot-source it; it changes nothing
    by itself. Functions that change the machine honor -WhatIf through $WhatIfPreference.
#>
Set-StrictMode -Version Latest

# Routes of the web application. An IIS application path named like one of them cannot be told apart from a
# page address, so the installer refuses it.
$script:WorkbenchRoutes = @(
    'start', 'mywork', 'requests', 'queues', 'registry', 'operations', 'recertification', 'exceptions', 'retirement',
    'reports', 'admin', 'teams', 'health', 'signin-oidc', 'signout-oidc', 'microsoftidentity', 'css', 'js', 'lib', 'img'
)

function Write-DeployStep([string] $Text) { Write-Information "== $Text" -InformationAction Continue }
function Write-DeployOk([string] $Text) { Write-Information "   ok  $Text" -InformationAction Continue }
function Write-DeployNote([string] $Text) { Write-Information "   --  $Text" -InformationAction Continue }

function ConvertTo-CheckResult {
    param([string] $Name, [ValidateSet('PASS', 'WARN', 'FAIL', 'SKIP')] [string] $Status, [string] $Detail)
    [pscustomobject]@{ Check = $Name; Status = $Status; Detail = $Detail }
}

function Test-IsAdministrator {
    if (-not $IsWindows) { return $false }
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-GmsaName([string] $Account) {
    # DOMAIN\name$ : a group managed service account always ends with $.
    return $Account -match '^[A-Za-z0-9][A-Za-z0-9.\-]{0,62}\\[A-Za-z0-9][A-Za-z0-9.\-_]{0,62}\$$'
}

function Get-WorkbenchCertificate {
    <#
    .SYNOPSIS Newest valid certificate with a private key and this exact subject, as Microsoft.Identity.Web selects it.
    #>
    param(
        [Parameter(Mandatory)] [string] $Subject,
        [ValidateSet('LocalMachine', 'CurrentUser')] [string] $StoreLocation = 'LocalMachine'
    )
    $now = Get-Date
    Get-ChildItem "Cert:\$StoreLocation\My" |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Get-CertificateKeyFile {
    <#
    .SYNOPSIS Path of the private key file behind a certificate (CNG or legacy CSP), or $null when it cannot be found.
    #>
    param([Parameter(Mandatory)] [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
          [ValidateSet('LocalMachine', 'CurrentUser')] [string] $StoreLocation = 'LocalMachine')
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) { return $null }
    try {
        # .NET wraps legacy CSP keys in RSACng too, so look in both the CNG and the CSP key folders.
        $name = if ($rsa -is [System.Security.Cryptography.RSACng]) { $rsa.Key.UniqueName } else { $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
        $roots = if ($StoreLocation -eq 'LocalMachine') {
            @((Join-Path $env:ProgramData 'Microsoft\Crypto\Keys'), (Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys'))
        }
        else {
            @(Join-Path $env:APPDATA 'Microsoft\Crypto\Keys') + @(Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Crypto\RSA') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
        }
        foreach ($root in $roots) {
            $candidate = Join-Path $root $name
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
        return $null
    }
    finally { $rsa.Dispose() }
}

function Test-AccountHasRead {
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Account)
    $acl = Get-Acl -LiteralPath $Path
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if (-not [string]::Equals($rule.IdentityReference.Value, $Account, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $rights = $rule.FileSystemRights
        if (($rights -band [System.Security.AccessControl.FileSystemRights]::Read) -eq [System.Security.AccessControl.FileSystemRights]::Read) { return $true }
    }
    return $false
}

function Grant-KeyFileRead {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $KeyFile, [Parameter(Mandatory)] [string] $Account)
    if (Test-AccountHasRead -Path $KeyFile -Account $Account) {
        Write-DeployOk "$Account already reads $(Split-Path $KeyFile -Leaf)"
        return
    }
    if ($PSCmdlet.ShouldProcess($KeyFile, "Grant Read on the private key to $Account")) {
        $acl = Get-Acl -LiteralPath $KeyFile
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($Account, 'Read', 'Allow')
        $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $KeyFile -AclObject $acl
        Write-DeployOk "granted $Account Read on the private key"
    }
}

function Assert-SafeMirrorTarget {
    <#
    .SYNOPSIS
        Refuses a robocopy /MIR target that is not this application's folder. /MIR deletes everything in the
        target that is not in the source, so it may only point at an empty folder, a new one, or a folder this
        kit installed before (marker file .cgw-install with the same application kind).
    #>
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Target,
        [Parameter(Mandatory)] [ValidateSet('Web', 'Worker')] [string] $Kind
    )
    if (-not [IO.Path]::IsPathRooted($Target)) { throw "Install path '$Target' must be an absolute path." }
    $full = [IO.Path]::GetFullPath($Target).TrimEnd('\')
    $src = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full -eq $root) { throw "Install path '$Target' is a drive root. Choose a folder of its own, such as D:\CopilotGovernance\$Kind." }
    if (($full.Substring($root.Length).Trim('\').Split('\')).Count -lt 2) {
        throw "Install path '$Target' is too close to the drive root. Use a folder two levels down, such as D:\CopilotGovernance\$Kind."
    }
    foreach ($protected in @($env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, 'C:\Users', 'C:\inetpub', 'C:\inetpub\wwwroot')) {
        if ($protected -and [string]::Equals($full, $protected.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Install path '$Target' is a system folder. Use a subfolder of its own."
        }
    }
    if ($full.StartsWith($src + '\', [StringComparison]::OrdinalIgnoreCase) -or $src.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase) -or $full -ieq $src) {
        throw "Install path '$Target' and publish path '$Source' overlap. Publish to a separate folder."
    }
    if (-not (Test-Path -LiteralPath $full)) { return }
    $marker = Join-Path $full '.cgw-install'
    $items = @(Get-ChildItem -LiteralPath $full -Force)
    if ($items.Count -eq 0) { return }
    if (-not (Test-Path -LiteralPath $marker)) {
        throw "Install path '$Target' already holds other files and was not installed by this kit (no .cgw-install marker). Mirroring would delete them. Choose an empty folder."
    }
    $kindInMarker = (Get-Content -LiteralPath $marker -TotalCount 1).Trim()
    if ($kindInMarker -ne $Kind) {
        throw "Install path '$Target' holds the Workbench $kindInMarker, not the $Kind. Use a separate folder for each."
    }
}

function Invoke-Mirror {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Target,
        [Parameter(Mandatory)] [ValidateSet('Web', 'Worker')] [string] $Kind,
        [string[]] $KeepFiles = @()
    )
    Assert-SafeMirrorTarget -Source $Source -Target $Target -Kind $Kind
    $keep = @('.cgw-install') + $KeepFiles
    if ($PSCmdlet.ShouldProcess($Target, "Mirror $Source (keeps $($keep -join ', '))")) {
        New-Item -ItemType Directory -Force -Path $Target | Out-Null
        $robocopyArgs = @($Source, $Target, '/MIR', '/R:2', '/W:2', '/NP', '/NFL', '/NDL', '/XF') + $keep + @('/XD', 'logs')
        & robocopy.exe @robocopyArgs | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE copying to $Target." }
        Set-Content -LiteralPath (Join-Path $Target '.cgw-install') -Value $Kind -Encoding ascii
        Write-DeployOk "files mirrored to $Target"
    }
}

function Get-HostingBundleStatus {
    # The ASP.NET Core Module V2 plus an ASP.NET Core 10 shared runtime.
    $ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    $runtimes = @()
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) { $runtimes = @(& $dotnet.Source --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' }) }
    [pscustomobject]@{
        ModulePresent  = Test-Path -LiteralPath $ancm
        AspNetCore10   = $runtimes.Count -gt 0
        RuntimeVersion = if ($runtimes.Count -gt 0) { ($runtimes[-1] -split ' ')[1] } else { $null }
    }
}

function Get-DotNetRuntime10 {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return $null }
    $rt = @(& $dotnet.Source --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' })
    if ($rt.Count -eq 0) { return $null }
    return ($rt[-1] -split ' ')[1]
}

function Assert-SettingsFile {
    <#
    .SYNOPSIS Checks the production settings file holds the keys the application needs and no test switches.
    #>
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string[]] $RequiredKeys)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Settings file '$Path' does not exist. Start from deploy\appsettings.*.Production.sample.json." }
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    $missing = foreach ($key in $RequiredKeys) {
        $node = $json
        foreach ($part in $key.Split(':')) {
            if ($node -is [System.Collections.IDictionary] -and $node.Contains($part)) { $node = $node[$part] } else { $node = $null; break }
        }
        if ($null -eq $node -or ($node -is [string] -and ($node -eq '' -or $node -like '<*>'))) { $key }
    }
    if ($missing) { throw "Settings file '$Path' is missing values for: $($missing -join ', ')." }
    if ($json.Contains('Testing')) {
        foreach ($k in @($json['Testing'].Keys)) {
            if ($json['Testing'][$k] -eq $true) { throw "Settings file '$Path' turns on Testing:$k. Test switches are never allowed in production." }
        }
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    if ($raw -match '(?i)password\s*=' -or $raw -match '(?i)"clientsecret"\s*:\s*"[^"<]') {
        throw "Settings file '$Path' contains a password or client secret. Production uses Windows authentication and certificates only."
    }
    return $json
}
