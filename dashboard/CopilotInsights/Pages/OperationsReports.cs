using System.Net;
using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Adoption
{
    /// <summary>Progress against the adoption targets an administrator set (Admin > Targets and assumptions).</summary>
    public sealed class TargetsModel(PageServices s, OpsReports reports) : InsightsPage(s)
    {
        public override string Title => "Targets";
        public override string Persona => "Adoption";
        public override bool UsesFilters => false;
        public override string Intro => "Each target against where it stood before it started, where it stands now (the last 30 days) and where a straight line to the target says it should be by now.";

        protected override async Task BuildAsync()
        {
            var all = await reports.TargetsAsync(Ctx);
            /* A target for part of the organisation is shown to people who may see that part. */
            var mine = Viewer.OrgWide ? all : Viewer.Upn is null ? [] :
                all.Where(t => t.RootUpn is not null).ToList() is var scoped && scoped.Count > 0
                    ? (await Db.QueryAsync<string>("SELECT DescendantUpn FROM dash.OrgClosure WHERE AncestorUpn = @v", new { v = Viewer.Upn })).ToHashSet(StringComparer.OrdinalIgnoreCase) is var under
                        ? scoped.Where(t => under.Contains(t.RootUpn!)).ToList() : []
                    : [];
            if (mine.Count == 0)
            {
                Notice = all.Count == 0 ? "No targets yet. An administrator sets them in Admin > Targets and assumptions." : "No target covers the part of the organisation you see.";
                return;
            }
            Kpis.Add(new("Targets", Num(mine.Count)));
            Kpis.Add(new("Met or on track", Num(mine.Count(t => t.Status is "Met" or "On track"))));
            Kpis.Add(new("Behind", Num(mine.Count(t => t.Status == "Behind")), null, mine.Any(t => t.Status == "Behind") ? "warn" : null));
            Kpis.Add(new("Missed", Num(mine.Count(t => t.Status == "Missed")), null, mine.Any(t => t.Status == "Missed") ? "crit" : null));
            string Unit(TargetRow t) => t.Metric == "ActivePeople" ? "" : "%";
            Panels.Add(new("Progress", Chart.Bars(mine.Select(t => new Bar($"{Label(t.Metric)}: {t.RootName ?? "Whole organisation"}",
                t.TargetValue > 0 && t.Current is not null ? t.Current / (double)t.TargetValue : null,
                Note: $"{t.Current:#,0.#}{Unit(t)} of {t.TargetValue:#,0.#}{Unit(t)} by {t.DueDate:d MMM yyyy}; {t.Status}",
                Slot: t.Status switch { "Met" or "On track" => 3, "Behind" => 5, "Missed" => 6, _ => 1 })).ToList(), Fmt.Percent, "Progress to each target", 1),
                "The share of the target reached."));
            var tb = new Table { Headers = ["Target", "For", "Before it started", "Now", "Should be by now", "Target", "Due", "Status", "Note"], Numeric = new HashSet<int> { 2, 3, 4, 5 } };
            foreach (var t in mine)
                tb.Rows.Add([Label(t.Metric), t.RootName ?? t.RootUpn ?? "Whole organisation", Round(t.Baseline), Round(t.Current), Round(t.Expected), (double)t.TargetValue,
                             t.DueDate, new HtmlString($"<span class=\"status-{(t.Status is "Met" or "On track" ? "ok" : t.Status == "Behind" ? "warn" : t.Status == "Missed" ? "bad" : "warn")}\">{E(t.Status)}</span>"), t.Note]);
            Panels.Add(new("Every target", tb.Render(), "Adoption: licensed people who used Copilot, over people licensed. Share active: people who used it, over everyone in the directory. People active: a count."));
        }

        static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);
        public static string Label(string metric) => metric switch { "Adoption" => "Adoption", "ActiveShare" => "Share of people active", _ => "People active" };
    }

    /// <summary>New joiners: how long from getting a licence (or joining) to first using Copilot.</summary>
    public sealed class OnboardingModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "New joiners";
        public override string Persona => "Adoption";
        public override string Intro => "People licensed in the period, and people hired in it where Entra has a hire date: how many days until they first used Copilot, and how many have not yet.";

        static readonly (string Label, int Lo, int Hi)[] Bands = [("Within a week", 0, 7), ("8 to 30 days", 8, 30), ("31 to 90 days", 31, 90), ("Over 90 days", 91, int.MaxValue)];

        sealed class Row
        {
            public string Dept { get; set; } = "";
            public int? Days { get; set; }
        }

        protected override async Task BuildAsync()
        {
            var licensed = await Db.QueryAsync<Row>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Dept, CAST(du.LicensedSinceUtc AS DATE) AS Since, du.UserKey
INTO #c
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
WHERE du.CopilotLicenceToday = N'Licensed' AND du.LicensedSinceUtc >= @d0 AND du.LicensedSinceUtc < DATEADD(DAY, 1, @d1) AND ISNULL(du.AccountEnabled, 1) = 1;
SELECT c.Dept, DATEDIFF(DAY, c.Since, (SELECT MIN(d.[Date]) FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
                                      WHERE ud.UserKey = c.UserKey AND ud.Interactions > 0 AND d.[Date] >= c.Since)) AS Days
FROM #c AS c;", Ctx.Args());
            var hasHire = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.CopilotLicense', N'EmployeeHireDate') IS NULL THEN 0 ELSE 1 END") == 1;
            var hired = !hasHire ? [] : await Db.QueryAsync<Row>($@"
SELECT ISNULL(NULLIF(du.Department, N''), N'(no department)') AS Dept, cl.EmployeeHireDate AS Since, du.UserKey
INTO #h
FROM bi.DimUser AS du {Ctx.Scope("du.UserKey")}
JOIN audit.CopilotLicense AS cl ON cl.UserPrincipalName = du.UserPrincipalName
WHERE cl.EmployeeHireDate BETWEEN @d0 AND @d1 AND ISNULL(du.AccountEnabled, 1) = 1;
SELECT h.Dept, DATEDIFF(DAY, h.Since, (SELECT MIN(d.[Date]) FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
                                      WHERE ud.UserKey = h.UserKey AND ud.Interactions > 0 AND d.[Date] >= h.Since)) AS Days
FROM #h AS h;", Ctx.Args());

            if (licensed.Count == 0 && hired.Count == 0) { Notice = "Nobody was licensed or hired in the period."; return; }
            var started = licensed.Where(r => r.Days is not null).Select(r => r.Days!.Value).OrderBy(d => d).ToList();
            Kpis.Add(new("Licensed in the period", Num(licensed.Count)));
            Kpis.Add(new("Have used it", Num(started.Count), licensed.Count > 0 ? Pct(Ratio(started.Count, licensed.Count)) : null));
            Kpis.Add(new("Median days to first use", started.Count >= Ctx.MinGroupSize ? $"{started[started.Count / 2]}" : "—", "from the licence"));
            Kpis.Add(new("Not started yet", Num(licensed.Count - started.Count), null, licensed.Count - started.Count > 0 ? "warn" : null));

            List<Bar> Dist(List<Row> rows) => Bands.Select(b => new Bar(b.Label, rows.Count(r => r.Days is int d && d >= b.Lo && d <= b.Hi), Slot: 3))
                .Append(new Bar("Not yet", rows.Count(r => r.Days is null), Slot: 5)).ToList();
            Panels.Add(new("From licence to first use", Chart.Bars(Dist(licensed), Fmt.Count, "Days from licence to first use"),
                "People licensed in the period, by how soon they first used Copilot. The Adoption role sees who has not started (Adoption > Not started).", "half"));
            if (hired.Count > 0)
                Panels.Add(new("From hire to first use", Chart.Bars(Dist(hired), Fmt.Count, "Days from hire to first use"),
                    "People with an Entra hire date (employeeHireDate) in the period.", "half"));

            var depts = licensed.GroupBy(r => r.Dept).Select(g => (Name: g.Key, People: g.Count(), Started: g.Count(r => r.Days is not null),
                    Median: g.Where(r => r.Days is not null).Select(r => r.Days!.Value).OrderBy(d => d).ToList())).ToList();
            var small = depts.Where(d => d.People < Ctx.MinGroupSize).ToList();
            var shown = depts.Where(d => d.People >= Ctx.MinGroupSize).OrderByDescending(d => d.People).ToList();
            var tb = new Table { Headers = ["Department", "Licensed in the period", "Have used it", "Median days to first use"], Numeric = new HashSet<int> { 1, 2, 3 } };
            foreach (var d in shown) tb.Rows.Add([d.Name, d.People, d.Started, d.Median.Count >= Ctx.MinGroupSize ? d.Median[d.Median.Count / 2] : null]);
            if (small.Count > 0) tb.Rows.Add(["Smaller groups", small.Sum(d => d.People), small.Sum(d => d.Started), null]);
            Panels.Add(new("By department", tb.Render(), small.Count > 0 ? $"{small.Count} department(s) with fewer than {Ctx.MinGroupSize} new licences are combined." : null));
        }
    }

    /// <summary>Days when a department's use dropped or jumped against the same weekday in the weeks before.</summary>
    public sealed class AnomaliesModel(PageServices s, OpsData ops) : InsightsPage(s)
    {
        public override string Title => "Drops and spikes";
        public override string Persona => "Adoption";
        public override bool UsesFilters => false;
        public override string Intro => "Days in the last two weeks when a department's Copilot use was far from its usual level for that weekday (the average of the four weeks before). A drop often means an outage, a policy change or a holiday; a spike, a launch or a training session.";

        protected override async Task BuildAsync()
        {
            var drop = await S.Settings.IntAsync("AnomalyDropPercent", 50);
            var spike = await S.Settings.IntAsync("AnomalySpikePercent", 200);
            var series = await ops.DepartmentDaysAsync(Ctx.End.AddDays(-41), Ctx.End, Ctx.MinGroupSize, Ctx.Root);
            var found = Anomalies.Find(series, Ctx.End.AddDays(-13), drop, spike);
            Kpis.Add(new("Departments watched", Num(series.Count), $"with at least {Ctx.MinGroupSize} people"));
            Kpis.Add(new("Drops", Num(found.Count(a => a.Kind == "Drop")), $"{drop}% or more below usual", found.Any(a => a.Kind == "Drop") ? "warn" : null));
            Kpis.Add(new("Spikes", Num(found.Count(a => a.Kind == "Spike")), $"{spike}% or more above usual"));
            var tb = new Table { Headers = ["Day", "Department", "Kind", "Interactions", "Usual for the weekday", "Change"], Numeric = new HashSet<int> { 3, 4 }, EmptyText = "Nothing stood out in the last two weeks." };
            foreach (var a in found) tb.Rows.Add([a.Day, a.Unit, a.Kind, a.Value, Math.Round(a.Baseline), $"{a.Change:+0%;-0%}"]);
            Panels.Add(new("Days that stood out", tb.Render(), $"The thresholds are set in Admin > Alerts and digests, which can also email them. Up to {Ctx.End:d MMM yyyy}."));
            var top = found.Select(a => a.Unit).Distinct().Take(8).ToList();
            if (top.Count > 0)
            {
                var days = Enumerable.Range(0, 42).Select(i => Ctx.End.AddDays(-41 + i)).ToList();
                var cells = new double?[top.Count, days.Count];
                for (var i = 0; i < top.Count; i++)
                    for (var j = 0; j < days.Count; j++)
                        cells[i, j] = series[top[i]].TryGetValue(days[j], out var v) ? v : null;
                Panels.Add(new("Six weeks of the departments that stood out", Chart.Heatmap(top, days.Select(d => d.ToString("d/M")).ToList(), cells, Fmt.Count, "Daily interactions by department"),
                    "Darker is busier. Weekends are usually light."));
            }
        }
    }
}

