using System.Globalization;
using System.Net;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// Microsoft 365 Copilot reservations and chargeback (2.6.8): each line of business's reserved seats against the add-on
/// licenses its people hold, and what that bills, as the client's reservation policy defines it (see ReservationRules).
/// The current operational state, from the licenses read last night, is never certified; month-end snapshots are kept
/// as they were. Leaders see the organizations led from inside their part; the whole organization and the Licensing role
/// see all, with the tenant's reconciliation and purchase gate. Administrators keep the register, the rate and the
/// Technology-funded pool here. Counts only: the people are on Licensing > Reservation holders, for the Licensing role.
/// </summary>
public sealed class ReservationsModel(PageServices s, ReservationData data, OpsData ops) : InsightsPage(s)
{
    public override string Title => "Copilot reservations";
    public override string Persona => "Leadership";
    public override string? OwnWindow => "today's licenses";
    public override bool UsesFilters => false;
    public override string Intro => "Each line of business's reserved Microsoft 365 Copilot seats against the add-on licenses its people hold, and what that bills: reserved, allocated, unused, over, billable and the monthly obligation, by organization.";

    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (!await data.ReadyAsync())
        {
            Notice = "Reservations cannot be recorded here yet. An administrator sets this up by running Install-CopilotAudit.ps1 -Dashboard from 2.6.8 or later.";
            return;
        }
        var whole = Ctx.Root is null && (Viewer.OrgWide || Viewer.Has(Roles.Licensing));
        var snaps = await data.SnapshotsAsync(Ctx);   // 2.7.0: the viewer's organizations only, as a snapshot shows them
        var snapParam = Request.Query["snap"].FirstOrDefault();
        ReservationView? v = null;
        if (DateTime.TryParseExact(snapParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var snapDate))
            v = await data.SnapshotAsync(Ctx, snapDate);
        v ??= await data.CurrentAsync(Ctx);
        if (v is null)
        {
            Notice = "No licenses have been read yet. The license lookup fills them each night.";
            return;
        }
        var cur = v.Currency;
        string M(decimal? x) => x is { } d ? CostRules.Money(Math.Round(d, MidpointRounding.AwayFromZero), cur) : "Pending Finance Decision";
        var holders = Open("/licensing/reservation-holders");

        /* The state shown, before anything else: current and not certified, or a snapshot as it was kept. */
        var state = v.IsSnapshot
            ? $"<b>Snapshot of {v.AsOf:d MMM yyyy}</b>, kept as it was on {v.SnapshotTaken:d MMM yyyy}: <b>Operational, not certified</b>. <a href=\"{H(Ctx.Url(Request.Path))}\">Back to today</a>."
            : $"<b>Current operational state</b>, from the licenses read for {v.AsOf:d MMM yyyy}: <b>not certified</b>. Billable licenses follow the policy's rule, MAX(reserved, allocated) for each organization, <b>pending Finance confirmation</b>." +
              (v.Rate is null ? " No rate is in force: obligations show <b>Pending Finance Decision</b>." : "");
        Panels.Add(new("State", new HtmlString($"<p class=\"res-state\" role=\"status\">{state}</p>"),
            "Certification, adjustments and the December close follow in a later release; until then no figure here is a certified financial result.", "full"));

        Kpis.Add(new("Reserved licenses", Num(v.Reserved), $"{Count(v.Orgs.Count, "organization")} in the register"));
        Kpis.Add(new("Licenses held (allocated)", Num(v.Allocated), "Microsoft 365 Copilot add-on licenses the organizations' people hold in Entra"));
        Kpis.Add(new("Unused reserved capacity", Num(v.Unused), "reserved seats nobody holds, for each organization, added up", v.Unused > 0 ? "warn" : null));
        Kpis.Add(new("Allocation over reservation", Num(v.Over), "licenses held beyond each organization's reservation, added up", v.Over > 0 ? "warn" : null));
        Kpis.Add(new("Billable licenses", Num(v.Billable), "reserved plus allocation over reservation; pending Finance confirmation"));
        Kpis.Add(new($"Monthly obligation ({cur})", M(v.Monthly), v.Rate is { } rt ? $"at {CostRules.Money(rt, cur)} a license a month" : "no rate in force",
            Why: await WhyAsync($"Billable licenses ({v.Billable:#,0}: for each organization, its reserved licenses plus its allocation over them) × the rate in force" + (v.Rate is { } r1 ? $" ({CostRules.Money(r1, cur)})." : ", none yet."), "ReservationRate")));
        Kpis.Add(new($"Remaining obligation ({cur})", M(v.Remaining), $"{Count(ReservationRules.MonthsLeft(v.AsOf), "month")} after this one, to December",
            Why: await WhyAsync($"The monthly obligation × the months left in the year after this one ({ReservationRules.MonthsLeft(v.AsOf)}).", "ReservationRate")));
        if (whole)
        {
            Kpis.Add(new("Under no listed leader", Num(v.Unmapped), "add-on holders no organization covers: to map", v.Unmapped > 0 ? "crit" : null,
                holders is null ? null : Open("/licensing/reservation-holders", "res=" + ReservationData.Unmapped)));
            Kpis.Add(new("Technology-funded", Num(v.Technology), "approved allocations, charged to the Technology pool",
                Href: holders is null ? null : Open("/licensing/reservation-holders", "res=" + ReservationData.Technology)));
        }

