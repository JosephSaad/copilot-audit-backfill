using CopilotInsights.Data;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The read-only data API, /api/v1: the dashboard's figures as JSON for other tools (a service
/// desk, an HR system, a SIEM). Same Windows sign-in, same scope and roles as the pages:
/// aggregates for anyone with data to see, named lists only with the page's role and never in
/// privacy mode. ?d= is the period (7 to 365 days) and ?org= a manager's UPN, as on the pages.
/// Every call is written to the audit log. Off when the ApiEnabled setting is 0.
/// </summary>
public static class DataApi
{
    public static void MapDataApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/", () => Results.Ok(new
        {
            endpoints = new object[]
            {
                new { path = "/api/v1/usage", what = "totals and the trend (days up to 30, else weeks) for the period and scope" },
                new { path = "/api/v1/units", what = "people, licenses and use by unit one level down" },
                new { path = "/api/v1/security", what = "security signals by day or week (Security role)" },
                new { path = "/api/v1/license-pool", what = "seats bought and assigned per SKU (Licensing role)" },
                new { path = "/api/v1/value", what = "estimated hours saved and their value, by app and department" },
                new { path = "/api/v1/targets", what = "adoption targets and where each stands (Adoption role)" },
                new { path = "/api/v1/license-actions?kind=upgrade|train|revoke", what = "named: the people behind a recommendation (Licensing role)" },
                new { path = "/api/v1/leavers", what = "named: licenses on disabled or departed accounts (Licensing role)" },
                new { path = "/api/v1/health", what = "pipeline runs and freshness (administrators)" },
            },
        }));

        api.MapGet("/usage", (HttpContext h, CancellationToken _) => Run(h, "usage", null, async (x, c) =>
        {
            var m = x.GetRequiredService<Metrics>();
            return new { scope = c.ScopeLabel, from = c.Start, to = c.End, totals = await m.TotalsAsync(c), trend = await m.TrendAsync(c) };
        }));

        api.MapGet("/units", (HttpContext h, CancellationToken _) => Run(h, "units", null, async (x, c) =>
        {
            var units = await x.GetRequiredService<Metrics>().UnitsAsync(c);
            var shown = units.Where(u => u.People >= c.MinGroupSize).ToList();
            var small = units.Where(u => u.People < c.MinGroupSize).ToList();
            if (small.Count > 0) shown.Add(Metrics.Combine(small, "Smaller groups"));
            return new { scope = c.ScopeLabel, from = c.Start, to = c.End, minGroupSize = c.MinGroupSize, units = shown };
        }));

        api.MapGet("/security", (HttpContext h, CancellationToken _) => Run(h, "security", null, async (x, c) =>
        {
            var t = await x.GetRequiredService<Metrics>().TrendAsync(c);
            return new { scope = c.ScopeLabel, from = c.Start, to = c.End, days = c.Daily,
                         points = t.Select(p => new { p.Bucket, p.Jailbreak, p.Injection, p.Blocked, p.Labeled, p.Interactions }) };
        }, section: Roles.Security));

        api.MapGet("/license-pool", (HttpContext h, CancellationToken _) => Run(h, "license-pool", null, async (x, _) =>
            new { skus = (await x.GetRequiredService<OpsData>().PoolAsync()).Select(p => new { p.SkuPartNumber, p.SkuId, p.IsCopilot, p.Bought, assigned = p.Consumed, p.Available, p.CapturedUtc }) }, section: Roles.Licensing));

        api.MapGet("/value", (HttpContext h, CancellationToken _) => Run(h, "value", null, async (x, c) =>
        {
            var v = await x.GetRequiredService<OpsReports>().ValueAsync(c);
            return new { scope = c.ScopeLabel, from = c.Start, to = c.End, hourlyCost = v.HourlyCost, defaultMinutes = v.DefaultMinutes, byApp = v.ByApp, byDepartment = v.ByDepartment };
        }));

        api.MapGet("/targets", (HttpContext h, CancellationToken _) => Run(h, "targets", null, async (x, c) =>
            new { asOf = c.End, targets = await x.GetRequiredService<OpsReports>().TargetsAsync(c) }, section: Roles.Adoption));

        api.MapGet("/license-actions", (HttpContext h, CancellationToken _) => Run(h, "license-actions", Roles.Licensing, async (x, c) =>
        {
            var kind = h.Request.Query["kind"].FirstOrDefault() is { } k && Pages.Forecasting.ActionsModel.Kinds.ContainsKey(k) ? k : "upgrade";
            var db = x.GetRequiredService<Db>();
            var rows = await db.QueryAsync<(string Upn, string? Name, string? Department, int Days, int Interactions)>($@"
WITH a AS (SELECT ud.UserKey, COUNT(CASE WHEN ud.Interactions > 0 THEN 1 END) AS D, SUM(ud.Interactions) AS I
           FROM dash.UserDay AS ud WHERE ud.DateKey BETWEEN @k30 AND @k1 GROUP BY ud.UserKey),
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1)
SELECT du.UserPrincipalName, du.DisplayName, du.Department, ISNULL(a.D, 0), ISNULL(a.I, 0)
FROM bi.DimUser AS du {c.Scope("du.UserKey")}
LEFT JOIN a ON a.UserKey = du.UserKey LEFT JOIN lic ON lic.UserKey = du.UserKey
WHERE {Pages.Forecasting.ActionsModel.Kinds[kind].Where} AND ISNULL(du.AccountEnabled, 1) = 1
ORDER BY du.UserPrincipalName;", c.Args(new { k30 = ReportContext.Key(c.End.AddDays(-29)) }));
            return new { kind, asOf = c.End, people = rows.Select(r => new { upn = r.Upn, name = r.Name, department = r.Department, daysUsed30 = r.Days, interactions30 = r.Interactions }) };
        }));

        api.MapGet("/leavers", (HttpContext h, CancellationToken _) => Run(h, "leavers", Roles.Licensing, async (x, c) =>
            new { people = (await x.GetRequiredService<OpsData>().LeaversAsync(c.Root)).Select(r => new { upn = r.UserPrincipalName, name = r.DisplayName, department = r.Department, reason = r.Reason, licensedSince = r.LicensedSinceUtc, lastUsed = r.LastUsed }) }));

        api.MapGet("/health", (HttpContext h, CancellationToken _) => Run(h, "health", Roles.Admin, async (x, _) =>
        {
            var ops = x.GetRequiredService<OpsData>();
            var (last, age, limit) = await ops.FreshnessAsync();
            return new { refreshedUtc = last, ageHours = age, freshnessLimitHours = limit, runs = await ops.PipelineRunsAsync(20) };
        }));
    }

    /* Each handler takes (HttpContext, CancellationToken) so it is a route handler whose IResult is written, not a bare RequestDelegate. */
    /// <summary>Resolves the viewer and scope, applies the role and privacy rules, audits the call, and returns the JSON.</summary>
    static async Task<IResult> Run(HttpContext h, string name, string? role, Func<IServiceProvider, ReportContext, Task<object>> body, string? section = null)
    {
        var sp = h.RequestServices;
        var settings = sp.GetRequiredService<Settings>();
        if (await settings.StringAsync("ApiEnabled") == "0") return Results.NotFound();
        var viewer = await sp.GetRequiredService<ViewerService>().GetAsync();
        var c = await sp.GetRequiredService<ReportContextFactory>().CreateAsync(viewer, h.Request);
        var audit = sp.GetRequiredService<AuditLog>();
        var path = "/api/v1/" + name;
        string? refusal = null;
        if (role == Roles.Admin) { if (!viewer.IsAdmin) refusal = "administrators only"; }
        else if (!viewer.Admitted) refusal = $"the dashboard is for levels 1 to {viewer.MaxLevel} of the organization";
        else if (!viewer.OrgWide && !viewer.InDirectory) refusal = "not in the directory, and not granted Organization";
        else if (section is not null && !viewer.Has(section)) refusal = $"needs the {section} role";   // the page's section needs it too
        else if (role is not null && await settings.PrivacyModeAsync()) refusal = "privacy mode is on: nothing that names people";
        else if (role is not null && !viewer.Has(role)) refusal = $"needs the {role} role";
        if (refusal is not null)
        {
            await audit.WriteAsync(viewer, "Denied", path, c.ScopeLabel, "api: " + refusal);
            return Results.Json(new { error = refusal }, statusCode: 403);
        }
        var result = await body(sp, c);
        await audit.WriteAsync(viewer, "Api", path, c.ScopeLabel, $"days={c.Days}{h.Request.QueryString}");
        return Results.Json(result);
    }
}
