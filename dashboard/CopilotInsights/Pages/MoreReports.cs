using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

/* More reports for the original personas (2.2.0). */

namespace CopilotInsights.Pages.Leadership
{
    public sealed class ScorecardModel(PageServices s, Metrics m) : InsightsPage(s)
    {
        public override string Title => "Scorecard";
        public override string Persona => "Leadership";
        public override string Intro => "This period against the one before it, of the same length: what moved, and in which part of the organization. "
            + "Adoption splits in two: Premium, license holders who used what the license pays for (Copilot in Word, Excel, PowerPoint, Outlook or Teams, or chat over work content); "
            + "and free only, those who used only what is free without a license (web Copilot Chat, agents).";

        protected override async Task BuildAsync()
        {
            /* Premium use is recorded once the rollups are rebuilt with 2.3.0; before that it is all zero, so the split is left out. */
            var split = await Forecasting.ActionsModel.PremiumReadyAsync(Db, Ctx.StartKey, Ctx.EndKey);
            if (!split)
                Notice = "Adoption is not yet split into Premium and free use here. An administrator separates them by rebuilding the dashboard once (Admin > Health, Rebuild every day).";
            var prev = new ReportContext
            {
                Viewer = Ctx.Viewer, Days = Ctx.Days, Start = Ctx.Start.AddDays(-Ctx.Days), End = Ctx.End.AddDays(-Ctx.Days),
                Root = Ctx.Root, RootName = Ctx.RootName, MinGroupSize = Ctx.MinGroupSize, InactiveDays = Ctx.InactiveDays, OrgTopUpn = Ctx.OrgTopUpn,
            };
            var now = await m.TotalsAsync(Ctx);
            var before = await m.TotalsAsync(prev);
            string Delta(double a, double b, bool pct = false) =>
                b == 0 ? "no earlier figure" : pct ? $"{(a - b) * 100:+0.0;-0.0;0} points on the {Ctx.Days} days before" : $"{(a - b) / b:+0%;-0%;0%} on the {Ctx.Days} days before";
            double Adopt(Totals t) => t.LicensedNow > 0 ? t.ActiveLicensed / (double)t.LicensedNow : 0;
            /* Premium + free only = Adoption: every active license holder either used a Premium feature or did not. */
            double Premium(Totals t) => t.LicensedNow > 0 ? t.PremiumLicensed / (double)t.LicensedNow : 0;
            double FreeOnly(Totals t) => t.LicensedNow > 0 ? (t.ActiveLicensed - t.PremiumLicensed) / (double)t.LicensedNow : 0;
            Kpis.Add(new("People using Copilot", Num(now.Active), Delta(now.Active, before.Active)));
            Kpis.Add(new("Adoption", Pct(Adopt(now)), Delta(Adopt(now), Adopt(before), pct: true)));
            if (split)
            {
                Kpis.Add(new("Premium adoption", Pct(Premium(now)), Delta(Premium(now), Premium(before), pct: true)));
                Kpis.Add(new("Free-only adoption", Pct(FreeOnly(now)), Delta(FreeOnly(now), FreeOnly(before), pct: true)));
            }
            Kpis.Add(new("Premium license holders", Num(now.LicensedNow), Delta(now.LicensedNow, before.LicensedNow)));
            Kpis.Add(new("Interactions", Num(now.Interactions), Delta(now.Interactions, before.Interactions)));
            Kpis.Add(new("Sessions with a follow-up", Pct(Ratio(now.MultiTurn, now.Sessions)), Delta(Ratio(now.MultiTurn, now.Sessions) ?? 0, Ratio(before.MultiTurn, before.Sessions) ?? 0, pct: true)));

            var u1 = (await m.UnitsAsync(Ctx)).ToDictionary(u => u.Unit);
            var u0 = (await m.UnitsAsync(prev)).ToDictionary(u => u.Unit);
            var t = new Table
            {
                Headers = split
                    ? ["Unit", "People", "Adoption", "Premium", "Premium change", "Free only", "Free-only change", "Active people", "Active change", "Interactions per active person", "Interactions change"]
                    : ["Unit", "People", "Adoption", "Adoption change", "Active people", "Active change", "Interactions per active person", "Interactions change"],
                Numeric = Enumerable.Range(1, split ? 10 : 7).ToHashSet(),
            };
            var rows = new List<(UnitRow Now, UnitRow? Before)>();
            foreach (var u in u1.Values.Where(u => u.People >= Ctx.MinGroupSize)) rows.Add((u, u0.GetValueOrDefault(u.Unit)));
            HtmlString Arrow(double? d, bool pct) => d is null ? new("<span class=\"muted\">—</span>")
                : new($"<span class=\"{(d > 0 ? "status-ok" : d < 0 ? "status-bad" : "muted")}\">{(d > 0 ? "▲" : d < 0 ? "▼" : "")} {(pct ? $"{Math.Abs(d.Value) * 100:0.0} pts" : $"{Math.Abs(d.Value):0%}")}</span>");
            foreach (var (n, b) in rows.OrderByDescending(r => r.Now.People))
            {
                double? a1 = Ratio(n.ActiveLicensed, n.Licensed), a0 = b is null ? null : Ratio(b.ActiveLicensed, b.Licensed);
                double? i1 = n.Active > 0 ? n.Interactions / (double)n.Active : null, i0 = b is { Active: > 0 } ? b.Interactions / (double)b.Active : null;
                var name = n.Drillable ? (object)new Link(n.Name, Ctx.Url(Request.Path, org: n.Unit)) : n.Name;
                var rest = new object?[] { n.Active, Arrow(b is { Active: > 0 } ? (n.Active - b.Active) / (double)b.Active : null, false),
                                           i1 is null ? null : Math.Round(i1.Value, 1), Arrow(i0 is > 0 ? (i1 - i0) / i0 : null, false) };
                if (split)
                {
                    double? p1 = Ratio(n.PremiumLicensed, n.Licensed), p0 = b is null ? null : Ratio(b.PremiumLicensed, b.Licensed);
                    double? f1 = Ratio(n.ActiveLicensed - n.PremiumLicensed, n.Licensed), f0 = b is null ? null : Ratio(b.ActiveLicensed - b.PremiumLicensed, b.Licensed);
                    t.Rows.Add([name, n.People, Pct(a1), Pct(p1), Arrow(p1 - p0, true), Pct(f1), Arrow(f1 - f0, true), .. rest]);
                }
                else
                    t.Rows.Add([name, n.People, Pct(a1), Arrow(a1 - a0, true), .. rest]);
            }
            Panels.Add(new("By unit, against the period before", t.Render(),
                $"Each part of the organization one level below. Units under {Ctx.MinGroupSize} people are left out. "
                + (split ? "Adoption is Premium plus free only: license holders who used a Premium feature, and those who used only free Copilot Chat or agents. " : "")
                + "Change in adoption is in percentage points; the others are relative."));
        }
    }
}

