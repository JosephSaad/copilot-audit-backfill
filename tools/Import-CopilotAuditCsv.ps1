<#
.SYNOPSIS
    Imports a folder of Purview audit log exports (CSV) into the audit
    database - the same tables the Graph backfill fills.

.DESCRIPTION
    Point it at a folder. Every *.csv in it is read, and the rows are loaded
    into audit.CopilotAudit and audit.CopilotInteraction in the shape the
    Graph backfill writes, so the flattened table, sessions and the bi schema
    work on them unchanged.

      - Exports may overlap in range and repeat each other. One row per
        RecordId is kept, and a record audit.CopilotAudit already holds - from
        the Graph backfill, -Incremental or an earlier import - wins.
      - A file already imported (same SHA-256) is skipped, so the folder can
        keep growing and the command can be run again.
      - Rows whose AuditData is not JSON (a file opened and saved in Excel
        cuts it at 32,767 characters), or that have no record ID or creation
        time, are not loaded; each is listed in audit.CopilotCsvReject.
      - Rows for other operations are counted and left out: a database holds
        one operation set, as the backfill records it.
      - After the merge it brings the flattened table, license events,
        sessions and the bi schema up to date from the earliest record it
        added, as -Incremental does - or from an earlier import's, if that
        one's refresh never finished.

    The files are read here, not by SQL Server, so the service account needs
    no access to them. Nothing is written to the files or the folder.

    A database can start from CSV files alone. Pass -CoveredThroughUtc (the end
    of the search range the exports were made for) and the import records the
    range, so Invoke-CopilotAuditBackfill.ps1 -Incremental can continue from
    there. The Graph API reaches back 180 days only: a gap between the exports
    and what it still holds cannot be filled.

    Works while a Graph backfill is finalized, before it has started, or with
    no backfill at all; refuses while one has started but is not finalized.

.PARAMETER Path
    The folder holding the exports.

.PARAMETER Recurse
    Include subfolders.

.PARAMETER Filter
    Which files to read. Default *.csv.

.PARAMETER SqlServer / -Database / -ConnectionString / -SqlCredential / -TrustServerCertificate
    Where the audit database lives. Same shape as Invoke-CopilotAuditBackfill.ps1.

.PARAMETER Operations
    The operations this database holds. Only needed for a database with no
    backfill yet; otherwise the backfill's are used, and a different value is
    refused. Default CopilotInteraction. For a license-event database, the same
    list the backfill takes: 'Change user license','Update user','Add user','Delete user'.
    A row matches when its Operation contains one of them, as the Graph
    search's operation filter does ('Change user license.' matches).

.PARAMETER CoveredFromUtc
    Start of the search range the exports were made for (UTC). Recorded with
    -CoveredThroughUtc; defaults to the day of the earliest record.

.PARAMETER CoveredThroughUtc
    End of the search range the exports were made for (UTC). Recorded so
    -Incremental knows where to continue. Only a range an import recorded is
    ever changed; a Graph backfill's is left alone.

.PARAMETER SkipPostProcessing
    Only import and merge; leave the derived tables for later. The refresh is
    then owed (audit.CopilotCsvImport.DerivedRefreshedUtc stays empty), and
    the next -Incremental run, or the next run of this tool without the
    switch, makes it up from the earliest record the import added - even with
    no new files. A refresh that stops part way is owed the same way.

.PARAMETER SessionLicenseDatabase
    Passed to usp_BuildCopilotSessions and bi.usp_RefreshBi as
    @LicenseEventDatabase, as -Incremental does: one database, or several
    comma-separated. Left out, the ones this database's last session build
    read (or its last bi refresh), so the build matches the nightly run's;
    -SessionLicenseDatabase '' reads none.

.EXAMPLE
    .\tools\Import-CopilotAuditCsv.ps1 -Path D:\PurviewExports -SqlServer SQLPROD01 -Database CopilotAudit `
        -SessionLicenseDatabase CopilotLicenseEvents

.EXAMPLE
    # A new database, from exports that covered up to 1 September:
    .\tools\Import-CopilotAuditCsv.ps1 -Path D:\PurviewExports -SqlServer SQLPROD01 -Database CopilotAudit `
        -CoveredThroughUtc 2026-09-01
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]   $Path,
    [switch]   $Recurse,
    [string]   $Filter = '*.csv',

    [string]   $SqlServer,
    [string]   $Database,
    [string]   $ConnectionString,
    [pscredential] $SqlCredential,
    [switch]   $TrustServerCertificate,

    [string[]] $Operations,
    [datetime] $CoveredFromUtc,
    [datetime] $CoveredThroughUtc,

    [switch]   $SkipPostProcessing,
    [string]   $SessionLicenseDatabase,

    [ValidateRange(100, 100000)] [int] $BatchRows = 5000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ToolRoot = Split-Path $PSScriptRoot -Parent
