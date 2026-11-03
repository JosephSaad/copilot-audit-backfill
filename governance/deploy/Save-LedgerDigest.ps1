<#
.SYNOPSIS
    Saves a ledger database digest of the CopilotGovernance database to write-once (WORM) storage, and can
    verify the database against every digest saved so far.

.DESCRIPTION
    Decisions, evidence metadata, attestations, verifications and the audit trail are append-only ledger
    tables. A digest is a hash of the ledger's state; kept outside the database on WORM storage, it lets an
    auditor prove that nobody, DBAs included, changed those rows afterwards.

    The installer schedules it daily as the worker's gMSA (a user of the database in app_worker, which may generate
    digests) with write access to the WORM share. Each run writes a row to intg.JobRun (job LedgerDigest), shown on
    Admin > Integrations; a failure also ends the script with exit code 1, the task's last result in Task Scheduler,
    which is the signal when the database itself cannot be reached.
    -Verify runs sys.sp_verify_database_ledger with all saved digests (monthly, and on an auditor's request), as an
    account with VIEW LEDGER CONTENT.

.EXAMPLE
    pwsh -File governance\deploy\Save-LedgerDigest.ps1 -SqlServer cgw-ag.corp.example -Database CopilotGovernance -DigestPath \\worm01\CgwLedger$
.EXAMPLE
    pwsh -File governance\deploy\Save-LedgerDigest.ps1 -SqlServer cgw-ag.corp.example -Database CopilotGovernance -DigestPath \\worm01\CgwLedger$ -Verify
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

function Write-DigestRun([string] $Status, [string] $Message, [datetime] $Started) {
    # One row in intg.JobRun, job LedgerDigest, so Admin > Integrations shows the digest beside the worker's jobs. When
    # the database cannot be reached this fails too: then the task's last result in Task Scheduler is the signal.
    $msg = $Message.Replace("'", "''")
    if ($msg.Length -gt 900) { $msg = $msg.Substring(0, 900) }
    $node = "$env:COMPUTERNAME".Replace("'", "''")
    $query = "INSERT intg.JobRun (JobName, Node, StartedUtc, FinishedUtc, Status, Message) VALUES ('LedgerDigest', N'$node', '$($Started.ToString('yyyy-MM-ddTHH:mm:ss'))', SYSUTCDATETIME(), '$Status', N'$msg');"
    [void] (Invoke-SqlScalar -Query $query -Server $SqlServer -Trust $TrustServerCertificate.IsPresent)
}

if (-not $Verify) {
    # A failure throws, so the task ends with a nonzero result, and is written to intg.JobRun where possible.
    $started = (Get-Date).ToUniversalTime()
    $saved = $null
    try {
        $lines = Invoke-SqlScalar -Query 'EXEC sys.sp_generate_database_ledger_digest;' -Server $SqlServer -Trust $TrustServerCertificate.IsPresent
        $digest = @($lines | Where-Object { $_.StartsWith('{') }) | Select-Object -First 1
        if (-not $digest) { throw "Unexpected digest output: $($lines -join ' ')" }
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
        $file = Join-Path $DigestPath "$Database-$stamp.json"
        if ($PSCmdlet.ShouldProcess($file, 'Write ledger digest')) {
            # New file per digest: WORM storage refuses overwrites, and verification uses every digest.
            [IO.File]::WriteAllText($file, $digest, [Text.UTF8Encoding]::new($false))
            Write-Information "Saved $file" -InformationAction Continue
            $saved = $file
        }
    }
    catch {
        $failure = $_
        try { Write-DigestRun -Status 'FAILED' -Message "The ledger digest was not saved: $($failure.Exception.Message)" -Started $started }
        catch { Write-Warning "The failure could not be recorded in intg.JobRun either: $($_.Exception.Message)" }
        throw $failure
    }
    if ($saved) {
        # The digest is saved: a row that cannot be written is a warning, not a failed digest.
        try { Write-DigestRun -Status 'SUCCEEDED' -Message "Ledger digest saved to $saved." -Started $started }
        catch { Write-Warning "The digest was saved, but its row in intg.JobRun was not written: $($_.Exception.Message)" }
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