        /* One row for each organization, then the two pools that are no organization's. */
        var t = new Table
        {
            Headers = ["Organization", "Org ID", "Leader", "Cost center", "Reserved", "Allocated", "Unused reserved", "Over reservation", "Billable", "Utilization", "Status", $"Monthly obligation ({cur})"],
            Numeric = new HashSet<int> { 4, 5, 6, 7, 8, 9, 11 },
            EmptyText = whole || Ctx.Root is null ? "No reservations are in the register yet: an administrator adds them below." : "No reservation is led from inside this part of the organization.",
        };
        foreach (var o in v.Orgs)
        {
            object name = holders is not null && !v.IsSnapshot ? new Link(o.Name, Open("/licensing/reservation-holders", "res=" + Uri.EscapeDataString(o.OrgId))!) : o.Name;
            var why = o.LeaderMissing ? " (leader not in the directory)" : o.SharedLeader ? " (leader on another organization too)" : "";
            /* 2.7.0: privacy mode names nobody, a leader included. */
            var leader = o.LeaderUpn is null ? "(no one: a pool)" : PrivacyMode ? "(named in the register)" : o.LeaderName ?? o.LeaderUpn;
            t.Rows.Add([name, o.OrgId, leader, o.CostCenter, o.Reserved, o.Allocated, o.Unused, o.Over, o.Billable,
                o.Utilization is { } u ? Pct(u) : "N/A", new HtmlString(StatusCell(o.Status) + H(why)), M(ReservationRules.Monthly(o.Billable, v.Rate))]);
        }
        if (whole)
        {
            object Pool(string label, string id) => holders is not null && !v.IsSnapshot ? new Link(label, Open("/licensing/reservation-holders", "res=" + id)!) : label;
            t.Rows.Add([Pool("Under no listed leader", ReservationData.Unmapped), "", "", "", 0, v.Unmapped, "", "", "", "N/A",
                new HtmlString(v.Unmapped > 0 ? StatusCell(ReservationStatus.DataException) + " (to map to an organization)" : "None"), ""]);
            t.Rows.Add([Pool("Technology-funded pool", ReservationData.Technology), "", "", "", 0, v.Technology, "", "", "", "N/A", "Charged to the Technology pool", ""]);
        }
        t.Rows.Add([new HtmlString("<b>All organizations</b>"), "", "", "", v.Reserved, v.Allocated, v.Unused, v.Over, v.Billable,
            ReservationRules.Utilization(v.Reserved, v.Allocated) is { } tu ? Pct(tu) : "N/A", "", M(v.Monthly)]);
        CsvTable = t;
        Panels.Add(new(v.IsSnapshot ? $"Organizations on {v.AsOf:d MMM yyyy}" : "Organizations", t.Render(),
            "Allocated: Microsoft 365 Copilot add-on licenses held by the organization's people, everyone under its leader in the org chart; under two listed leaders, a person counts with the nearer one. " +
            "Copilot held only inside a suite (E7) and Technology-funded accounts are not counted. Utilization is allocated over reserved; with nothing reserved it is N/A. " +
            "The organizations total is the sum of the rows: an organization under its reservation never offsets one over it. Copilot Credits are charged to the same organizations on Leadership › Credit chargeback." +
            (holders is null ? "" : " Select an organization for its people."), "full", CsvUrl()));

