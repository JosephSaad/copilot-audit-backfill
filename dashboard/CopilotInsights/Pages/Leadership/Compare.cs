using CopilotInsights.Charts;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// Two managers' teams side by side: each manager with everyone under them, over the same period. Each team
/// is a report scope of its own, made the way a page's is (<see cref="ReportContextFactory"/>), so the rules that
/// decide whose part of the organization a viewer may open decide it here too: a manager outside the viewer's part,
/// a team under the minimum group size, and any team in privacy mode are refused, and nothing is shown for them.
/// </summary>
public sealed class CompareModel(PageServices s, Metrics m) : InsightsPage(s)
{
    public override string Title => "Compare two teams";
    public override string Persona => "Leadership";
    public override string Intro => "Two managers' teams side by side, each manager with everyone under them, over the same period. Totals only: no one is named but the two managers.";
    /* The two teams are chosen on the page, so the way down the organization would change nothing. */
    public override bool UsesTeam => false;

    /// <summary>The most managers offered in the two boxes: the largest teams. Any other manager's sign-in can be typed.</summary>
    public const int Offered = 2000;

    sealed record Side(string Upn, string Name, ReportContext Ctx, Totals T, List<TrendRow> Trend);

    protected override async Task BuildAsync()
    {
        var a = Warming ? null : Request.Query["a"].FirstOrDefault()?.Trim();
        var b = Warming ? null : Request.Query["b"].FirstOrDefault()?.Trim();

        if (PrivacyMode)
        {
            Notice = "Privacy mode is on, so a team is not named after its manager and two teams cannot be chosen. Adoption › Departments compares departments.";
            return;
        }

        /* The managers offered: those in the viewer's part of the organization with a team that can reach the minimum
           group size, the largest teams first when there are more than the boxes hold. */
        var managers = await Db.QueryAsync<(string Upn, string? Name, string? Department, int PeopleBelow)>($@"
SELECT TOP ({Offered}) p.Upn, p.DisplayName, p.Department, p.PeopleBelow
FROM dash.Person AS p {Ctx.PersonScope("p.Upn")}
WHERE p.PeopleBelow + 1 >= @minGroup AND p.PeopleBelow > 0
ORDER BY p.PeopleBelow DESC, p.DisplayName;", Ctx.Args());
        var options = string.Concat(managers.OrderBy(x => x.Name ?? x.Upn, StringComparer.OrdinalIgnoreCase).Select(x =>
            $"<option value=\"{E(x.Upn)}\">{E(x.Name ?? x.Upn)}{(string.IsNullOrWhiteSpace(x.Department) ? "" : ", " + E(x.Department))} ({x.PeopleBelow:#,0} {(x.PeopleBelow == 1 ? "person" : "people")})</option>"));
        string Box(string name, string label, string? value, string tip) =>
            $"<label>{Html(InfoTip.After(label, tip))}<input type=\"text\" name=\"{name}\" value=\"{E(value)}\" size=\"34\" list=\"managers\" autocomplete=\"off\" placeholder=\"Name or sign-in\"/></label>";
        var form = $"<form class=\"admin\" method=\"get\" action=\"{E(Request.Path)}\"><input type=\"hidden\" name=\"d\" value=\"{Ctx.Days}\"/>" +
                   Box("a", "First\u00A0manager", a, "Start typing a name and choose the manager from the list, or type their sign-in (name@domain). The team is the manager and everyone under them, at any depth.") +
                   Box("b", "Second\u00A0manager", b, "The manager of the team to set beside the first. The two teams may overlap: one manager can report to the other.") +
                   $"<datalist id=\"managers\">{options}</datalist>{Button("Compare")}</form>";
        var listed = managers.Count >= Offered ? $" The list offers the {Offered:#,0} largest teams; type the sign-in of any other manager." : "";
        Panels.Add(new("Which two teams", new HtmlString(form),
            $"A team is shown when it has at least {Ctx.MinGroupSize} enabled people, and only where you may see it.{listed}"));

        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) { Notice = "Choose two different managers: both boxes name the same one."; return; }

        var first = await SideAsync(a);
        var second = await SideAsync(b);
        if (first.Refusal is not null || second.Refusal is not null)
        {
            Notice = string.Join(" ", new[] { first.Refusal, second.Refusal }.Where(r => r is not null));
            return;
        }
        var (A, B) = (first.Side!, second.Side!);

        Kpis.Add(new($"Adoption: {A.Name}", AdoptionOf(A), "Premium license holders in the team who used Copilot in the period"));
        Kpis.Add(new($"Adoption: {B.Name}", AdoptionOf(B), "Premium license holders in the team who used Copilot in the period"));
        Kpis.Add(new($"Using Copilot: {A.Name}", Pct(Ratio(A.T.Active, A.T.People)), $"{A.T.Active:#,0} of {A.T.People:#,0} people"));
        Kpis.Add(new($"Using Copilot: {B.Name}", Pct(Ratio(B.T.Active, B.T.People)), $"{B.T.Active:#,0} of {B.T.People:#,0} people"));

        var t = new Table { Headers = ["Measure", A.Name, B.Name, "Difference"], Numeric = new HashSet<int> { 1, 2, 3 }, Sortable = false };
        void Number(string measure, double? x, double? y, string format = "#,0")
        {
            string? Show(double? v) => v is null ? null : v.Value.ToString(format);
            t.Rows.Add([measure, Show(x), Show(y), x is null || y is null ? null : Signed(x.Value - y.Value, format)]);
        }
        void Share(string measure, double? x, double? y) =>
            t.Rows.Add([measure, x is null ? null : Pct(x), y is null ? null : Pct(y), x is null || y is null ? null : Points(x.Value - y.Value)]);

        Number("People", A.T.People, B.T.People);
        Number("Premium license holders", A.T.LicensedNow, B.T.LicensedNow);
        Share("Share of people with a Premium license", Ratio(A.T.LicensedNow, A.T.People), Ratio(B.T.LicensedNow, B.T.People));
        Number("People who used Copilot", A.T.Active, B.T.Active);
        Share("Share of people who used Copilot", Ratio(A.T.Active, A.T.People), Ratio(B.T.Active, B.T.People));
        Share("Adoption (license holders who used Copilot)", AdoptionRatio(A), AdoptionRatio(B));
        Share("Share of people active in the last 7 days", Ratio(A.T.ActiveLast7, A.T.People), Ratio(B.T.ActiveLast7, B.T.People));
        Share("Active license holders who used Premium features", Ratio(A.T.PremiumLicensed, A.T.ActiveLicensed), Ratio(B.T.PremiumLicensed, B.T.ActiveLicensed));
        Number("Interactions", A.T.Interactions, B.T.Interactions);
        Number("Interactions per active person", Ratio(A.T.Interactions, A.T.Active), Ratio(B.T.Interactions, B.T.Active), "#,0.0");
        Number("Days active per active person", Ratio(A.T.ActiveDays, A.T.Active), Ratio(B.T.ActiveDays, B.T.Active), "#,0.0");
        Number("Prompts per session", Ratio(A.T.Prompts, A.T.Sessions), Ratio(B.T.Prompts, B.T.Sessions), "#,0.0");
        Share("Sessions with a follow-up", Ratio(A.T.MultiTurn, A.T.Sessions), Ratio(B.T.MultiTurn, B.T.Sessions));
        Panels.Add(new("Side by side", t.Render(),
            $"{A.Name}'s team against {B.Name}'s, {Ctx.PeriodLabel}. Difference is the first team less the second: in percentage points for a share, in the measure's own unit otherwise. " +
            $"People counts enabled accounts. Adoption is shown where a team has at least {Ctx.MinGroupSize} license holders. A person in both teams counts in each."));

        var x = Labels();
        Panels.Add(new("Share of people who used Copilot",
            Chart.Line(x, [
                new Series(A.Name, A.Trend.Select(w => Ratio(w.Active, A.T.People)).ToList(), 1),
                new Series(B.Name, B.Trend.Select(w => Ratio(w.Active, B.T.People)).ToList(), 2),
            ], Fmt.Percent, "Share of each team's people who used Copilot"),
            $"Of each team's enabled people, the share who used Copilot at each point, so teams of different sizes can be compared. {TrendNote}", "half"));
        Panels.Add(new("Interactions per person in the team",
            Chart.Line(x, [
                new Series(A.Name, A.Trend.Select(w => Ratio(w.Interactions, A.T.People)).ToList(), 1),
                new Series(B.Name, B.Trend.Select(w => Ratio(w.Interactions, B.T.People)).ToList(), 2),
            ], Fmt.Decimal1, "Interactions per person in each team"),
            $"Each team's interactions at each point divided by its enabled people, users of Copilot or not. {TrendNote}", "half"));

        /* Where each team uses Copilot: each app's share of the team's interactions, the busiest apps of the two together. */
        var appsA = await AppsAsync(A.Ctx);
        var appsB = await AppsAsync(B.Ctx);
        var names = appsA.Keys.Union(appsB.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => appsA.GetValueOrDefault(n) + appsB.GetValueOrDefault(n)).Take(8).ToList();
        if (names.Count > 0)
        {
            double totalA = appsA.Values.Sum(), totalB = appsB.Values.Sum();
            var apps = new Table { Headers = ["App", A.Name, B.Name, "Difference"], Numeric = new HashSet<int> { 1, 2, 3 } };
            foreach (var n in names)
            {
                double? sa = Ratio(appsA.GetValueOrDefault(n), totalA), sb = Ratio(appsB.GetValueOrDefault(n), totalB);
                apps.Rows.Add([n, sa is null ? null : Pct(sa), sb is null ? null : Pct(sb), sa is null || sb is null ? null : Points(sa.Value - sb.Value)]);
            }
            Panels.Add(new("Where each team uses Copilot", apps.Render(),
                "Each app's share of the team's interactions in the period: the eight busiest apps of the two teams together. Difference is in percentage points, the first team less the second."));
        }
    }

