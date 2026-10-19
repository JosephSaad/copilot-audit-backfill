<#
.SYNOPSIS
    What tools\Import-GitHubCopilotUsage.ps1, tools\Import-FoundryUsage.ps1 and tools\Import-PowerAppsActivity.ps1
    share: signing in as the app by its certificate, the connection to the Copilot database, writing a set of rows
    to one of their tables, and the run's own log.

.DESCRIPTION
    Dot-sourced by the three tools; it runs nothing by itself. Every table is written the same way: the rows go
    to a temporary table in one bulk copy, then, in one transaction, the rows they replace are deleted and the
    new ones inserted. So a load can be run again over the same days, and a reader never sees a table half
    written. A table replaced whole for its scope (the seats, the deployments) is held by the delete guard
    (2.6.5, tools\DropGuard.ps1, which this loads).
#>

. (Join-Path $PSScriptRoot 'DropGuard.ps1')

function ConvertTo-Base64Url([byte[]] $Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function New-ClientAssertion {
    <#
        The signed statement an app signs in to Entra with in place of a secret: a JWT signed with the
        certificate's private key (PS256), naming the certificate by the SHA-256 thumbprint of its DER encoding
        (x5t#S256), good for ten minutes. Needs the certificate with its private key; nothing leaves the machine
        but the signature.
    #>
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate, [string] $Tenant, [string] $Client, [string] $Login,
          [datetime] $Now = [datetime]::UtcNow)
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) { throw "The certificate $($Certificate.Thumbprint) has no RSA private key this account can use." }
    $epoch = [datetime]::new(1970, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
    $nbf = [long] ($Now.ToUniversalTime() - $epoch).TotalSeconds
    $thumb = ConvertTo-Base64Url ([System.Security.Cryptography.SHA256]::HashData($Certificate.RawData))
    $header = [ordered]@{ alg = 'PS256'; typ = 'JWT'; 'x5t#S256' = $thumb } | ConvertTo-Json -Compress
    $claims = [ordered]@{ aud = "$($Login.TrimEnd('/'))/$Tenant/oauth2/v2.0/token"; iss = $Client; sub = $Client; jti = [guid]::NewGuid().ToString()
                          nbf = $nbf; iat = $nbf; exp = $nbf + 600 } | ConvertTo-Json -Compress
    $unsigned = (ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($header))) + '.' + (ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($claims)))
    $signature = $rsa.SignData([System.Text.Encoding]::ASCII.GetBytes($unsigned), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
    $unsigned + '.' + (ConvertTo-Base64Url $signature)
}

function Get-AppOnlyToken {
    <#
        An app-only token for -Resource (Azure Resource Manager, Microsoft Graph), signed in as the app by its
        certificate in LocalMachine\My or CurrentUser\My: Foundry's loader for Azure (2.6.2), and the Power Apps
        activity loader for Graph (2.6.9).
    #>
    param([string] $Tenant, [string] $Client, [string] $Thumbprint, [string] $Login, [string] $Resource)
    $clean = ("$Thumbprint" -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = $null
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\CurrentUser\My') { $cert = Get-Item -LiteralPath "$store\$clean" -ErrorAction SilentlyContinue; if ($cert) { break } }
    if (-not $cert) { throw "No certificate $clean in LocalMachine\My or CurrentUser\My." }
    $r = Invoke-RestMethod -Method Post -Uri "$($Login.TrimEnd('/'))/$Tenant/oauth2/v2.0/token" -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 60 -Body @{
        client_id = $Client; scope = "$($Resource.TrimEnd('/'))/.default"; grant_type = 'client_credentials'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        client_assertion = (New-ClientAssertion -Certificate $cert -Tenant $Tenant -Client $Client -Login $Login)
    }
    $r.access_token
}

function New-UsageConnection {
    <# An open connection to the Copilot database: Windows authentication, encrypted, as the other tools connect. #>
    param([string] $SqlServer, [string] $Database, [switch] $TrustServerCertificate, [string] $ConnectionString)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    if ($ConnectionString) { $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString) }
    else {
        $b['Data Source'] = $SqlServer; $b['Initial Catalog'] = $Database; $b['Integrated Security'] = $true
        $b['Encrypt'] = $true; $b['TrustServerCertificate'] = [bool] $TrustServerCertificate
    }
    $b['Application Name'] = 'Copilot external usage'
    $c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    $c.Open()
    $c
}

function Invoke-UsageSql {
    <# One statement on the connection: a scalar back. Parameters by name; $null goes in as NULL. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Sql, [hashtable] $Parameters = @{}, [System.Data.SqlClient.SqlTransaction] $Transaction)
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql; $cmd.CommandTimeout = 600; $cmd.Transaction = $Transaction
    foreach ($k in $Parameters.Keys) { [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $Parameters[$k]) { [DBNull]::Value } else { $Parameters[$k] })) }
    $cmd.ExecuteScalar()
}

function Test-UsageSchema {
    <# Whether the tables are there: the tools stop, saying what to run, when they are not. #>
    param([System.Data.SqlClient.SqlConnection] $Connection)
    [int] (Invoke-UsageSql $Connection "SELECT CASE WHEN OBJECT_ID(N'audit.ExternalUsageRun', N'U') IS NULL THEN 0 ELSE 1 END;") -eq 1
}

