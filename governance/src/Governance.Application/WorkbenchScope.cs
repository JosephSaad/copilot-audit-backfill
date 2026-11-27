namespace Governance.Application;

/// <summary>
/// What the Workbench is used for (ref.Setting Workbench.Scope). SEATS, the default: Copilot seats per line of business
/// only. The lines of business, their AD tracking groups, who holds a seat and their use, seat chargeback, roles, the
/// settings and the sync page stay; every other page is closed and the worker runs only the jobs the seats need.
/// FULL: the whole governance Workbench (requests, approvals, workloads, envelopes, recertification and the rest).
/// Nothing is deleted either way: turning FULL back on brings every page and job back with its data.
/// </summary>
public static class WorkbenchScope
{
    public const string Key = "Workbench.Scope";
    public const string Seats = "SEATS";
    public const string Full = "FULL";

    public const string Sql = "SELECT SettingValue FROM ref.Setting WHERE SettingKey = 'Workbench.Scope'";

    /// <summary>Seats only unless the setting says FULL: a database without the setting yet is seats only too.</summary>
    public static bool SeatsOnly(string? value) => !string.Equals(value?.Trim(), Full, StringComparison.OrdinalIgnoreCase);

    /// <summary>The worker's jobs that run with seats only: notices, the people directory, the seat read and its own record keeping.</summary>
    public static readonly IReadOnlyList<string> SeatJobs = ["OutboxDispatcher", "DirectorySync", "SeatSync", "Retention"];

    public static bool JobRuns(string job, bool seatsOnly) => !seatsOnly || SeatJobs.Contains(job, StringComparer.OrdinalIgnoreCase);

    /// <summary>The Workbench addresses open with seats only (each with what is under it).</summary>
    public static readonly IReadOnlyList<string> SeatPages =
    [
        "/workbench/Registry/Seats", "/workbench/Registry/FundingOwners", "/workbench/Reports/SeatChargeback",
        "/workbench/Admin/SeatGroups", "/workbench/Admin/Roles", "/workbench/Admin/Settings",
        "/workbench/Operations/Sync", "/workbench/Operations/SyncRun",
        "/workbench/People", "/workbench/Me", "/workbench/AccessDenied", "/workbench/Error", "/workbench/ActAs",
    ];

    /// <summary>Where a closed address goes: the seats per line of business.</summary>
    public const string Home = "/workbench/Registry/Seats";

    public static bool PageOpen(string? path, bool seatsOnly)
    {
        if (!seatsOnly) return true;
        var p = (path ?? "").TrimEnd('/');
        return SeatPages.Any(s => p.Equals(s, StringComparison.OrdinalIgnoreCase)
                                  || p.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase));
    }
}
