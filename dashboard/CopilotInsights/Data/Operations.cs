using System.Globalization;
using System.Text;

namespace CopilotInsights.Data;

/// <summary>When a scheduled digest is next due. Pure, for the tests.</summary>
public static class Schedule
{
    public static readonly string[] Frequencies = ["Daily", "Weekly", "Monthly"];

    /// <summary>
    /// The most recent moment the subscription was due at or before <paramref name="nowUtc"/>, in UTC:
    /// its hour on the right day, in the reporting time zone. Null for an unknown frequency.
    /// </summary>
    public static DateTime? LastDueUtc(string frequency, int? dayOfWeek, int? dayOfMonth, int hourLocal, DateTime nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        DateTime candidate;
        switch (frequency)
        {
            case "Daily":
                candidate = local.Date.AddHours(hourLocal);
                if (candidate > local) candidate = candidate.AddDays(-1);
                break;
            case "Weekly":
                var dow = Math.Clamp(dayOfWeek ?? 1, 0, 6);
                candidate = local.Date.AddDays(-(((int)local.DayOfWeek - dow + 7) % 7)).AddHours(hourLocal);
                if (candidate > local) candidate = candidate.AddDays(-7);
                break;
            case "Monthly":
                var dom = Math.Clamp(dayOfMonth ?? 1, 1, 28);
                candidate = new DateTime(local.Year, local.Month, dom).AddHours(hourLocal);
                if (candidate > local) candidate = candidate.AddMonths(-1);
                break;
            default:
                return null;
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), zone);
    }

    /// <summary>Due when its last due moment has passed since it was last sent (or it was never sent).</summary>
    public static bool IsDue(string frequency, int? dayOfWeek, int? dayOfMonth, int hourLocal, DateTime? lastSentUtc, DateTime nowUtc, TimeZoneInfo zone) =>
        LastDueUtc(frequency, dayOfWeek, dayOfMonth, hourLocal, nowUtc, zone) is DateTime due && (lastSentUtc is null || lastSentUtc < due);

    /// <summary>A Windows time zone name to a TimeZoneInfo, falling back to UTC.</summary>
    public static TimeZoneInfo Zone(string? windowsName)
    {
        try { return windowsName is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(windowsName); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}

/// <summary>
/// The time zone the dashboard shows times in and runs its schedules by (2.4.10). An administrator's choice (Admin >
/// Access, the TimeZone setting); with none, the zone the data's days were cut in when it was installed, which is the
/// job server's unless one was named; with neither, this web server's own. Before, times were shown in UTC.
/// </summary>
public static class Zones
{
    public const string Setting = "TimeZone";

    public static TimeZoneInfo Resolve(string? setting, string? reporting)
    {
        foreach (var name in new[] { setting, reporting })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(name.Trim()); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>Where the zone in use came from, for the settings page.</summary>
    public static string Source(string? setting, string? reporting)
    {
        bool Known(string? n) { try { return !string.IsNullOrWhiteSpace(n) && TimeZoneInfo.FindSystemTimeZoneById(n.Trim()) is not null; } catch (Exception) { return false; } }
        return Known(setting) ? "chosen here" : Known(reporting) ? "the zone the data's days are cut in" : "this web server's own";
    }

    /// <summary>A short name for a column header or a sentence: "Eastern Time", "UTC".</summary>
    public static string Label(TimeZoneInfo zone)
    {
        if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime) return "UTC";
        var name = zone.StandardName;
        return name.EndsWith(" Standard Time", StringComparison.Ordinal) ? name[..^" Standard Time".Length] + " Time" : name;
    }

    public static DateTime FromUtc(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>"2026-10-02 08:45 Eastern Time".</summary>
    public static string Stamp(DateTime? utc, TimeZoneInfo zone, string format = "yyyy-MM-dd HH:mm") =>
        utc is null ? "" : $"{FromUtc(utc.Value, zone).ToString(format, System.Globalization.CultureInfo.InvariantCulture)} {Label(zone)}";
}

/// <summary>One kind of security signal on one day, with its 28-day daily average.</summary>
public sealed record SignalDay(string Signal, long Count, double Average, int Threshold);

/// <summary>Security signals that crossed a threshold or spiked. Pure, for the tests.</summary>
public static class SecuritySignals
{
    public static List<string> Findings(IEnumerable<SignalDay> days, double spikeFactor)
    {
        var f = new List<string>();
        foreach (var d in days)
        {
            if (d.Count <= 0) continue;
            if (d.Threshold > 0 && d.Count >= d.Threshold)
                f.Add($"{d.Signal}: {d.Count:#,0} (the alert threshold is {d.Threshold:#,0}).");
            else if (spikeFactor > 1 && d.Average > 0 && d.Count >= d.Average * spikeFactor && d.Count >= 3)
                f.Add($"{d.Signal}: {d.Count:#,0}, {d.Count / d.Average:0.#} times the 28-day daily average of {d.Average:0.#}.");
        }
        return f;
    }
}

/// <summary>A day that stands out from the same weekday in the weeks before it.</summary>
public sealed record Anomaly(string Unit, DateTime Day, long Value, double Baseline, string Kind)
{
    public double Change => Baseline > 0 ? Value / Baseline - 1 : 0;
}

/// <summary>Drops and spikes in daily use, against the same weekday over the previous four weeks. Pure, for the tests.</summary>
public static class Anomalies
{
    /// <param name="series">interactions per unit per day, every day present (zeros included).</param>
    /// <param name="from">only days on or after this are judged; the four weeks before them are the baseline.</param>
    /// <param name="minBaseline">a unit whose baseline is smaller than this is too small to judge.</param>
    public static List<Anomaly> Find(IReadOnlyDictionary<string, IReadOnlyDictionary<DateTime, long>> series, DateTime from,
        double dropPercent, double spikePercent, double minBaseline = 20)
    {
        var found = new List<Anomaly>();
        foreach (var (unit, days) in series)
        {
            foreach (var (day, value) in days.Where(kv => kv.Key >= from).OrderBy(kv => kv.Key))
            {
                var prior = Enumerable.Range(1, 4).Select(w => day.AddDays(-7 * w)).Where(days.ContainsKey).Select(d => (double)days[d]).ToList();
                if (prior.Count < 3) continue;
                var baseline = prior.Average();
                if (baseline < minBaseline) continue;
                if (dropPercent > 0 && value <= baseline * (1 - dropPercent / 100)) found.Add(new(unit, day, value, baseline, "Drop"));
                else if (spikePercent > 0 && value >= baseline * (1 + spikePercent / 100)) found.Add(new(unit, day, value, baseline, "Spike"));
            }
        }
        return found.OrderByDescending(a => a.Day).ThenBy(a => a.Change).ToList();
    }
}

/// <summary>The license recommendations as something an administrator can apply. Pure, for the tests.</summary>
public static class LicenseExport
{
    /// <summary>Entra's bulk add or remove members template (Groups > Bulk operations): two header lines, then one UPN per line.</summary>
    public static string BulkMembersCsv(IEnumerable<string> upns)
    {
        var sb = new StringBuilder();
        sb.Append("version:v1.0\r\n");
        sb.Append("Member object ID or user principal name [memberObjectIdOrUpn] Required\r\n");
        foreach (var u in upns) sb.Append(u.Replace(",", "").Replace("\"", "")).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// A PowerShell script for an administrator to review and run with their own rights (Microsoft
    /// Graph PowerShell): the dashboard itself changes nothing. With a licensing group, it adds or
    /// removes members; otherwise it assigns or removes the SKU directly. It confirms before acting.
    /// </summary>
    public static string Script(string action, IReadOnlyList<string> upns, string? groupId, string? skuId, string generatedBy, DateTime generatedUtc)
    {
        var add = action.Equals("upgrade", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine("#Requires -Modules Microsoft.Graph.Authentication");
        sb.AppendLine("<#");
        sb.AppendLine($"    Copilot Insights: {(add ? "assign a Copilot license to" : "remove the Copilot license from")} {upns.Count} people.");
        sb.AppendLine($"    Generated {generatedUtc:yyyy-MM-dd HH:mm} UTC by {generatedBy}. Review the list, then run it signed in as a license administrator.");
        sb.AppendLine("    Add -WhatIf to see what it would do without changing anything.");
        sb.AppendLine("#>");
        sb.AppendLine("[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')] param()");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$people = @(");
        foreach (var u in upns) sb.AppendLine($"    '{u.Replace("'", "''")}'");
        sb.AppendLine(")");
        if (groupId is not null)
        {
            sb.AppendLine($"$groupId = '{groupId.Replace("'", "")}'   # the group that assigns Copilot licenses");
            sb.AppendLine("Connect-MgGraph -Scopes 'GroupMember.ReadWrite.All', 'User.Read.All' -NoWelcome");
            sb.AppendLine("foreach ($upn in $people) {");
            sb.AppendLine("    $id = (Invoke-MgGraphRequest -Method GET -Uri \"/v1.0/users/$([uri]::EscapeDataString($upn))?`$select=id\").id");
            sb.AppendLine($"    if ($PSCmdlet.ShouldProcess($upn, '{(add ? "Add to" : "Remove from")} the Copilot license group')) {{");
            if (add) sb.AppendLine("        Invoke-MgGraphRequest -Method POST -Uri \"/v1.0/groups/$groupId/members/`$ref\" -Body @{ '@odata.id' = \"https://graph.microsoft.com/v1.0/directoryObjects/$id\" }");
            else sb.AppendLine("        Invoke-MgGraphRequest -Method DELETE -Uri \"/v1.0/groups/$groupId/members/$id/`$ref\"");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine($"$skuId = '{(skuId ?? "PUT-THE-COPILOT-SKU-ID-HERE").Replace("'", "")}'   # the Copilot SKU (Licensing > License pool shows it)");
            sb.AppendLine("Connect-MgGraph -Scopes 'User.ReadWrite.All' -NoWelcome");
            sb.AppendLine("foreach ($upn in $people) {");
            sb.AppendLine($"    if ($PSCmdlet.ShouldProcess($upn, '{(add ? "Assign" : "Remove")} the Copilot license')) {{");
            sb.AppendLine(add
                ? "        Invoke-MgGraphRequest -Method POST -Uri \"/v1.0/users/$([uri]::EscapeDataString($upn))/assignLicense\" -Body @{ addLicenses = @(@{ skuId = $skuId }); removeLicenses = @() }"
                : "        Invoke-MgGraphRequest -Method POST -Uri \"/v1.0/users/$([uri]::EscapeDataString($upn))/assignLicense\" -Body @{ addLicenses = @(); removeLicenses = @($skuId) }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }
}

/// <summary>A row of an uploaded CSV, and what was wrong with the rest. Pure, for the tests.</summary>
public static class CsvUpload
{
    /// <summary>Splits one CSV line, honoring double quotes.</summary>
    public static List<string> Split(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',' || ch == ';' || ch == '\t') { cells.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    static IEnumerable<(int Line, List<string> Cells)> Rows(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select((l, i) => (Line: i + 1, Text: l.TrimStart('﻿')))
            .Where(x => !string.IsNullOrWhiteSpace(x.Text)).Select(x => (x.Line, Split(x.Text).Select(Unguard).ToList()));

    /// <summary>
    /// A cell as it was before a download guarded it against spreadsheet formulas ("'=x" back to "=x"), so a
    /// mapping file downloaded, edited and uploaded again matches the values it came from (2.4.5).
    /// </summary>
    public static string Unguard(string cell) => cell.Length > 1 && cell[0] == '\'' &&"=+-@".Contains(cell[1]) ? cell[1..] : cell;

    /// <summary>Training attendance: UPN, session, date (yyyy-MM-dd). A header row is skipped.</summary>
    public static (List<(string Upn, string Session, DateTime Date)> Rows, List<string> Errors) Training(string text)
    {
        var rows = new List<(string, string, DateTime)>();
        var errors = new List<string>();
        foreach (var (line, c) in Rows(text))
        {
            if (line == 1 && c.Count > 0 && !c[0].Contains('@')) continue;   // a header
            if (c.Count < 3) { errors.Add($"Line {line}: needs UPN, session and date."); continue; }
            if (!c[0].Contains('@') || c[0].Length > 256) { errors.Add($"Line {line}: '{c[0]}' is not a UPN."); continue; }
            if (c[1].Length is 0 or > 190) { errors.Add($"Line {line}: the session name is empty or longer than 190 characters."); continue; }
            if (!DateTime.TryParseExact(c[2], ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            { errors.Add($"Line {line}: '{c[2]}' is not a date (yyyy-MM-dd)."); continue; }
            rows.Add((c[0].ToLowerInvariant(), c[1], d.Date));
        }
        return (rows, errors);
    }

    /// <summary>Value names: column id, value, name[, note]. A header row is skipped.</summary>
    public static (List<(short ColumnId, string Value, string Name, string? Note)> Rows, List<string> Errors) ValueNames(string text)
    {
        var rows = new List<(short, string, string, string?)>();
        var errors = new List<string>();
        foreach (var (line, c) in Rows(text))
        {
            if (line == 1 && c.Count > 0 && !short.TryParse(c[0], out _)) continue;   // a header
            if (c.Count < 3) { errors.Add($"Line {line}: needs column id, value and name."); continue; }
            if (!short.TryParse(c[0], out var id)) { errors.Add($"Line {line}: '{c[0]}' is not a column id."); continue; }
            if (c[1].Length is 0 or > 400) { errors.Add($"Line {line}: the value is empty or too long."); continue; }
            if (c[2].Length > 200) { errors.Add($"Line {line}: the name is longer than 200 characters."); continue; }
            rows.Add((id, c[1], c[2], c.Count > 3 && c[3].Length > 0 ? c[3][..Math.Min(400, c[3].Length)] : null));
        }
        return (rows, errors);
    }
}
