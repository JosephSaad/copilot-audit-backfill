<#
.SYNOPSIS
    Saves a ledger database digest of the CopilotGovernance database to write-once (WORM) storage, and can
    verify the database against every digest saved so far.

.DESCRIPTION
    Decisions, evidence metadata, attestations, verifications and the audit trail are append-only ledger
    tables. A digest is a hash of the ledger's state; kept outside the database on WORM storage, it lets an
    auditor prove that nobody, DBAs included, changed those rows afterwards.

    Schedule it (SQL Server Agent PowerShell step or Task Scheduler) every few hours, running as an account
    with VIEW LEDGER CONTENT / GENERATE LEDGER DIGEST on the database and write access to the WORM share.
    -Verify runs sys.sp_verify_database_ledger with all saved digests (monthly, and on an auditor's request).

.EXAMPLE
    pwsh -File deploy\Save-LedgerDigest.ps1 -SqlServer cgw-ag.corp.example -Database CopilotGovernance -DigestPath \\worm01\CgwLedger$
.EXAMPLE
    pwsh -File deploy\Save-LedgerDigest.ps1 -SqlServer cgw-ag.corp.example -Database CopilotGovernance -DigestPath \\worm01\CgwLedger$ -Verify
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SqlServer,
    [string] $Database = 'CopilotGovernance',
    [Parameter(Mandatory)] [string] $DigestPath,
    [switch] $Verify,
    # Test servers only: accept a self-signed SQL Server certificate.
    [switch] $TrustServerCertificate
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Database -notmatch '^[A-Za-z][A-Za-z0-9_]{0,100}$') { throw "Database name '$Database' must be letters, digits and underscores." }
$sqlcmd = (Get-Command sqlcmd -ErrorAction SilentlyContinue).Source
if (-not $sqlcmd) { throw 'sqlcmd is required (SQL Server command-line tools).' }
if (-not (Test-Path -LiteralPath $DigestPath)) { throw "Digest path '$DigestPath' is not reachable." }

function Invoke-SqlScalar([string] $Query, [string] $Server, [bool] $Trust) {
    # The query goes through a file: the digest list outgrows a command line. -N encrypts with a validated
    # certificate; -y 0 returns long text whole (and keeps the header line, so callers pick their line).
    $tmp = [IO.Path]::Combine([IO.Path]::GetTempPath(), "cgw-ledger-$([guid]::NewGuid().ToString('n')).sql")
    try {
        [IO.File]::WriteAllText($tmp, "SET NOCOUNT ON; $Query", [Text.UTF8Encoding]::new($false))
        $trustArgs = [string[]]@()
        if ($Trust) { $trustArgs = [string[]]@('-C') }
        $out = & $sqlcmd -S $Server -d $Database -E -N @trustArgs -b -y 0 -f 65001 -i $tmp 2>&1
        if ($LASTEXITCODE -ne 0) {
            if ("$out" -match 'Msg 37498') { throw "Ledger verification needs snapshot isolation: run ALTER DATABASE [$Database] SET ALLOW_SNAPSHOT_ISOLATION ON; once (Deploy-Production.md, step 4)." }
            throw "sqlcmd failed: $("$out".Trim())"
        }
        return @($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    }
    finally { if (Test-Path -LiteralPath $tmp) { [IO.File]::Delete($tmp) } }
}

if (-not $Verify) {
    $lines = Invoke-SqlScalar -Query 'EXEC sys.sp_generate_database_ledger_digest;' -Server $SqlServer -Trust $TrustServerCertificate.IsPresent
    $digest = @($lines | Where-Object { $_.StartsWith('{') }) | Select-Object -First 1
    if (-not $digest) { throw "Unexpected digest output: $($lines -join ' ')" }
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $file = Join-Path $DigestPath "$Database-$stamp.json"
    if ($PSCmdlet.ShouldProcess($file, 'Write ledger digest')) {
        # New file per digest: WORM storage refuses overwrites, and verification uses every digest.
        [IO.File]::WriteAllText($file, $digest, [Text.UTF8Encoding]::new($false))
        Write-Information "Saved $file" -InformationAction Continue
    }
    return
}

$files = @(Get-ChildItem -LiteralPath $DigestPath -Filter "$Database-*.json" | Sort-Object Name)
if ($files.Count -eq 0) { throw "No digests for $Database in $DigestPath." }
$digests = '[' + (($files | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw).Trim() }) -join ',') + ']'
$escaped = $digests.Replace("'", "''")
$result = (Invoke-SqlScalar -Query "EXEC sys.sp_verify_database_ledger N'$escaped'; SELECT 'VERIFIED';" -Server $SqlServer -Trust $TrustServerCertificate.IsPresent) -join ' '
if ($result -match 'VERIFIED') { Write-Information "Ledger verified against $($files.Count) digest(s)." -InformationAction Continue }
else { throw "Ledger verification failed: $result" }
