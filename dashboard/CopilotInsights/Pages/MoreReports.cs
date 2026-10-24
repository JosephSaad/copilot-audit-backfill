using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

/* More reports for the original personas. */

namespace CopilotInsights.Pages.Adoption
{
    public sealed class RetentionModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Retention and habit";
        public override string Persona => "Adoption";
        public override string? OwnWindow => "the last 20 complete weeks";
        public override string Intro => "Do people keep using Copilot once they start? Of everyone active in a week, how many come back in each of the eight weeks after; and how many weeks of the last twelve people used it.";
        public override bool UsesFilters => true;

        protected override async Task BuildAsync()
        {
            var w1 = Forecasting.Weekly.LastCompleteWeek(Ctx.End);
            var start = w1.AddDays(-7 * 19 - 6);
            /* Each person's twenty weeks as bits (week 0 the oldest), and how many people share each pattern: the curve
               and the weeks-active count are worked out from the patterns. The page used to read every
               person-week (2.5 million rows at 210,000 people) and loop over them, 3 seconds on every visit.
               Cohorts: the 12 weeks that have 8 weeks after them in the 20-week window. */
            var patterns = await Db.QueryAsync<(int Weeks, int People)>($@"
WITH w AS (SELECT DISTINCT ud.UserKey, DATEDIFF(DAY, @first, d.WeekEnding) / 7 AS W
           FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
           JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
           WHERE ud.DateKey BETWEEN @a AND @b AND ud.Interactions > 0),
     m AS (SELECT w.UserKey, SUM(POWER(2, w.W)) AS Weeks FROM w GROUP BY w.UserKey)
SELECT m.Weeks, COUNT(*) AS People FROM m GROUP BY m.Weeks;", Ctx.Args(new { a = ReportContext.Key(start), b = ReportContext.Key(w1), first = start.AddDays(6) }));
            var curve = new double?[8];
            for (var lag = 1; lag <= 8; lag++)
            {
                long active = 0, back = 0;
                for (var c = 0; c < 12; c++)
                    foreach (var (weeks, people) in patterns)
                    {
                        if ((weeks & (1 << c)) == 0) continue;
                        active += people;
                        if ((weeks & (1 << (c + lag))) != 0) back += people;
                    }
                curve[lag - 1] = active > 0 ? back / (double)active : null;
            }
            Kpis.Add(new("Back the next week", Pct(curve[0]), "of people active in a week"));
            Kpis.Add(new("Still there 4 weeks on", Pct(curve[3]), "of people active in a week, active again four weeks later"));
            Kpis.Add(new("Still there 8 weeks on", Pct(curve[7]), "of people active in a week, active again eight weeks later"));
            Panels.Add(new("Share of a week's users active again, weeks later",
                Chart.Line(Enumerable.Range(1, 8).Select(i => $"+{i} wk").ToList(), [new Series("Came back", curve, 3)], Fmt.Percent, "Weekly retention", 1),
                "Each line follows the people who used Copilot in one week, over the weeks after it: twelve starting weeks, ending with the last complete week. A flattening line is a habit forming; one that keeps falling is novelty wearing off.", "half"));

            /* Weeks 8 to 19 are the last twelve. */
            var counts = patterns.Select(x => (N: System.Numerics.BitOperations.PopCount((uint)x.Weeks >> 8), x.People)).Where(x => x.N > 0)
                .GroupBy(x => x.N).ToDictionary(g => g.Key, g => g.Sum(x => x.People));
            Panels.Add(new("Weeks active out of the last twelve",
                Chart.Columns(Enumerable.Range(1, 12).Select(i => i.ToString()).ToList(), [new Series("People", Enumerable.Range(1, 12).Select(i => (double?)counts.GetValueOrDefault(i)).ToList(), 1)], Fmt.Count, "People by weeks active"),
                "People who used Copilot at least once in the last twelve weeks, by how many of those weeks. A pile on the right is habit; on the left, trial.", "half"));
        }
    }
}

namespace CopilotInsights.Pages.Compliance
{
    public sealed class UnitsModel(PageServices s, Metrics m) : InsightsPage(s)
    {
        public override string Title => "Labeled content by unit";
        public override string Persona => "Compliance";
        public override string Intro => "Which parts of the organization have Copilot working with labeled content, and what kind of content it is.";

