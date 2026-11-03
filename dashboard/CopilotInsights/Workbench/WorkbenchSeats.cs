using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Queries;
using Governance.Domain;
using Microsoft.Extensions.Options;

namespace Governance.Web.Infrastructure;

/// <summary>
/// The Workbench's Copilot seats by line of business, as Copilot Insights' reservation pages show them when the
/// Workbench is on: core.SeatReservation is the record and dash.Reservation is kept as it is, unread by these pages.
/// A viewer with the Licensing role, or one whose role covers the whole organization, reads every line of business;
/// anyone else reads the lines of business their Workbench roles let them see, as the Workbench's own pages do.
/// </summary>
public sealed class WorkbenchSeats(IServiceProvider services, WorkbenchState state, CopilotInsights.Infrastructure.TenantAccessor tenants)
{
    /// <summary>On, and the page is in the Workbench's tenant: another tenant's pages keep their own register.</summary>
    public bool On => state.OnFor(tenants);

    /// <summary>Reads every line of business: central scope, the seat view only, never a decider.</summary>
    sealed class Reader : ICurrentPrincipal
    {
        public static readonly Reader Instance = new();
        public bool IsAuthenticated => true;
        public int PersonId => 0;
        public int? RealPersonId => null;
        public string DisplayName => "Copilot Insights";
        public bool IsCentral => true;
        public IReadOnlyList<RoleGrant> Roles { get; } = Array.Empty<RoleGrant>();
        public IReadOnlySet<string> AppRoles { get; } = new HashSet<string>();
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { SeatAccess.View };
        public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = new Dictionary<int, IReadOnlySet<string>>();
    }

    /// <summary>
    /// The seat queries for this viewer: every line of business when <paramref name="whole"/>, else the viewer's own
    /// Workbench scope (loaded here, as a Workbench page loads it). Null when the Workbench is off.
    /// </summary>
    public SeatQueries? Queries(bool whole) =>
        !On ? null : Make(whole ? Reader.Instance : services.GetRequiredService<WebPrincipal>());

    SeatQueries Make(ICurrentPrincipal p) =>
        new(new Db(services.GetRequiredService<IOptions<DbOptions>>(), p), p, services.GetRequiredService<IClock>());

    /// <summary>The viewer as the Workbench knows them, loaded once a request; null with no one signed in.</summary>
    public async Task<WebPrincipal?> PersonAsync(HttpContext http)
    {
        if (!On || http.User.Identity?.IsAuthenticated != true) return null;
        var p = services.GetRequiredService<WebPrincipal>();
        if (!p.IsAuthenticated) await services.GetRequiredService<PrincipalLoader>().LoadAsync(http, p);
        return p.IsAuthenticated ? p : null;
    }

    /// <summary>What a reservation page shows: the seat position by line of business and the certified chargeback.</summary>
    public async Task<SeatPosition> ReadAsync(SeatQueries q, bool whole)
    {
        var lobs = await q.LobsAsync();
        var settings = await q.SettingsAsync();
        var summary = whole ? await q.SummaryAsync() : null;
        var months = new List<SeatMonth>();
        foreach (var m in (await q.SnapshotMonthsAsync()).Take(12))
        {
            var rows = await q.SnapshotAsync(m);
            if (rows.Count == 0) continue;
            var charged = rows.Where(r => r.FundingOwnerId is not null).ToList();
            months.Add(new SeatMonth(m, rows.All(r => r.Status == "CERTIFIED"), charged.Sum(r => r.ReservedSeats), charged.Sum(r => r.AssignedSeats),
                charged.Sum(r => r.ChargeSeats), charged.Sum(r => r.Amount), rows[0].Currency, charged));
        }
        return new SeatPosition(summary, lobs, settings.Currency, settings.RatePerSeatMonth, months) { Settings = settings };
    }

    /// <summary>Where a figure opens in the Workbench, for this viewer: null where the Workbench page would refuse them or show another number.</summary>
    public static SeatLinks Links(WebPrincipal? p, bool whole)
    {
        if (p is null) return SeatLinks.None;
        var all = SeatAccess.SeesAll(p);
        var reports = p.AppRoles.Contains(AppRoles.Auditor) || p.AppRoles.Contains(AppRoles.Central);
        /* A figure for every line of business opens a page that shows every one; a viewer's own lines open their own. */
        var list = whole ? all : Governance.Web.Pages.Registry.SeatsModel.CanOpen(p);
        return new SeatLinks(
            list ? "/workbench/Registry/Seats" : null,
            all ? "/workbench/Registry/Seats?show=nolob#nolob" : null,
            reports && (all || !whole) ? "/workbench/Reports/SeatChargeback" : null,
            fo => SeatAccess.CanView(p, fo));
    }
}