function ConvertTo-UsageTable {
    <#
        Rows (objects) as a DataTable with the columns named, in order, each of the type given: what
        Write-UsageTable takes. A property a row lacks, or one that is $null or '', is NULL. Pure, for the tests.
    #>
    param([AllowEmptyCollection()][object[]] $Rows, [System.Collections.Specialized.OrderedDictionary] $Columns)
    $t = [System.Data.DataTable]::new()
    foreach ($name in $Columns.Keys) { [void] $t.Columns.Add($name, $Columns[$name]) }
    foreach ($r in $Rows) {
        $row = $t.NewRow()
        foreach ($name in $Columns.Keys) {
            $p = $r.PSObject.Properties[$name]
            $v = if ($p) { $p.Value } else { $null }
            $row[$name] = if ($null -eq $v -or ($v -is [string] -and $v -eq '' -and $Columns[$name] -ne [string])) { [DBNull]::Value } else { $v }
        }
        $t.Rows.Add($row)
    }
    , $t
}

function Write-UsageTable {
    <#
        Writes the rows to -Table, replacing what they supersede, in one transaction. What is replaced is either
        every row matching -ReplaceWhere (a whole scope, or a scope's month; its parameters in -ReplaceParameters),
        or, without it, each row with the same -Key as a new one. Returns how many rows were written.

        With -MaxDropPercent above 0 and -ReplaceWhere (2.6.5), the delete guard first: when the new rows are more
        than that percentage fewer than the rows they replace, nothing is written and the guard's refusal is thrown
        (tools\DropGuard.ps1). -What names the rows for the message, -Setting the setting the percentage is in.
    #>
    param(
        [System.Data.SqlClient.SqlConnection] $Connection,
        [string] $Table,
        [System.Data.DataTable] $Rows,
        [string[]] $Key = @(),
        [string] $ReplaceWhere,
        [hashtable] $ReplaceParameters = @{},
        [double] $MaxDropPercent = 0,
        [string] $What = $Table,
        [string] $Setting,
        [switch] $AllowDrop
    )
    if ($Table -notmatch '^audit\.[A-Za-z]+$') { throw "Not a table of these tools: $Table" }
    if ($MaxDropPercent -gt 0 -and $ReplaceWhere) {
        $before = [long] (Invoke-UsageSql $Connection "SELECT COUNT_BIG(*) FROM $Table WHERE $ReplaceWhere;" $ReplaceParameters)
        Assert-DropGuard -What $What -Before $before -After $Rows.Rows.Count -MaxDropPercent $MaxDropPercent -Setting $Setting -AllowDrop:$AllowDrop
    }
    $cols = @($Rows.Columns | ForEach-Object { "[$($_.ColumnName)]" }) -join ', '
    [void] (Invoke-UsageSql $Connection "IF OBJECT_ID(N'tempdb..#usage') IS NOT NULL DROP TABLE #usage; SELECT TOP (0) $cols INTO #usage FROM $Table;")
    if ($Rows.Rows.Count -gt 0) {
        $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($Connection)
        try {
            $bulk.DestinationTableName = '#usage'; $bulk.BulkCopyTimeout = 600
            foreach ($c in $Rows.Columns) { [void] $bulk.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
            $bulk.WriteToServer($Rows)
        }
        finally { $bulk.Close() }
    }
    $tx = $Connection.BeginTransaction()
    try {
        if ($ReplaceWhere) { [void] (Invoke-UsageSql $Connection "DELETE FROM $Table WHERE $ReplaceWhere;" $ReplaceParameters $tx) }
        elseif ($Key.Count -gt 0) {
            $on = @($Key | ForEach-Object { "t.[$_] = s.[$_]" }) -join ' AND '
            [void] (Invoke-UsageSql $Connection "DELETE t FROM $Table AS t JOIN #usage AS s ON $on;" @{} $tx)
        }
        [void] (Invoke-UsageSql $Connection "INSERT INTO $Table ($cols) SELECT $cols FROM #usage;" @{} $tx)
        $tx.Commit()
    }
    catch { $tx.Rollback(); throw }
    finally { [void] (Invoke-UsageSql $Connection "IF OBJECT_ID(N'tempdb..#usage') IS NOT NULL DROP TABLE #usage;") }
    $Rows.Rows.Count
}

function Start-UsageRun {
    <# Records the run from its start, so a failure shows too. Returns its id. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [ValidateSet('GitHub', 'Foundry', 'PowerApps')][string] $Source, [string] $Scope, $FromDay, $ToDay)
    [int] (Invoke-UsageSql $Connection @'
INSERT INTO audit.ExternalUsageRun (Source, Scope, FromDay, ToDay, Host) VALUES (@s, LEFT(@scope, 400), @f, @t, @h);
SELECT CAST(SCOPE_IDENTITY() AS INT);
'@ @{ '@s' = $Source; '@scope' = $Scope; '@f' = $FromDay; '@t' = $ToDay; '@h' = [Environment]::MachineName })
}

function Complete-UsageRun {
    <# How the run ended: Succeeded, Partial (a part of it could not be read, and Detail says which) or Failed. #>
    param([System.Data.SqlClient.SqlConnection] $Connection, [int] $RunId, [ValidateSet('Succeeded', 'Partial', 'Failed')][string] $Status,
          [int] $Rows, [string] $Detail, $ExpiresUtc)
    [void] (Invoke-UsageSql $Connection @'
UPDATE audit.ExternalUsageRun SET EndedUtc = SYSUTCDATETIME(), Status = @st, RowsWritten = @r, Detail = LEFT(@d, 2000), CredentialExpiresUtc = @e WHERE RunId = @id;
'@ @{ '@st' = $Status; '@r' = $Rows; '@d' = $Detail; '@e' = $ExpiresUtc; '@id' = $RunId })
}

function Get-UsageDay {
    <# The UTC days a load covers: -Days of them, ending yesterday. Pure, for the tests. #>
    param([ValidateRange(1, 400)][int] $Days, [datetime] $Today = [datetime]::UtcNow.Date)
    $last = $Today.Date.AddDays(-1)
    , @(for ($i = $Days - 1; $i -ge 0; $i--) { $last.AddDays(-$i) })
}