        protected override async Task BuildAsync()
        {
            var units = SmallGroups.Shown(await m.UnitsAsync(Ctx), u => u.People, Ctx.MinGroupSize).Where(u => u.Interactions > 0).ToList();
            var series = (await m.UnitSeriesAsync(Ctx)).ToDictionary(x => (x.Unit, x.Bucket));
            var buckets = Buckets();
            var grid = new double?[units.Count, buckets.Count];
            for (var r = 0; r < units.Count; r++)
                for (var c = 0; c < buckets.Count; c++)
                    grid[r, c] = series.TryGetValue((units[r].Unit, buckets[c].Key), out var p) && p.Interactions > 0 ? p.Labeled / (double)p.Interactions : null;
            Panels.Add(new("Share of interactions touching labeled content", Chart.Heatmap(units.Select(u => u.Name).ToList(), Labels(), grid, Fmt.Percent, "Labeled share by unit", 1, unit: "of interactions touched labeled content"),
                $"Each part of the organization one level below. Darker is a larger share. {TrendNote}"));

            /* Counted per key pair, then named: grouping on the names worked out a string for each of the rows. */
            var mix = await Db.QueryAsync<(string Type, string Label, long N)>($@"
WITH g AS (SELECT f.ResourceTypeKey, f.SensitivityLabelKey, COUNT_BIG(*) AS N
           FROM bi.FactAccessedResource AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.ResourceTypeKey, f.SensitivityLabelKey)
SELECT COALESCE(vn.Name, t.ResourceType), COALESCE(sl.Name, LOWER(CAST(l.LabelId AS NVARCHAR(36))), N'(no label)'), SUM(g.N)
FROM g
JOIN bi.DimResourceType AS t ON t.ResourceTypeKey = g.ResourceTypeKey
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 31 AND vn.Value = t.ResourceType
JOIN bi.DimSensitivityLabel AS l ON l.SensitivityLabelKey = g.SensitivityLabelKey
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = LOWER(CAST(l.LabelId AS NVARCHAR(36)))
GROUP BY COALESCE(vn.Name, t.ResourceType), COALESCE(sl.Name, LOWER(CAST(l.LabelId AS NVARCHAR(36))), N'(no label)');", Ctx.Args());
            var types = mix.GroupBy(x => x.Type).OrderByDescending(g => g.Sum(x => x.N)).Take(8).Select(g => g.Key).ToList();
            var labels = mix.Where(x => x.Label != "(no label)").GroupBy(x => x.Label).OrderByDescending(g => g.Sum(x => x.N)).Take(6).Select(g => g.Key).ToList();
            var t2 = new Table { Headers = ["Resource type", "All", .. labels], Numeric = new HashSet<int>(Enumerable.Range(1, labels.Count + 1)) };
            foreach (var ty in types)
                t2.Rows.Add([ty, mix.Where(x => x.Type == ty).Sum(x => x.N), .. labels.Select(l => (object?)mix.Where(x => x.Type == ty && x.Label == l).Sum(x => x.N))]);
            CsvTable = t2;
            Panels.Add(new("Resources read, by type and label", t2.Render(), "The eight busiest resource types against the six commonest labels. Type names are set in Admin › Value names, label names in Admin › Reference data.", "full", CsvUrl()));
        }
    }
}

namespace CopilotInsights.Pages.Agents
{
    public sealed class HandoffsModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Agent hand-offs";
        public override string Persona => "Agents";
        public override string Intro => "When one agent hands a request to another agent: how often, and to which agents.";

        protected override async Task BuildAsync()
        {
            var tr = await Db.QueryAsync<(DateTime Bucket, long Calls, long All)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(CAST(f.CalledAnotherAgent AS INT)), COUNT_BIG(*)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
            var targets = await Db.QueryAsync<(string Target, long N, int People)>($@"
SELECT COALESCE(vn.Name, f.TargetAgentName), COUNT_BIG(*), COUNT(DISTINCT f.UserKey)
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
LEFT JOIN dash.ValueName AS vn ON vn.ColumnId = 12 AND vn.Value = f.TargetAgentName
WHERE f.DateKey BETWEEN @k0 AND @k1 AND f.CalledAnotherAgent = 1
GROUP BY f.TargetAgentName, vn.Name;", Ctx.Args());
            long calls = tr.Sum(x => x.Calls), all = tr.Sum(x => x.All);
            Kpis.Add(new("Hand-offs", Num(calls), $"{Pct(Ratio(calls, all))} of interactions"));
            Kpis.Add(new("Agents called", Num(targets.Count), "different agents another agent handed work to"));
            var buckets = Buckets();
            Panels.Add(new("Hand-offs over time", Chart.Columns(Labels(), [new Series("Hand-offs", buckets.Select(b => (double?)tr.Where(x => x.Bucket == b.Key).Sum(x => x.Calls)).ToList(), 5)], Fmt.Count, "Hand-offs"),
                TrendNote, "half"));
            Panels.Add(new("Agents called", Chart.Bars(targets.OrderByDescending(t => t.N).Take(12).Select(t => new Bar(t.Target, t.N, Note: AgentPeople(t.People))).ToList(), Fmt.Count, "Target agents"),
                targets.Count == 0 ? "No agent called another in the period." : null, "half"));
        }
    }
}

namespace CopilotInsights.Pages.Enablement
{
    public sealed class DepthModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Conversation depth";
        public override string Persona => "Enablement";
        public override string Intro => "How far people take a conversation with Copilot: prompts per session, how long a session lasts, and replies per prompt.";