namespace CopilotInsights.Pages.Leadership
{
    /// <summary>Estimated time saved and what it is worth, from the administrators' assumptions.</summary>
    public sealed class ValueModel(PageServices s, OpsReports reports) : InsightsPage(s)
    {
        public override string Title => "Value and time saved";
        public override string Persona => "Leadership";
        public override string Intro => "An estimate: interactions in each app times the minutes one saves, valued at an hourly cost. The minutes and the hourly cost are your organisation's assumptions (Admin > Targets and assumptions); change them and every figure here follows.";

        protected override async Task BuildAsync()
        {
            var v = await reports.ValueAsync(Ctx);
            var cur = await S.Settings.StringAsync("LicenceCurrency") ?? "USD";
            var price = decimal.TryParse(await S.Settings.StringAsync("LicenceMonthlyCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 30m;
            var t = await HttpContext.RequestServices.GetRequiredService<Metrics>().TotalsAsync(Ctx);
            var hours = v.ByApp.Sum(x => x.Hours);
            var value = v.ByApp.Sum(x => x.Value);
            var licenceCost = t.LicensedNow * price * (decimal)(Ctx.Days / 30.4);
            Kpis.Add(new("Hours saved (estimate)", Num(hours), $"{v.DefaultMinutes:0.#} minutes an interaction unless set per app"));
            Kpis.Add(new($"Worth ({cur})", Num((double)value), $"at {v.HourlyCost:#,0.##} {cur} an hour"));
            Kpis.Add(new("Per person using Copilot", t.Active > 0 ? $"{hours / t.Active:0.#} h" : "—", "in the period"));
            Kpis.Add(new("Against the licence cost", licenceCost > 0 ? $"{value / licenceCost:0.#}×" : "—", $"licences for the period: {licenceCost:#,0} {cur}"));
            Panels.Add(new("Hours saved by app", Chart.Bars(v.ByApp.Take(12).Select(a => new Bar(a.Name, a.Hours, Note: $"{a.Interactions:#,0} interactions")).ToList(), Fmt.Count, "Hours saved by app"),
                "Set the minutes each app saves in Admin > Targets and assumptions.", "half"));
            var shown = v.ByDepartment.Where(d => d.People >= Ctx.MinGroupSize).ToList();
            var small = v.ByDepartment.Where(d => d.People < Ctx.MinGroupSize).ToList();
            var tb = new Table { Headers = ["Department", "People using it", "Interactions", "Hours saved", $"Worth ({cur})", "Hours per person"], Numeric = new HashSet<int> { 1, 2, 3, 4, 5 } };
            foreach (var d in shown) tb.Rows.Add([d.Name, d.People, d.Interactions, Math.Round(d.Hours), Math.Round(d.Value), d.People > 0 ? Math.Round(d.Hours / d.People, 1) : null]);
            if (small.Count > 0) tb.Rows.Add(["Smaller groups", small.Sum(d => d.People), small.Sum(d => d.Interactions), Math.Round(small.Sum(d => d.Hours)), Math.Round(small.Sum(d => d.Value)), null]);
            CsvTable = tb;
            Panels.Add(new("By department", tb.Render(), small.Count > 0 ? $"{small.Count} department(s) with fewer than {Ctx.MinGroupSize} people using Copilot are combined." : null, "half", CsvUrl()));
        }
    }
}

namespace CopilotInsights.Pages.Compliance
{
    /// <summary>The SharePoint and OneDrive sites Copilot draws answers from most, and how well they are labelled.</summary>
    public sealed class SitesModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Oversharing hotspots";
        public override string Persona => "Compliance";
        public override string Intro => "The sites Copilot read from most, by how many people reached them through it, with how much of what it read carried a sensitivity label. A widely reached site with little labelled, or with confidential content, is where to check permissions first.";