    async Task<(Side? Side, string? Refusal)> SideAsync(string asked)
    {
        /* The managers' box offers names, and its value is the sign-in; a name typed in full is taken too, when one manager the viewer may see has it. */
        var upn = asked;
        if (!asked.Contains('@'))
        {
            var byName = await Db.QueryAsync<string>($@"
SELECT TOP (2) p.Upn FROM dash.Person AS p {Ctx.PersonScope("p.Upn")} WHERE p.DisplayName = @name AND p.PeopleBelow > 0;", Ctx.Args(new { name = asked }));
            if (byName.Count == 1) upn = byName[0];
            else if (byName.Count > 1) return (null, $"More than one manager is named {asked}: choose one from the list, which gives each by sign-in.");
        }
        var c = await S.Contexts.CreateAsync(Viewer, Ctx.Days, upn);
        /* What was asked for is not the part the scope covers: not in the directory, outside the viewer's part, or too small. */
        if (c.OrgAsked is not null || !string.Equals(c.Root, upn, StringComparison.OrdinalIgnoreCase))
            return (null, c.OrgWhy switch
            {
                OrgRefusal.TooSmall => $"{asked} and the people under them are fewer than {Ctx.MinGroupSize}, the minimum group size, so that team is not shown.",
                OrgRefusal.Privacy => "Privacy mode is on, so a team is not shown by its manager.",
                OrgRefusal.Outside => $"{asked} is not in your part of the organization, so that team is not shown.",
                _ => $"No one in the organization directory has the name or sign-in {asked}. Choose a manager from the list.",
            });
        return (new Side(upn, c.RootName ?? upn, c, await m.TotalsAsync(c), await m.TrendAsync(c)), null);
    }

    async Task<Dictionary<string, long>> AppsAsync(ReportContext c) =>
        (await Db.QueryAsync<(string App, long N)>($@"
SELECT a.App, COUNT_BIG(*)
FROM bi.FactInteraction AS f {c.Scope("f.UserKey")}
JOIN bi.DimApp AS a ON a.AppKey = f.AppKey
WHERE f.DateKey BETWEEN @k0 AND @k1
GROUP BY a.App;", c.Args())).ToDictionary(r => r.App, r => r.N, StringComparer.OrdinalIgnoreCase);

    double? AdoptionRatio(Side s) => s.T.LicensedNow >= Ctx.MinGroupSize ? Ratio(s.T.ActiveLicensed, s.T.LicensedNow) : null;
    string AdoptionOf(Side s) => Pct(AdoptionRatio(s));

    static string Html(Microsoft.AspNetCore.Html.IHtmlContent h) => h is HtmlString hs ? hs.Value ?? "" : h.ToString() ?? "";

    /// <summary>A difference with its sign: "+12", "-3.5", and "0" for none.</summary>
    public static string Signed(double v, string format = "#,0")
    {
        var text = Math.Abs(v).ToString(format);
        return text.Trim('0', '.', ',').Length == 0 ? "0" : (v > 0 ? "+" : "-") + text;
    }

    /// <summary>A difference between two shares, in percentage points: "+4.2 pts".</summary>
    public static string Points(double v) => Signed(v * 100, "#,0.0") + " pts";
}
