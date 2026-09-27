using CopilotInsights.Charts;
using CopilotInsights.Data;

namespace CopilotInsights.Pages.Workforce;

public sealed class IndexModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Workforce";
    public override string Persona => "People";
    public override string Intro => "Adoption across the workforce, from Active Directory: employees and contractors, managers and individual contributors, job titles and levels.";

    protected override async Task BuildAsync()
    {
        var types = Groups.Fold(this, await m.ByAttributeAsync(Ctx, "EmployeeType"), Ctx.MinGroupSize, out _);
        var roles = await m.ByAttributeAsync(Ctx, "Role");
        var levels = Groups.Fold(this, await m.ByAttributeAsync(Ctx, "Level"), Ctx.MinGroupSize, out _);
        var titles = Groups.Fold(this, await m.ByAttributeAsync(Ctx, "JobTitle"), Ctx.MinGroupSize, out var ft);
        GroupRow? Find(IEnumerable<GroupRow> r, string n) => r.FirstOrDefault(x => x.Name == n);
        var mgr = Find(roles, "Managers"); var ic = Find(roles, "Individual contributors");
        if (mgr is not null) Kpis.Add(new("Managers' adoption", Pct(Ratio(mgr.ActiveLicensed, mgr.Licensed)), $"{mgr.Licensed:#,0} licensed managers"));
        if (ic is not null) Kpis.Add(new("Individual contributors'", Pct(Ratio(ic.ActiveLicensed, ic.Licensed)), $"{ic.Licensed:#,0} licensed"));
        foreach (var t in types.Where(t => t.Name != "Smaller groups").OrderByDescending(t => t.People).Take(2))
            Kpis.Add(new($"{t.Name}: adoption", Pct(Ratio(t.ActiveLicensed, t.Licensed)), $"{t.People:#,0} people, {Pct(Ratio(t.Licensed, t.People))} licensed"));

        Panels.Add(new("By employee type", Groups.AdoptionBars(types, "Adoption by employee type"), "From the employeeType attribute in AD.", "half"));
        Panels.Add(new("Managers and individual contributors", Groups.AdoptionBars(roles, "Adoption by role"),
            "A manager is anyone with someone reporting to them.", "half"));
        Panels.Add(new("By level in the organisation",
            Chart.Bars(levels.Where(l => l.Licensed > 0).OrderBy(l => l.Name).Select(l => new Bar(l.Name, l.ActiveLicensed / (double)l.Licensed, Note: $"{l.ActiveLicensed:#,0} of {l.Licensed:#,0} licensed")).ToList(), Fmt.Percent, "Adoption by level", 1),
            "Level 1 is the top of the organisation.", "half"));
        Panels.Add(new("By job title", Groups.AdoptionBars(titles, "Adoption by job title", 15),
            "The fifteen titles with the highest adoption." + (ft > 0 ? $" {ft} title(s) under {Ctx.MinGroupSize} people combined." : ""), "half"));
        var tt = Groups.Table(titles, "Job title");
        CsvTable = tt;
        Panels.Add(new("Every job title", tt.Render(), null, "full", CsvUrl()));
    }
}

public sealed class ManagersModel(PageServices s) : InsightsPage(s)
{
    public override string Title => "Managers and their teams";
    public override string Persona => "People";
    public override string Intro => "Do teams follow their manager? Adoption in teams whose manager uses Copilot, against teams whose manager does not.";

    sealed class TeamRow
    {
        public string Manager { get; set; } = "";
        public string? Name { get; set; }
        public bool ManagerActive { get; set; }
        public int Team { get; set; }
        public int Licensed { get; set; }
        public int ActiveLicensed { get; set; }
    }

    protected override async Task BuildAsync()
    {
        /* Each manager's direct reports: licensed, and licensed and active in the period. */
        var teams = await Db.QueryAsync<TeamRow>($@"
WITH act AS (SELECT ud.UserKey, SUM(ud.LicensedUse) AS L, SUM(ud.Interactions) AS I FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k0 AND @k1 GROUP BY ud.UserKey),
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1)
SELECT m.Upn AS Manager, m.DisplayName AS Name,
       CAST(CASE WHEN MAX(ma.I) > 0 THEN 1 ELSE 0 END AS BIT) AS ManagerActive,
       COUNT(*) AS Team, COUNT(l.UserKey) AS Licensed, COUNT(CASE WHEN l.UserKey IS NOT NULL AND a.L > 0 THEN 1 END) AS ActiveLicensed
FROM dash.Person AS m {Ctx.PersonScope("m.Upn")}
JOIN dash.Person AS r ON r.ManagerUpn = m.Upn AND r.AccountEnabled = 1
LEFT JOIN bi.DimUser AS ru ON ru.UserPrincipalName = r.Upn
LEFT JOIN lic AS l ON l.UserKey = ru.UserKey
LEFT JOIN act AS a ON a.UserKey = ru.UserKey
LEFT JOIN bi.DimUser AS mu ON mu.UserPrincipalName = m.Upn
LEFT JOIN act AS ma ON ma.UserKey = mu.UserKey
GROUP BY m.Upn, m.DisplayName;", Ctx.Args());
        var withLic = teams.Where(t => t.Licensed > 0).ToList();
        double Rate(IEnumerable<TeamRow> r) { var l = r.Sum(x => x.Licensed); return l > 0 ? r.Sum(x => x.ActiveLicensed) / (double)l : 0; }
        var on = withLic.Where(t => t.ManagerActive).ToList(); var off = withLic.Where(t => !t.ManagerActive).ToList();
        Kpis.Add(new("Teams with a manager using Copilot", Pct(Rate(on)), $"adoption across {on.Count:#,0} teams"));
        Kpis.Add(new("Teams whose manager does not", Pct(Rate(off)), $"across {off.Count:#,0} teams"));
        Kpis.Add(new("Managers using Copilot", Pct(Ratio(teams.Count(t => t.ManagerActive), teams.Count)), $"of {teams.Count:#,0} managers in scope"));

        Panels.Add(new("Team adoption, by whether the manager uses Copilot",
            Chart.Bars([new Bar("Manager uses Copilot", Rate(on), Note: $"{on.Sum(t => t.ActiveLicensed):#,0} of {on.Sum(t => t.Licensed):#,0} licensed direct reports", Slot: 3),
                        new Bar("Manager does not", Rate(off), Note: $"{off.Sum(t => t.ActiveLicensed):#,0} of {off.Sum(t => t.Licensed):#,0}", Slot: 2)], Fmt.Percent, "Team adoption by manager use", 1),
            "Direct reports only. A gap suggests managers set the pace, though it is a correlation, not proof.", "half"));
        var bands = new[] { (0.0, 0.25, "0-25%"), (0.25, 0.5, "25-50%"), (0.5, 0.75, "50-75%"), (0.75, 1.01, "75-100%") };
        Panels.Add(new("Teams by adoption",
            Chart.Columns(bands.Select(b => b.Item3).ToList(), [
                new Series("Manager uses Copilot", bands.Select(b => (double?)on.Count(t => t.ActiveLicensed / (double)t.Licensed >= b.Item1 && t.ActiveLicensed / (double)t.Licensed < b.Item2)).ToList(), 3),
                new Series("Manager does not", bands.Select(b => (double?)off.Count(t => t.ActiveLicensed / (double)t.Licensed >= b.Item1 && t.ActiveLicensed / (double)t.Licensed < b.Item2)).ToList(), 2),
            ], Fmt.Count, "Teams by adoption band", stacked: false),
            "How many teams fall in each band of adoption among their licensed direct reports.", "half"));
    }
}