        sealed class Row
        {
            public string Site { get; set; } = "";
            public bool IsPersonal { get; set; }
            public long Reads { get; set; }
            public int People { get; set; }
            public long Labelled { get; set; }
            public long Blocked { get; set; }
            public int? TopRank { get; set; }
            public string? TopLabel { get; set; }
        }

        protected override async Task BuildAsync()
        {
            var rows = await Db.QueryAsync<Row>($@"
SELECT f.SiteKey, COUNT_BIG(*) AS Reads, COUNT(DISTINCT f.UserKey) AS People,
       SUM(CASE WHEN f.SensitivityLabelKey > 1 THEN 1 ELSE 0 END) AS Labelled, SUM(CAST(f.IsBlocked AS INT)) AS Blocked,
       MAX(ISNULL(sl.Rank, CASE WHEN f.SensitivityLabelKey > 1 THEN 0 END)) AS TopRank
INTO #s
FROM bi.FactAccessedResource AS f {Ctx.Scope("f.UserKey")}
LEFT JOIN bi.DimSensitivityLabel AS l ON l.SensitivityLabelKey = f.SensitivityLabelKey
LEFT JOIN dash.SensitivityLabel AS sl ON sl.LabelId = l.LabelId
WHERE f.DateKey BETWEEN @k0 AND @k1 AND f.SiteKey > 1
GROUP BY f.SiteKey;
SELECT TOP (200) st.Site, st.IsPersonal, s.Reads, s.People, s.Labelled, s.Blocked, s.TopRank,
       (SELECT TOP (1) sl.Name FROM dash.SensitivityLabel AS sl WHERE sl.Rank = s.TopRank ORDER BY sl.Name) AS TopLabel
FROM #s AS s JOIN bi.DimSite AS st ON st.SiteKey = s.SiteKey
ORDER BY s.People DESC, s.Reads DESC;", Ctx.Args());
            if (rows.Count == 0) { Notice = "No site was read by Copilot in the period."; return; }
            var wide = rows.Where(r => r.People >= Ctx.MinGroupSize).ToList();
            Kpis.Add(new("Sites Copilot read from", Num(rows.Count)));
            Kpis.Add(new("Reached by many people", Num(wide.Count), $"{Ctx.MinGroupSize} or more"));
            Kpis.Add(new("Of those, mostly unlabelled", Num(wide.Count(r => r.Reads > 0 && r.Labelled < r.Reads * 0.1)), "under 10% of what was read carried a label",
                wide.Any(r => r.Reads > 0 && r.Labelled < r.Reads * 0.1) ? "warn" : null));
            Kpis.Add(new("With blocked content", Num(rows.Count(r => r.Blocked > 0))));
            Panels.Add(new("The widest-reaching sites", Chart.Bars(wide.Take(15).Select(r => new Bar(Short(r.Site), r.People,
                Note: $"{r.Reads:#,0} reads, {Pct(Ratio(r.Labelled, r.Reads))} labelled{(r.TopLabel is null ? "" : $", up to {r.TopLabel}")}", Slot: r.Labelled < r.Reads * 0.1 ? 5 : 1)).ToList(),
                Fmt.Count, "People reaching each site through Copilot"), "Orange: under 10% of what Copilot read there carried a label.", "half"));
            var tb = new Table { Headers = ["Site", "OneDrive", "People", "Reads", "Labelled", "Most sensitive label", "Blocked"], Numeric = new HashSet<int> { 2, 3, 5 } };
            foreach (var r in rows)
                tb.Rows.Add([r.Site, r.IsPersonal ? "Yes" : "", r.People < Ctx.MinGroupSize ? null : r.People, r.Reads, Pct(Ratio(r.Labelled, r.Reads)),
                             r.TopLabel ?? (r.TopRank is not null ? "(unnamed label)" : null), r.Blocked]);
            CsvTable = tb;
            Panels.Add(new("Every site (the 200 most reached)", tb.Render(),
                $"People are shown only where {Ctx.MinGroupSize} or more reached the site. Name labels and set their order in Admin > Reference data.", "full", CsvUrl()));
        }

        static string Short(string url) => url.Replace("https://", "").Length > 48 ? url.Replace("https://", "")[..48] + "…" : url.Replace("https://", "");
    }
}

namespace CopilotInsights.Pages.Agents
{
    /// <summary>Every agent seen: when first and last used, by how many people, named and owned or not.</summary>
    public sealed class InventoryModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Agent inventory";
        public override string Persona => "Agents";
        public override bool UsesFilters => true;
        public override string Intro => "Every agent the audit log has shown, whoever built it: first and last use, people using it in the period, and whether it has a friendly name and an owner (Admin > Reference data). New agents raise an alert when that is switched on.";

