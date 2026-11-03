using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using SeatLinks = Governance.Web.Infrastructure.SeatLinks;
using SeatPosition = Governance.Web.Infrastructure.SeatPosition;
using WorkbenchSeats = Governance.Web.Infrastructure.WorkbenchSeats;

namespace CopilotInsights.Pages.Leadership;

/// <summary>
/// The page with the Workbench on: Copilot seats by line of business from core.SeatReservation and the seat read-back,
/// and the monthly chargeback the FinOps lead certified. The leader-based register (dash.Reservation) keeps its rows
/// and is not read or written here. Each linked figure opens the Workbench page that shows the same number.
/// </summary>
public sealed partial class ReservationsModel
{
    public const string RegisterKept =
        "the Workbench's lines of business are the Copilot seat record, so the reservation register, its rate, the purchase gate and the Technology pool are kept as they are and no longer changed here. " +
        "Record reservations in Ownership registry › Copilot seats and the groups behind them in Workbench settings › Seat groups.";

    const string LinesOfBusinessIntro = "Each line of business's reserved Microsoft 365 Copilot seats, from the Workbench, against the seats its tracking group holds, and the monthly chargeback FinOps certified: reserved, assigned, unused, over and charged.";

    async Task LinesOfBusinessAsync()
    {
        /* The warm-up fills Insights' own queries; these are the Workbench's, read for each viewer. */
        if (Warming) return;
        /* As the register's page: every line of business only for a viewer whose page shows the whole organization. */
        var whole = Ctx.Root is null && (Viewer.OrgWide || Viewer.Has(Roles.Licensing));
        SeatPosition v;
        SeatLinks links;
        try
        {
            var person = await _seats.PersonAsync(HttpContext);
            if (!whole && person is null)
            {
                Notice = "Copilot seats are recorded by line of business in the Workbench, and it does not know your account yet. The whole organization's figures are for the Licensing role.";
                return;
            }
            v = await _seats.ReadAsync(_seats.Queries(whole)!, whole);
            links = WorkbenchSeats.Links(person, whole);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice = Failure(ex, "reading the Workbench's Copilot seats");
            return;
        }
        if (v.Lobs.Count == 0)
        {
            Notice = whole
                ? "No line of business has Copilot seats yet. FinOps records each one's first reservation in Ownership registry › Copilot seats" +
                  (Viewer.IsAdmin ? "; Workbench settings › Seat groups can start them from this page's earlier reservation register." : ".")
                : "Copilot seats are recorded by line of business in the Workbench, and your Workbench roles name none. The whole organization's figures are for the Licensing role.";
            if (Viewer.IsAdmin) AdminNote();
            return;
        }

        var cur = v.Currency;
        string M(decimal x) => CostRules.Money(Math.Round(x, MidpointRounding.AwayFromZero), cur);
        var s = v.Summary;
        var read = s?.LastReadUtc is { } r ? $"from the license read-back of {When(r)}" : "from the Workbench's latest license read-back";
        var state = $"<b>Copilot seats by line of business</b>, {E(read)}: the Workbench's reservations (core.SeatReservation) against the seats each line of business's tracking group holds. " +
                    "A line of business pays its full reservation, plus the seats assigned above it." +
                    (s?.Stale == true ? " <b>The read-back is more than a day old</b>: the figures may be out of date." : "") +
                    (whole ? "" : " Only the lines of business your Workbench roles cover are shown.");
        Panels.Add(new("State", new HtmlString($"<p class=\"res-state\" role=\"status\">{state}</p>"),
            "The leader-based reservation register is kept as it was and no longer read here. Figures open the Workbench's own pages where your Workbench roles let you open them.", "full"));

        Kpis.Add(new("Reserved seats", Num(v.Reserved), $"{Count(v.Charged.Count(), "line of business", "lines of business")}, in effect today", Href: links.Seats));
        Kpis.Add(new("Seats assigned", Num(v.Assigned), "Copilot add-on seats held, by the tracking groups' members and outside them", Href: links.Seats));
        Kpis.Add(new("Unused reserved seats", Num(v.Unused), "reserved seats nobody holds: still paid until a true-down", v.Unused > 0 ? "warn" : null, links.Seats));
        Kpis.Add(new("Seats over reservation", Num(v.Over), "assigned above a line of business's reservation: charged on top", v.Over > 0 ? "warn" : null, links.Seats));
        if (whole && v.NoLob is { } none)
            Kpis.Add(new("No line of business", Num(none.AssignedSeats), "seats no tracking group claims: nobody is charged", none.AssignedSeats > 0 ? "crit" : null, links.NoLob));
        if (s is not null)
        {
            Kpis.Add(new("Seats purchased", s.Purchased is { } p ? Num(p) : "Not read", "Copilot add-on units the tenant owns", Href: links.Seats));
            Kpis.Add(new("Free to reserve", s.Unallocated is { } u ? Num(u) : "Not read", $"purchased, less reserved, less a buffer of {s.BufferSeats:#,0}, less increases asked for",
                s.Unallocated < 0 ? "crit" : null, links.Seats));
        }
        var last = v.LastCertified;
        Kpis.Add(new("Monthly chargeback", last is null ? "Not certified yet" : M(last.Amount),
            last is null ? "no month certified by the FinOps lead yet" : $"{last.Month:MMMM yyyy}, certified: {last.Charged:#,0} seats charged",
            Href: last is null ? null : links.Month(last.Month)));

        /* A count opens its people where that list counts the same people: the whole organization's. */
        var holders = Ctx.Root is null ? Open("/licensing/reservation-holders") : null;
        var byLob = last?.Rows.Where(x => x.FundingOwnerId is not null).ToDictionary(x => x.FundingOwnerId!.Value, x => x.Amount) ?? [];
        var t = new Table
        {
            Headers = ["Line of business", "Code", "Tracking group", "Reserved", "Assigned", "Over reservation", "Unused reserved", "Inactive", "Asked for", "Open findings",
                       last is null ? "Certified chargeback" : $"Chargeback, {last.Month:MMM yyyy}"],
            Numeric = new HashSet<int> { 3, 4, 5, 6, 7, 8, 9, 10 },
            EmptyText = "No line of business has Copilot seats yet.",
        };
        foreach (var l in v.Lobs)
        {
            object name = l.FundingOwnerId is null
                ? (links.NoLob is { } n ? new Link("No line of business", n) : "No line of business")
                : links.Lob(l) is { } h ? new Link(l.Name, h) : l.Name;
            object assigned = holders is not null && (l.PublicId is not null || whole)
                ? new Link(l.AssignedSeats.ToString("#,0", System.Globalization.CultureInfo.CurrentCulture),
                    Open("/licensing/reservation-holders", "res=" + (l.PublicId?.ToString() ?? CopilotInsights.Pages.Licensing.ReservationHoldersModel.NoLob))!)
                : l.AssignedSeats;
            t.Rows.Add([name, l.Code, l.FundingOwnerId is null ? "" : l.TrackingGroup ?? "(none mapped)", l.ReservedSeats, assigned, l.OverSeats,
                l.FundingOwnerId is null ? "" : l.UnusedReserved, l.Inactive, l.PendingSeats, l.OpenFindings,
                l.FundingOwnerId is int fo && byLob.TryGetValue(fo, out var a) ? M(a) : ""]);
        }
        t.Rows.Add([new HtmlString("<b>All lines of business</b>"), "", "", v.Lobs.Sum(l => l.ReservedSeats), v.Assigned, v.Over, v.Unused, v.Lobs.Sum(l => l.Inactive),
            v.Lobs.Sum(l => l.PendingSeats), v.Lobs.Sum(l => l.OpenFindings), last is null ? "" : M(byLob.Values.Sum())]);
        CsvTable = t;
        Panels.Add(new("Lines of business", t.Render(),
            "Reserved: the line of business's reservation in effect today. Assigned: its tracking group's members who hold the Copilot add-on. Over: assigned above the reservation. " +
            "Inactive: holders with no Copilot use in the usage report period. Asked for: extra seats in open reservation requests. " +
            "Select a line of business for its Workbench page" + (holders is null ? "." : ", or its assigned count for its people."), "full", CsvUrl()));

        ExceptionsPanel(v);
        MonthsPanel(v, links);
        if (Viewer.IsAdmin) AdminNote();
    }

