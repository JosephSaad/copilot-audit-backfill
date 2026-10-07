using CopilotInsights.Data;
using Xunit;

namespace CopilotInsights.Tests;

/// <summary>
/// Admin > Health, the Audit searches card. A load's searches are rows only while it runs, so after every night that
/// finished the card read "0 submitted to Microsoft Purview in the last 24 hours", which a client took for nothing
/// fetched. It now says what is waiting, then what the last merged load took.
/// </summary>
public class AuditSearchesTests
{
    static readonly AuditSearches.Live Idle = new(0, 0, 0, 0, null);
    const string Merged = "2026-10-04 20:14 Eastern Time";

    [Fact]
    public void After_a_night_that_finished_the_card_names_the_last_load_and_its_searches()
    {
        var (text, status) = AuditSearches.Describe(Idle, new(Merged, 210016, 12579, 3, 0));
        Assert.Equal("No load is under way: 0 waiting or downloading; 0 had to be resubmitted; 0 failed. " +
                     "The last load, merged 2026-10-04 20:14 Eastern Time, fetched 210,016 records (12,579 new) in 3 searches. " + AuditSearches.Limit, text);
        Assert.Equal("status-ok", status);
        Assert.DoesNotContain("in the last 24 hours", text);   // the count that read 0 after every run that succeeded
    }

    [Fact]
    public void A_load_merged_before_its_searches_were_kept_is_named_without_them()
    {
        var (text, _) = AuditSearches.Describe(Idle, new(Merged, 210016, 12579, null, null));
        Assert.Contains("fetched 210,016 records (12,579 new). Microsoft limits", text);
        Assert.DoesNotContain("search.", text);
        Assert.DoesNotContain("searches.", text);
    }

    [Fact]
    public void One_record_and_one_search_are_singular_and_resubmitted_searches_are_said()
    {
        var (one, _) = AuditSearches.Describe(Idle, new(Merged, 1, 1, 1, 0));
        Assert.Contains("fetched 1 record (1 new) in 1 search.", one);
        var (again, status) = AuditSearches.Describe(Idle, new(Merged, 5000, 40, 6, 2));
        Assert.Contains("in 6 searches, 2 of them resubmitted.", again);
        Assert.Equal("status-ok", status);
        Assert.Equal("status-warn", AuditSearches.Describe(Idle, new(Merged, 5000, 40, 30, 11)).Status);   // the last load was throttled
    }

    [Fact]
    public void A_database_with_no_merged_load_says_so()
    {
        var (text, status) = AuditSearches.Describe(Idle, null);
        Assert.StartsWith("No load is under way: 0 waiting or downloading; 0 had to be resubmitted; 0 failed. No load has been merged here yet. ", text);
        Assert.Equal("status-ok", status);
    }

    [Fact]
    public void A_load_under_way_shows_its_searches_and_the_oldest_one_waiting()
    {
        var (text, status) = AuditSearches.Describe(new(4, 2, 0, 1, "2026-10-05 08:00 Eastern Time"), new(Merged, 210016, 12579, 3, 0));
        Assert.StartsWith("4 submitted to Microsoft Purview for the load under way; 2 waiting or downloading (the oldest covers 2026-10-05 08:00 Eastern Time); " +
                          "1 had to be resubmitted; 0 failed. The last load, merged ", text);
        Assert.Equal("status-ok", status);
        /* Waiting with nothing submitted yet (the first searches are planned, not sent) is still a load under way. */
        Assert.StartsWith("0 submitted to Microsoft Purview for the load under way; 3 waiting or downloading", AuditSearches.Describe(new(0, 3, 0, 0, null), null).Text);
    }

    [Theory]
    [InlineData(0, 1, 0, "status-bad")]     // a search failed
    [InlineData(21, 0, 0, "status-warn")]   // many waiting
    [InlineData(20, 0, 0, "status-ok")]
    [InlineData(0, 0, 11, "status-warn")]   // many resubmitted
    [InlineData(0, 0, 10, "status-ok")]
    [InlineData(21, 1, 11, "status-bad")]   // a failure outranks the rest
    public void The_card_keeps_its_colors(int waiting, int failed, int resubmitted, string expected) =>
        Assert.Equal(expected, AuditSearches.Describe(new(waiting, waiting, failed, resubmitted, null), null).Status);
}