        sealed class Row
        {
            public string AgentName { get; set; } = "";
            public string AgentSource { get; set; } = "";
            public string? Owner { get; set; }
            public bool Named { get; set; }
            public DateTime FirstSeenUtc { get; set; }
            public DateTime? LastUsed { get; set; }
            public long Interactions { get; set; }
            public int People { get; set; }
        }

        protected override async Task BuildAsync()
        {
            var rows = await Db.QueryAsync<Row>($@"
SELECT f.AgentKey, COUNT_BIG(*) AS Interactions, COUNT(DISTINCT f.UserKey) AS People
INTO #u
FROM bi.FactInteraction AS f {Ctx.Scope("f.UserKey")}
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY f.AgentKey;
SELECT a.AgentName, a.AgentSource, a.Owner, CAST(CASE WHEN al.AgentKey IS NULL THEN 0 ELSE 1 END AS BIT) AS Named, a.FirstSeenUtc,
       (SELECT d.[Date] FROM bi.DimDate AS d WHERE d.DateKey = au.LastDateKey) AS LastUsed,
       ISNULL(u.Interactions, 0) AS Interactions, ISNULL(u.People, 0) AS People
FROM dash.Agent AS a
LEFT JOIN dash.AgentAlias AS al ON al.AgentKey = a.AgentKey
LEFT JOIN dash.AgentUse AS au ON au.AgentKey = a.AgentKey
LEFT JOIN #u AS u ON u.AgentKey = a.AgentKey
WHERE a.AgentSource <> N'(no agent)'
ORDER BY a.FirstSeenUtc DESC;", Ctx.Args());
            Kpis.Add(new("Agents seen", Num(rows.Count)));
            Kpis.Add(new("New in the period", Num(rows.Count(r => r.FirstSeenUtc >= Ctx.Start))));
            Kpis.Add(new("Used in the period", Num(rows.Count(r => r.Interactions > 0))));
            Kpis.Add(new("Without an owner", Num(rows.Count(r => r.Owner is null)), "set one in Admin > Reference data", rows.Any(r => r.Owner is null) ? "warn" : null));
            var bySource = rows.GroupBy(r => r.AgentSource).Select(g => new Bar(g.Key, g.Count(), Note: $"{g.Count(r => r.Interactions > 0)} used in the period")).OrderByDescending(b => b.Value).ToList();
            Panels.Add(new("By where they were built", Chart.Bars(bySource, Fmt.Count, "Agents by source"), null, "half"));
            var tb = new Table { Headers = ["Agent", "Built in", "Owner", "First used", "Last used", "People in the period", "Interactions in the period"], Numeric = new HashSet<int> { 5, 6 } };
            foreach (var r in rows)
                tb.Rows.Add([r.AgentName + (r.Named ? "" : " (unnamed)"), r.AgentSource, r.Owner, r.FirstSeenUtc.Date, r.LastUsed,
                             r.People is > 0 and var n && n < Ctx.MinGroupSize ? null : r.People, r.Interactions]);
            CsvTable = tb;
            Panels.Add(new("Every agent", tb.Render(), $"People are left out where fewer than {Ctx.MinGroupSize} used an agent; named users are on Agents > Agent detail for the Agents role.", "full", CsvUrl()));
        }
    }
}

namespace CopilotInsights.Pages.Enablement
{
    /// <summary>Whether training changed anything: attendees' use before and after, against colleagues who did not attend.</summary>
    public sealed class TrainingModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Training effect";
        public override string Persona => "Enablement";
        public override bool UsesFilters => false;
        public override string Intro => "For each training session (Admin > Training records): attendees' active days and interactions in the 28 days before and the 28 days after, against people in the same departments who did not attend, over the same weeks. The difference between the two changes is the likely effect of the training.";

