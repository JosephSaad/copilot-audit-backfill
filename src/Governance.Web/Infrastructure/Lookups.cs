using Dapper;
using Governance.Application.Data;

namespace Governance.Web.Infrastructure;

/// <summary>Names for codes that live in reference tables (RACI activities, roles, SLA policies), cached for ten minutes.</summary>
public static class Lookups
{
    private static Dictionary<string, string>? _names;
    private static DateTime _loadedUtc;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Name for a RACI activity, role or SLA policy code; falls back to <see cref="Labels.For"/>.</summary>
    public static async Task<Func<string?, string>> NamesAsync(Db db)
    {
        var map = await LoadAsync(db);
        return code => string.IsNullOrEmpty(code) ? "" : map.TryGetValue(code, out var n) ? n : Labels.For(code);
    }

    private static async Task<Dictionary<string, string>> LoadAsync(Db db)
    {
        if (_names is not null && DateTime.UtcNow - _loadedUtc < TimeSpan.FromMinutes(10)) return _names;
        await Gate.WaitAsync();
        try
        {
            if (_names is not null && DateTime.UtcNow - _loadedUtc < TimeSpan.FromMinutes(10)) return _names;
            await using var conn = await db.OpenUserAsync();
            var rows = await conn.QueryAsync<(string Code, string Name)>(
                """
                SELECT ActivityCode, Name FROM sec.RaciActivity
                UNION ALL SELECT RoleCode, Name FROM sec.RoleDefinition
                UNION ALL SELECT SlaPolicyCode, Name FROM wf.SlaPolicy
                UNION ALL SELECT FunctionCode, Name FROM sec.RaciFunction
                """);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (code, name) in rows) map.TryAdd(code, name);
            _names = map;
            _loadedUtc = DateTime.UtcNow;
            return map;
        }
        catch
        {
            return _names ?? new Dictionary<string, string>();
        }
        finally { Gate.Release(); }
    }
}