# The parameters given: -SessionLicenseDatabase '' given means none, left out means the last build's.
$script:Bound = $PSBoundParameters

function Write-Log {
    param([Parameter(Mandatory)][string] $Message,
          [ValidateSet('INFO', 'WARN', 'ERROR')][string] $Level = 'INFO')
    $line = '{0:yyyy-MM-dd HH:mm:ss}Z [{1,-5}] {2}' -f [datetime]::UtcNow, $Level, $Message
    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        default { Write-Host $line }
    }
}

# ===========================================================================
# 1. Reading an export
# ===========================================================================

$script:ReaderSource = @'
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace CopilotAudit
{
    public sealed class CsvReject
    {
        public long LineNumber;
        public string RecordId;
        public string Reason;
    }

    /// <summary>
    /// Streams one Purview audit log export into DataTables shaped like
    /// audit.CopilotCsvStage, a batch at a time. Maps each row the way the
    /// Graph backfill maps an auditLogRecord, so both land the same:
    /// UserId is the object ID (AuditData.UserKey), UserPrincipalName the
    /// sign-in name, Service the workload, UserType the enum name, and
    /// AuditDataGz the gzip of the UTF-16 AuditData text - what SQL Server's
    /// COMPRESS() produces, so DECOMPRESS reads it back.
    /// </summary>
    public sealed class CsvStageReader : IDisposable
    {
        static readonly string[] UserTypeNames =
        {
            "Regular", "Reserved", "Admin", "DcAdmin", "System", "Application", "ServicePrincipal",
            "CustomPolicy", "SystemPolicy", "PartnerTechnician", "Guest"
        };

        readonly TextFieldParser _parser;
        readonly string[] _operations;
        readonly int _recordId, _creation, _recordType, _operation, _userId, _auditData, _adminUnits;

        public long RowsRead, RowsStaged, RowsRejected, RowsOtherOperation;
        public DateTime? Earliest, Latest;
        public readonly List<CsvReject> Rejects = new List<CsvReject>();
        public int MaxRejectsKept = 1000;

        public CsvStageReader(string path, string[] operations)
        {
            _parser = new TextFieldParser(path, new UTF8Encoding(false), true);
            _parser.TextFieldType = FieldType.Delimited;
            _parser.SetDelimiters(",");
            _parser.HasFieldsEnclosedInQuotes = true;
            _parser.TrimWhiteSpace = false;

            var ops = new List<string>();
            foreach (var o in operations ?? new string[0])
            {
                var n = Normalize(o);
                if (n.Length > 0) ops.Add(n);
            }
            _operations = ops.ToArray();

            string[] header = _parser.EndOfData ? null : _parser.ReadFields();
            if (header == null) throw new InvalidDataException("The file is empty.");
            _recordId   = Find(header, "RecordId");
            _creation   = Find(header, "CreationDate");
            _recordType = Find(header, "RecordType");
            _operation  = Find(header, "Operation", "Operations");
            _userId     = Find(header, "UserId", "UserIds");
            _auditData  = Find(header, "AuditData");
            _adminUnits = Find(header, "AssociatedAdminUnits");
            if (_auditData < 0)
                throw new InvalidDataException("No AuditData column: this is not a Purview audit log export.");
        }

        public static DataTable NewTable()
        {
            var t = new DataTable();
            t.Columns.Add("FileId", typeof(int));
            t.Columns.Add("LineNumber", typeof(long));
            t.Columns.Add("RecordId", typeof(string));
            t.Columns.Add("CreatedDateTime", typeof(DateTime));
            foreach (var c in new[] { "RecordType", "Operation", "OrganizationId", "UserType", "UserId",
                                      "UserPrincipalName", "Service", "ObjectId", "ClientIp", "AdministrativeUnits" })
                t.Columns.Add(c, typeof(string));
            t.Columns.Add("AuditDataGz", typeof(byte[]));
            return t;
        }

        public bool EndOfData { get { return _parser.EndOfData; } }

        /// <summary>Adds up to max staged rows to the table; fewer at the end of the file.</summary>
        public int Fill(DataTable table, int fileId, int max)
        {
            int added = 0;
            while (added < max && !_parser.EndOfData)
            {
                long line = _parser.LineNumber;
                string[] f;
                try { f = _parser.ReadFields(); }
                catch (MalformedLineException ex)
                {
                    RowsRead++;
                    Reject(ex.LineNumber, null, "Malformed CSV line: unbalanced quotes or a cut-off row.");
                    continue;
                }
                if (f == null || (f.Length == 1 && string.IsNullOrWhiteSpace(f[0]))) continue;
                RowsRead++;
                if (Map(f, line, fileId, table)) added++;
            }
            return added;
        }

        bool Map(string[] f, long line, int fileId, DataTable table)
        {
            string csvId = Blank(Get(f, _recordId));
            string audit = Get(f, _auditData);
            if (string.IsNullOrEmpty(audit)) return Reject(line, csvId, "AuditData is empty.");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(audit); }
            catch (JsonException)
            {
                return Reject(line, csvId, "AuditData is not valid JSON. A file opened and saved in Excel cuts it at 32,767 characters.");
            }
            using (doc)
            {
                var j = doc.RootElement;
                if (j.ValueKind != JsonValueKind.Object) return Reject(line, csvId, "AuditData is not a JSON object.");

                string op = Blank(Get(f, _operation)) ?? Str(j, "Operation");
                if (!Wanted(op)) { RowsOtherOperation++; return false; }

                string id = csvId ?? Blank(Str(j, "Id"));
                if (id == null) return Reject(line, null, "No RecordId, and no Id in AuditData.");
                if (id.Length > 64) return Reject(line, id.Substring(0, 64), "RecordId is longer than 64 characters.");

                DateTime created;
                if (!TryUtc(Str(j, "CreationTime"), out created) && !TryUtc(Get(f, _creation), out created))
                    return Reject(line, id, "No readable creation time in AuditData.CreationTime or CreationDate.");

                string userType = Str(j, "UserType");
                int ut;
                if (userType != null && int.TryParse(userType, NumberStyles.Integer, CultureInfo.InvariantCulture, out ut)
                    && ut >= 0 && ut < UserTypeNames.Length)
                    userType = UserTypeNames[ut];

                var row = table.NewRow();
                row[0] = fileId;
                row[1] = line;
                row[2] = id;
                row[3] = created;
                row[4] = Fit(Blank(Get(f, _recordType)), 128);
                row[5] = Fit(op, 128);
                row[6] = Fit(Str(j, "OrganizationId"), 64);
                row[7] = Fit(userType, 64);
                row[8] = Fit(Str(j, "UserKey"), 512);
                row[9] = Fit(Blank(Get(f, _userId)) ?? Str(j, "UserId"), 512);
                row[10] = Fit(Str(j, "Workload"), 128);
                row[11] = Fit(Str(j, "ObjectId"), 4000);
                row[12] = Fit(Str(j, "ClientIP"), 128);
                row[13] = Fit(Blank(Get(f, _adminUnits)), 4000);
                row[14] = Gzip(audit);
                for (int i = 4; i <= 13; i++) if (row[i] == null) row[i] = DBNull.Value;
                table.Rows.Add(row);

                RowsStaged++;
                if (!Earliest.HasValue || created < Earliest.Value) Earliest = created;
                if (!Latest.HasValue || created > Latest.Value) Latest = created;
                return true;
            }
        }

        bool Reject(long line, string recordId, string reason)
        {
            RowsRejected++;
            if (Rejects.Count < MaxRejectsKept)
                Rejects.Add(new CsvReject { LineNumber = line, RecordId = recordId, Reason = reason });
            return false;
        }

        bool Wanted(string op)
        {
            if (_operations.Length == 0) return true;
            if (op == null) return false;
            var n = Normalize(op);
            foreach (var o in _operations) if (n.Contains(o)) return true;
            return false;
        }

        static string Normalize(string s)
        {
            return (s ?? "").Trim().TrimEnd('.').Trim().ToLowerInvariant();
        }

        static int Find(string[] header, params string[] names)
        {
            for (int i = 0; i < header.Length; i++)
            {
                var h = (header[i] ?? "").Trim().Trim('\uFEFF').Trim('"').Trim();
                foreach (var n in names)
                    if (string.Equals(h, n, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        static string Get(string[] f, int i) { return i >= 0 && i < f.Length ? f[i] : null; }
        static string Blank(string s) { return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }
        static object Fit(string s, int max) { return s == null ? null : (s.Length > max ? s.Substring(0, max) : s); }

        static string Str(JsonElement e, string prop)
        {
            JsonElement v;
            if (!e.TryGetProperty(prop, out v)) return null;
            switch (v.ValueKind)
            {
                case JsonValueKind.String: return Blank(v.GetString());
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False: return v.GetRawText();
                default: return null;
            }
        }

        static bool TryUtc(string s, out DateTime utc)
        {
            utc = default(DateTime);
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        }

        static byte[] Gzip(string text)
        {
            var raw = Encoding.Unicode.GetBytes(text);
            using (var ms = new MemoryStream(raw.Length / 4 + 64))
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        public void Dispose() { _parser.Dispose(); }
    }
}
'@

function Import-CsvStageReader {
    if ('CopilotAudit.CsvStageReader' -as [type]) { return }
    $refs = @('Microsoft.VisualBasic.Core', 'System.Data.Common', 'System.Text.Json', 'System.IO.Compression',
              'System.Collections', 'System.Runtime', 'System.Memory', 'System.Text.Encoding.Extensions',
              'System.ComponentModel.TypeConverter', 'System.Private.CoreLib', 'netstandard')
    Add-Type -TypeDefinition $script:ReaderSource -ReferencedAssemblies $refs -IgnoreWarnings -WarningAction SilentlyContinue
}

function ConvertTo-UtcDate {
    param([Parameter(Mandatory)][datetime] $Value)
    switch ($Value.Kind) {
        'Utc'   { return $Value }
        'Local' { return $Value.ToUniversalTime() }
        default { return [datetime]::SpecifyKind($Value, [DateTimeKind]::Utc) }   # 2026-09-01 means UTC
    }
}

function Get-OperationSet {
    # The backfill's form: sorted, comma-joined.
    param([string[]] $Names)
    return (@($Names | ForEach-Object { $_.Trim() } | Where-Object { $_ }) | Sort-Object) -join ','
}

# ===========================================================================
# 2. SQL
# ===========================================================================

function New-CsvImportConnectionString {
    $csb = if ($ConnectionString) {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    else {
        [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    }
    if (-not $ConnectionString) {
        if (-not $SqlServer -or -not $Database) { throw 'Specify -SqlServer and -Database (or -ConnectionString).' }
        $csb['Data Source'] = $SqlServer
        $csb['Initial Catalog'] = $Database
        if ($SqlCredential) {
            $csb['User ID'] = $SqlCredential.UserName
            $csb['Password'] = $SqlCredential.GetNetworkCredential().Password
        }
        else { $csb['Integrated Security'] = $true }
        $csb['Encrypt'] = $true
        $csb['TrustServerCertificate'] = [bool] $TrustServerCertificate
    }
    $csb['Application Name'] = 'CopilotAuditCsvImport'
    return $csb.ConnectionString
}

function Enter-RunLock {
    <#
        Holds the database for this import alone, as the nightly pipeline, Install-CopilotAudit.ps1 and the
        maintenance rebuilds do: an exclusive application lock, on a connection of its own kept open to the end of the
        import. An import took none, so it could merge and rebuild beside the nightly, and two imports at once each
        emptied the stage the other was filling. Under the pipeline's lock (CopilotAuditRunLock) it holds it already.
        Returns the connection (dispose it to let go), or nothing under the pipeline's lock.
    #>
    param([string] $ConnString)
    $b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnString)
    if ($env:CopilotAuditRunLock -eq "$($b['Data Source'])/$($b['Initial Catalog'])") { return $null }
    $b['Pooling'] = $false   # closing a pooled connection keeps its session, and the session's lock with it
    $c = [System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotAudit.Run', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;"
        $granted = [int] $cmd.ExecuteScalar() -ge 0
    }
    catch { $c.Dispose(); throw }
    if (-not $granted) {
        $c.Dispose()
        throw "Another run is using $($b['Initial Catalog']) now: the nightly pipeline, the installer, a maintenance rebuild, a load or another import. Nothing was imported. Let it finish (Admin > Health shows the pipeline's runs), then run this again."
    }
    $c
}

function Invoke-CsvSql {
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlConnection] $Connection,
        [Parameter(Mandatory)][string] $Sql,
        [hashtable] $Parameters = @{},
        [ValidateSet('NonQuery', 'Scalar', 'Table')][string] $As = 'NonQuery',
        [int] $TimeoutSeconds = 600
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = $TimeoutSeconds
    foreach ($k in $Parameters.Keys) {
        $v = $Parameters[$k]
        [void] $cmd.Parameters.AddWithValue($k, $(if ($null -eq $v) { [DBNull]::Value } else { $v }))
    }
    switch ($As) {
        'NonQuery' { [void] $cmd.ExecuteNonQuery() }
        'Scalar'   { return $cmd.ExecuteScalar() }
        'Table'    {
            $t = [System.Data.DataTable]::new()
            $r = $cmd.ExecuteReader()
            try { $t.Load($r) } finally { $r.Dispose() }
            return , $t
        }
    }
}

function Invoke-CsvSqlScript {
    # GO alone on a line separates batches, as sqlcmd splits them.
    param([Parameter(Mandatory)][System.Data.SqlClient.SqlConnection] $Connection, [Parameter(Mandatory)][string] $Path)
    $text = Get-Content -LiteralPath $Path -Raw
    foreach ($batch in [regex]::Split($text, '(?im)^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$')) {
        if (-not [string]::IsNullOrWhiteSpace($batch)) { Invoke-CsvSql -Connection $Connection -Sql $batch }
    }
}

function Test-CsvProc {
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Name)
    return [bool] (Invoke-CsvSql -Connection $Connection -As Scalar -Sql "SELECT CASE WHEN OBJECT_ID(N'$Name', N'P') IS NULL THEN 0 ELSE 1 END")
}

function Write-CsvStage {
    param([System.Data.SqlClient.SqlConnection] $Connection, [System.Data.DataTable] $Table, [string] $Destination)
    $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($Connection, [System.Data.SqlClient.SqlBulkCopyOptions]::TableLock, $null)
    try {
        $bulk.DestinationTableName = $Destination
        $bulk.BulkCopyTimeout = 0
        foreach ($c in $Table.Columns) { [void] $bulk.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
        $bulk.WriteToServer($Table)
    }
    finally { $bulk.Close() }
}

function Import-CsvFile {
    <#
        Streams one export into audit.CopilotCsvStage and records it in
        audit.CopilotCsvFile. Returns the reader's counts.
    #>
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlConnection] $Connection,
        [Parameter(Mandatory)][int] $ImportId,
        [Parameter(Mandatory)] $File,
        [Parameter(Mandatory)][string] $Sha256,
        [string[]] $OperationNames,
        [int] $BatchRows = 5000
    )
    $fileId = [int] (Invoke-CsvSql -Connection $Connection -As Scalar -Parameters @{
        '@i' = $ImportId; '@n' = $File.Name; '@p' = $File.FullName; '@h' = $Sha256
        '@s' = [long] $File.Length; '@w' = $File.LastWriteTimeUtc } -Sql @'
INSERT INTO audit.CopilotCsvFile (ImportId, FileName, FullPath, Sha256, SizeBytes, LastWriteUtc)
OUTPUT INSERTED.FileId VALUES (@i, @n, @p, @h, @s, @w);
'@)

    $reader = [CopilotAudit.CsvStageReader]::new($File.FullName, [string[]] $OperationNames)
    try {
        $table = [CopilotAudit.CsvStageReader]::NewTable()
        while (-not $reader.EndOfData) {
            $table.Clear()
            [void] $reader.Fill($table, $fileId, $BatchRows)
            if ($table.Rows.Count -gt 0) { Write-CsvStage -Connection $Connection -Table $table -Destination 'audit.CopilotCsvStage' }
        }

        if ($reader.Rejects.Count -gt 0) {
            $rt = [System.Data.DataTable]::new()
            [void] $rt.Columns.Add('FileId', [int]); [void] $rt.Columns.Add('LineNumber', [long])
            [void] $rt.Columns.Add('RecordId', [string]); [void] $rt.Columns.Add('Reason', [string])
            foreach ($r in $reader.Rejects) {
                [void] $rt.Rows.Add($fileId, $r.LineNumber, $(if ($r.RecordId) { $r.RecordId } else { [DBNull]::Value }), $r.Reason)
            }
            Write-CsvStage -Connection $Connection -Table $rt -Destination 'audit.CopilotCsvReject'
        }

        Invoke-CsvSql -Connection $Connection -Parameters @{
            '@f' = $fileId; '@r' = $reader.RowsRead; '@x' = $reader.RowsRejected; '@o' = $reader.RowsOtherOperation
            '@s' = $reader.RowsStaged
            '@e' = $reader.Earliest     # PowerShell unwraps the Nullable: a DateTime or $null
            '@l' = $reader.Latest } -Sql @'
UPDATE audit.CopilotCsvFile
SET RowsRead = @r, RowsRejected = @x, RowsOtherOperation = @o, RowsStaged = @s,
    EarliestRecordUtc = @e, LatestRecordUtc = @l
WHERE FileId = @f;
'@
        return [pscustomobject]@{
            FileId = $fileId; RowsRead = $reader.RowsRead; RowsStaged = $reader.RowsStaged
            RowsRejected = $reader.RowsRejected; RowsOtherOperation = $reader.RowsOtherOperation
            Earliest = $reader.Earliest; Latest = $reader.Latest
        }
    }
    finally { $reader.Dispose() }
}

function Invoke-CsvDerivedRefresh {
    <#
        The same refresh -Incremental runs after a merge, from the earliest
        record the import added: flattened rows and license events (both
        MERGE), sessions from that day, then the bi schema.
    #>
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlConnection] $Connection,
        [Parameter(Mandatory)][datetime] $SinceUtc,
        [string] $LicenseDatabase
    )
    $lic = if ($LicenseDatabase) { $LicenseDatabase } else { $null }
    if (Test-CsvProc $Connection 'audit.usp_FlattenCopilotAudit') {
        Write-Log ('Flattening records from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-CsvSql -Connection $Connection -TimeoutSeconds 0 -Parameters @{ '@s' = $SinceUtc } -Sql 'EXEC audit.usp_FlattenCopilotAudit @SinceUtc = @s;'
    }
    if (Test-CsvProc $Connection 'audit.usp_ShredLicenseEvents') {
        Write-Log ('Shredding license events from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-CsvSql -Connection $Connection -TimeoutSeconds 0 -Parameters @{ '@s' = $SinceUtc } -Sql 'EXEC audit.usp_ShredLicenseEvents @SinceUtc = @s;'
    }
    if (Test-CsvProc $Connection 'audit.usp_BuildCopilotSessions') {
        Write-Log ('Rebuilding sessions and the weekly rollup from {0:yyyy-MM-dd}...' -f $SinceUtc)
        Invoke-CsvSql -Connection $Connection -TimeoutSeconds 0 -Parameters @{ '@l' = $lic; '@s' = $SinceUtc } `
            -Sql 'EXEC audit.usp_BuildCopilotSessions @LicenseEventDatabase = @l, @SinceUtc = @s;'
    }
    if (Test-CsvProc $Connection 'bi.usp_RefreshBi') {
        Write-Log ('Refreshing the bi star schema from {0:yyyy-MM-dd HH:mm}Z...' -f $SinceUtc)
        Invoke-CsvSql -Connection $Connection -TimeoutSeconds 0 -Parameters @{ '@s' = $SinceUtc; '@l' = $lic } `
            -Sql 'EXEC bi.usp_RefreshBi @SinceUtc = @s, @LicenseEventDatabase = @l;'
    }
    if (Test-CsvProc $Connection 'dash.usp_RefreshDashboard') {
        Write-Log ('Refreshing the dashboard rollups from {0:yyyy-MM-dd}...' -f $SinceUtc)
        Invoke-CsvSql -Connection $Connection -TimeoutSeconds 0 -Parameters @{ '@s' = $SinceUtc } -Sql 'EXEC dash.usp_RefreshDashboard @SinceUtc = @s;'
    }
}

function Get-SessionLicenseDatabase {
    <#
        The license databases the refresh reads: -SessionLicenseDatabase
        when given, else the ones this database's last session build read, else
        its last bi refresh's. Left out, it was none, so in a database built with
        license history the session build refused ('different settings') after
        the merge had committed, and bi and the dashboard missed the import.
    #>
    param([System.Data.SqlClient.SqlConnection] $Connection)
    if ($script:Bound.ContainsKey('SessionLicenseDatabase')) { return $(if ($SessionLicenseDatabase) { $SessionLicenseDatabase } else { $null }) }
    $recorded = Invoke-CsvSql -Connection $Connection -As Scalar -Sql @'
DECLARE @l NVARCHAR(1000), @found BIT = 0;
IF OBJECT_ID(N'audit.CopilotSessionRun', N'U') IS NOT NULL
    SELECT TOP (1) @l = LicenseEventDatabase, @found = 1 FROM audit.CopilotSessionRun ORDER BY RunId DESC;
IF @found = 0 AND OBJECT_ID(N'bi.RefreshRun', N'U') IS NOT NULL
    SELECT TOP (1) @l = LicenseEventDatabase FROM bi.RefreshRun ORDER BY RunId DESC;
SELECT @l;
'@
    if ($null -eq $recorded -or $recorded -is [DBNull] -or [string]::IsNullOrWhiteSpace([string] $recorded)) { return $null }
    Write-Log "License history from $recorded, as this database's last build (-SessionLicenseDatabase to change it)."
    return [string] $recorded
}

function Invoke-OwedCsvRefresh {
    <#
        Brings the derived tables up to date with every merged import that owes
        a refresh, this run's included: from the earliest record any of
        them added, then marks them. A refresh that failed or was skipped was
        lost before - a second run skipped the merged files, and the nightly
        refreshes its own days only. Returns where it refreshed from, or $null.
    #>
    param([System.Data.SqlClient.SqlConnection] $Connection)
    $owed = (Invoke-CsvSql -Connection $Connection -As Table -Sql @'
SELECT MIN(EarliestAddedUtc) AS SinceUtc, MAX(ImportId) AS LastId, COUNT(*) AS Imports
FROM audit.CopilotCsvImport WHERE MergedUtc IS NOT NULL AND RowsAdded > 0 AND DerivedRefreshedUtc IS NULL;
'@).Rows[0]
    if ($owed.SinceUtc -is [DBNull]) { return $null }
    $since = [datetime]::SpecifyKind($owed.SinceUtc, [DateTimeKind]::Utc)
    Write-Log ('Bringing the derived tables up to date with {0} import(s) from {1:yyyy-MM-dd HH:mm}Z.' -f $owed.Imports, $since)
    Invoke-CsvDerivedRefresh -Connection $Connection -SinceUtc $since -LicenseDatabase (Get-SessionLicenseDatabase $Connection)
    Invoke-CsvSql -Connection $Connection -Parameters @{ '@i' = [int] $owed.LastId } -Sql @'
UPDATE audit.CopilotCsvImport SET DerivedRefreshedUtc = SYSUTCDATETIME()
WHERE MergedUtc IS NOT NULL AND DerivedRefreshedUtc IS NULL AND ImportId <= @i;
'@
    return $since
}

# ===========================================================================
# Main
# ===========================================================================

function Invoke-Main {
    if (-not $Path) { throw 'Specify -Path: the folder holding the exports.' }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Folder not found: $Path" }
    $from = if ($null -ne $CoveredFromUtc -and $CoveredFromUtc -ne [datetime]::MinValue) { ConvertTo-UtcDate $CoveredFromUtc } else { $null }
    $to   = if ($null -ne $CoveredThroughUtc -and $CoveredThroughUtc -ne [datetime]::MinValue) { ConvertTo-UtcDate $CoveredThroughUtc } else { $null }

    Import-CsvStageReader
    $connString = New-CsvImportConnectionString
    # One run at a time: held to the end of the import, and let go in the finally below.
    $runLock = Enter-RunLock $connString
    $conn = [System.Data.SqlClient.SqlConnection]::new($connString)
    $conn.add_InfoMessage({
        param($s, $e)
        foreach ($m in $e.Errors) {
            if ($m.Message -and $m.Message -notlike 'Warning: Null value is eliminated*') { Write-Log $m.Message }
        }
    })
    try {
        $conn.Open()
        Write-Log ('Connected to {0}/{1}.' -f $conn.DataSource, $conn.Database)
        Invoke-CsvSql -Connection $conn -Sql (Get-Content -LiteralPath (Join-Path $script:ToolRoot 'sql' 'Create-CopilotAuditSchema.sql') -Raw)
        Invoke-CsvSqlScript -Connection $conn -Path (Join-Path $script:ToolRoot 'sql' 'Create-CopilotCsvImport.sql')

        # An import that stopped before its merge is started again from scratch.
        Invoke-CsvSql -Connection $conn -Sql @'
DELETE r FROM audit.CopilotCsvReject AS r
JOIN audit.CopilotCsvFile AS f ON f.FileId = r.FileId
JOIN audit.CopilotCsvImport AS i ON i.ImportId = f.ImportId
WHERE i.MergedUtc IS NULL;
DELETE f FROM audit.CopilotCsvFile AS f
JOIN audit.CopilotCsvImport AS i ON i.ImportId = f.ImportId
WHERE i.MergedUtc IS NULL;
DELETE FROM audit.CopilotCsvImport WHERE MergedUtc IS NULL;
TRUNCATE TABLE audit.CopilotCsvStage;
'@

        # The operation set: the backfill's when there is one.
        $range = Invoke-CsvSql -Connection $conn -As Table -Sql @'
SELECT r.Operations, r.Source, r.CompletedUtc, r.RangeStartUtc, r.RangeEndUtc,
       CASE WHEN OBJECT_ID(N'audit.CopilotAudit', N'U') IS NULL THEN 0 ELSE 1 END AS HasFinal
FROM audit.CopilotBackfillRange AS r WHERE r.Id = 1;
'@
        $asked = if ($Operations) { Get-OperationSet $Operations } else { $null }
        if ($range.Rows.Count -gt 0) {
            $r = $range.Rows[0]
            $isCsv = ($r.Source -isnot [DBNull]) -and $r.Source -eq 'CSV'
            if (-not $isCsv -and (($r.CompletedUtc -is [DBNull]) -or -not $r.HasFinal)) {
                throw 'A Graph backfill has started in this database but is not finalized. Finish it and run -FinalizeOnly first: finalize rebuilds audit.CopilotAudit and would drop imported rows.'
            }
            if ($asked -and $asked -ne $r.Operations) { throw "This database holds '$($r.Operations)', not '$asked'. Leave out -Operations, or use another database." }
            $ops = [string] $r.Operations
            if ($to -and -not $isCsv) { Write-Log 'The Graph backfill recorded this database''s range; -CoveredThroughUtc is not applied to it.' -Level WARN }
        }
        else {
            $ops = if ($asked) { $asked } else { 'CopilotInteraction' }
            if (-not $to) {
                Write-Log 'No backfill range yet, and no -CoveredThroughUtc: the rows load, but -Incremental cannot continue from them until an import records the range the exports cover.' -Level WARN
            }
        }
        $opNames = [string[]] ($ops -split ',')
        Write-Log "Operations: $ops."

        # The files, less those already imported.
        $files = @(Get-ChildItem -LiteralPath $Path -Filter $Filter -File -Recurse:$Recurse | Sort-Object LastWriteTimeUtc, FullName)
        if ($files.Count -eq 0) { Write-Log "No files matching $Filter in $Path."; return }
        $done = @{}
        foreach ($row in (Invoke-CsvSql -Connection $conn -As Table -Sql @'
SELECT DISTINCT f.Sha256 FROM audit.CopilotCsvFile AS f
JOIN audit.CopilotCsvImport AS i ON i.ImportId = f.ImportId WHERE i.MergedUtc IS NOT NULL;
'@).Rows) { $done[[string] $row.Sha256] = $true }

        $todo = [System.Collections.Generic.List[object]]::new()
        $seen = @{}
        $already = 0; $same = 0
        foreach ($f in $files) {
            $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($done.ContainsKey($h)) { $already++; Write-Log "Already imported: $($f.Name)"; continue }
            if ($seen.ContainsKey($h)) { $same++; Write-Log "Same content as $($seen[$h]): $($f.Name) - skipped"; continue }
            $seen[$h] = $f.Name
            $todo.Add([pscustomobject]@{ File = $f; Sha256 = $h })
        }
        if ($todo.Count -eq 0) {
            Write-Log 'Nothing new to import.'
            # An earlier import's refresh that never finished is made up even so.
            if (-not $SkipPostProcessing) { [void] (Invoke-OwedCsvRefresh -Connection $conn) }
            return
        }
        Write-Log ('{0} file(s) to import; {1} already imported, {2} repeating another file.' -f $todo.Count, $already, $same)

        $importId = [int] (Invoke-CsvSql -Connection $conn -As Scalar -Parameters @{ '@p' = (Resolve-Path -LiteralPath $Path).Path; '@o' = $ops } -Sql @'
INSERT INTO audit.CopilotCsvImport (FolderPath, Operations) OUTPUT INSERTED.ImportId VALUES (@p, @o);
'@)

        foreach ($t in $todo) {
            try {
                $s = Import-CsvFile -Connection $conn -ImportId $importId -File $t.File -Sha256 $t.Sha256 -OperationNames $opNames -BatchRows $BatchRows
            }
            catch [System.IO.InvalidDataException] {
                throw "$($t.File.Name): $($_.Exception.Message) Move it out of the folder and run again."
            }
            Write-Log ('{0}: {1:N0} row(s) read, {2:N0} staged, {3:N0} other operation(s), {4:N0} rejected.' -f `
                       $t.File.Name, $s.RowsRead, $s.RowsStaged, $s.RowsOtherOperation, $s.RowsRejected) `
                      -Level $(if ($s.RowsRejected -gt 0) { 'WARN' } else { 'INFO' })
        }

        $m = (Invoke-CsvSql -Connection $conn -As Table -TimeoutSeconds 0 `
                -Parameters @{ '@i' = $importId; '@f' = $from; '@t' = $to } `
                -Sql 'EXEC audit.usp_MergeCopilotCsv @ImportId = @i, @CoveredFromUtc = @f, @CoveredToUtc = @t;').Rows[0]

        $rejected = [long] (Invoke-CsvSql -Connection $conn -As Scalar -Parameters @{ '@i' = $importId } `
                    -Sql 'SELECT ISNULL(RowsRejected, 0) FROM audit.CopilotCsvImport WHERE ImportId = @i;')
        if ($rejected -gt 0) {
            Write-Log ("{0:N0} row(s) were not loaded. See: SELECT f.FileName, r.* FROM audit.CopilotCsvReject r JOIN audit.CopilotCsvFile f ON f.FileId = r.FileId WHERE f.ImportId = {1};" -f $rejected, $importId) -Level WARN
        }
        if ($m.RangeEndUtc -isnot [DBNull]) {
            Write-Log ('The database''s range is now {0:yyyy-MM-dd HH:mm}Z -> {1:yyyy-MM-dd HH:mm}Z ({2}).' -f `
                       $m.RangeStartUtc, $m.RangeEndUtc, $(if ($m.RangeSource -is [DBNull]) { 'Graph backfill' } else { 'CSV' }))
            $end = [datetime]::SpecifyKind($m.RangeEndUtc, [DateTimeKind]::Utc)
            if (($m.RangeSource -isnot [DBNull]) -and $end -lt [datetime]::UtcNow.AddDays(-180)) {
                Write-Log ('The exports end {0:yyyy-MM-dd}, more than 180 days ago. The Graph API keeps 180 days, so -Incremental cannot fill the days between.' -f $end) -Level WARN
            }
        }

        if (-not $SkipPostProcessing) {
            $since = Invoke-OwedCsvRefresh -Connection $conn
            if ($since -and (Test-CsvProc $conn 'bi.usp_RefreshBi')) {
                Write-Log ('If a Power BI model refreshes incrementally and this import added records before its refresh window ({0:yyyy-MM-dd}), refresh the model fully once.' -f $since)
            }
        }
        elseif ([long] $m.RowsAdded -gt 0) {
            Write-Log 'Derived tables not updated (-SkipPostProcessing). The next -Incremental run, or this command without the switch, brings them up to date.'
        }
        Write-Log ("Import {0} complete: {1:N0} record(s) added." -f $importId, [long] $m.RowsAdded)
    }
    finally { $conn.Dispose(); if ($runLock) { $runLock.Dispose() } }
}

# Dot-sourcing (tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') { Invoke-Main }
