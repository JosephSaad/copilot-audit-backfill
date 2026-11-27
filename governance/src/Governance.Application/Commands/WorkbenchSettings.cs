using System.Globalization;
using Governance.Application.Data;

namespace Governance.Application.Commands;

/// <summary>
/// The Workbench's own settings (ref.Setting) changed on Workbench settings › Settings, each checked here so a value the
/// worker or the pages cannot read is refused on the page. The seat settings are on Seat groups, intake on the Intake
/// switch, and the setup steps' records are written by the steps themselves: none of those is changed here.
/// </summary>
public static class WorkbenchSettings
{
    /// <summary>Whether a setting is changed on the Settings page.</summary>
    public static bool OnSettingsPage(string key) =>
        !key.StartsWith("Seats.", StringComparison.Ordinal) && !key.StartsWith("Setup.", StringComparison.Ordinal) && key != "Intake.Open";

    /// <summary>With seats only, the settings the seat pages and the worker still read; the rest belong to closed pages.</summary>
    public static readonly IReadOnlyList<string> SeatSettings =
        [WorkbenchScope.Key, "Calendar.TimeZone", "Workbench.BaseUrl", "Retention.WorkerRecordDays", "Directory.StaleAfterRuns"];

    /// <summary>The value as it is kept, or a refusal saying what it takes. Pure, for the tests.</summary>
    public static string Check(string key, string? value)
    {
        var v = (value ?? "").Trim();
        static string Whole(string v, int min, int max, string what) =>
            int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n.ToString(CultureInfo.InvariantCulture)
                : throw new DomainException($"{what} is a whole number from {min} to {max}.");
        static string Days(string v, string what)
        {
            var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is 0 or > 6 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 365))
                throw new DomainException($"{what} are days from 1 to 365, comma separated, for example 30,7,1.");
            return string.Join(',', parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).Distinct().OrderByDescending(d => d));
        }
        return key switch
        {
            "Calendar.TimeZone" => TimeZoneOk(v) ? v : throw new DomainException("The time zone is a Windows time zone name, for example Eastern Standard Time."),
            "Sla.ReturnedAutoCloseDays" => Whole(v, 1, 90, "The days before a returned request closes"),
            "Exception.NoticeDays" => Days(v, "Exception notices"),
            "Commercial.NoticeDays" => Days(v, "Renewal notices"),
            "Inventory.MaxAgeDays" => Whole(v, 1, 90, "The inventory's age"),
            "Attestation.OverdueDays" => Whole(v, 1, 60, "The days before an attestation escalates"),
            "Evidence.MaxUploadMb" => Whole(v, 1, 25, "The largest evidence file, in MB,"),
            "Evidence.AllowedTypes" => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } t
                                       && t.All(x => x.Length is > 1 and <= 10 && x[0] == '.' && x[1..].All(char.IsAsciiLetterOrDigit))
                ? string.Join(',', t.Select(x => x.ToLowerInvariant()).Distinct())
                : throw new DomainException("Evidence types are file extensions, comma separated, for example .pdf,.png,.xlsx."),
            "Evidence.RetentionYears" => Whole(v, 1, 50, "Evidence retention, in years,"),
            "Retention.WorkerRecordDays" => Whole(v, 30, 3650, "The days the worker keeps its records"),
            "Directory.StaleAfterRuns" => Whole(v, 1, 30, "The missed directory reads before the banner"),
            "Workbench.BaseUrl" => Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && string.IsNullOrEmpty(u.Query)
                ? v.TrimEnd('/') : throw new DomainException("The address is the site's own, for example https://insights.contoso.com, without /workbench."),
            WorkbenchScope.Key => v.ToUpperInvariant() is WorkbenchScope.Seats or WorkbenchScope.Full ? v.ToUpperInvariant()
                : throw new DomainException("Choose SEATS (Copilot seats per line of business only) or FULL (the whole Workbench)."),
            _ => throw new DomainException("That setting is not changed here."),
        };
    }

    static bool TimeZoneOk(string id)
    {
        if (id.Length == 0) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
