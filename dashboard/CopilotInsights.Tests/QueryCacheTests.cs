using CopilotInsights.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// The query cache (2.4.7): a result is found again only by the very same query with the very same parameters, in the
/// same tenant, while the data's stamp stands. The scope root is a parameter, so one viewer's rows can never answer another's.
/// </summary>
public class QueryCacheTests
{
    static QueryCache New(int minutes = 60) =>
        new(new MemoryCache(new MemoryCacheOptions()), Options.Create(new InsightsOptions { QueryCacheMinutes = minutes }), NullLogger<QueryCache>.Instance);

    [Fact]
    public void The_key_changes_with_the_sql_and_with_any_parameter()
    {
        var a = QueryCache.Key("SELECT 1 WHERE x = @root", new { root = "ceo@contoso.com", k0 = 20260101 });
        Assert.NotNull(a);
        Assert.Equal(a, QueryCache.Key("SELECT 1 WHERE x = @root", new { root = "ceo@contoso.com", k0 = 20260101 }));
        Assert.NotEqual(a, QueryCache.Key("SELECT 1 WHERE x = @root", new { root = "vp@contoso.com", k0 = 20260101 }));
        Assert.NotEqual(a, QueryCache.Key("SELECT 1 WHERE x = @root", new { root = (string?)null, k0 = 20260101 }));
        Assert.NotEqual(a, QueryCache.Key("SELECT 1 WHERE x = @root", new { root = "ceo@contoso.com", k0 = 20260102 }));
        Assert.NotEqual(a, QueryCache.Key("SELECT 2 WHERE x = @root", new { root = "ceo@contoso.com", k0 = 20260101 }));
    }

    [Fact]
    public void Dapper_parameters_built_from_objects_and_added_one_by_one_are_all_in_the_key()
    {
        Dapper.DynamicParameters P(string? root, string q, int extra)
        {
            var d = new Dapper.DynamicParameters(new { root, k1 = 20260926 });
            d.AddDynamicParams(new { q });
            d.Add("p0", extra);
            return d;
        }
        var a = QueryCache.Key("sql", P("ceo@contoso.com", "%smith%", 1));
        Assert.NotNull(a);
        Assert.Equal(a, QueryCache.Key("sql", P("ceo@contoso.com", "%smith%", 1)));
        Assert.NotEqual(a, QueryCache.Key("sql", P("vp@contoso.com", "%smith%", 1)));     // the scope root, from the first object
        Assert.NotEqual(a, QueryCache.Key("sql", P("ceo@contoso.com", "%jones%", 1)));    // the search, from the second
        Assert.NotEqual(a, QueryCache.Key("sql", P("ceo@contoso.com", "%smith%", 2)));    // one added by name
    }

    [Fact]
    public void Lists_and_dates_are_part_of_the_key()
    {
        Assert.NotEqual(QueryCache.Key("s", new { list = new[] { "a", "b" } }), QueryCache.Key("s", new { list = new[] { "a", "c" } }));
        Assert.NotEqual(QueryCache.Key("s", new { d = new DateTime(2026, 9, 1) }), QueryCache.Key("s", new { d = new DateTime(2026, 9, 2) }));
        Assert.NotEqual(QueryCache.Key("s", new { v = "1" }), QueryCache.Key("s", new { v = 1 }));
    }

    [Fact]
    public async Task Only_code_that_asked_for_it_reads_the_cache_and_a_new_stamp_or_a_write_ends_it()
    {
        var cache = New();
        var runs = 0;
        Task<int> Read() => cache.GetAsync("T", "s", "SELECT 1", new { a = 1 }, () => Task.FromResult(++runs));
        Assert.Equal(1, await Read());
        Assert.Equal(2, await Read());                      // not asked for: the database both times
        using (QueryCache.Use())
        {
            Assert.Equal(3, await Read());
            Assert.Equal(3, await Read());                  // answered from memory
            Assert.Equal(4, await cache.GetAsync("Other tenant", "s", "SELECT 1", new { a = 1 }, () => Task.FromResult(++runs)));
            cache.SetStamp("T", "after the refresh");
            Assert.Equal(5, await Read());
            Assert.Equal(5, await Read());
            cache.Wrote("T");
            Assert.Equal(6, await Read());
        }
        Assert.False(QueryCache.On);
    }

    [Fact]
    public async Task A_failed_read_is_not_kept_and_minutes_zero_turns_it_off()
    {
        var cache = New();
        using (QueryCache.Use())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync<int>("T", "s", "bad", null, () => throw new InvalidOperationException()));
            Assert.Equal(7, await cache.GetAsync("T", "s", "bad", null, () => Task.FromResult(7)));
            var off = New(0);
            var runs = 0;
            await off.GetAsync("T", "s", "x", null, () => Task.FromResult(++runs));
            await off.GetAsync("T", "s", "x", null, () => Task.FromResult(++runs));
            Assert.Equal(2, runs);
        }
    }

    [Theory]
    [InlineData("SELECT 1 FROM dash.FlagCase AS fc", true)]
    [InlineData("SELECT 1 FROM x LEFT JOIN DASH.NONHUMANEXCEPTION AS ex ON 1 = 1", true)]
    [InlineData("SELECT 1 FROM dash.LicenseAction", true)]
    [InlineData("SELECT 1 FROM dash.UserDay", false)]
    public void A_query_that_reads_a_table_changed_all_day_is_never_cached(string sql, bool isVolatile) =>
        Assert.Equal(isVolatile, DataStamp.Volatile(sql));

    [Fact]
    public async Task A_volatile_query_always_reads_the_database()
    {
        var cache = New();
        var runs = 0;
        using (QueryCache.Use())
        {
            await cache.GetAsync("T", "s", "SELECT COUNT(*) FROM dash.FlagCase", null, () => Task.FromResult(++runs));
            await cache.GetAsync("T", "s", "SELECT COUNT(*) FROM dash.FlagCase", null, () => Task.FromResult(++runs));
        }
        Assert.Equal(2, runs);
    }

    [Theory]
    [InlineData("INSERT INTO dash.AuditLog (Viewer) VALUES (@v)", false)]                       // every page view writes this
    [InlineData("UPDATE dash.FlagCase SET Status = @s WHERE RecordId = @r;\nIF @@ROWCOUNT = 0 INSERT INTO dash.FlagCase (RecordId) VALUES (@r);", false)]
    [InlineData("DELETE FROM dash.NonHumanException WHERE Upn = @upn", false)]
    [InlineData("UPDATE dash.Setting SET Value = @v WHERE Name = @n;", true)]
    [InlineData("INSERT INTO dash.AuditLog (Viewer) VALUES (@v); UPDATE dash.Target SET Note = @n;", true)]
    [InlineData("MERGE dash.ValueName AS t USING x ON 1 = 1 WHEN MATCHED THEN UPDATE SET Name = @n;", true)]
    [InlineData("EXEC dash.usp_RefreshDashboard @SinceUtc = @today;", true)]                    // no table named: assume it matters
    public void Only_a_write_that_can_change_a_report_ends_the_cache(string sql, bool matters) =>
        Assert.Equal(matters, DataStamp.Matters(sql));
}