namespace CopilotInsights.Pages.Adoption
{
    public sealed class RetentionModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Retention and habit";
        public override string Persona => "Adoption";
        public override string? OwnWindow => "the last 12 complete weeks";
        public override string Intro => "Do people keep using Copilot once they start? Of everyone active in a week, how many come back in each of the eight weeks after; and how many weeks of the last twelve people used it.";
        public override bool UsesFilters => true;

        protected override async Task BuildAsync()
        {
            var w1 = Forecasting.Weekly.LastCompleteWeek(Ctx.End);
            var start = w1.AddDays(-7 * 19 - 6);
            var weeks = await Db.QueryAsync<(int UserKey, DateTime WeekEnding)>($@"
SELECT DISTINCT ud.UserKey, d.WeekEnding FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
WHERE ud.DateKey BETWEEN @a AND @b AND ud.Interactions > 0;", Ctx.Args(new { a = ReportContext.Key(start), b = ReportContext.Key(w1) }));
            var byUser = weeks.GroupBy(w => w.UserKey).ToDictionary(g => g.Key, g => g.Select(x => x.WeekEnding).ToHashSet());
            /* Cohorts: the 12 weeks that have 8 weeks after them in the 20-week window. */
            var curve = new double?[8];
            for (var lag = 1; lag <= 8; lag++)
            {
                long active = 0, back = 0;
                for (var c = 0; c < 12; c++)
                {
                    var wk = start.AddDays(6 + 7 * c);
                    foreach (var set in byUser.Values.Where(v => v.Contains(wk)))
                    {
                        active++;
                        if (set.Contains(wk.AddDays(7 * lag))) back++;
                    }
                }
                curve[lag - 1] = active > 0 ? back / (double)active : null;
            }
            Kpis.Add(new("Back the next week", Pct(curve[0]), "of people active in a week"));
            Kpis.Add(new("Still there 4 weeks on", Pct(curve[3])));
            Kpis.Add(new("Still there 8 weeks on", Pct(curve[7])));
            Panels.Add(new("Share of a week's users active again, weeks later",
                Chart.Line(Enumerable.Range(1, 8).Select(i => $"+{i} wk").ToList(), [new Series("Came back", curve, 3)], Fmt.Percent, "Weekly retention", 1),
                "Each line follows the people who used Copilot in one week, over the weeks after it: twelve starting weeks, ending with the last complete week. A flattening line is a habit forming; one that keeps falling is novelty wearing off.", "half"));

            var last12 = Enumerable.Range(0, 12).Select(i => w1.AddDays(-7 * i)).ToHashSet();
            var counts = byUser.Values.Select(v => v.Count(last12.Contains)).Where(n => n > 0).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
            Panels.Add(new("Weeks active out of the last twelve",
                Chart.Columns(Enumerable.Range(1, 12).Select(i => i.ToString()).ToList(), [new Series("People", Enumerable.Range(1, 12).Select(i => (double?)counts.GetValueOrDefault(i)).ToList(), 1)], Fmt.Count, "People by weeks active"),
                "People who used Copilot at least once in the last twelve weeks, by how many of those weeks. A pile on the right is habit; on the left, trial.", "half"));
        }
    }
}

