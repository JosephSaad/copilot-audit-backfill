// In-memory stand-ins for SQL Server, used by Test-Engine.ps1.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CopilotAudit;

namespace CopilotAudit.Tests
{
    /// <summary>Behaves like the SQL control table: stores copies, not references.</summary>
    public sealed class MemoryWindowStore : IWindowStore
    {
        readonly object _lock = new object();
        readonly Dictionary<long, AuditWindow> _rows = new Dictionary<long, AuditWindow>();
        long _next;

        static AuditWindow Copy(AuditWindow w) => new AuditWindow
        {
            WindowId = w.WindowId, ParentWindowId = w.ParentWindowId, StartUtc = w.StartUtc, EndUtc = w.EndUtc,
            Status = w.Status, QueryId = w.QueryId, Attempts = w.Attempts, Submissions = w.Submissions,
            ApproxRecords = w.ApproxRecords, RowsLoaded = w.RowsLoaded, SubmittedUtc = w.SubmittedUtc,
            CompletedUtc = w.CompletedUtc, LastError = w.LastError
        };

        public Task<List<AuditWindow>> LoadAllAsync(CancellationToken ct)
        {
            lock (_lock) return Task.FromResult(_rows.Values.Select(Copy).ToList());
        }

        public Task InsertAsync(AuditWindow w, CancellationToken ct)
        {
            lock (_lock) { w.WindowId = ++_next; _rows[w.WindowId] = Copy(w); }
            return Task.CompletedTask;
        }

        public Task UpdateAsync(AuditWindow w, CancellationToken ct)
        {
            lock (_lock)
            {
                if (!_rows.ContainsKey(w.WindowId)) throw new InvalidOperationException("update of unknown window " + w.WindowId);
                _rows[w.WindowId] = Copy(w);
            }
            return Task.CompletedTask;
        }

        public Task SplitAsync(AuditWindow parent, AuditWindow older, AuditWindow newer, CancellationToken ct)
        {
            lock (_lock)
            {
                if (!_rows.ContainsKey(parent.WindowId)) throw new InvalidOperationException("split of unknown window " + parent.WindowId);
                _rows[parent.WindowId] = Copy(parent);
                older.WindowId = ++_next; _rows[older.WindowId] = Copy(older);
                newer.WindowId = ++_next; _rows[newer.WindowId] = Copy(newer);
            }
            return Task.CompletedTask;
        }

        // Like the SQL store's application lock: one run at a time.
        int _claimed;
        public Task<IDisposable> ClaimAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
                throw new WindowTableInUseException("Another run is loading this window table now.");
            return Task.FromResult<IDisposable>(new Claim(this));
        }

        sealed class Claim : IDisposable
        {
            MemoryWindowStore _store;
            public Claim(MemoryWindowStore store) { _store = store; }
            public void Dispose()
            {
                var s = Interlocked.Exchange(ref _store, null);
                if (s != null) Volatile.Write(ref s._claimed, 0);
            }
        }

        public AuditWindow[] Snapshot() { lock (_lock) return _rows.Values.Select(Copy).OrderBy(w => w.StartUtc).ToArray(); }
    }

    public sealed class StoredRow
    {
        public long WindowId;
        public int Attempt;
        public string RecordId;
        public DateTime Created;
    }

    /// <summary>Validates every row the way SQL Server would reject it, and injects transient failures.</summary>
    public sealed class MemorySink : IBatchSink
    {
        public readonly ConcurrentQueue<StoredRow> Rows = new ConcurrentQueue<StoredRow>();
        public int FailEvery { get; set; }
        public int DelayMs { get; set; }
        public int InjectedFailures => _injected;
        public int Writes => _writes;
        public int MaxConcurrentWrites => _maxConcurrent;
        public readonly ConcurrentQueue<string> Violations = new ConcurrentQueue<string>();

        int _writes, _injected, _concurrent, _maxConcurrent, _auditChecked;
        public int AuditPayloadsVerified => _auditChecked;

        public async Task WriteAsync(RowBatch batch, CancellationToken ct)
        {
            int c = Interlocked.Increment(ref _concurrent);
            int m;
            while (c > (m = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, c, m) != m) { }
            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, ct);
                int n = Interlocked.Increment(ref _writes);
                if (FailEvery > 0 && n % FailEvery == 0)
                {
                    Interlocked.Increment(ref _injected);
                    throw new TransientException("injected transient failure #" + n);
                }

                // Validate like SqlBulkCopy + the DDL would, via the real IDataReader.
                using (var reader = new RowBatchReader(batch.Rows))
                {
                    if (reader.FieldCount != RowSchema.Columns.Length) Violations.Enqueue("field count");
                    while (reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var v = reader.GetValue(i);
                            if (v is DBNull)
                            {
                                if (i <= 3) Violations.Enqueue("NULL in NOT NULL column " + reader.GetName(i));
                                continue;
                            }
                            if (!RowSchema.Types[i].IsInstanceOfType(v))
                                Violations.Enqueue(reader.GetName(i) + " has type " + v.GetType().Name);
                            if (v is string s && RowSchema.MaxLength[i] > 0 && s.Length > RowSchema.MaxLength[i])
                                Violations.Enqueue(reader.GetName(i) + " exceeds " + RowSchema.MaxLength[i]);
                        }
                        var created = (DateTime)reader.GetValue(3);
                        if (created.Kind != DateTimeKind.Utc) Violations.Enqueue("CreatedDateTime not UTC");
                        var packed = reader.GetValue(14) as byte[];
                        if (packed != null)
                        {
                            // What SQL's CAST(DECOMPRESS(x) AS NVARCHAR(MAX)) does: gunzip, read as UTF-16LE.
                            try
                            {
                                using (var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
                                using (var ms = new MemoryStream())
                                {
                                    gz.CopyTo(ms);
                                    var json = Encoding.Unicode.GetString(ms.ToArray());
                                    JsonDocument.Parse(json).Dispose();
                                    Interlocked.Increment(ref _auditChecked);
                                }
                            }
                            catch { Violations.Enqueue("AuditDataGz does not decompress to valid UTF-16 JSON"); }
                        }

                        Rows.Enqueue(new StoredRow
                        {
                            WindowId = reader.GetInt64(0),
                            Attempt = reader.GetInt32(1),
                            RecordId = reader.GetString(2),
                            Created = created,
                        });
                    }
                }
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }
}