    void ExceptionsPanel(SeatPosition v)
    {
        var t = new Table { Headers = ["Exception", "How many", "What to do"], Numeric = new HashSet<int> { 1 }, Sortable = false,
            EmptyText = "No exceptions: every line of business has a tracking group, and every seat is claimed by one." };
        void Add(string what, int n, string todo) { if (n > 0) t.Rows.Add([what, n, todo]); }
        var untracked = v.Charged.Where(l => l.TrackingGroup is null).Select(l => l.Name).ToList();
        Add("Line of business with no tracking group", untracked.Count, "Map its tracking group in Workbench settings › Seat groups: " + string.Join(", ", untracked));
        Add("Seats no line of business claims", v.NoLob?.AssignedSeats ?? 0, "Add the people to their line of business's tracking group through its OIM role, or remove the seat.");
        Add("Open seat findings", v.Charged.Sum(l => l.OpenFindings), "Work them in the Workbench: each line of business's page lists its own.");
        Add("Months waiting to be certified", v.Months.Count(m => !m.Certified && m.Month < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)), "The FinOps lead certifies each month in Governance operations › Seat chargeback.");
        if (v.Summary is { Stale: true }) Add("License read-back more than a day old", 1, "Platform Operations runs it again from Workbench › Operations.");
        Panels.Add(new("Exceptions", t.Render(), "Raised from the Workbench's seat read-back and its findings.", "half"));
    }

    void MonthsPanel(SeatPosition v, SeatLinks links)
    {
        var cur = v.Currency;
        var t = new Table { Headers = ["Month", "Status", "Reserved", "Assigned", "Charged seats", $"Charged to lines of business ({cur})", ""], Numeric = new HashSet<int> { 2, 3, 4, 5 }, Sortable = false,
            EmptyText = "No month kept yet: the Workbench takes a draft after each month ends, and the FinOps lead certifies it." };
        foreach (var m in v.Months)
            t.Rows.Add([m.Month.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture), m.Certified ? "Certified" : "Draft", m.Reserved, m.Assigned, m.Charged,
                Math.Round(m.Amount, MidpointRounding.AwayFromZero), links.Month(m.Month) is { } h ? new Link("View", h) : ""]);
        Panels.Add(new("Monthly seat chargeback", t.Render(),
            $"Each month's reservation plus the seats over it, at one rate a seat ({CostRules.Money(v.Rate, cur)} a month now). A certified month never changes; a draft can be taken again.", "half"));
    }

    void AdminNote() =>
        Panels.Add(new("Reservation register", new HtmlString(
            $"<p>Not changed here: {E(RegisterKept)}</p><p class=\"sub\">Its rows, its rate and its month-end snapshots are kept as they were, and nothing is added to them. " +
            "Workbench settings › Seat groups offers the register's current rows once, as each line of business's first reservation, for an administrator to confirm.</p>"),
            "With the Workbench off, this page shows the register and its forms again.", "full"));
}