        protected override async Task BuildAsync()
        {
            var sessions = await Db.QueryAsync<(int Bucket, long N)>($@"
SELECT CASE WHEN fs.PromptCount >= 10 THEN 10 ELSE fs.PromptCount END, COUNT_BIG(*)
FROM bi.FactSession AS fs {Ctx.Scope("fs.UserKey")}
WHERE fs.DateKey BETWEEN @k0 AND @k1 GROUP BY CASE WHEN fs.PromptCount >= 10 THEN 10 ELSE fs.PromptCount END;", Ctx.Args());
            var spans = await Db.QueryAsync<(int Bucket, long N)>($@"
SELECT CASE WHEN fs.SpanMinutes < 1 THEN 0 WHEN fs.SpanMinutes < 5 THEN 1 WHEN fs.SpanMinutes < 15 THEN 2 WHEN fs.SpanMinutes < 30 THEN 3 WHEN fs.SpanMinutes < 60 THEN 4 ELSE 5 END, COUNT_BIG(*)
FROM bi.FactSession AS fs {Ctx.Scope("fs.UserKey")}
WHERE fs.DateKey BETWEEN @k0 AND @k1
GROUP BY CASE WHEN fs.SpanMinutes < 1 THEN 0 WHEN fs.SpanMinutes < 5 THEN 1 WHEN fs.SpanMinutes < 15 THEN 2 WHEN fs.SpanMinutes < 30 THEN 3 WHEN fs.SpanMinutes < 60 THEN 4 ELSE 5 END;", Ctx.Args());
            var tr = await Db.QueryAsync<(DateTime Bucket, long Messages, long Prompts)>($@"
SELECT {Ctx.BucketSql("d")}, SUM(CAST(f.MessageCount AS BIGINT)), SUM(CAST(f.PromptCount AS BIGINT))
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
            long total = sessions.Sum(x => x.N);
            Kpis.Add(new("One-prompt sessions", Pct(Ratio(sessions.Where(x => x.Bucket <= 1).Sum(x => x.N), total)), "ask once and leave"));
            Kpis.Add(new("Five prompts or more", Pct(Ratio(sessions.Where(x => x.Bucket >= 5).Sum(x => x.N), total)), "real back-and-forth"));
            Panels.Add(new("Sessions by prompts", Chart.Columns(Enumerable.Range(1, 10).Select(i => i == 10 ? "10+" : i.ToString()).ToList(),
                [new Series("Sessions", Enumerable.Range(1, 10).Select(i => (double?)sessions.Where(x => x.Bucket == i).Sum(x => x.N)).ToList(), 1)], Fmt.Count, "Sessions by prompt count"),
                "How many prompts each session had.", "half"));
            string[] spanLabels = ["Under 1 min", "1-5 min", "5-15 min", "15-30 min", "30-60 min", "An hour or more"];
            Panels.Add(new("Sessions by length", Chart.Bars(spanLabels.Select((l, i) => new Bar(l, sessions.Count == 0 ? 0 : spans.Where(x => x.Bucket == i).Sum(x => x.N))).ToList(), Fmt.Count, "Sessions by length"),
                "From the first prompt to the last.", "half"));
            var buckets = Buckets();
            Panels.Add(new("Messages per prompt", Chart.Line(Labels(), [new Series("Messages per prompt", buckets.Select(b => { var r = tr.FirstOrDefault(x => x.Bucket == b.Key); return r.Prompts > 0 ? r.Messages / (double?)r.Prompts : null; }).ToList(), 2)], Fmt.Decimal1, "Messages per prompt"),
                $"Prompts and Copilot's replies together, per prompt: above 2, Copilot answered some prompts in more than one message. {TrendNote}"));
        }
    }
}