        if (whole && !v.IsSnapshot) await ReconcileAsync(v);
        if (!v.IsSnapshot) await ExceptionsAsync(v, whole);
        SnapshotsPanel(snaps, cur, v.IsSnapshot ? v.AsOf : null);
        if (Viewer.IsAdmin) await AdminAsync(cur);
    }

    static string StatusCell(ReservationStatus st) =>
        $"<span class=\"{(st is ReservationStatus.OverReservation or ReservationStatus.DataException ? "status-bad" : st is ReservationStatus.WithinReservation ? "status-warn" : "status-ok")}\">{H(ReservationRules.Label(st))}</span>";

    /* The tenant's add-on seats against what the organizations hold, and the purchase gate. */
    async Task ReconcileAsync(ReservationView v)
    {
        /* 2.7.0: the Microsoft 365 Copilot add-on only, as Costs and savings counts it: not Copilot Studio's licenses, nor a
           suite with Copilot inside (E7), which the organizations' figures leave out too. */
        var suiteSkus = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.LicenseSku', N'CopilotInSuite') IS NULL THEN 0 ELSE 1 END;") == 1
            ? (await Db.QueryAsync<string>("SELECT SkuPartNumber FROM audit.LicenseSku WHERE CopilotInSuite = 1;")).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pool = (await ops.PoolAsync()).Where(p => p.IsCopilot && !suiteSkus.Contains(p.SkuPartNumber) && !p.SkuPartNumber.Contains("STUDIO", StringComparison.OrdinalIgnoreCase)).ToList();
        int bought = pool.Sum(p => p.Bought), assigned = pool.Sum(p => p.Consumed), unassigned = Math.Max(0, bought - assigned);
        var counted = v.Allocated + v.Unmapped + v.Technology;
        var pct = await S.Settings.IntAsync("ReservationGatePercent", 95);
        var baseline = await S.Settings.IntAsync("ReservationGateBaseline", 0);
        string gate;
        if (baseline <= 0) gate = "No purchase-gate baseline is set: an administrator sets the unassigned count it starts from below.";
        else
        {
            var g = ReservationRules.Gate(baseline, unassigned, pct);
            gate = $"{g.Assigned:#,0} of the {baseline:#,0} unassigned licenses counted at the baseline are now assigned ({Pct(g.Share)}). " +
                   (g.Reached ? $"The {pct}% gate is reached: a purchase may be considered, with a buffer of {g.Buffer:#,0}."
                              : $"The gate is {pct}%: {Math.Max(0, unassigned - g.Buffer):#,0} more to assign before a purchase is considered, leaving a buffer of {g.Buffer:#,0}.");
        }
        Panels.Add(new("Reconciliation and purchase gate", new HtmlString(
            "<div class=\"facts\">" +
            $"<div><span class=\"fl\">Add-on licenses bought</span><b>{bought:#,0}</b><p>{assigned:#,0} assigned in Entra, {unassigned:#,0} unassigned</p></div>" +
            $"<div><span class=\"fl\">Held, as counted here</span><b>{counted:#,0}</b><p>{v.Allocated:#,0} by organizations, {v.Unmapped:#,0} under no leader, {v.Technology:#,0} Technology-funded</p></div>" +
            $"<div><span class=\"fl\">Difference to reconcile</span><b>{assigned - counted:+#,0;-#,0;0}</b><p>{(assigned == counted ? "the license pool and the license holders agree" : "the license pool's assigned count against the holders read for each person: a refresh or an assignment in flight")}</p></div>" +
            $"<div><span class=\"fl\">Unused reserved capacity</span><b>{v.Unused:#,0}</b><p>reserved and not allocated: not the same as unassigned inventory</p></div>" +
            /* 2.7.0: what the organizations are charged against what is bought. */
            $"<div><span class=\"fl\">Billable against bought</span><b>{v.Billable - bought:+#,0;-#,0;0}</b><p>{v.Billable:#,0} billable to the organizations, {bought:#,0} bought: reserved seats nobody holds are charged, and seats held over a reservation are billed beyond it</p></div>" +
            "</div>" + $"<p class=\"sub\"><b>Purchase gate.</b> {H(gate)}</p>"),
            "From the license pool (Graph's subscribed SKUs) and the licenses read for each person. The policy and the proposal name different pools for the 95% gate; until Finance decides, the gate counts unassigned inventory and the unused reserved capacity is shown beside it. The gate decides whether more licenses are bought; it is not the spare seats Costs and savings keeps for joiners, which decide what is a saving.", "half"));
    }

    /* What needs someone's attention before the figures can be relied on. */
    async Task ExceptionsAsync(ReservationView v, bool whole)
    {
        var t = new Table { Headers = ["Exception", "How many", "What to do", "Where"], Numeric = new HashSet<int> { 1 }, Sortable = false,
            EmptyText = "No exceptions: every organization's leader is in the directory, every holder maps to an organization or the Technology pool." };
        void Add(string what, int n, string todo, object? where) { if (n > 0) t.Rows.Add([what, n, todo, where]); }
        object? To(string path, string? extra = null, string text = "Open the list") => Open(path, extra) is { } u ? new Link(text, u) : null;

        var missing = v.Orgs.Where(o => o.LeaderMissing).ToList();
        Add("Leader not in the directory", missing.Count, "Correct the sign-in in the register: " + string.Join(", ", missing.Select(o => o.Name)), null);
        var shared = v.Orgs.Where(o => o.SharedLeader).Select(o => o.Name).ToList();
        Add("A leader on more than one organization", shared.Count, "Give each organization its own leader; their people count with the first: " + string.Join(", ", shared), null);
        if (whole)
        {
            Add("Add-on holders under no listed leader", v.Unmapped, "Map them: add their leader's organization, or approve them as Technology-funded.", To("/licensing/reservation-holders", "res=" + ReservationData.Unmapped));
            var tech = (await Db.QueryAsync<string>("SELECT Upn FROM dash.TechnologyAllocation;")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = (await NonHuman.AllAsync(Db, S.Settings)).Count(b => !tech.Contains(b.UserPrincipalName));
            Add("Accounts that look non-human, not in the Technology register", candidates, "Approve each as Technology-funded with its owner and review date, or remove its license.", To("/licensing/non-person"));
            var expired = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.TechnologyAllocation WHERE ReviewDate < CAST(GETDATE() AS DATE);");
            Add("Technology-funded allocations past their review date", expired, "Review and renew, or end the allocation.", null);
        }
        /* 2.7.0: only the add-on licenses an organization counts: Copilot inside a suite, and Technology-funded accounts, are not. */
        var notCounted = (await Db.QueryAsync<string>("SELECT Upn FROM dash.CopilotSuiteOnly UNION SELECT Upn FROM dash.TechnologyAllocation;")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leavers = (await ops.LeaversAsync(Ctx.Root)).Count(l => !notCounted.Contains(l.UserPrincipalName));
        Add("Add-on licenses on disabled or departed accounts", leavers, "Reclaim them: they count against their organization.", To("/licensing/leavers"));
        if (v.Rate is null) Add("No rate in force", 1, "Finance enters the rate: until then obligations are Pending Finance Decision.", null);
        Panels.Add(new("Exceptions", t.Render(), "Raised from today's data. The register's leaders, the org chart and the licenses held are what each check reads.", "half"));
    }

    void SnapshotsPanel(List<(DateTime Date, DateTime Taken, List<ReservationOrg> Orgs, decimal? Rate)> snaps, string cur, DateTime? shown)
    {
        var t = new Table { Headers = ["Month end", "Kept on", "Reserved", "Allocated", "Billable", $"Monthly obligation ({cur})", ""], Numeric = new HashSet<int> { 2, 3, 4, 5 }, Sortable = false,
            EmptyText = "No month-end snapshot yet: the nightly run keeps one for each month once the register has rows." };
        foreach (var s in snaps)
        {
            var billable = s.Orgs.Sum(o => o.Billable);
            t.Rows.Add([s.Date, s.Taken.Date, s.Orgs.Sum(o => o.Reserved), s.Orgs.Sum(o => o.Allocated), billable,
                ReservationRules.Monthly(billable, s.Rate) is { } m ? Math.Round(m) : "Pending Finance Decision",
                shown == s.Date ? "Shown" : new Link("View", Ctx.Url(Request.Path, extra: $"snap={s.Date:yyyy-MM-dd}"))]);
        }
        Panels.Add(new("Month-end snapshots", t.Render(),
            "Each month's position, kept once and never rewritten: Operational, not certified. The org chart a snapshot used is the one on the night it was kept.", "half"));
    }

    /* ---- The register, the rate, the purchase gate and the Technology pool: administrators -------------------- */

    sealed class RegRow
    {
        public int ReservationId { get; set; }
        public string OrgId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? LeaderUpn { get; set; }
        public string? CostCenter { get; set; }
        public int Reserved { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string? Note { get; set; }
    }

    async Task AdminAsync(string cur)
    {
        var reg = await Db.QueryAsync<RegRow>("SELECT ReservationId, OrgId, Name, LeaderUpn, CostCenter, Reserved, EffectiveFrom, EffectiveTo, Note FROM dash.Reservation ORDER BY OrgId, EffectiveFrom;");
        var rt = new Table { Headers = ["Org ID", "Organization", "Leader", "Cost center", "Reserved", "From", "To", "Note", ""], Numeric = new HashSet<int> { 4 }, EmptyText = "The register is empty." };
        foreach (var r in reg)
            rt.Rows.Add([r.OrgId, r.Name, r.LeaderUpn, r.CostCenter, r.Reserved, r.EffectiveFrom, r.EffectiveTo, r.Note,
                /* 2.7.0: ended on a day, so past months keep it, or removed as entered by mistake. */
                new HtmlString((r.EffectiveTo is null ? InlineForm("endreserve", Hidden("id", r.ReservationId) + Input("end", "End on", null, 10, "date") + Button("End", quiet: true)) : "") +
                    Form("delreserve", Hidden("id", r.ReservationId) + RemoveBox($"Remove {r.Name} from {r.EffectiveFrom:yyyy-MM-dd}")))]);
        var add = "<h3>Add an organization's reservation</h3>" + Form("reserve",
            Input("orgId", "Org ID", null, 12) + Input("name", "Organization", null, 20) + Input("leader", "Leader's sign-in", null, 26) + Input("costCenter", "Cost center", null, 10) +
            Input("reserved", "Reserved", null, 6, "number") + Input("from", "From", new DateTime(DateTime.Today.Year, 1, 1).ToString("yyyy-MM-dd"), 10, "date") + Input("to", "To (optional)", null, 10, "date") +
            Input("note", "Note", null, 20) + Button("Add"));
        var paste = "<h3>Add several at once</h3><p class=\"sub\">One organization a line: Org ID, organization, leader's sign-in, reserved, from (yyyy-mm-dd), and optionally cost center and to. A header line is skipped.</p>" +
            Form("paste", "<label>Rows<textarea name=\"rows\" rows=\"5\" cols=\"90\" placeholder=\"LOB-01,Retail Banking,leader@contoso.com,7403,2027-01-01,CC-100\"></textarea></label>" + Button("Add these"));
        Panels.Add(new("Reservation register", new HtmlString(rt.Render().Value + add + paste),
            $"{reg.Sum(r => r.Reserved):#,0} reserved over {reg.Select(r => r.OrgId).Distinct().Count():#,0} organizations, every row whatever its dates. A change to a reservation is a new row from its date: the organization's open row is ended the day before, so past months keep what they were. *End* ends a row on a day; the cross removes one entered by mistake. Every change is in the audit log.", "full"));

        var rates = await Db.QueryAsync<(DateTime From, decimal Rate, string? Currency, string? Note)>("SELECT EffectiveFrom, MonthlyRate, Currency, Note FROM dash.ReservationRate ORDER BY EffectiveFrom DESC;");
        var ratesT = new Table { Headers = ["From", "Rate a license a month", "Currency", "Note", ""], Numeric = new HashSet<int> { 1 }, EmptyText = "No rate: obligations are Pending Finance Decision." };
        foreach (var r in rates)
            ratesT.Rows.Add([r.From, r.Rate.ToString("0.####", CultureInfo.InvariantCulture), r.Currency ?? cur, r.Note, new HtmlString(Form("delrate", Hidden("from", r.From.ToString("yyyy-MM-dd")) + RemoveBox($"Remove the rate from {r.From:yyyy-MM-dd}")))]);
        var pct = await S.Settings.IntAsync("ReservationGatePercent", 95);
        var baseline = await S.Settings.IntAsync("ReservationGateBaseline", 0);
        Panels.Add(new("Rate and purchase gate", new HtmlString(ratesT.Render().Value +
            "<h3>Add a rate</h3>" + Form("rate", Input("from", "From", new DateTime(DateTime.Today.Year, 1, 1).ToString("yyyy-MM-dd"), 10, "date") +
                Input("rate", $"Rate a license a month ({cur})", null, 8) + Input("note", "Note", "Microsoft 365 Copilot at 30 a month, less 40%", 34) + Button("Add")) +
            "<h3>Purchase gate</h3>" + Form("gate", Input("baseline", "Unassigned licenses at the baseline", baseline > 0 ? baseline.ToString(CultureInfo.InvariantCulture) : null, 8, "number") +
                Input("percent", "Share to assign first (%)", pct.ToString(CultureInfo.InvariantCulture), 4, "number") + Button("Save"))),
            "Finance's rate after any discount, from the date it applies. The gate: no more licenses are bought until this share of the unassigned licenses counted at the baseline is assigned.", "half"));

        var tech = await Db.QueryAsync<(string Upn, string Category, string Owner, string? Ref, DateTime From, DateTime? Review, string? Note)>(
            "SELECT Upn, Category, Owner, ApprovalRef, EffectiveFrom, ReviewDate, Note FROM dash.TechnologyAllocation ORDER BY Category, Upn;");
        var tt = new Table { Headers = ["Account", "Category", "Owner", "Approval", "From", "Review by", "Note", ""], EmptyText = "No Technology-funded allocations yet." };
        foreach (var r in tech)
            tt.Rows.Add([r.Upn, r.Category, r.Owner, r.Ref, r.From, r.Review, r.Note, new HtmlString(Form("deltech", Hidden("upn", r.Upn) + RemoveBox($"Remove {r.Upn} from the Technology pool")))]);
        Panels.Add(new("Technology-funded register", new HtmlString(tt.Render().Value + "<h3>Approve an allocation</h3>" + Form("tech",
            Input("upn", "Account's sign-in", null, 26) + Select("category", "Category", TechCategories, "Service") + Input("owner", "Accountable owner", null, 22) +
            Input("approval", "Approval reference", null, 12) + Input("from", "From", DateTime.Today.ToString("yyyy-MM-dd"), 10, "date") +
            Input("review", "Review by", DateTime.Today.AddMonths(6).ToString("yyyy-MM-dd"), 10, "date") + Input("note", "Note", null, 20) + Button("Approve"))),
            "Executive, service, test, shared, migration and exception accounts whose license the Technology pool funds: never charged to a line of business, and counted once there.", "half"));
    }

    static readonly string[] TechCategories = ["Executive", "Service", "Test", "Shared", "Migration", "Exception"];

    static string Clip(string? s, int n) => (s ?? "").Trim() is var t && t.Length > n ? t[..n] : (s ?? "").Trim();
    static string? Opt(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : Clip(s, n);

    /// <summary>One register row, checked (2.7.0: every row of a paste is checked before any is added).</summary>
    public sealed record RegisterRow(string OrgId, string Name, string? LeaderUpn, string? CostCenter, int Reserved, DateTime From, DateTime? To, string? Note);

    /// <summary>A row's fields made into a register row, or why not. Pure, for the tests.</summary>
    public static RegisterRow Check(string? orgId, string? name, string? leader, string? costCenter, int? reserved, DateTime? from, DateTime? to, string? note)
    {
        orgId = Clip(orgId, 64); name = Clip(name, 200);
        if (orgId.Length == 0) throw new ArgumentException("give the organization its persistent Org ID.");
        if (orgId.StartsWith('~')) throw new ArgumentException("an Org ID cannot start with ~ (kept for the pools).");
        if (name.Length == 0) throw new ArgumentException("give the organization a name.");
        if (reserved is null or < 0 or > 10_000_000) throw new ArgumentException("reserved is a whole number of licenses.");
        if (from is null) throw new ArgumentException("give the date the reservation is effective from.");
        if (to is not null && to < from) throw new ArgumentException("the end date comes after the start.");
        return new(orgId, name, Opt(leader, 256)?.ToLowerInvariant(), Opt(costCenter, 100), reserved.Value, from.Value.Date, to?.Date, Opt(note, 400));
    }

    /// <summary>
    /// Adds the rows in one transaction, all or none (2.7.0). An organization's row open on a new row's start is ended the
    /// day before, so a change is a new row and never a second reservation summed with the first; a new row that would
    /// overlap a row starting on or after it is refused. Returns what each row ended.
    /// </summary>
    async Task<int> AddAsync(IReadOnlyList<RegisterRow> rows)
    {
        if (rows.GroupBy(r => r.OrgId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("each organization once a paste: add its later reservation in a second paste.");
        var json = System.Text.Json.JsonSerializer.Serialize(rows.Select(r => new { r.OrgId, r.Name, r.LeaderUpn, r.CostCenter, r.Reserved, From = r.From.ToString("yyyy-MM-dd"), To = r.To?.ToString("yyyy-MM-dd"), r.Note }));
        var clash = await Db.ScalarAsync<string?>(@"
SELECT TOP (1) CONCAT(n.OrgId, N' from ', CONVERT(CHAR(10), n.[From], 120))
FROM OPENJSON(@json) WITH (OrgId NVARCHAR(64), [From] DATE, [To] DATE) AS n
JOIN dash.Reservation AS r ON r.OrgId = n.OrgId AND r.EffectiveFrom >= n.[From] AND (n.[To] IS NULL OR r.EffectiveFrom <= n.[To])
ORDER BY n.OrgId;", new { json });
        if (clash is not null) throw new ArgumentException($"{clash} overlaps a row that starts on or after it: end or remove that row first.");
        /* A new row with an end, inside a row that runs past that end: ending the old row the day before would leave the
           organization with no reservation after the new one ends. */
        var inside = await Db.ScalarAsync<string?>(@"
SELECT TOP (1) CONCAT(n.OrgId, N' from ', CONVERT(CHAR(10), n.[From], 120), N' to ', CONVERT(CHAR(10), n.[To], 120))
FROM OPENJSON(@json) WITH (OrgId NVARCHAR(64), [From] DATE, [To] DATE) AS n
JOIN dash.Reservation AS r ON r.OrgId = n.OrgId AND r.EffectiveFrom < n.[From] AND (r.EffectiveTo IS NULL OR r.EffectiveTo > n.[To])
WHERE n.[To] IS NOT NULL
ORDER BY n.OrgId;", new { json });
        if (inside is not null) throw new ArgumentException($"{inside} falls inside a row that runs past its end: end that row on the day before, add this one, then add the row that follows it.");
        return await Db.ScalarAsync<int>(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @n TABLE (OrgId NVARCHAR(64), Name NVARCHAR(200), LeaderUpn NVARCHAR(256), CostCenter NVARCHAR(100), Reserved INT, [From] DATE, [To] DATE, Note NVARCHAR(400));
INSERT INTO @n SELECT * FROM OPENJSON(@json) WITH (OrgId NVARCHAR(64), Name NVARCHAR(200), LeaderUpn NVARCHAR(256), CostCenter NVARCHAR(100), Reserved INT, [From] DATE, [To] DATE, Note NVARCHAR(400));
UPDATE r SET EffectiveTo = DATEADD(DAY, -1, n.[From]), UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
FROM dash.Reservation AS r JOIN @n AS n ON n.OrgId = r.OrgId
WHERE r.EffectiveFrom < n.[From] AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= n.[From]);
DECLARE @ended INT = @@ROWCOUNT;
INSERT INTO dash.Reservation (OrgId, Name, LeaderUpn, CostCenter, Reserved, EffectiveFrom, EffectiveTo, Note, UpdatedBy)
SELECT OrgId, Name, LeaderUpn, CostCenter, Reserved, [From], [To], Note, @by FROM @n;
COMMIT TRANSACTION;
SELECT @ended;", new { json, by = Viewer.Upn ?? Viewer.WindowsName });
    }

    static string Said(RegisterRow r) => $"{r.Name} ({r.OrgId}): {r.Reserved:#,0} reserved from {r.From:yyyy-MM-dd}{(r.LeaderUpn is null ? ", with no leader" : $", led by {r.LeaderUpn}")}";

    public Task<IActionResult> OnPostReserveAsync(string? orgId, string? name, string? leader, string? costCenter, int? reserved, DateTime? from, DateTime? to, string? note) =>
        AdminChangeAsync(async () =>
        {
            var row = Check(orgId, name, leader, costCenter, reserved, from, to, note);
            var ended = await AddAsync([row]);
            return "Reservation added: " + Said(row) + (ended > 0 ? $"; its row before was ended on {row.From.AddDays(-1):yyyy-MM-dd}." : ".");
        });

    public Task<IActionResult> OnPostEndreserveAsync(int id, DateTime? end) => AdminChangeAsync(async () =>
    {
        if (end is null) throw new ArgumentException("give the last day the reservation is in effect.");
        var row = (await Db.QueryAsync<(string OrgId, string Name, int Reserved, DateTime From)>("SELECT OrgId, Name, Reserved, EffectiveFrom FROM dash.Reservation WHERE ReservationId = @id;", new { id })).FirstOrDefault();
        if (row.OrgId is null) throw new ArgumentException("that reservation is gone.");
        if (end < row.From) throw new ArgumentException("the end comes on or after its start.");
        await Db.ExecuteAsync("UPDATE dash.Reservation SET EffectiveTo = @to, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE ReservationId = @id;", new { id, to = end, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Reservation ended: {row.Name} ({row.OrgId}), {row.Reserved:#,0} from {row.From:yyyy-MM-dd}, now to {end:yyyy-MM-dd}.";
    });

    public Task<IActionResult> OnPostPasteAsync(string? rows) => AdminChangeAsync(async () =>
    {
        var parsed = ParsePaste(rows);
        var ended = await AddAsync(parsed);
        return $"{parsed.Count:#,0} reservations added, {parsed.Sum(p => p.Reserved):#,0} licenses" + (ended > 0 ? $"; {ended:#,0} open row{(ended == 1 ? "" : "s")} ended the day before." : ".");
    });

    /// <summary>
    /// The pasted lines as register rows (2.7.0): a field may be quoted, holding commas; a first line that is not a row
    /// (its reserved is not a number and its from is not a date) is the header; every line is checked before any is
    /// added. Pure, for the tests.
    /// </summary>
    public static List<RegisterRow> ParsePaste(string? text)
    {
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) throw new ArgumentException("paste one organization a line.");
        if (lines.Count > 500) throw new ArgumentException("at most 500 lines at once.");
        var parsed = new List<RegisterRow>();
        foreach (var (line, i) in lines.Select((l, i) => (l, i + 1)))
        {
            var f = Fields(line);
            static bool IsDate(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            if (i == 1 && f.Count > 4 && !int.TryParse(f[3], out _) && !IsDate(f[4])) continue;   // the header
            if (f.Count < 5) throw new ArgumentException($"line {i} has {f.Count} fields; it needs Org ID, organization, leader, reserved and from.");
            if (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) throw new ArgumentException($"line {i}: reserved is a whole number.");
            if (!DateTime.TryParseExact(f[4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new ArgumentException($"line {i}: from is a date, yyyy-mm-dd.");
            DateTime? to = f.Count > 6 && f[6].Length > 0 ? DateTime.TryParseExact(f[6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : throw new ArgumentException($"line {i}: to is a date, yyyy-mm-dd.") : null;
            try { parsed.Add(Check(f[0], f[1], f[2], f.Count > 5 ? f[5] : null, n, d, to, null)); }
            catch (ArgumentException ex) { throw new ArgumentException($"line {i}: {ex.Message}"); }
        }
        if (parsed.Count == 0) throw new ArgumentException("no rows below the header.");
        return parsed;
    }

    /// <summary>A line's comma-separated fields, a quoted field kept whole ("Retail Banking, East"; "" is a quote).</summary>
    static List<string> Fields(string line)
    {
        var fields = new List<string>(); var cur = new System.Text.StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else if (ch == '"') quoted = false; else cur.Append(ch); }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString().Trim());
        return fields;
    }

    public Task<IActionResult> OnPostDelreserveAsync(int id) => AdminChangeAsync(async () =>
    {
        /* 2.7.0: the audit log says which reservation went. */
        var row = (await Db.QueryAsync<(string OrgId, string Name, int Reserved, DateTime From, DateTime? To)>("SELECT OrgId, Name, Reserved, EffectiveFrom, EffectiveTo FROM dash.Reservation WHERE ReservationId = @id;", new { id })).FirstOrDefault();
        if (row.OrgId is null) return "That reservation was already gone.";
        await Db.ExecuteAsync("DELETE FROM dash.Reservation WHERE ReservationId = @id;", new { id });
        return $"Reservation removed: {row.Name} ({row.OrgId}), {row.Reserved:#,0} from {row.From:yyyy-MM-dd}{(row.To is { } e ? $" to {e:yyyy-MM-dd}" : "")}; month-end snapshots keep what they were.";
    });

    public Task<IActionResult> OnPostRateAsync(DateTime? from, string? rate, string? note) => AdminChangeAsync(async () =>
    {
        if (from is null) throw new ArgumentException("give the date the rate applies from.");
        if (!decimal.TryParse(rate, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) || r < 0 || r > 100000) throw new ArgumentException("the rate is a number.");
        if (decimal.Round(r, 2) != r) throw new ArgumentException("the rate has at most two decimals, as it is kept.");
        /* 2.7.0: a rate replaced on the same day says what it was. */
        var was = await Db.ScalarAsync<decimal?>("SELECT MonthlyRate FROM dash.ReservationRate WHERE EffectiveFrom = @from;", new { from });
        await Db.ExecuteAsync(@"
MERGE dash.ReservationRate AS t USING (SELECT @from AS EffectiveFrom) AS s ON t.EffectiveFrom = s.EffectiveFrom
WHEN MATCHED THEN UPDATE SET MonthlyRate = @r, Note = @note, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (EffectiveFrom, MonthlyRate, Note, UpdatedBy) VALUES (@from, @r, @note, @by);",
            new { from, r, note = Opt(note, 400), by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Rate {r:0.####} a license a month from {from:yyyy-MM-dd}" + (was is { } w && w != r ? $" (was {w:0.####})." : ".");
    });

    public Task<IActionResult> OnPostDelrateAsync(DateTime? from) => AdminChangeAsync(async () =>
    {
        var was = await Db.ScalarAsync<decimal?>("SELECT MonthlyRate FROM dash.ReservationRate WHERE EffectiveFrom = @from;", new { from });
        return was is { } w && await Db.ExecuteAsync("DELETE FROM dash.ReservationRate WHERE EffectiveFrom = @from;", new { from }) > 0
            ? $"The rate from {from:yyyy-MM-dd} ({w:0.####}) removed." : "That rate was already gone.";
    });

    public Task<IActionResult> OnPostGateAsync(int? baseline, int? percent) => AdminChangeAsync(async () =>
    {
        if (baseline is < 0) throw new ArgumentException("the baseline is a whole number of licenses.");
        if (percent is null or < 1 or > 100) throw new ArgumentException("the share is a percentage, 1 to 100.");
        await Db.ExecuteAsync(@"
UPDATE dash.Setting SET Value = @b WHERE Name = N'ReservationGateBaseline';
UPDATE dash.Setting SET Value = @p WHERE Name = N'ReservationGatePercent';",
            new { b = baseline is null or 0 ? null : baseline.Value.ToString(CultureInfo.InvariantCulture), p = percent.Value.ToString(CultureInfo.InvariantCulture) });
        S.Settings.Invalidate();
        return baseline is null or 0 ? "Purchase gate turned off." : $"Purchase gate: {percent}% of {baseline:#,0} unassigned licenses.";
    });

    public Task<IActionResult> OnPostTechAsync(string? upn, string? category, string? owner, string? approval, DateTime? from, DateTime? review, string? note) => AdminChangeAsync(async () =>
    {
        var u = Clip(upn, 256).ToLowerInvariant();
        if (u.Length == 0 || !u.Contains('@')) throw new ArgumentException("give the account's sign-in.");
        if (category is null || !TechCategories.Contains(category)) throw new ArgumentException("choose a category.");
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("name the accountable owner.");
        if (from is null) throw new ArgumentException("give the date it applies from.");
        await Db.ExecuteAsync(@"
MERGE dash.TechnologyAllocation AS t USING (SELECT @u AS Upn) AS s ON t.Upn = s.Upn
WHEN MATCHED THEN UPDATE SET Category = @category, Owner = @owner, ApprovalRef = @approval, EffectiveFrom = @from, ReviewDate = @review, Note = @note, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (Upn, Category, Owner, ApprovalRef, EffectiveFrom, ReviewDate, Note, UpdatedBy) VALUES (@u, @category, @owner, @approval, @from, @review, @note, @by);",
            new { u, category, owner = Clip(owner, 256), approval = Opt(approval, 200), from, review, note = Opt(note, 400), by = Viewer.Upn ?? Viewer.WindowsName });
        return $"{u} is Technology-funded ({category}), owned by {Clip(owner, 256)}.";
    });

    public Task<IActionResult> OnPostDeltechAsync(string? upn) => AdminChangeAsync(async () =>
        await Db.ExecuteAsync("DELETE FROM dash.TechnologyAllocation WHERE Upn = @upn;", new { upn }) > 0 ? $"{upn} is no longer Technology-funded." : "That account was not in the register.");
}
