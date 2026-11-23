using CopilotInsights.Data;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The read-only data API, /api/v1: the dashboard's figures as JSON for other tools (a service
/// desk, an HR system, a SIEM). Same Windows sign-in, same scope and roles as the pages:
/// each call needs the section its figures belong to (usage, units and value are Leadership's), named lists the
/// page's role too, and never in privacy mode. ?d= is the period (7 to 730 days, or 0 for all the data) and ?org= a manager's UPN, as on the pages.
/// Every call is written to the audit log. Off when the ApiEnabled setting is 0.
/// </summary>
public static class DataApi
{
    public static void MapDataApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        /* The list of endpoints goes through Run as every call does: it answered with the API turned off, to anyone
           outside the dashboard's levels, and wrote nothing to the audit log. */
        api.MapGet("/", (HttpContext h, CancellationToken _) => Run(h, "", null, (_, _) => Task.FromResult<object>(new
        {
            endpoints = new object[]
            {
                new { path = "/api/v1/usage", what = "totals and the trend (days up to 30, else weeks) for the period and scope (Leadership section)" },
                new { path = "/api/v1/units", what = "people, licenses and use by unit one level down (Leadership section)" },
                new { path = "/api/v1/security", what = "security signals by day or week (Security section)" },
                new { path = "/api/v1/license-pool", what = "seats bought and assigned per SKU (Licensing section)" },
                new { path = "/api/v1/value", what = "estimated hours saved and their value, by app and department (Leadership section)" },
                new { path = "/api/v1/targets", what = "adoption targets and where each stands (Adoption section)" },
                new { path = "/api/v1/license-actions?kind=upgrade|train|revoke", what = "named: the people behind a recommendation (Forecasting section, with named people)" },
                new { path = "/api/v1/leavers", what = "named: licenses on disabled or departed accounts (Licensing section, with named people)" },
                new { path = "/api/v1/health", what = "pipeline runs and freshness (administrators)" },
            },
        })));

        /* The security signals are the Security section's and labeled content the Compliance section's: the
           usage trend and the units carry them only for those roles, as /security and the section pages do. */
        api.MapGet("/usage", (HttpContext h, CancellationToken _) => Run(h, "usage", null, async (x, c) =>
        {
            var m = x.GetRequiredService<Metrics>();
            bool sec = c.Viewer.Sees(Sections.Security), comp = c.Viewer.Sees(Sections.Compliance);
            var trend = (await m.TrendAsync(c)).Select(p => new
            {
                p.Bucket, p.Active, p.ActiveLicensed, p.ActiveUnlicensed, p.Interactions, p.Prompts, p.Sessions, p.MultiTurn, p.Chat, p.Agent, p.Web, p.Licensed,
                jailbreak = sec ? p.Jailbreak : (long?)null, injection = sec ? p.Injection : (long?)null, blocked = sec ? p.Blocked : (long?)null,
                labeled = comp ? p.Labeled : (long?)null,
            });
            return new { scope = c.ScopeLabel, from = c.Start, to = c.End, totals = await m.TotalsAsync(c), trend };
        }, section: Sections.Leadership));

        api.MapGet("/units", (HttpContext h, CancellationToken _) => Run(h, "units", null, async (x, c) =>
        {
            bool sec = c.Viewer.Sees(Sections.Security), comp = c.Viewer.Sees(Sections.Compliance);
            var shown = SmallGroups.Fold(await x.GetRequiredService<Metrics>().UnitsAsync(c), u => u.People, r => Metrics.Combine(r, "Smaller groups"), c.MinGroupSize, out var foldedUnits);
            return new
            {
                scope = c.ScopeLabel, from = c.Start, to = c.End, minGroupSize = c.MinGroupSize,
                units = shown.Select(u => new
                {
                    /* In privacy mode a unit is not named after its manager, and its sign-in is not given (see Metrics.UnitsAsync). */
                    unit = u.Named ? u.Unit : null, u.Name, u.Department, u.People, u.Licensed, u.Active, u.ActiveLicensed, u.PremiumLicensed,
                    u.Interactions, u.ActiveDays, u.Sessions, u.MultiTurn,
                    flags = sec ? u.Flags : (long?)null, labeled = comp ? u.Labeled : (long?)null,
                }),
            };
        }, section: Sections.Leadership));

        api.MapGet("/security", (HttpContext h, CancellationToken _) => Run(h, "security", null, async (x, c) =>
        {
            var t = await x.GetRequiredService<Metrics>().TrendAsync(c);
            return new { scope = c.ScopeLabel, from = c.Start, to = c.End, days = c.Daily,
                         points = t.Select(p => new { p.Bucket, p.Jailbreak, p.Injection, p.Blocked, p.Labeled, p.Interactions }) };
        }, section: Sections.Security));