        sealed class Row
        {
            public string Session { get; set; } = "";
            public DateTime AttendedOn { get; set; }
            public int Attendees { get; set; }
            public double DaysBefore { get; set; }
            public double DaysAfter { get; set; }
            public double OthersBefore { get; set; }
            public double OthersAfter { get; set; }
            public int Others { get; set; }
        }

        protected override async Task BuildAsync()
        {
            var rows = await Db.QueryAsync<Row>($@"
/* Attendees and the sessions' dates, in the viewer's scope. */
SELECT t.Session, t.AttendedOn, u.UserKey, ISNULL(NULLIF(u.Department, N''), N'(no department)') AS Dept
INTO #a
FROM dash.TrainingAttendance AS t
JOIN bi.DimUser AS u ON u.UserPrincipalName = t.Upn {Ctx.Scope("u.UserKey")}
WHERE t.AttendedOn <= DATEADD(DAY, -28, @d1);
/* Colleagues who did not attend that session: the same departments. */
SELECT DISTINCT s.Session, s.AttendedOn, u.UserKey
INTO #o
FROM (SELECT DISTINCT Session, AttendedOn, Dept FROM #a) AS s
JOIN bi.DimUser AS u ON ISNULL(NULLIF(u.Department, N''), N'(no department)') = s.Dept AND u.AccountEnabled = 1 {Ctx.Scope("u.UserKey")}
WHERE NOT EXISTS (SELECT 1 FROM #a AS x WHERE x.Session = s.Session AND x.AttendedOn = s.AttendedOn AND x.UserKey = u.UserKey);
WITH win AS
(
    SELECT g.Session, g.AttendedOn, g.UserKey, g.Grp,
           (SELECT COUNT(*) FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
            WHERE ud.UserKey = g.UserKey AND ud.Interactions > 0 AND d.[Date] >= DATEADD(DAY, -28, g.AttendedOn) AND d.[Date] < g.AttendedOn) AS B,
           (SELECT COUNT(*) FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey
            WHERE ud.UserKey = g.UserKey AND ud.Interactions > 0 AND d.[Date] > g.AttendedOn AND d.[Date] <= DATEADD(DAY, 28, g.AttendedOn)) AS A
    FROM (SELECT Session, AttendedOn, UserKey, 'a' AS Grp FROM #a UNION ALL SELECT Session, AttendedOn, UserKey, 'o' FROM #o) AS g
)
SELECT Session, AttendedOn,
       COUNT(CASE WHEN Grp = 'a' THEN 1 END) AS Attendees,
       ISNULL(AVG(CASE WHEN Grp = 'a' THEN CAST(B AS FLOAT) END), 0) AS DaysBefore, ISNULL(AVG(CASE WHEN Grp = 'a' THEN CAST(A AS FLOAT) END), 0) AS DaysAfter,
       ISNULL(AVG(CASE WHEN Grp = 'o' THEN CAST(B AS FLOAT) END), 0) AS OthersBefore, ISNULL(AVG(CASE WHEN Grp = 'o' THEN CAST(A AS FLOAT) END), 0) AS OthersAfter,
       COUNT(CASE WHEN Grp = 'o' THEN 1 END) AS Others
FROM win
GROUP BY Session, AttendedOn
ORDER BY AttendedOn DESC;", Ctx.Args(), timeout: 300);
            var shown = rows.Where(r => r.Attendees >= Ctx.MinGroupSize).ToList();
            if (shown.Count == 0)
            {
                Notice = rows.Count == 0
                    ? "No training records yet with 28 days after them. An administrator uploads attendance in Admin > Training records."
                    : $"No session had {Ctx.MinGroupSize} or more attendees you may see, so none is shown.";
                return;
            }
            var effect = shown.Select(r => (r.DaysAfter - r.DaysBefore) - (r.OthersAfter - r.OthersBefore)).ToList();
            Kpis.Add(new("Sessions measured", Num(shown.Count)));
            Kpis.Add(new("People trained", Num(shown.Sum(r => r.Attendees))));
            Kpis.Add(new("Average effect", $"{effect.Average():+0.0;-0.0} days", "active days in 28, against colleagues who did not attend", effect.Average() < 0 ? "warn" : null));
            Panels.Add(new("Effect by session", Chart.Bars(shown.Zip(effect).Select(x => new Bar($"{x.First.Session} ({x.First.AttendedOn:d MMM})", x.Second, Slot: x.Second >= 0 ? 3 : 5)).ToList(),
                Fmt.Decimal1, "Change in active days against colleagues"), "Active days in the 28 days after, less the 28 before, less the same change for colleagues in the same departments.", "half"));
            var tb = new Table
            {
                Headers = ["Session", "Date", "Attendees", "Active days before", "After", "Colleagues before", "After", "Colleagues", "Effect (days)"],
                Numeric = new HashSet<int> { 2, 3, 4, 5, 6, 7, 8 },
            };
            foreach (var (r, e) in shown.Zip(effect))
                tb.Rows.Add([r.Session, r.AttendedOn, r.Attendees, Math.Round(r.DaysBefore, 1), Math.Round(r.DaysAfter, 1), Math.Round(r.OthersBefore, 1), Math.Round(r.OthersAfter, 1), r.Others, Math.Round(e, 1)]);
            CsvTable = tb;
            Panels.Add(new("Every session", tb.Render(), $"Sessions with fewer than {Ctx.MinGroupSize} attendees you may see are left out. Averages per person.", "full", CsvUrl()));
        }
    }

    /// <summary>Suggested champions: each department's broadest, most regular users.</summary>
    public sealed class ChampionsModel(PageServices s) : InsightsPage(s)
    {
        public override string Title => "Champions";
        public override string Persona => "Enablement";
        public override string? RequiredRole => Roles.Enablement;
        public override string Intro => "The people in each department who use Copilot most regularly and most broadly, as candidates for a champions network: days active, apps used and conversations that went back and forth. Ask them; this only suggests.";

        sealed class Row
        {
            public string Dept { get; set; } = "";
            public string? DisplayName { get; set; }
            public string UserPrincipalName { get; set; } = "";
            public string? JobTitle { get; set; }
            public int Days { get; set; }
            public int Apps { get; set; }
            public long Interactions { get; set; }
            public double MultiTurnShare { get; set; }
            public int Rank { get; set; }
        }

        protected override async Task BuildAsync()
        {
            var per = Math.Clamp(await S.Settings.IntAsync("ChampionsPerDepartment", 3), 1, 20);
            var rows = await Db.QueryAsync<Row>($@"
SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS Days, SUM(CAST(ud.Interactions AS BIGINT)) AS Interactions,
       MAX(ud.AppsUsed) AS Apps, SUM(ud.MultiTurnSessions) AS MT, SUM(ud.Sessions) AS S
INTO #p
FROM dash.UserDay AS ud {Ctx.Scope("ud.UserKey")}
WHERE ud.DateKey BETWEEN @k0 AND @k1
GROUP BY ud.UserKey;
WITH scored AS
(
    SELECT ISNULL(NULLIF(u.Department, N''), N'(no department)') AS Dept, u.DisplayName, u.UserPrincipalName, u.JobTitle,
           p.Days, p.Apps, p.Interactions, CASE WHEN p.S > 0 THEN CAST(p.MT AS FLOAT) / p.S ELSE 0 END AS MultiTurnShare,
           ROW_NUMBER() OVER (PARTITION BY ISNULL(NULLIF(u.Department, N''), N'(no department)')
                              ORDER BY p.Days DESC, p.Apps DESC, CASE WHEN p.S > 0 THEN CAST(p.MT AS FLOAT) / p.S ELSE 0 END DESC, p.Interactions DESC) AS Rank
    FROM #p AS p JOIN bi.DimUser AS u ON u.UserKey = p.UserKey
    WHERE p.Days > 0 AND u.AccountEnabled = 1
)
SELECT * FROM scored WHERE Rank <= @per ORDER BY Dept, Rank;", Ctx.Args(new { per }));
            Kpis.Add(new("Departments", Num(rows.Select(r => r.Dept).Distinct().Count())));
            Kpis.Add(new("Candidates", Num(rows.Count), $"up to {per} per department (Admin > Targets and assumptions)"));
            var tb = new Table { Headers = ["Department", "", "Name", "Sign-in", "Job title", "Days active", "Apps used (most in a day)", "Back-and-forth conversations", "Interactions"], Numeric = new HashSet<int> { 5, 6, 8 } };
            foreach (var r in rows) tb.Rows.Add([r.Dept, r.Rank, r.DisplayName, r.UserPrincipalName, r.JobTitle, r.Days, r.Apps, Pct(r.MultiTurnShare), r.Interactions]);
            CsvTable = tb;
            Panels.Add(new("Candidates by department", tb.Render(), "Ranked by days active, then breadth, then conversation depth.", "full", CsvUrl()));
        }
    }
}
