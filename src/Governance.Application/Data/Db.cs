using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Governance.Application.Data;

public sealed class DbOptions
{
    /// <summary>Connection string for this process's application principal (app_web or app_worker).</summary>
    public string ConnectionString { get; set; } = "";
    public int CommandTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Opens SQL connections with the row-level security session context set read-only, so it cannot be
/// changed later on the same connection and is cleared when the connection returns to the pool.
/// <para>User mode: the signed-in person's scope (pages and queries).</para>
/// <para>System mode: central scope, used by command handlers after application authorization and by the
/// worker. RLS is the second line of defense; application authorization stays the first.</para>
/// </summary>
public sealed class Db(IOptions<DbOptions> options, ICurrentPrincipal principal)
{
    private readonly DbOptions _options = options.Value;

    public int CommandTimeout => _options.CommandTimeoutSeconds;

    public Task<SqlConnection> OpenUserAsync(CancellationToken ct = default)
        => OpenAsync(principal.PersonId, principal.IsCentral, ct);

    public Task<SqlConnection> OpenSystemAsync(CancellationToken ct = default)
        => OpenAsync(principal.PersonId, true, ct);

    public async Task<SqlConnection> OpenAsync(int personId, bool isCentral, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new InvalidOperationException("ConnectionStrings:Governance is not configured.");
        var conn = new SqlConnection(_options.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "EXEC sp_set_session_context @key = N'PersonId', @value = @p, @read_only = 1; " +
                "EXEC sp_set_session_context @key = N'IsCentral', @value = @c, @read_only = 1;";
            cmd.Parameters.Add(new SqlParameter("@p", SqlDbType.Int) { Value = personId });
            cmd.Parameters.Add(new SqlParameter("@c", SqlDbType.Bit) { Value = isCentral });
            await cmd.ExecuteNonQueryAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs <paramref name="work"/> in one transaction on a system-mode connection.</summary>
    public async Task<T> InTransactionAsync<T>(Func<Uow, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await OpenSystemAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var uow = new Uow(conn, tx, _options.CommandTimeoutSeconds, ct);
        try
        {
            var result = await work(uow);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection may be gone */ }
            throw;
        }
    }

    public Task InTransactionAsync(Func<Uow, Task> work, CancellationToken ct = default)
        => InTransactionAsync<bool>(async u => { await work(u); return true; }, ct);
}

/// <summary>A connection and transaction pair, with Dapper helpers that always pass both.</summary>
public sealed class Uow(SqlConnection connection, SqlTransaction transaction, int timeout, CancellationToken ct)
{
    public SqlConnection Connection { get; } = connection;
    public SqlTransaction Transaction { get; } = transaction;
    public CancellationToken Cancellation { get; } = ct;

    private CommandDefinition Cmd(string sql, object? args) => new(sql, args, Transaction, timeout, cancellationToken: Cancellation);

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? args = null) => Connection.QueryAsync<T>(Cmd(sql, args));
    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? args = null) => Connection.QuerySingleOrDefaultAsync<T>(Cmd(sql, args));
    public Task<T> QuerySingleAsync<T>(string sql, object? args = null) => Connection.QuerySingleAsync<T>(Cmd(sql, args));
    public Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? args = null) => Connection.QueryFirstOrDefaultAsync<T>(Cmd(sql, args));
    public Task<T?> ScalarAsync<T>(string sql, object? args = null) => Connection.ExecuteScalarAsync<T>(Cmd(sql, args));
    public Task<int> ExecuteAsync(string sql, object? args = null) => Connection.ExecuteAsync(Cmd(sql, args));
}

/// <summary>Thrown when a command is refused for a business reason; the message is safe to show.</summary>
public sealed class DomainException(string message) : Exception(message);

/// <summary>Thrown when a row changed since the user loaded it (rowversion mismatch).</summary>
public sealed class ConcurrencyException() : Exception("Someone else changed this item since you opened it. The page now shows the newer state.");