namespace CopilotInsights.Pages.Security
{
    public sealed class UnitsModel(PageServices s, Metrics m) : InsightsPage(s)
    {
        public override string Title => "Security signals by unit";
        public override string Persona => "Security";
        public override string Intro => "Where jailbreak attempts, prompt injection and blocked content cluster, in each part of the organization one level below, over time. Rates per 1,000 interactions, so big and small units compare fairly.";

        protected override async Task BuildAsync()
        {
            var units = (await m.UnitsAsync(Ctx)).Where(u => u.People >= Ctx.MinGroupSize && u.Interactions > 0).ToList();
            var series = await m.UnitSeriesAsync(Ctx);
            var buckets = Buckets();
            var grid = new double?[units.Count, buckets.Count];
            for (var r = 0; r < units.Count; r++)
                for (var c = 0; c < buckets.Count; c++)
                {
                    var p = series.FirstOrDefault(x => x.Unit == units[r].Unit && x.Bucket == buckets[c].Key);
                    grid[r, c] = p is { Interactions: > 0 } ? p.Flags * 1000.0 / p.Interactions : null;
                }
            var worst = units.OrderByDescending(u => u.Flags * 1000.0 / u.Interactions).FirstOrDefault();
            if (worst is not null) Kpis.Add(new("Highest rate", worst.Name, $"{worst.Flags * 1000.0 / worst.Interactions:0.0} per 1,000 interactions", "crit"));
            Kpis.Add(new("Security signals", Num(units.Sum(u => u.Flags)), $"in {Num(units.Sum(u => u.Interactions))} interactions"));
            Panels.Add(new("Signals per 1,000 interactions, by unit", Chart.Heatmap(units.Select(u => u.Name).ToList(), Labels(), grid, Fmt.Decimal1, "Signal rate by unit", unit: "signals per 1,000 interactions"),
                $"Jailbreak, prompt injection and blocked resources together. Darker is a higher rate. Units under {Ctx.MinGroupSize} people are left out. {TrendNote}"));
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
            var units = (await m.UnitsAsync(Ctx)).Where(u => u.People >= Ctx.MinGroupSize && u.Interactions > 0).ToList();
            var series = await m.UnitSeriesAsync(Ctx);
            var buckets = Buckets();
            var grid = new double?[units.Count, buckets.Count];
            for (var r = 0; r < units.Count; r++)
                for (var c = 0; c < buckets.Count; c++)
                {
                    var p = series.FirstOrDefault(x => x.Unit == units[r].Unit && x.Bucket == buckets[c].Key);
                    grid[r, c] = p is { Interactions: > 0 } ? p.Labeled / (double)p.Interactions : null;
                }
            Panels.Add(new("Share of interactions touching labeled content", Chart.Heatmap(units.Select(u => u.Name).ToList(), Labels(), grid, Fmt.Percent, "Labeled share by unit", 1, unit: "of interactions touched labeled content"),
                $"Each part of the organization one level below. Darker is a larger share. {TrendNote}"));

            /* Counted per key pair, then named (2.4.2): grouping on the names worked out a string for each of the rows. */
            var mix = await Db.QueryAsync<(string Type, string Label, long N)>($@"
WITH g AS (SELECT f.ResourceTypeKey, f.SensitivityLabelKey, COUNT_BIG(*) AS N
           FROM bi.FactAccessedResource AS f {Ctx.Scope("f.UserKey")}
           WHERE f.DateKey BETWEEN @k0 AND @k1
           GROUP BY f.ResourceTypeKey, f.SensitivityLabelKey)
SELECT t.ResourceType, COALESCE(sl.Name, LOWER(CAST(l.LabelId AS NVARCHAR(36))), N'(no label)'), SUM(g.N)
FROM g
JOIN bi.DimResourceType AS t ON t.ResourceTypeKey = g.ResourceTypeKey
JOIN bi.DimSensitivityLabel AS l ON l.SensitivityLabelKey = g.SensitivityLabelKey
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = LOWER(CAST(l.LabelId AS NVARCHAR(36)))
GROUP BY t.ResourceType, COALESCE(sl.Name, LOWER(CAST(l.LabelId AS NVARCHAR(36))), N'(no label)');", Ctx.Args());
            var types = mix.GroupBy(x => x.Type).OrderByDescending(g => g.Sum(x => x.N)).Take(8).Select(g => g.Key).ToList();
            var labels = mix.Where(x => x.Label != "(no label)").GroupBy(x => x.Label).OrderByDescending(g => g.Sum(x => x.N)).Take(6).Select(g => g.Key).ToList();
            var t2 = new Table { Headers = ["Resource type", "All", .. labels], Numeric = new HashSet<int>(Enumerable.Range(1, labels.Count + 1)) };
            foreach (var ty in types)
                t2.Rows.Add([ty, mix.Where(x => x.Type == ty).Sum(x => x.N), .. labels.Select(l => (object?)mix.Where(x => x.Type == ty && x.Label == l).Sum(x => x.N))]);
            Panels.Add(new("Resources read, by type and label", t2.Render(), "The eight busiest resource types against the six commonest labels. Names from Admin › Reference data."));
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
            Kpis.Add(new("Agents called", Num(targets.Count)));
            var buckets = Buckets();
            Panels.Add(new("Hand-offs over time", Chart.Columns(Labels(), [new Series("Hand-offs", buckets.Select(b => (double?)tr.Where(x => x.Bucket == b.Key).Sum(x => x.Calls)).ToList(), 5)], Fmt.Count, "Hand-offs"),
                TrendNote, "half"));
            Panels.Add(new("Agents called", Chart.Bars(targets.OrderByDescending(t => t.N).Take(12).Select(t => new Bar(t.Target, t.N, Note: $"{t.People:#,0} people")).ToList(), Fmt.Count, "Target agents"),
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

namespace CopilotInsights.Pages.Governance
{
    public sealed class ControlsModel(PageServices s, Metrics m) : InsightsPage(s)
    {
        public override string Title => "Copilot controls";
        public override string Persona => "Governance";
        public override string Intro => "The controls around Copilot at work: memory updates, deferred data-loss checks, and searches of the public web, over time and by unit.";

        protected override async Task BuildAsync()
        {
            var tr = await Db.QueryAsync<(DateTime Bucket, long All, long Memory, long Dlp, long Web)>($@"
SELECT {Ctx.BucketSql("d")}, COUNT_BIG(*), SUM(CAST(f.MemoryUpdated AS INT)), SUM(CAST(f.DlpEvaluationDeferred AS INT)), SUM(CAST(f.UsedWebSearch AS INT))
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")} JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE f.DateKey BETWEEN @k0 AND @k1 GROUP BY {Ctx.BucketSql("d")};", Ctx.Args());
            var buckets = Buckets();
            (long All, long Memory, long Dlp, long Web) At(DateTime b) { var r = tr.FirstOrDefault(x => x.Bucket == b); return (r.All, r.Memory, r.Dlp, r.Web); }
            Kpis.Add(new("Memory updated", Pct(Ratio(tr.Sum(x => x.Memory), tr.Sum(x => x.All))), "of interactions"));
            Kpis.Add(new("DLP check deferred", Num(tr.Sum(x => x.Dlp))));
            Kpis.Add(new("Grounded on the web", Pct(Ratio(tr.Sum(x => x.Web), tr.Sum(x => x.All)))));
            Panels.Add(new("Memory updates and web search", Chart.Line(Labels(), [
                new Series("Memory updated", buckets.Select(b => Ratio(At(b.Key).Memory, At(b.Key).All)).ToList(), 5),
                new Series("Searched the web", buckets.Select(b => Ratio(At(b.Key).Web, At(b.Key).All)).ToList(), 1),
            ], Fmt.Percent, "Memory and web share"), $"Shares of interactions. {TrendNote}", "half"));
            Panels.Add(new("Deferred data-loss checks", Chart.Columns(Labels(), [new Series("Deferred", buckets.Select(b => (double?)At(b.Key).Dlp).ToList(), 4)], Fmt.Count, "DLP deferred"),
                "Interactions that Copilot's own record marks as having their data-loss prevention (DLP) check deferred. Microsoft sets the mark; a rising share is worth raising with them.", "half"));

            var units = (await m.UnitsAsync(Ctx)).Where(u => u.People >= Ctx.MinGroupSize).ToDictionary(u => u.Unit);
            var series = await m.UnitSeriesAsync(Ctx);
            var web = series.GroupBy(x => x.Unit).Where(g => units.ContainsKey(g.Key))
                .Select(g => (Unit: units[g.Key], Web: g.Sum(x => x.Web), All: g.Sum(x => x.Interactions))).Where(x => x.All > 0).OrderByDescending(x => x.Web / (double)x.All).ToList();
            Panels.Add(new("Web search by unit", Chart.Bars(web.Take(15).Select(x => new Bar(x.Unit.Name, x.Web / (double)x.All, x.Unit.Drillable ? Ctx.Url(Request.Path, org: x.Unit.Unit) : null, $"{x.Web:#,0} of {x.All:#,0}")).ToList(), Fmt.Percent, "Web grounding by unit", 1),
                $"Each part of the organization one level below; units under {Ctx.MinGroupSize} people are left out."));
        }
    }
}
