// ============================================================================
// CopilotAuditEngine.cs
//
// Pipelined backfill of Microsoft Purview audit records into SQL Server:
//
//   planner ──(succeeded queries)──► N download streams ──(row batches)──► M SqlBulkCopy writers
//      │                                   │                                      │
//      └─ submits audit searches ahead,    └─ adaptive concurrency (AIMD),        └─ parallel bulk load
//         sized to stay under the            Retry-After honored, global            into a heap (TABLOCK /
//         per-query record cap and the       pause on 429                            BU locks)
//         daily submission budget
//
// Every window's state lives in a SQL control table, so a crash or Ctrl+C
// loses nothing: re-run and it resumes. Rows are tagged (WindowId, Attempt);
// the finalize step keeps only each window's last successful attempt.
//
// Compiled at runtime by Invoke-CopilotAuditBackfill.ps1 via Add-Type.
// Requires PowerShell 7.2+ (.NET 6+). Uses only in-box assemblies plus the
// System.Data.SqlClient that ships with PowerShell 7.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CopilotAudit
{
    // ------------------------------------------------------------------------
    // Options
    // ------------------------------------------------------------------------

    public sealed class EngineOptions
    {
        public string   GraphBaseUrl            { get; set; } = "https://graph.microsoft.com";
        public string[] Operations              { get; set; } = new[] { "CopilotInteraction" };
        public string[] RecordTypes             { get; set; }
        public DateTime RangeStartUtc           { get; set; }
        public DateTime RangeEndUtc             { get; set; }

        // Window planning. Microsoft documents a per-query cap of at least
        // 1,000,000 records; past it the query still "succeeds" but stops
        // generating records. Target well under it.
        public double   InitialWindowHours      { get; set; } = 24;
        public double   MinWindowHours          { get; set; } = 0.25;
        public double   MaxWindowHours          { get; set; } = 168;
        public long     TargetRecordsPerWindow  { get; set; } = 600000;
        public long     AssumedRecordCap        { get; set; } = 1000000;

        // Service limits (documented floors: 200 submissions per rolling 24h and
        // 50 queued/running queries, both tenant-wide; the portal allows 10 per user).
        public int      MaxQueriesInFlight      { get; set; } = 8;
        public int      MaxSubmissionsPer24h    { get; set; } = 150;
        public int      StatusPollSeconds       { get; set; } = 15;
        public int      QueryTimeoutMinutes     { get; set; } = 240;
        public bool     UseBetaForStatus        { get; set; } = true;
        public int      MaxWindowAttempts       { get; set; } = 4;
        public double   TokenWaitMinutes        { get; set; } = 15;   // after a 401, how long to wait for a new sign-in

        // Download streams (adaptive).
        public int      PageSize                { get; set; } = 1000;
        public int      InitialDownloadStreams  { get; set; } = 4;
        public int      MaxDownloadStreams      { get; set; } = 8;
        public int      IncreaseAfterCleanPages { get; set; } = 40;
        public double   PageTimeoutMinutes      { get; set; } = 5;    // one page, its body read through (2.4.12)

        // SQL load.
        public int      BatchRows               { get; set; } = 2000;
        public int      SqlWriters              { get; set; } = 4;
        public int      BatchQueueCapacity      { get; set; } = 12;
    }

    // ------------------------------------------------------------------------
    // Window model
    // ------------------------------------------------------------------------

    public static class WindowStatus
    {
        public const string Pending     = "Pending";
        public const string Submitted   = "Submitted";
        public const string Ready       = "Ready";
        public const string Downloading = "Downloading";
        public const string Loaded      = "Loaded";
        public const string Split       = "Split";
        public const string Failed      = "Failed";
    }

    public sealed class AuditWindow
    {
        public long      WindowId       { get; set; }
        public long?     ParentWindowId { get; set; }
        public DateTime  StartUtc       { get; set; }
        public DateTime  EndUtc         { get; set; }
        public string    Status         { get; set; } = WindowStatus.Pending;
        public string    QueryId        { get; set; }
        public int       Attempts       { get; set; }   // download attempts; the row tag
        public int       Submissions    { get; set; }   // audit searches submitted for this window
        public long?     ApproxRecords  { get; set; }
        public long?     RowsLoaded     { get; set; }
        public DateTime? SubmittedUtc   { get; set; }
        public DateTime? CompletedUtc   { get; set; }
        public string    LastError      { get; set; }

        // runtime only
        internal bool?   CapExceeded;
        internal long    RecordCap = -1;

        public double Hours => (EndUtc - StartUtc).TotalHours;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "#{0} {1:yyyy-MM-dd HH:mm}->{2:yyyy-MM-dd HH:mm}",
                          WindowId, StartUtc, EndUtc);
    }

    // One download attempt of one window. Batches point at this, not at the
    // window, so late batches from an abandoned attempt can never complete a
    // newer attempt.
    public sealed class AttemptState
    {
        public readonly AuditWindow Window;
        public readonly int Attempt;
        internal int  Outstanding;   // batches queued but not yet committed
        internal int  DownloadDone;  // 1 when the last page has been queued
        internal int  Finished;      // 1 once marked Loaded (exactly once)
        internal long Rows;

        public AttemptState(AuditWindow w, int attempt) { Window = w; Attempt = attempt; }
    }

    public sealed class RowBatch
    {
        public AttemptState State;
        public List<object[]> Rows;
    }

    // ------------------------------------------------------------------------
    // Storage abstractions (SQL in production, in-memory in tests)
    // ------------------------------------------------------------------------

    public interface IWindowStore
    {
        Task<List<AuditWindow>> LoadAllAsync(CancellationToken ct);
        Task InsertAsync(AuditWindow w, CancellationToken ct);   // must set WindowId
        Task UpdateAsync(AuditWindow w, CancellationToken ct);

        // The parent marked Split and both halves inserted, all or none (2.4.12; must set both WindowIds). Three
        // separate writes left the parent Split with a half missing when a run stopped between them: a hole nothing
        // planned again, which merge and finalize then refused for good.
        Task SplitAsync(AuditWindow parent, AuditWindow older, AuditWindow newer, CancellationToken ct);

        // The window table for this run alone until disposed, or WindowTableInUseException when another run holds it
        // (2.4.12). Nothing claimed a window, so two runs on one table planned the same windows from the same cursor
        // and wrote over each other's rows.
        Task<IDisposable> ClaimAsync(CancellationToken ct);
    }

    /// <summary>Another run is loading the same window table; this one stops before touching it.</summary>
    public sealed class WindowTableInUseException : Exception
    {
        public WindowTableInUseException(string message) : base(message) { }
    }

    public interface IBatchSink
    {
        Task WriteAsync(RowBatch batch, CancellationToken ct);
    }

    /// <summary>Thrown by a sink for failures worth retrying (timeouts, deadlocks, dropped connections).</summary>
    public class TransientException : Exception
    {
        public TransientException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>A Graph error that retrying cannot fix; stops the pipeline.</summary>
    public sealed class FatalGraphException : Exception
    {
        public FatalGraphException(Exception inner) : base(inner.Message, inner) { }
    }

    public sealed class GraphRequestException : Exception
    {
        public int StatusCode { get; }
        public GraphRequestException(int status, string body, string url)
            : base(string.Format(CultureInfo.InvariantCulture, "Graph returned {0} for {1}: {2}",
                                 status, url, Truncate(body, 600)))
        { StatusCode = status; }

        static string Truncate(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n) + "...");
    }

    // ------------------------------------------------------------------------
    // Staging table row layout
    // ------------------------------------------------------------------------

    public static class RowSchema
    {
        public static readonly string[] Columns =
        {
            "WindowId", "Attempt", "RecordId", "CreatedDateTime", "RecordType", "Operation",
            "OrganizationId", "UserType", "UserId", "UserPrincipalName", "Service", "ObjectId",
            "ClientIp", "AdministrativeUnits", "AuditDataGz"
        };

        public static readonly Type[] Types =
        {
            typeof(long), typeof(int), typeof(string), typeof(DateTime), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(byte[])
        };

        // Must match the DDL. -1 = MAX, 0 = not a string. AuditDataGz is gzip bytes (VARBINARY(MAX)).
        public static readonly int[] MaxLength =
        {
            0, 0, 64, 0, 128, 128,
            64, 64, 512, 512, 128, 4000,
            128, 4000, -1
        };

        // JSON property on the Graph auditLogRecord for each scalar column.
        internal static readonly (int col, string prop)[] Scalars =
        {
            (4, "auditLogRecordType"), (5, "operation"), (6, "organizationId"), (7, "userType"),
            (8, "userId"), (9, "userPrincipalName"), (10, "service"), (11, "objectId"), (12, "clientIp")
        };
    }

    /// <summary>IDataReader over an in-memory batch - the SqlBulkCopy source.</summary>
    public sealed class RowBatchReader : IDataReader
    {
        static readonly DataTable Schema = BuildSchema();
        readonly List<object[]> _rows;
        int _i = -1;

        public RowBatchReader(List<object[]> rows) { _rows = rows; }

        static DataTable BuildSchema()
        {
            var t = new DataTable("SchemaTable");
            t.Columns.Add("ColumnName", typeof(string));
            t.Columns.Add("ColumnOrdinal", typeof(int));
            t.Columns.Add("ColumnSize", typeof(int));
            t.Columns.Add("DataType", typeof(Type));
            t.Columns.Add("AllowDBNull", typeof(bool));
            for (int i = 0; i < RowSchema.Columns.Length; i++)
            {
                t.Rows.Add(RowSchema.Columns[i], i, RowSchema.MaxLength[i] > 0 ? RowSchema.MaxLength[i] : -1,
                           RowSchema.Types[i], i > 3);
            }
            return t;
        }

        public int FieldCount => RowSchema.Columns.Length;
        public bool Read() => ++_i < _rows.Count;
        public object GetValue(int i) => _rows[_i][i] ?? DBNull.Value;
        public bool IsDBNull(int i) => _rows[_i][i] == null || _rows[_i][i] is DBNull;
        public string GetName(int i) => RowSchema.Columns[i];
        public Type GetFieldType(int i) => RowSchema.Types[i];
        public string GetDataTypeName(int i) => RowSchema.Types[i].Name;
        public int GetOrdinal(string name)
        {
            for (int i = 0; i < RowSchema.Columns.Length; i++)
                if (string.Equals(RowSchema.Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new IndexOutOfRangeException(name);
        }
        public int GetValues(object[] values)
        {
            int n = Math.Min(values.Length, FieldCount);
            for (int i = 0; i < n; i++) values[i] = GetValue(i);
            return n;
        }
        public object this[int i] => GetValue(i);
        public object this[string name] => GetValue(GetOrdinal(name));
        public DataTable GetSchemaTable() => Schema.Copy();
        public int Depth => 0;
        public bool IsClosed => false;
        public int RecordsAffected => -1;
        public bool NextResult() => false;
        public void Close() { }
        public void Dispose() { }

        public string GetString(int i) => (string)GetValue(i);
        public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
        public int GetInt32(int i) => Convert.ToInt32(GetValue(i), CultureInfo.InvariantCulture);
        public long GetInt64(int i) => Convert.ToInt64(GetValue(i), CultureInfo.InvariantCulture);
        public bool GetBoolean(int i) => Convert.ToBoolean(GetValue(i), CultureInfo.InvariantCulture);
        public byte GetByte(int i) => Convert.ToByte(GetValue(i), CultureInfo.InvariantCulture);
        public char GetChar(int i) => Convert.ToChar(GetValue(i), CultureInfo.InvariantCulture);
        public short GetInt16(int i) => Convert.ToInt16(GetValue(i), CultureInfo.InvariantCulture);
        public float GetFloat(int i) => Convert.ToSingle(GetValue(i), CultureInfo.InvariantCulture);
        public double GetDouble(int i) => Convert.ToDouble(GetValue(i), CultureInfo.InvariantCulture);
        public decimal GetDecimal(int i) => Convert.ToDecimal(GetValue(i), CultureInfo.InvariantCulture);
        public Guid GetGuid(int i) => Guid.Parse(GetString(i));
        public long GetBytes(int i, long o, byte[] b, int bo, int l) => throw new NotSupportedException();
        public long GetChars(int i, long o, char[] b, int bo, int l) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------------
    // SQL implementations
    // ------------------------------------------------------------------------

    public static class SqlTransient
    {
        static readonly HashSet<int> Numbers = new HashSet<int>
        {
            -2, 20, 64, 121, 233, 1205, 1222, 10053, 10054, 10060, 10928, 10929,
            40143, 40197, 40501, 40613, 49918, 49919, 49920
        };

        public static bool IsTransient(Exception ex)
        {
            if (ex is SqlException s)
            {
                foreach (SqlError e in s.Errors) if (Numbers.Contains(e.Number)) return true;
                return Numbers.Contains(s.Number);
            }
            return ex is TimeoutException || ex is System.IO.IOException;
        }
    }

    public sealed class SqlBatchSink : IBatchSink
    {
        readonly string _cs;
        readonly string _table;

        public SqlBatchSink(string connectionString, string stageTable)
        {
            _cs = connectionString;
            _table = stageTable;
        }

        public async Task WriteAsync(RowBatch batch, CancellationToken ct)
        {
            try
            {
                using (var conn = new SqlConnection(_cs))
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    // TableLock on a heap takes a bulk-update (BU) lock, which lets
                    // several writers load the same table concurrently with minimal logging.
                    using (var bcp = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null))
                    {
                        bcp.DestinationTableName = _table;
                        bcp.BatchSize = 0;
                        bcp.BulkCopyTimeout = 600;
                        bcp.EnableStreaming = true;
                        foreach (var c in RowSchema.Columns) bcp.ColumnMappings.Add(c, c);
                        using (var reader = new RowBatchReader(batch.Rows))
                        {
                            await bcp.WriteToServerAsync(reader, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) && SqlTransient.IsTransient(ex))
            {
                throw new TransientException("SQL bulk copy: " + ex.Message, ex);
            }
        }
    }

    public sealed class SqlWindowStore : IWindowStore
    {
        readonly string _cs;
        readonly string _table;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public SqlWindowStore(string connectionString, string windowTable)
        {
            _cs = connectionString;
            _table = windowTable;
        }

        async Task<T> WithRetry<T>(Func<SqlConnection, Task<T>> op, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        using (var conn = new SqlConnection(_cs))
                        {
                            await conn.OpenAsync(ct).ConfigureAwait(false);
                            return await op(conn).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (attempt < 5 && SqlTransient.IsTransient(ex) && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                    }
                }
            }
            finally { _gate.Release(); }
        }

        static object Db(object v) => v ?? DBNull.Value;

        static void AddParams(SqlCommand cmd, AuditWindow w)
        {
            cmd.Parameters.Add("@ParentWindowId", SqlDbType.BigInt).Value = Db(w.ParentWindowId);
            cmd.Parameters.Add("@StartUtc", SqlDbType.DateTime2).Value = w.StartUtc;
            cmd.Parameters.Add("@EndUtc", SqlDbType.DateTime2).Value = w.EndUtc;
            cmd.Parameters.Add("@Status", SqlDbType.VarChar, 16).Value = w.Status;
            cmd.Parameters.Add("@QueryId", SqlDbType.VarChar, 64).Value = Db(w.QueryId);
            cmd.Parameters.Add("@Attempts", SqlDbType.Int).Value = w.Attempts;
            cmd.Parameters.Add("@Submissions", SqlDbType.Int).Value = w.Submissions;
            cmd.Parameters.Add("@ApproxRecords", SqlDbType.BigInt).Value = Db(w.ApproxRecords);
            cmd.Parameters.Add("@RowsLoaded", SqlDbType.BigInt).Value = Db(w.RowsLoaded);
            cmd.Parameters.Add("@SubmittedUtc", SqlDbType.DateTime2).Value = Db(w.SubmittedUtc);
            cmd.Parameters.Add("@CompletedUtc", SqlDbType.DateTime2).Value = Db(w.CompletedUtc);
            var err = w.LastError;
            if (err != null && err.Length > 2000) err = err.Substring(0, 2000);
            cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value = Db(err);
        }

        public Task<List<AuditWindow>> LoadAllAsync(CancellationToken ct) => WithRetry(async conn =>
        {
            var list = new List<AuditWindow>();
            using (var cmd = new SqlCommand(
                "SELECT WindowId, ParentWindowId, StartUtc, EndUtc, Status, QueryId, Attempts, Submissions, " +
                "ApproxRecords, RowsLoaded, SubmittedUtc, CompletedUtc, LastError FROM " + _table, conn))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add(new AuditWindow
                    {
                        WindowId       = r.GetInt64(0),
                        ParentWindowId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                        StartUtc       = DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc),
                        EndUtc         = DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc),
                        Status         = r.GetString(4),
                        QueryId        = r.IsDBNull(5) ? null : r.GetString(5),
                        Attempts       = r.GetInt32(6),
                        Submissions    = r.GetInt32(7),
                        ApproxRecords  = r.IsDBNull(8) ? (long?)null : r.GetInt64(8),
                        RowsLoaded     = r.IsDBNull(9) ? (long?)null : r.GetInt64(9),
                        SubmittedUtc   = r.IsDBNull(10) ? (DateTime?)null : DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc),
                        CompletedUtc   = r.IsDBNull(11) ? (DateTime?)null : DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc),
                        LastError      = r.IsDBNull(12) ? null : r.GetString(12),
                    });
                }
            }
            return list;
        }, ct);

        SqlCommand InsertCommand(SqlConnection conn, SqlTransaction tx, AuditWindow w)
        {
            var cmd = new SqlCommand(
                "INSERT INTO " + _table + " (ParentWindowId, StartUtc, EndUtc, Status, QueryId, Attempts, Submissions, " +
                "ApproxRecords, RowsLoaded, SubmittedUtc, CompletedUtc, LastError) OUTPUT INSERTED.WindowId VALUES " +
                "(@ParentWindowId, @StartUtc, @EndUtc, @Status, @QueryId, @Attempts, @Submissions, @ApproxRecords, " +
                "@RowsLoaded, @SubmittedUtc, @CompletedUtc, @LastError)", conn, tx);
            AddParams(cmd, w);
            return cmd;
        }

        SqlCommand UpdateCommand(SqlConnection conn, SqlTransaction tx, AuditWindow w)
        {
            var cmd = new SqlCommand(
                "UPDATE " + _table + " SET Status=@Status, QueryId=@QueryId, Attempts=@Attempts, Submissions=@Submissions, " +
                "ApproxRecords=@ApproxRecords, RowsLoaded=@RowsLoaded, SubmittedUtc=@SubmittedUtc, " +
                "CompletedUtc=@CompletedUtc, LastError=@LastError, UpdatedUtc=SYSUTCDATETIME() WHERE WindowId=@WindowId", conn, tx);
            AddParams(cmd, w);
            cmd.Parameters.Add("@WindowId", SqlDbType.BigInt).Value = w.WindowId;
            return cmd;
        }

        public Task InsertAsync(AuditWindow w, CancellationToken ct) => WithRetry(async conn =>
        {
            using (var cmd = InsertCommand(conn, null, w))
            {
                w.WindowId = (long)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
            return true;
        }, ct);

        public Task UpdateAsync(AuditWindow w, CancellationToken ct) => WithRetry(async conn =>
        {
            using (var cmd = UpdateCommand(conn, null, w))
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return true;
        }, ct);

        public Task SplitAsync(AuditWindow parent, AuditWindow older, AuditWindow newer, CancellationToken ct) => WithRetry(async conn =>
        {
            // Disposed without the commit (a stop, an error), the transaction rolls back: the parent stays as it was,
            // and the next run splits it again.
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = UpdateCommand(conn, tx, parent))
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                using (var cmd = InsertCommand(conn, tx, older))
                    older.WindowId = (long)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                using (var cmd = InsertCommand(conn, tx, newer))
                    newer.WindowId = (long)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                tx.Commit();
            }
            return true;
        }, ct);

        public async Task<IDisposable> ClaimAsync(CancellationToken ct)
        {
            // An application lock held by a connection of its own, not pooled: the lock belongs to the session, and
            // closing a pooled connection keeps the session, lock and all. A run that dies drops the connection, and
            // the lock goes with it.
            var b = new SqlConnectionStringBuilder(_cs) { Pooling = false };
            var conn = new SqlConnection(b.ConnectionString);
            try
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(
                    "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;", conn))
                {
                    cmd.Parameters.Add("@res", SqlDbType.NVarChar, 255).Value = "CopilotAudit.Windows:" + _table;
                    var r = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                    if (r < 0)
                        throw new WindowTableInUseException("Another run is loading " + _table + " in this database now: two at once " +
                            "would plan the same windows twice. Let it finish, or stop it, then run this again.");
                }
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }
    }

    // ------------------------------------------------------------------------
    // Adaptive concurrency limiter (AIMD)
    // ------------------------------------------------------------------------

    /// <summary>
    /// Caps concurrent page requests. Additive increase after a run of clean
    /// pages; multiplicative decrease plus a global pause on any 429. Permits
    /// are withdrawn by parking waiters on the semaphore, so no request is
    /// ever cut off mid-flight.
    /// </summary>
    public sealed class AdaptiveLimiter
    {
        readonly SemaphoreSlim _sem;
        readonly int _max;
        readonly object _lock = new object();
        int _limit;
        int _clean;
        int _consecutiveThrottles;
        long _pauseUntilTicks;

        public AdaptiveLimiter(int initial, int max)
        {
            _max = Math.Max(1, max);
            _limit = Math.Max(1, Math.Min(initial, _max));
            _sem = new SemaphoreSlim(_limit, _max);
        }

        public int Limit { get { lock (_lock) return _limit; } }

        public DateTime PausedUntilUtc => new DateTime(Interlocked.Read(ref _pauseUntilTicks), DateTimeKind.Utc);

        public async Task WaitAsync(CancellationToken ct)
        {
            await _sem.WaitAsync(ct).ConfigureAwait(false);
            var wait = PausedUntilUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch { _sem.Release(); throw; }
            }
        }

        public void Release() => _sem.Release();

        public void OnSuccess(int increaseAfter)
        {
            lock (_lock)
            {
                _consecutiveThrottles = 0;
                if (++_clean < increaseAfter) return;
                _clean = 0;
                if (_limit < _max) { _limit++; _sem.Release(); }
            }
        }

        /// <returns>The pause actually applied.</returns>
        public TimeSpan OnThrottled(TimeSpan? retryAfter)
        {
            TimeSpan pause;
            int reduceBy;
            lock (_lock)
            {
                _clean = 0;
                _consecutiveThrottles++;
                // Honor Retry-After as Microsoft asks; escalate gently only if 429s
                // keep coming. Without the header, exponential backoff.
                pause = retryAfter.HasValue
                    ? retryAfter.Value + TimeSpan.FromSeconds(Math.Min(60, 5 * (_consecutiveThrottles - 1)))
                    : TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, _consecutiveThrottles - 1)));

                int target = Math.Max(1, _limit / 2);
                reduceBy = _limit - target;
                _limit = target;
            }

            long until = (DateTime.UtcNow + pause).Ticks;
            long current;
            do { current = Interlocked.Read(ref _pauseUntilTicks); }
            while (until > current && Interlocked.CompareExchange(ref _pauseUntilTicks, until, current) != current);

            // Park permits permanently (until OnSuccess releases new ones).
            for (int i = 0; i < reduceBy; i++) _ = _sem.WaitAsync();
            return pause;
        }
    }

    // ------------------------------------------------------------------------
    // Status snapshot for the orchestrator
    // ------------------------------------------------------------------------

    public sealed class EngineStatus
    {
        public string   Phase { get; set; }
        public int      WindowsKnown { get; set; }
        public int      WindowsLoaded { get; set; }
        public int      WindowsSplit { get; set; }
        public int      WindowsFailed { get; set; }
        public int      QueriesInFlight { get; set; }
        public int      ReadyToDownload { get; set; }
        public int      Downloading { get; set; }
        public int      BatchesQueued { get; set; }
        public int      DownloadStreams { get; set; }
        public long     RowsDownloaded { get; set; }
        public long     RowsWritten { get; set; }
        public long     Pages { get; set; }
        public long     PageThrottles { get; set; }
        public long     SubmitThrottles { get; set; }
        public long     Submissions { get; set; }
        public int      SubmissionsLast24h { get; set; }
        public long     TruncatedValues { get; set; }
        public long     SkippedRecords { get; set; }
        public double   HoursLoaded { get; set; }
        public double   HoursTotal { get; set; }
        public DateTime? OldestRecordUtc { get; set; }
        public DateTime? NewestRecordUtc { get; set; }
        public DateTime  PausedUntilUtc { get; set; }
        public DateTime  SubmitBlockedUntilUtc { get; set; }
        public long      AuditBytesRaw { get; set; }        // UTF-16 JSON before compression
        public long      AuditBytesCompressed { get; set; } // what actually went to SQL
    }

    // ------------------------------------------------------------------------
    // The engine
    // ------------------------------------------------------------------------

    public sealed class BackfillEngine : IDisposable
    {
        readonly EngineOptions _o;
        readonly IWindowStore _store;
        readonly IBatchSink _sink;
        readonly HttpClient _http;
        readonly bool _ownsHttp;
        readonly AdaptiveLimiter _limiter;
        readonly Channel<AuditWindow> _ready;
        readonly Channel<RowBatch> _batches;
        readonly ConcurrentQueue<string> _log = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<AuditWindow> _resplit = new ConcurrentQueue<AuditWindow>();
        readonly ConcurrentDictionary<long, AuditWindow> _windows = new ConcurrentDictionary<long, AuditWindow>();
        readonly object _submitLock = new object();
        readonly Queue<DateTime> _submitTimes = new Queue<DateTime>();

        CancellationTokenSource _cts;
        Exception _fault;

        // token
        volatile string _token;
        long _tokenVersion;
        int _tokenRefreshRequested;
        long _tokenExpiresTicks;

        // planner state
        DateTime _cursor;
        double _density = -1;          // records/hour, most recent observation
        double _lastPlannedHours;
        bool _useTop = true;
        bool _useBeta;
        int _queriesInFlight;
        int _windowsInDownload;
        int _downloadingNow;
        long _submitBlockedUntilTicks;
        volatile string _phase = "Starting";

        // counters
        long _rowsDownloaded, _rowsWritten, _pages, _pageThrottles, _submitThrottles, _submissions;
        long _truncated, _skipped, _oldestTicks = long.MaxValue, _newestTicks = long.MinValue;
        long _auditBytesRaw, _auditBytesPacked;

        public BackfillEngine(EngineOptions options, IWindowStore store, IBatchSink sink, HttpMessageHandler handler = null)
        {
            _o = options ?? throw new ArgumentNullException(nameof(options));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            if (_o.RangeEndUtc <= _o.RangeStartUtc) throw new ArgumentException("RangeEndUtc must be after RangeStartUtc.");

            _useBeta = _o.UseBetaForStatus;
            _limiter = new AdaptiveLimiter(_o.InitialDownloadStreams, _o.MaxDownloadStreams);
            _ready = Channel.CreateUnbounded<AuditWindow>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
            _batches = Channel.CreateBounded<RowBatch>(new BoundedChannelOptions(Math.Max(1, _o.BatchQueueCapacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

            if (handler == null)
            {
                var h = new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    MaxConnectionsPerServer = _o.MaxDownloadStreams + 4,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    UseProxy = true,
                    DefaultProxyCredentials = CredentialCache.DefaultCredentials,
                };
                handler = h;
            }
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CopilotAuditBackfill/2.0");
            _ownsHttp = true;
        }

        // ---------------- public surface ------------------------------------

        public DateTime TokenExpiresUtc => new DateTime(Interlocked.Read(ref _tokenExpiresTicks), DateTimeKind.Utc);
        public bool TokenRefreshRequested => Volatile.Read(ref _tokenRefreshRequested) == 1;
        public Exception Fault => _fault;

        public void SetAccessToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Empty access token.");
            _token = token;
            var exp = JwtExpiry(token) ?? DateTime.UtcNow.AddMinutes(50);
            Interlocked.Exchange(ref _tokenExpiresTicks, exp.Ticks);
            Interlocked.Increment(ref _tokenVersion);
            Interlocked.Exchange(ref _tokenRefreshRequested, 0);
        }

        public string[] DrainLog()
        {
            var list = new List<string>();
            while (_log.TryDequeue(out var s)) list.Add(s);
            return list.ToArray();
        }

        public void RequestStop() => _cts?.Cancel();

        public Task Start()
        {
            if (_token == null) throw new InvalidOperationException("Call SetAccessToken before Start.");
            _cts = new CancellationTokenSource();
            return Task.Run(() => RunAsync(_cts.Token));
        }

        public EngineStatus GetStatus()
        {
            var all = _windows.Values.ToArray();
            long oldest = Interlocked.Read(ref _oldestTicks), newest = Interlocked.Read(ref _newestTicks);
            int last24;
            lock (_submitLock) { PruneSubmitTimes(); last24 = _submitTimes.Count; }
            return new EngineStatus
            {
                Phase            = _phase,
                WindowsKnown     = all.Count(w => w.Status != WindowStatus.Split),
                WindowsLoaded    = all.Count(w => w.Status == WindowStatus.Loaded),
                WindowsSplit     = all.Count(w => w.Status == WindowStatus.Split),
                WindowsFailed    = all.Count(w => w.Status == WindowStatus.Failed),
                QueriesInFlight  = Volatile.Read(ref _queriesInFlight),
                ReadyToDownload  = _ready.Reader.Count,
                Downloading      = Volatile.Read(ref _downloadingNow),
                BatchesQueued    = _batches.Reader.Count,
                DownloadStreams  = _limiter.Limit,
                RowsDownloaded   = Interlocked.Read(ref _rowsDownloaded),
                RowsWritten      = Interlocked.Read(ref _rowsWritten),
                Pages            = Interlocked.Read(ref _pages),
                PageThrottles    = Interlocked.Read(ref _pageThrottles),
                SubmitThrottles  = Interlocked.Read(ref _submitThrottles),
                Submissions      = Interlocked.Read(ref _submissions),
                SubmissionsLast24h = last24,
                TruncatedValues  = Interlocked.Read(ref _truncated),
                SkippedRecords   = Interlocked.Read(ref _skipped),
                HoursLoaded      = all.Where(w => w.Status == WindowStatus.Loaded).Sum(w => w.Hours),
                HoursTotal       = (_o.RangeEndUtc - _o.RangeStartUtc).TotalHours,
                OldestRecordUtc  = oldest == long.MaxValue ? (DateTime?)null : new DateTime(oldest, DateTimeKind.Utc),
                NewestRecordUtc  = newest == long.MinValue ? (DateTime?)null : new DateTime(newest, DateTimeKind.Utc),
                PausedUntilUtc   = _limiter.PausedUntilUtc,
                SubmitBlockedUntilUtc = new DateTime(Interlocked.Read(ref _submitBlockedUntilTicks), DateTimeKind.Utc),
                AuditBytesRaw    = Interlocked.Read(ref _auditBytesRaw),
                AuditBytesCompressed = Interlocked.Read(ref _auditBytesPacked),
            };
        }

        public void Dispose()
        {
            _cts?.Cancel();
            if (_ownsHttp) _http.Dispose();
        }

        // ---------------- orchestration --------------------------------------

        async Task RunAsync(CancellationToken ct)
        {
            // One run at a time on a window table, held to the end (2.4.12). A second run beside this one (-History
            // started again from another console, a run by hand during the nightly) planned the same windows from the
            // same cursor and re-marked this run's windows as its own, leaving overlaps that finalize refuses for good,
            // or a load marked downloaded whose windows never all finished.
            using (await _store.ClaimAsync(ct).ConfigureAwait(false))
            {
                var planner = Guard("planner", () => PlannerLoopAsync(ct));
                var downloaders = Enumerable.Range(0, _o.MaxDownloadStreams)
                                            .Select(i => Guard("download-" + i, () => DownloadLoopAsync(ct))).ToArray();
                var writers = Enumerable.Range(0, Math.Max(1, _o.SqlWriters))
                                        .Select(i => Guard("writer-" + i, () => WriterLoopAsync(ct))).ToArray();

                await planner.ConfigureAwait(false);
                await Task.WhenAll(downloaders).ConfigureAwait(false);
                _batches.Writer.TryComplete();
                await Task.WhenAll(writers).ConfigureAwait(false);
            }

            if (_fault != null) throw new AggregateException("Backfill stopped: " + _fault.Message, _fault);
            ct.ThrowIfCancellationRequested();
            _phase = "Complete";
            Log("INFO", "Pipeline complete.");
        }

        Task Guard(string name, Func<Task> body) => Task.Run(async () =>
        {
            try { await body().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (Interlocked.CompareExchange(ref _fault, ex, null) == null)
                    Log("ERROR", name + " failed: " + ex.GetBaseException().Message + " - stopping the pipeline.");
                _cts.Cancel();
                _ready.Writer.TryComplete();
                _batches.Writer.TryComplete();
            }
        });

        void Log(string level, string message)
        {
            _log.Enqueue(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss}Z [{1,-5}] {2}",
                                       DateTime.UtcNow, level, message));
        }

        // ---------------- planner ---------------------------------------------

        async Task PlannerLoopAsync(CancellationToken ct)
        {
            _phase = "Planning";
            var pending = new LinkedList<AuditWindow>();
            var inFlight = new List<AuditWindow>();

            // Resume from the control table.
            var existing = await _store.LoadAllAsync(ct).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                var originalEnd = existing.Max(x => x.EndUtc);
                if (originalEnd != _o.RangeEndUtc)
                {
                    Log("WARN", string.Format(CultureInfo.InvariantCulture,
                        "The control table was started with an end of {0:yyyy-MM-dd HH:mm}Z; keeping that (requested {1:yyyy-MM-dd HH:mm}Z). " +
                        "To cover a later period, use a separate table or run the ongoing feed.", originalEnd, _o.RangeEndUtc));
                    _o.RangeEndUtc = originalEnd;
                }
            }
            // A split that stopped part way before 2.4.12 left its parent Split with a half missing. Resuming skips Split
            // windows and plans only below the lowest window on record, so that hole was never planned again, and the
            // run ended "complete" for merge or finalize to refuse every time after. Its missing halves are planned now.
            foreach (var p in existing.Where(x => x.Status == WindowStatus.Split).ToList())
            {
                foreach (var half in MissingHalves(p, existing))
                {
                    await _store.InsertAsync(half, ct).ConfigureAwait(false);
                    existing.Add(half);
                    Log("WARN", string.Format(CultureInfo.InvariantCulture,
                        "{0}: its split stopped part way; {1} was never planned - planning it now.", p, half));
                }
            }
            _cursor = _o.RangeEndUtc;
            foreach (var w in existing.OrderByDescending(x => x.StartUtc))
            {
                _windows[w.WindowId] = w;
                if (w.StartUtc < _cursor) _cursor = w.StartUtc;
                if (w.SubmittedUtc.HasValue) lock (_submitLock) _submitTimes.Enqueue(w.SubmittedUtc.Value);

                switch (w.Status)
                {
                    case WindowStatus.Loaded:
                    case WindowStatus.Split:
                        break;
                    case WindowStatus.Submitted:
                    case WindowStatus.Ready:
                    case WindowStatus.Downloading:
                        if (w.QueryId != null) { w.Status = WindowStatus.Submitted; inFlight.Add(w); }
                        else { w.Status = WindowStatus.Pending; pending.AddLast(w); }
                        break;
                    default:   // Pending, Failed: try again
                        w.Status = WindowStatus.Pending;
                        w.QueryId = null;
                        pending.AddLast(w);
                        break;
                }
            }
            lock (_submitLock)
            {
                var sorted = _submitTimes.OrderBy(t => t).ToList();
                _submitTimes.Clear();
                foreach (var t in sorted) _submitTimes.Enqueue(t);
            }

            // Seed density from the most recent completed windows, if resuming.
            var lastLoaded = existing.Where(w => w.Status == WindowStatus.Loaded && (w.ApproxRecords ?? w.RowsLoaded) != null)
                                     .OrderBy(w => w.StartUtc).FirstOrDefault();
            if (lastLoaded != null && lastLoaded.Hours > 0)
                _density = (double)(lastLoaded.ApproxRecords ?? lastLoaded.RowsLoaded ?? 0) / lastLoaded.Hours;
            _lastPlannedHours = _o.InitialWindowHours;
            Volatile.Write(ref _queriesInFlight, inFlight.Count);

            if (existing.Count > 0)
            {
                Log("INFO", string.Format(CultureInfo.InvariantCulture,
                    "Resuming: {0} windows on record ({1} loaded), {2} to re-poll, {3} to (re)submit; planning continues below {4:yyyy-MM-dd HH:mm}.",
                    existing.Count, existing.Count(w => w.Status == WindowStatus.Loaded), inFlight.Count, pending.Count, _cursor));
            }

            _phase = "Running";
            bool announcedBudget = false;

            while (!ct.IsCancellationRequested)
            {
                // a) Windows handed back by downloaders (row count hit the cap
                //    and the service did not say whether it truncated).
                while (_resplit.TryDequeue(out var r))
                    await SplitAsync(r, pending, "download returned ~cap rows; re-querying as two halves", ct).ConfigureAwait(false);

                // b) Poll queries in flight.
                foreach (var w in inFlight.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    QueryState st;
                    try { st = await GetQueryStateAsync(w.QueryId, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is FatalGraphException))
                    {
                        Log("WARN", "Status check failed for " + w + ": " + ex.GetBaseException().Message);
                        continue;
                    }

                    if (st.NotFound)
                    {
                        inFlight.Remove(w);
                        Log("WARN", w + ": query " + w.QueryId + " no longer exists - resubmitting.");
                        w.QueryId = null; w.Status = WindowStatus.Pending;
                        await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                        pending.AddFirst(w);
                        continue;
                    }

                    var status = (st.Status ?? "").ToLowerInvariant();
                    if (status == "succeeded")
                    {
                        inFlight.Remove(w);
                        w.ApproxRecords = st.ApproxRecords;
                        w.CapExceeded = st.CapExceeded;
                        if (st.RecordCap.HasValue && st.RecordCap.Value > 0) w.RecordCap = st.RecordCap.Value;

                        if (st.CapExceeded == true)
                        {
                            long cap = w.RecordCap > 0 ? w.RecordCap : _o.AssumedRecordCap;
                            ObserveDensity(Math.Max(cap, st.ApproxRecords ?? 0), w.Hours);
                            if (w.Hours > _o.MinWindowHours * 2)
                            {
                                await SplitAsync(w, pending, string.Format(CultureInfo.InvariantCulture,
                                    "service reports the {0:N0}-record cap was exceeded", cap), ct).ConfigureAwait(false);
                                continue;
                            }
                            Log("WARN", w + ": over the record cap even at the minimum window size - loading what the service returned.");
                        }
                        else if (st.ApproxRecords.HasValue)
                        {
                            ObserveDensity(st.ApproxRecords.Value, w.Hours);
                        }

                        w.Status = WindowStatus.Ready;
                        await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _windowsInDownload);
                        _ready.Writer.TryWrite(w);
                        Log("INFO", string.Format(CultureInfo.InvariantCulture, "{0}: search complete{1} - queued for download.",
                            w, st.ApproxRecords.HasValue ? string.Format(CultureInfo.InvariantCulture, ", ~{0:N0} records", st.ApproxRecords.Value) : ""));
                    }
                    else if (status == "failed" || status == "cancelled")
                    {
                        inFlight.Remove(w);
                        w.LastError = "audit search " + status;
                        await RetryOrSplitAsync(w, pending, ct).ConfigureAwait(false);
                    }
                    else if (w.SubmittedUtc.HasValue && DateTime.UtcNow - w.SubmittedUtc.Value > TimeSpan.FromMinutes(_o.QueryTimeoutMinutes))
                    {
                        inFlight.Remove(w);
                        w.LastError = "audit search still '" + status + "' after " + _o.QueryTimeoutMinutes + " minutes";
                        await RetryOrSplitAsync(w, pending, ct).ConfigureAwait(false);
                    }
                }
                Volatile.Write(ref _queriesInFlight, inFlight.Count);

                // c) Submit more, within the in-flight cap and the rolling budget.
                while (inFlight.Count < _o.MaxQueriesInFlight && !ct.IsCancellationRequested)
                {
                    var blocked = new DateTime(Interlocked.Read(ref _submitBlockedUntilTicks), DateTimeKind.Utc);
                    if (DateTime.UtcNow < blocked) break;

                    int used;
                    DateTime oldest;
                    lock (_submitLock) { PruneSubmitTimes(); used = _submitTimes.Count; oldest = used > 0 ? _submitTimes.Peek() : DateTime.UtcNow; }
                    if (used >= _o.MaxSubmissionsPer24h)
                    {
                        var until = oldest.AddHours(24).AddMinutes(1);
                        Interlocked.Exchange(ref _submitBlockedUntilTicks, until.Ticks);
                        if (!announcedBudget)
                        {
                            Log("WARN", string.Format(CultureInfo.InvariantCulture,
                                "Submission budget reached ({0} in the last 24h). Next submission at {1:yyyy-MM-dd HH:mm}Z; downloads continue meanwhile.",
                                used, until));
                            announcedBudget = true;
                        }
                        break;
                    }
                    announcedBudget = false;

                    AuditWindow next = null;
                    if (pending.First != null) { next = pending.First.Value; pending.RemoveFirst(); }
                    else if (_cursor > _o.RangeStartUtc)
                    {
                        next = PlanNext();
                        await _store.InsertAsync(next, ct).ConfigureAwait(false);
                        _windows[next.WindowId] = next;
                    }
                    else break;

                    SubmitOutcome outcome;
                    try { outcome = await SubmitAsync(next, ct).ConfigureAwait(false); }
                    catch (FatalGraphException) { pending.AddFirst(next); throw; }
                    if (outcome == SubmitOutcome.Submitted)
                    {
                        inFlight.Add(next);
                    }
                    else if (outcome == SubmitOutcome.Throttled)
                    {
                        pending.AddFirst(next);
                        break;
                    }
                    else
                    {
                        await RetryOrSplitAsync(next, pending, ct).ConfigureAwait(false);
                        break;
                    }
                }
                Volatile.Write(ref _queriesInFlight, inFlight.Count);

                // d) Done when nothing is left anywhere upstream of the writers.
                if (pending.Count == 0 && inFlight.Count == 0 && _cursor <= _o.RangeStartUtc &&
                    Volatile.Read(ref _windowsInDownload) == 0 && _resplit.IsEmpty)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(inFlight.Count > 0 ? _o.StatusPollSeconds : 2), ct).ConfigureAwait(false);
            }

            _phase = "Draining";
            _ready.Writer.TryComplete();
        }

        void PruneSubmitTimes()
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            while (_submitTimes.Count > 0 && _submitTimes.Peek() < cutoff) _submitTimes.Dequeue();
        }

        // Called from the planner and from the SQL writers (TryFinishAsync), so the
        // write is volatile and PlanNext reads it once.
        void ObserveDensity(long records, double hours)
        {
            if (hours <= 0) return;
            Volatile.Write(ref _density, records / hours);
        }

        AuditWindow PlanNext()
        {
            // density < 0: nothing observed yet. 0: the last window was empty, which
            // must widen the next one (sparse tenants, days past retention) rather
            // than fall back to the initial size and exhaust the submission budget.
            double density = Volatile.Read(ref _density);
            double hours = density < 0 ? _o.InitialWindowHours
                         : density == 0 ? _o.MaxWindowHours
                         : _o.TargetRecordsPerWindow / density;
            if (density >= 0) hours = Math.Min(hours, _lastPlannedHours * 2);   // grow cautiously
            hours = Math.Max(_o.MinWindowHours, Math.Min(_o.MaxWindowHours, hours));
            if (hours >= 1) hours = Math.Floor(hours);                           // whole hours when possible

            var start = _cursor.AddMinutes(-Math.Round(hours * 60));
            if (start < _o.RangeStartUtc) start = _o.RangeStartUtc;

            var w = new AuditWindow { StartUtc = start, EndUtc = _cursor, Status = WindowStatus.Pending };
            _cursor = start;
            _lastPlannedHours = hours;
            return w;
        }

        async Task SplitAsync(AuditWindow w, LinkedList<AuditWindow> pending, string reason, CancellationToken ct)
        {
            var span = w.EndUtc - w.StartUtc;
            var mid = w.StartUtc.AddMinutes(Math.Floor(span.TotalMinutes / 2));
            if (mid <= w.StartUtc || mid >= w.EndUtc)
            {
                Log("ERROR", w + ": cannot split further (" + reason + ").");
                w.Status = WindowStatus.Failed;
                w.LastError = reason;
                await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                return;
            }

            w.Status = WindowStatus.Split;
            w.LastError = reason;
            var older = new AuditWindow { ParentWindowId = w.WindowId, StartUtc = w.StartUtc, EndUtc = mid };
            var newer = new AuditWindow { ParentWindowId = w.WindowId, StartUtc = mid, EndUtc = w.EndUtc };
            await _store.SplitAsync(w, older, newer, ct).ConfigureAwait(false);
            _windows[older.WindowId] = older;
            _windows[newer.WindowId] = newer;
            pending.AddFirst(older);
            pending.AddFirst(newer);
            Log("INFO", string.Format(CultureInfo.InvariantCulture, "{0}: split ({1}) into {2} and {3}.", w, reason, newer, older));
        }

        // The parts of a Split window's range that none of its halves covers, as new windows: both halves, cut where
        // SplitAsync cuts, when neither was written.
        static List<AuditWindow> MissingHalves(AuditWindow parent, List<AuditWindow> all)
        {
            var missing = new List<AuditWindow>();
            var halves = all.Where(x => x.ParentWindowId == parent.WindowId).OrderBy(x => x.StartUtc).ToList();
            if (halves.Count == 0)
            {
                var mid = parent.StartUtc.AddMinutes(Math.Floor((parent.EndUtc - parent.StartUtc).TotalMinutes / 2));
                if (mid > parent.StartUtc && mid < parent.EndUtc)
                {
                    missing.Add(new AuditWindow { ParentWindowId = parent.WindowId, StartUtc = parent.StartUtc, EndUtc = mid });
                    missing.Add(new AuditWindow { ParentWindowId = parent.WindowId, StartUtc = mid, EndUtc = parent.EndUtc });
                }
                else missing.Add(new AuditWindow { ParentWindowId = parent.WindowId, StartUtc = parent.StartUtc, EndUtc = parent.EndUtc });
                return missing;
            }
            var at = parent.StartUtc;
            foreach (var h in halves)
            {
                if (h.StartUtc > at) missing.Add(new AuditWindow { ParentWindowId = parent.WindowId, StartUtc = at, EndUtc = h.StartUtc });
                if (h.EndUtc > at) at = h.EndUtc;
            }
            if (at < parent.EndUtc) missing.Add(new AuditWindow { ParentWindowId = parent.WindowId, StartUtc = at, EndUtc = parent.EndUtc });
            return missing;
        }

        async Task RetryOrSplitAsync(AuditWindow w, LinkedList<AuditWindow> pending, CancellationToken ct)
        {
            w.QueryId = null;
            if (w.Submissions < 2)
            {
                Log("WARN", w + ": " + (w.LastError ?? "submission failed") + " - retrying.");
                w.Status = WindowStatus.Pending;
                await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                pending.AddLast(w);
            }
            else if (w.Hours > _o.MinWindowHours * 2)
            {
                await SplitAsync(w, pending, (w.LastError ?? "repeated failures"), ct).ConfigureAwait(false);
            }
            else if (w.Submissions < _o.MaxWindowAttempts)
            {
                w.Status = WindowStatus.Pending;
                await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                pending.AddLast(w);
            }
            else
            {
                w.Status = WindowStatus.Failed;
                await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                Log("ERROR", w + ": giving up after " + w.Submissions + " submissions (" + w.LastError + "). Re-run to retry it.");
            }
        }

        enum SubmitOutcome { Submitted, Throttled, Failed }

        async Task<SubmitOutcome> SubmitAsync(AuditWindow w, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.security.auditLogQuery",
                ["displayName"] = string.Format(CultureInfo.InvariantCulture, "CopilotBackfill {0:yyyyMMdd-HHmm}-{1:yyyyMMdd-HHmm}", w.StartUtc, w.EndUtc),
                ["filterStartDateTime"] = w.StartUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["filterEndDateTime"] = w.EndUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["operationFilters"] = _o.Operations,
            };
            if (_o.RecordTypes != null && _o.RecordTypes.Length > 0) body["recordTypeFilters"] = _o.RecordTypes;
            var json = JsonSerializer.Serialize(body);

            var resp = await SendControlAsync(HttpMethod.Post, _o.GraphBaseUrl + "/v1.0/security/auditLog/queries", json, ct).ConfigureAwait(false);
            if (resp.Status == 429)
            {
                Interlocked.Increment(ref _submitThrottles);
                var wait = resp.RetryAfter ?? TimeSpan.FromMinutes(5);
                Interlocked.Exchange(ref _submitBlockedUntilTicks, (DateTime.UtcNow + wait).Ticks);
                Log("WARN", string.Format(CultureInfo.InvariantCulture,
                    "Audit search submission throttled (429). Waiting {0:N0} min before the next submission; downloads continue.", wait.TotalMinutes));
                return SubmitOutcome.Throttled;
            }
            if (resp.Status == 400 || resp.Status == 403)
            {
                // Not transient: a bad filter or a missing permission/role. Retrying or
                // splitting would only burn the tenant's submission budget.
                throw new GraphRequestException(resp.Status, resp.Body,
                    "audit search submission" + (resp.Status == 403
                        ? " (the signed-in account needs AuditLogsQuery.Read.All consented AND an Audit Reader/Manager role in Purview)"
                        : ""));
            }
            if (resp.Status < 200 || resp.Status > 299)
            {
                w.Submissions++;
                w.LastError = "submit failed: HTTP " + resp.Status + " " + resp.Body;
                Log("WARN", w + ": " + w.LastError);
                return SubmitOutcome.Failed;
            }

            using (var doc = JsonDocument.Parse(resp.Body))
            {
                w.QueryId = doc.RootElement.GetProperty("id").GetString();
            }
            w.Submissions++;
            w.Status = WindowStatus.Submitted;
            w.SubmittedUtc = DateTime.UtcNow;
            w.LastError = null;
            await _store.UpdateAsync(w, ct).ConfigureAwait(false);
            lock (_submitLock) _submitTimes.Enqueue(w.SubmittedUtc.Value);
            Interlocked.Increment(ref _submissions);
            Log("INFO", string.Format(CultureInfo.InvariantCulture, "{0}: submitted ({1:N1}h, query {2}).", w, w.Hours, w.QueryId));
            return SubmitOutcome.Submitted;
        }

        sealed class QueryState
        {
            public bool NotFound;
            public string Status;
            public bool? CapExceeded;
            public long? RecordCap;
            public long? ApproxRecords;
        }

        async Task<QueryState> GetQueryStateAsync(string queryId, CancellationToken ct)
        {
            if (_useBeta)
            {
                var b = await SendControlAsync(HttpMethod.Get, _o.GraphBaseUrl + "/beta/security/auditLog/queries/" + queryId, null, ct).ConfigureAwait(false);
                if (b.Status >= 200 && b.Status < 300) return ParseQueryState(b.Body);
                if (b.Status != 429)
                {
                    _useBeta = false;
                    Log("WARN", "Beta status endpoint unavailable (HTTP " + b.Status + ") - using v1.0. Over-cap windows will be detected by row count instead.");
                }
                else
                {
                    return new QueryState { Status = "running" };   // treat as not-yet-known; poll again later
                }
            }

            var v = await SendControlAsync(HttpMethod.Get, _o.GraphBaseUrl + "/v1.0/security/auditLog/queries/" + queryId, null, ct).ConfigureAwait(false);
            if (v.Status == 404) return new QueryState { NotFound = true };
            if (v.Status == 429) return new QueryState { Status = "running" };
            if (v.Status == 403) throw new FatalGraphException(new GraphRequestException(v.Status, v.Body, "status " + queryId));
            if (v.Status < 200 || v.Status > 299) throw new GraphRequestException(v.Status, v.Body, "status " + queryId);
            return ParseQueryState(v.Body);
        }

        static QueryState ParseQueryState(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var r = doc.RootElement;
                var s = new QueryState();
                if (r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String) s.Status = st.GetString();
                if (r.TryGetProperty("isRecordCountLimitExceeded", out var ex) &&
                    (ex.ValueKind == JsonValueKind.True || ex.ValueKind == JsonValueKind.False)) s.CapExceeded = ex.GetBoolean();
                if (r.TryGetProperty("recordCountLimit", out var cap) && cap.ValueKind == JsonValueKind.Number) s.RecordCap = cap.GetInt64();
                if (r.TryGetProperty("approximateReturnedRecordCount", out var ap) && ap.ValueKind == JsonValueKind.Number) s.ApproxRecords = ap.GetInt64();
                return s;
            }
        }

        struct ControlResponse
        {
            public int Status;
            public string Body;
            public TimeSpan? RetryAfter;
        }

        /// <summary>Low-volume control-plane calls (submit / status). Not subject to the page limiter.</summary>
        async Task<ControlResponse> SendControlAsync(HttpMethod method, string url, string jsonBody, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                long tv = Interlocked.Read(ref _tokenVersion);
                try
                {
                    using (var req = new HttpRequestMessage(method, url))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        req.Headers.Add("client-request-id", Guid.NewGuid().ToString());
                        if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            int code = (int)resp.StatusCode;
                            if (code == 401)
                            {
                                await WaitForFreshTokenAsync(tv, ct).ConfigureAwait(false);
                                continue;
                            }
                            if ((code >= 500 || code == 408) && attempt < 5)
                            {
                                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                                continue;
                            }
                            return new ControlResponse
                            {
                                Status = code,
                                Body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false),
                                RetryAfter = RetryAfterOf(resp)
                            };
                        }
                    }
                }
                catch (HttpRequestException) when (attempt < 5)
                {
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 5)
                {
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);   // HttpClient timeout
                }
                catch (System.IO.IOException) when (attempt < 5)
                {
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                }
            }
        }

        // ---------------- downloads -------------------------------------------

        async Task DownloadLoopAsync(CancellationToken ct)
        {
            await foreach (var w in _ready.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _downloadingNow);
                try
                {
                    await DownloadWindowAsync(w, ct).ConfigureAwait(false);
                }
                catch (FatalGraphException)
                {
                    throw;
                }
                catch (GraphRequestException gx) when (gx.StatusCode == 400 || gx.StatusCode == 403)
                {
                    throw new FatalGraphException(gx);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    w.LastError = "download: " + ex.GetBaseException().Message;
                    if (w.Attempts < _o.MaxWindowAttempts)
                    {
                        Log("WARN", w + ": " + w.LastError + " - will re-download (attempt " + (w.Attempts + 1) + ").");
                        w.Status = WindowStatus.Ready;
                        await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                        _ready.Writer.TryWrite(w);      // still counted in _windowsInDownload
                    }
                    else
                    {
                        w.Status = WindowStatus.Failed;
                        await _store.UpdateAsync(w, ct).ConfigureAwait(false);
                        Log("ERROR", w + ": " + w.LastError + " - giving up after " + w.Attempts + " attempts. Re-run to retry it.");
                        Interlocked.Decrement(ref _windowsInDownload);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _downloadingNow);
                }
            }
        }

        async Task DownloadWindowAsync(AuditWindow w, CancellationToken ct)
        {
            w.Attempts++;
            w.Status = WindowStatus.Downloading;
            await _store.UpdateAsync(w, ct).ConfigureAwait(false);

            var st = new AttemptState(w, w.Attempts);
            string baseUrl = _o.GraphBaseUrl + "/v1.0/security/auditLog/queries/" + w.QueryId + "/records";
            string url = _useTop ? baseUrl + "?$top=" + _o.PageSize.ToString(CultureInfo.InvariantCulture) : baseUrl;
            bool firstPage = true;
            var batch = NewBatch(st);

            while (url != null)
            {
                JsonDocument doc;
                try
                {
                    doc = await GetPageAsync(url, ct).ConfigureAwait(false);
                }
                catch (GraphRequestException gx) when (firstPage && gx.StatusCode == 400 && _useTop)
                {
                    _useTop = false;
                    Log("WARN", "Service rejected $top=" + _o.PageSize + " - falling back to the default page size.");
                    url = baseUrl;
                    continue;
                }
                firstPage = false;

                using (doc)
                {
                    var root = doc.RootElement;
                    int n = 0;
                    if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rec in values.EnumerateArray())
                        {
                            var row = MapRow(rec, st);
                            if (row == null) continue;
                            batch.Rows.Add(row);
                            n++;
                            if (batch.Rows.Count >= _o.BatchRows)
                            {
                                await EnqueueAsync(batch, ct).ConfigureAwait(false);
                                batch = NewBatch(st);
                            }
                        }
                    }
                    st.Rows += n;
                    Interlocked.Add(ref _rowsDownloaded, n);
                    Interlocked.Increment(ref _pages);

                    url = root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
                        ? next.GetString() : null;
                }
            }

            if (batch.Rows.Count > 0) await EnqueueAsync(batch, ct).ConfigureAwait(false);

            // Fallback truncation check when the service did not report the cap flag.
            long cap = w.RecordCap > 0 ? w.RecordCap : _o.AssumedRecordCap;
            if (w.CapExceeded == null && st.Rows >= (long)(cap * 0.98) && w.Hours > _o.MinWindowHours * 2)
            {
                Log("WARN", string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1:N0} rows is at the ~{2:N0} record cap and the service did not report truncation - discarding this attempt and splitting.",
                    w, st.Rows, cap));
                _resplit.Enqueue(w);                          // enqueue before decrement (planner termination check)
                Interlocked.Decrement(ref _windowsInDownload);
                return;
            }

            Interlocked.Exchange(ref st.DownloadDone, 1);
            if (Interlocked.CompareExchange(ref st.Outstanding, 0, 0) == 0)
                await TryFinishAsync(st, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref _windowsInDownload);
        }

        static RowBatch NewBatch(AttemptState st) => new RowBatch { State = st, Rows = new List<object[]>() };

        async Task EnqueueAsync(RowBatch batch, CancellationToken ct)
        {
            Interlocked.Increment(ref batch.State.Outstanding);
            try
            {
                await _batches.Writer.WriteAsync(batch, ct).ConfigureAwait(false);   // back-pressure when SQL lags
            }
            catch
            {
                Interlocked.Decrement(ref batch.State.Outstanding);
                throw;
            }
        }

        async Task<JsonDocument> GetPageAsync(string url, CancellationToken ct)
        {
            int transient = 0;
            while (true)
            {
                await _limiter.WaitAsync(ct).ConfigureAwait(false);
                long tv = Interlocked.Read(ref _tokenVersion);
                HttpResponseMessage resp = null;
                // Each page has a time limit of its own, body included (2.4.12). HttpClient's timeout ends once the
                // headers arrive (ResponseHeadersRead), so a connection that went silent mid-body - a proxy or firewall
                // dropping it without a reset - was awaited forever, and the whole run with it, until Task Scheduler
                // killed it 20 hours later.
                var page = CancellationTokenSource.CreateLinkedTokenSource(ct);
                page.CancelAfter(TimeSpan.FromMinutes(_o.PageTimeoutMinutes));
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        req.Headers.Add("client-request-id", Guid.NewGuid().ToString());
                        resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, page.Token).ConfigureAwait(false);
                    }

                    int code = (int)resp.StatusCode;
                    if (resp.IsSuccessStatusCode)
                    {
                        using (var stream = await resp.Content.ReadAsStreamAsync(page.Token).ConfigureAwait(false))
                        {
                            var doc = await JsonDocument.ParseAsync(stream, default, page.Token).ConfigureAwait(false);
                            _limiter.OnSuccess(_o.IncreaseAfterCleanPages);
                            return doc;
                        }
                    }
                    if (code == 429)
                    {
                        Interlocked.Increment(ref _pageThrottles);
                        var pause = _limiter.OnThrottled(RetryAfterOf(resp));
                        Log("WARN", string.Format(CultureInfo.InvariantCulture,
                            "Page request throttled (429). All streams paused {0:N0}s; concurrency cut to {1}.",
                            pause.TotalSeconds, _limiter.Limit));
                        continue;
                    }
                    if (code == 401)
                    {
                        await WaitForFreshTokenAsync(tv, ct).ConfigureAwait(false);
                        continue;
                    }
                    if ((code >= 500 || code == 408) && ++transient <= 8)
                    {
                        await Task.Delay(Backoff(transient), ct).ConfigureAwait(false);
                        continue;
                    }
                    var body = await resp.Content.ReadAsStringAsync(page.Token).ConfigureAwait(false);
                    throw new GraphRequestException(code, body, url);
                }
                catch (HttpRequestException ex) when (++transient <= 8)
                {
                    Log("WARN", "Network error paging records (" + ex.Message + ") - retrying.");
                    await Task.Delay(Backoff(transient), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && ++transient <= 8)
                {
                    // The page's own limit, or HttpClient's: not a stop.
                    Log("WARN", "Page request timed out - retrying.");
                    await Task.Delay(Backoff(transient), ct).ConfigureAwait(false);
                }
                catch (JsonException) when (++transient <= 8)
                {
                    Log("WARN", "Truncated/invalid JSON page - retrying.");
                    await Task.Delay(Backoff(transient), ct).ConfigureAwait(false);
                }
                catch (System.IO.IOException ex) when (++transient <= 8)
                {
                    // .NET 8 surfaces a connection dropped mid-body as HttpIOException (an IOException).
                    Log("WARN", "Connection dropped while reading a page (" + ex.Message + ") - retrying.");
                    await Task.Delay(Backoff(transient), ct).ConfigureAwait(false);
                }
                finally
                {
                    resp?.Dispose();
                    page.Dispose();
                    _limiter.Release();
                }
            }
        }

        object[] MapRow(JsonElement rec, AttemptState st)
        {
            var row = new object[RowSchema.Columns.Length];
            row[0] = st.Window.WindowId;
            row[1] = st.Attempt;

            string id = Str(rec, "id");
            JsonElement ad = default;
            bool hasAudit = rec.TryGetProperty("auditData", out ad) &&
                            ad.ValueKind != JsonValueKind.Null && ad.ValueKind != JsonValueKind.Undefined;
            if (string.IsNullOrEmpty(id) && hasAudit && ad.ValueKind == JsonValueKind.Object) id = Str(ad, "Id");
            if (string.IsNullOrEmpty(id)) { Interlocked.Increment(ref _skipped); return null; }
            row[2] = Fit(id, 2);

            string created = Str(rec, "createdDateTime");
            if (string.IsNullOrEmpty(created) && hasAudit && ad.ValueKind == JsonValueKind.Object) created = Str(ad, "CreationTime");
            if (string.IsNullOrEmpty(created) ||
                !DateTime.TryParse(created, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdUtc))
            {
                Interlocked.Increment(ref _skipped);
                return null;
            }
            row[3] = createdUtc;
            TrackRange(createdUtc.Ticks);

            foreach (var (col, prop) in RowSchema.Scalars) row[col] = Fit(Str(rec, prop), col);

            if (rec.TryGetProperty("administrativeUnits", out var au) && au.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var a in au.EnumerateArray())
                {
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText());
                }
                row[13] = sb.Length > 0 ? Fit(sb.ToString(), 13) : null;
            }

            row[14] = hasAudit ? Gzip(ad.GetRawText()) : null;
            return row;
        }

        /// <summary>
        /// gzip of the UTF-16LE JSON - exactly what SQL Server's COMPRESS() produces
        /// for NVARCHAR, so CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX)) reads it
        /// back. Measured on Microsoft's documented CopilotInteraction samples:
        /// 2.4x for a record with one accessed file, 7.2x with eight - both land
        /// around 1.1-1.3 KB per record, versus 2.5-9 KB uncompressed.
        /// </summary>
        byte[] Gzip(string json)
        {
            var raw = Encoding.Unicode.GetBytes(json);
            using (var ms = new MemoryStream(raw.Length / 4 + 64))
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    gz.Write(raw, 0, raw.Length);
                var packed = ms.ToArray();
                Interlocked.Add(ref _auditBytesRaw, raw.Length);
                Interlocked.Add(ref _auditBytesPacked, packed.Length);
                return packed;
            }
        }

        static string Str(JsonElement e, string prop)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
            switch (v.ValueKind)
            {
                case JsonValueKind.String: return v.GetString();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined: return null;
                default: return v.GetRawText();
            }
        }

        string Fit(string value, int col)
        {
            int max = RowSchema.MaxLength[col];
            if (value == null || max <= 0 || value.Length <= max) return value;
            Interlocked.Increment(ref _truncated);
            return value.Substring(0, max);
        }

        void TrackRange(long ticks)
        {
            long cur;
            while (ticks < (cur = Interlocked.Read(ref _oldestTicks)) &&
                   Interlocked.CompareExchange(ref _oldestTicks, ticks, cur) != cur) { }
            while (ticks > (cur = Interlocked.Read(ref _newestTicks)) &&
                   Interlocked.CompareExchange(ref _newestTicks, ticks, cur) != cur) { }
        }

        // ---------------- SQL writers -----------------------------------------

        async Task WriterLoopAsync(CancellationToken ct)
        {
            await foreach (var batch in _batches.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        await _sink.WriteAsync(batch, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (TransientException tx) when (attempt < 6 && !ct.IsCancellationRequested)
                    {
                        Log("WARN", "SQL write failed transiently (" + tx.Message + ") - retry " + attempt + ".");
                        await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    }
                }

                Interlocked.Add(ref _rowsWritten, batch.Rows.Count);
                var st = batch.State;
                if (Interlocked.Decrement(ref st.Outstanding) == 0 &&
                    Interlocked.CompareExchange(ref st.DownloadDone, 0, 0) == 1)
                {
                    await TryFinishAsync(st, ct).ConfigureAwait(false);
                }
            }
        }

        async Task TryFinishAsync(AttemptState st, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref st.Finished, 1) != 0) return;
            var w = st.Window;
            w.Status = WindowStatus.Loaded;
            w.RowsLoaded = st.Rows;
            w.CompletedUtc = DateTime.UtcNow;
            w.LastError = null;
            // The rows actually loaded are the only density signal when the status
            // call reports no approximate count - which a live tenant did for every
            // search, Copilot and Entra alike. Without this, a fresh run plans every
            // window at InitialWindowHours and only a restart (which reads the last
            // loaded window) lets them grow.
            if (!w.ApproxRecords.HasValue) ObserveDensity(st.Rows, w.Hours);
            await _store.UpdateAsync(w, ct).ConfigureAwait(false);
            Log("INFO", string.Format(CultureInfo.InvariantCulture, "{0}: loaded {1:N0} rows.", w, st.Rows));
        }

        // ---------------- helpers ---------------------------------------------

        async Task WaitForFreshTokenAsync(long seenVersion, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _tokenRefreshRequested, 1) == 0)
                Log("WARN", "Graph returned 401 - waiting for a fresh access token.");
            var deadline = DateTime.UtcNow.AddMinutes(_o.TokenWaitMinutes);
            while (Interlocked.Read(ref _tokenVersion) == seenVersion)
            {
                if (DateTime.UtcNow > deadline)
                {
                    // Nobody completed the sign-in. Stop the whole pipeline rather than
                    // burning window retries; everything done so far is saved.
                    throw new FatalGraphException(new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "Graph rejected the access token and no new sign-in completed within {0:N0} minutes. " +
                        "Progress is saved - run the same command again to resume.", _o.TokenWaitMinutes)));
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        static TimeSpan Backoff(int attempt) =>
            TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, attempt)) + Random.Shared.NextDouble() * 2);

        static TimeSpan? RetryAfterOf(HttpResponseMessage resp)
        {
            var ra = resp.Headers.RetryAfter;
            if (ra == null) return null;
            if (ra.Delta.HasValue) return ra.Delta.Value;
            if (ra.Date.HasValue)
            {
                var d = ra.Date.Value - DateTimeOffset.UtcNow;
                return d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(1);
            }
            return null;
        }

        static DateTime? JwtExpiry(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                var p = parts[1].Replace('-', '+').Replace('_', '/');
                switch (p.Length % 4) { case 2: p += "=="; break; case 3: p += "="; break; }
                using (var doc = JsonDocument.Parse(Convert.FromBase64String(p)))
                {
                    if (doc.RootElement.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number)
                        return DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()).UtcDateTime;
                }
            }
            catch { }
            return null;
        }
    }
}