/// <summary>
/// Tells the nightly run which seat record is in use, as the site starts: dash.Setting SeatModel is Governance with
/// the Workbench on, so dash.usp_TakeReservationSnapshot adds nothing to the register's tables; Leader with it off, as
/// before. Only a value that differs is written. A database that cannot be reached is logged and left as it was.
/// </summary>
public sealed class SeatModelSwitch(WorkbenchState state, CopilotInsights.Infrastructure.TenantAccessor tenants, CopilotInsights.Infrastructure.Db db,
    ILogger<SeatModelSwitch> log) : BackgroundService
{
    public const string Name = "SeatModel", Governance = "Governance", Leader = "Leader";

    /// <summary>Again every quarter hour: a database that could not be reached when the site started is set once it answers.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do { await SetAsync(stoppingToken); }
        while (!stoppingToken.IsCancellationRequested && await Wait(stoppingToken));
    }

    static async Task<bool> Wait(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(15), ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    async Task SetAsync(CancellationToken stoppingToken)
    {
        var home = state.Home(tenants);
        foreach (var t in tenants.All)
        {
            if (stoppingToken.IsCancellationRequested) return;
            /* Only the Workbench's own tenant takes its seats; every other tenant keeps the register. */
            var value = state.On && string.Equals(t.Name, home.Name, StringComparison.OrdinalIgnoreCase) ? Governance : Leader;
            tenants.Use(t);
            try
            {
                if (await db.ExecuteAsync("UPDATE dash.Setting SET Value = @value, UpdatedBy = N'Copilot Insights', UpdatedUtc = SYSUTCDATETIME() WHERE Name = @name AND ISNULL(Value, N'') <> @value;",
                        new { name = Name, value }) > 0)
                    log.LogInformation("The Copilot seat record for {Tenant} is now {Model}.", t.Name, value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "The Copilot seat record for {Tenant} could not be set to {Model}.", t.Name, value);
            }
        }
    }
}

/// <summary>One month's seat chargeback: the lines of business charged, added up, and whether all of it is certified.</summary>
public sealed record SeatMonth(DateTime Month, bool Certified, int Reserved, int Assigned, int Charged, decimal Amount, string Currency, IReadOnlyList<SeatSnapshotRow> Rows);

/// <summary>The seat position by line of business today, with the months kept.</summary>
public sealed record SeatPosition(SeatSummary? Summary, IReadOnlyList<SeatLobRow> Lobs, string Currency, decimal Rate, IReadOnlyList<SeatMonth> Months)
{
    /// <summary>The seat policy's settings, one rule set for the Workbench's pages and these.</summary>
    public Governance.Application.Seats.SeatSettings? Settings { get; init; }
    public IEnumerable<SeatLobRow> Charged => Lobs.Where(l => l.FundingOwnerId is not null);
    public SeatLobRow? NoLob => Lobs.FirstOrDefault(l => l.FundingOwnerId is null);
    public int Reserved => Charged.Sum(l => l.ReservedSeats);
    /* As the Workbench's seat list adds them up: every row, the seats no line of business claims included. */
    public int Assigned => Lobs.Sum(l => l.AssignedSeats);
    public int Over => Lobs.Sum(l => l.OverSeats);
    public int Unused => Charged.Sum(l => l.UnusedReserved);
    public SeatMonth? LastCertified => Months.FirstOrDefault(m => m.Certified);
}

/// <summary>The Workbench pages a viewer may open from a figure.</summary>
public sealed record SeatLinks(string? Seats, string? NoLob, string? Chargeback, Func<int, bool> CanOpenLob)
{
    public static readonly SeatLinks None = new(null, null, null, _ => false);
    public string? Lob(SeatLobRow r) => r.PublicId is Guid g && r.FundingOwnerId is int fo && CanOpenLob(fo) ? "/workbench/Registry/Seats/" + g : null;
    public string? Month(DateTime m) => Chargeback is null ? null : Chargeback + "?month=" + m.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