        api.MapGet("/license-pool", (HttpContext h, CancellationToken _) => Run(h, "license-pool", null, async (x, _) =>
            new { skus = (await x.GetRequiredService<OpsData>().PoolAsync()).Select(p => new { p.SkuPartNumber, p.SkuId, p.IsCopilot, p.IsSuite, p.Bought, assigned = p.Consumed, p.Available, p.CapturedUtc }) }, section: Sections.Licensing));

        api.MapGet("/value", (HttpContext h, CancellationToken _) => Run(h, "value", null, async (x, c) =>
        {
            var v = await x.GetRequiredService<OpsReports>().ValueAsync(c);
            /* Departments folded as the page folds them: the API returned every one, down to one person. */
            var byDepartment = SmallGroups.Fold(v.ByDepartment, d => d.People, OpsReports.CombineValue, c.MinGroupSize, out var foldedDepartments);
            return new { scope = c.ScopeLabel, from = c.Start, to = c.End, minGroupSize = c.MinGroupSize, hourlyCost = v.HourlyCost, defaultMinutes = v.DefaultMinutes, byApp = v.ByApp, byDepartment };
        }, section: Sections.Leadership));

        /* The targets for the caller's part of the organization, as the Targets page shows them (see OpsReports.Visible):
           every target went to anyone with the Adoption role, the whole organization's and other executives' parts too. */
        api.MapGet("/targets", (HttpContext h, CancellationToken _) => Run(h, "targets", null, async (x, c) =>
            new { asOf = c.End, targets = await x.GetRequiredService<OpsReports>().TargetsAsync(c) }, section: Sections.Adoption));

        api.MapGet("/license-actions", (HttpContext h, CancellationToken _) => Run(h, "license-actions", Roles.Licensing, async (x, c) =>
        {
            var kind = h.Request.Query["kind"].FirstOrDefault() is { } k && Pages.Forecasting.ActionsModel.Kinds.ContainsKey(k) ? k : "upgrade";
            var db = x.GetRequiredService<Db>();
            /* The page's own activity (with a.P, Premium days), which train and revoke read: without it they failed. */
            var k30 = ReportContext.Key(c.End.AddDays(-29));
            var rows = await db.QueryAsync<(string Upn, string? Name, string? Department, int Days, int Interactions)>($@"
WITH {await Pages.Forecasting.ActionsModel.ActivitySqlAsync(db, k30, c.EndKey)},
lic AS (SELECT UserKey FROM bi.FactLicensedDay WHERE DateKey = @k1)
SELECT du.UserPrincipalName, du.DisplayName, du.Department, ISNULL(a.D, 0), ISNULL(a.I, 0)
FROM bi.DimUser AS du {c.Scope("du.UserKey")}
LEFT JOIN a ON a.UserKey = du.UserKey LEFT JOIN lic ON lic.UserKey = du.UserKey
WHERE {Pages.Forecasting.ActionsModel.Kinds[kind].Where} AND {Pages.Forecasting.ActionsModel.Enabled(kind)}
ORDER BY du.UserPrincipalName;", c.Args(new { k30 }));
            return new { kind, asOf = c.End, people = rows.Select(r => new { upn = r.Upn, name = r.Name, department = r.Department, daysUsed30 = r.Days, interactions30 = r.Interactions }) };
        }, section: Sections.Forecasting));

        api.MapGet("/leavers", (HttpContext h, CancellationToken _) => Run(h, "leavers", Roles.Licensing, async (x, c) =>
            new { people = (await x.GetRequiredService<OpsData>().LeaversAsync(c.Root)).Select(r => new { upn = r.UserPrincipalName, name = r.DisplayName, department = r.Department, reason = r.Reason, licensedSince = r.LicensedSinceUtc, lastUsed = r.LastUsed }) }, section: Sections.Licensing));

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
        /* A section granted across the whole organization widens that section's calls, as it widens its pages. */
        var viewer = (await sp.GetRequiredService<ViewerService>().GetAsync()).ForSection(section);
        var c = await sp.GetRequiredService<ReportContextFactory>().CreateAsync(viewer, h.Request);
        var audit = sp.GetRequiredService<AuditLog>();
        var path = "/api/v1/" + name;
        string? refusal = null;
        /* The only Admin call is health: for whoever may open Admin › Health, a limited administrator too. */
        if (role == Roles.Admin) { if (!viewer.CanAdmin("/admin/" + name)) refusal = "administrators only"; }
        else if (!viewer.Admitted) refusal = $"the dashboard is for levels 1 to {viewer.MaxLevel} of the organization";
        else if (!viewer.OrgWide && !viewer.InDirectory) refusal = "not in the directory, and not granted Organization";
        else if (c.OrgWhy == OrgRefusal.Privacy) refusal = "privacy mode is on: no report on a manager's part of the organization";
        else if (c.OrgWhy == OrgRefusal.TooSmall) refusal = $"that part of the organization has fewer than {c.MinGroupSize} people, the minimum group size";
        else if (section is not null && !viewer.Sees(section)) refusal = $"needs the {Sections.NameOf(section)} section, granted in Admin › Access";   // the page's section needs it too
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
