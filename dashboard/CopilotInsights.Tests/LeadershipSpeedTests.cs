using Xunit;

namespace CopilotInsights.Tests;

/// <summary>The Leadership pages' reads: the same figures from less work.</summary>
public class LeadershipSpeedTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) d = d.Parent!;
        return d.FullName;
    }

    static string Read(params string[] p) => File.ReadAllText(Path.Combine([Root(), "dashboard", "CopilotInsights", .. p])).Replace("\r\n", "\n");

    [Fact]
    public void Units_meet_their_members_by_hash_and_name_the_managers_after_counting()
    {
        var m = Read("Data", "Metrics.cs");
        Assert.DoesNotContain("INTO #u", m);   // no temporary table: no compile on every visit
        Assert.Contains("LEFT HASH JOIN act AS a ON a.UserKey = u.UserKey", m);
        Assert.Contains("LEFT HASH JOIN bi.FactLicensedDay AS ld ON ld.UserKey = u.UserKey AND ld.DateKey = @k1", m);
        Assert.Contains("FROM u INNER HASH JOIN {c.PerPoint()} AS pp ON pp.UserKey = u.UserKey", m);
        Assert.Contains("LEFT JOIN dash.Person AS mp ON mp.Upn = g.Unit", m);
        /* The members with a key and an account on the closure come from its part index alone. */
        Assert.Contains("WHERE oc.DescendantEnabled IS NOT NULL AND oc.DescendantKey IS NOT NULL\n      UNION ALL", m);
        Assert.Contains("WHERE oc.DescendantEnabled IS NULL OR oc.DescendantKey IS NULL) AS m", m);
    }

    [Fact]
    public void Pages_that_chart_only_use_read_no_license_days()
    {
        Assert.Contains("var trTask = m.SumsTrendAsync(Ctx);", Read("Pages", "Leadership", "Leadership.cs"));
        Assert.Contains("var rpTask = full ? metrics.SumsTrendAsync(prev) : null;", Read("Pages", "Leadership", "BoardPack.cs"));
        Assert.Contains("metrics.PeriodSumsAsync(Ctx)", Read("Pages", "Leadership", "ActOn.cs"));
        var m = Read("Data", "Metrics.cs");
        var sums = m[m.IndexOf("public async Task<List<TrendRow>> SumsTrendAsync", StringComparison.Ordinal)..m.IndexOf("public const int AdoptionDays", StringComparison.Ordinal)];
        Assert.DoesNotContain("FactLicensedDay", sums);
        Assert.DoesNotContain("EnabledJoin", sums);   // use is everyone's
    }

    [Fact]
    public void Overview_counts_the_adopted_with_the_license_holders()
    {
        var m = Read("Data", "Metrics.cs");
        Assert.Contains("var adopted = withAdopted && c.Rollups;", m);
        Assert.Contains("h.AdoptedEnabled AS Adopted", m);
        Assert.Contains("LEFT JOIN dash.AdoptionDay AS ad ON ad.DateKey = e.EndKey AND ad.UserKey = ld.UserKey", m);
        Assert.Contains("tr.ToDictionary(w => w.Bucket, w => w.Adopted ?? 0)", Read("Pages", "Leadership", "Leadership.cs"));
    }
}
