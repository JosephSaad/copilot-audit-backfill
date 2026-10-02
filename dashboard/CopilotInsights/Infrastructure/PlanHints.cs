using System.Text;
using System.Text.RegularExpressions;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Keeps a report query from running with a plan built for other dates (2.4.11).
///
/// SQL Server builds a query's plan for the parameter values of whoever runs it first and reuses it for everyone
/// after. A report's queries take a date range, and the same text is run for a week and for a year, for the period
/// and for the one before it. The period before a year of data is empty; a plan built for that (one row expected,
/// one thread, a lookup per row) was then reused for the year itself. At 120 million interactions the trend behind
/// most pages took 77 seconds that way and timed out at 180 days, against 5 seconds with a plan built for its own
/// dates; the warm-up took 37 minutes.
///
/// So every statement that reads between date keys says how it wants to be planned:
/// for the whole organization, OPTIMIZE FOR UNKNOWN (one plan, built for a typical range, never for an empty one);
/// inside a manager's part of it (@root), RECOMPILE (ten people or seventy thousand: planned for the ones asked for).
/// A hint changes how a query runs, never what it returns.
///
/// Inside a manager's part there is a second way to go wrong: SQL Server has to guess how many people that is, from a
/// sample of the org chart, and for some managers it guesses one. Planned for one person, a query reads the facts
/// again for each person who is really there: for a team of 584 at 120 million interactions, 100 seconds and a
/// timeout, where the same page for the team next to it took a second. So the people in scope are listed first, into
/// a temporary table (<see cref="ScopeFirst"/>), and every statement reads that list: it is counted, not guessed.
/// </summary>
public static partial class PlanHints
{
    public const string Typical = "OPTION (OPTIMIZE FOR UNKNOWN)";
    public const string Exact = "OPTION (RECOMPILE)";

    /// <summary>How a report query names the people in the viewer's scope (ReportContext.Scope), and what it reads instead.</summary>
    public const string ScopeCall = "dash.fnScopeUsers(@root)", ScopeTable = "#scope";

    /// <summary>The people in scope, listed once at the head of the batch. It returns no rows, so the batch's results are the ones it had.</summary>
    public const string ScopeFirst = "SELECT sc.UserKey INTO #scope FROM dash.fnScopeUsers(@root) AS sc;\n";

    /// <summary>A yyyymmdd key, as bi.DimDate numbers its days: what a report passes as @k0, @k1, @k7 and the like.</summary>
    public static bool IsDateKey(object? value) =>
        value is int k && k is >= 19000101 and <= 22001231 && k / 100 % 100 is >= 1 and <= 12 && k % 100 is >= 1 and <= 31;

    /// <summary>
    /// <paramref name="sql"/> with the people in scope listed first where it reads inside a manager's part, and a hint on
    /// each statement that reads between date keys. Unchanged when its parameters cannot be read; a statement that
    /// already carries a hint keeps it.
    /// </summary>
    public static string Apply(string sql, object? args)
    {
        var values = QueryCache.Values(args);
        if (values is null) return sql;
        var scoped = values.TryGetValue("root", out var root) && root is string;
        if (scoped && sql.Contains(ScopeCall, StringComparison.OrdinalIgnoreCase) && !sql.Contains(ScopeTable, StringComparison.OrdinalIgnoreCase))
            sql = ScopeFirst + sql.Replace(ScopeCall, ScopeTable, StringComparison.OrdinalIgnoreCase);
        var keys = values.Where(kv => IsDateKey(kv.Value)).Select(kv => kv.Key).ToList();
        if (keys.Count == 0) return sql;
        var dates = new Regex(@"@(?:" + string.Join("|", keys.Select(Regex.Escape)) + @")(?![\w@#$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!dates.IsMatch(sql)) return sql;

        var plain = Blank(sql);   // the same text with strings and comments spaced out: positions agree
        var sb = new StringBuilder(sql.Length + 64);
        var from = 0;
        while (from < sql.Length)
        {
            var end = plain.IndexOf(';', from);
            if (end < 0) end = sql.Length;
            var body = plain[from..end];
            sb.Append(sql, from, end - from);
            if (Reads().IsMatch(body) && dates.IsMatch(body) && !Hinted().IsMatch(body))
            {
                /* On a line of its own: the statement may end in a comment that runs to the end of its line. */
                while (sb.Length > 0 && char.IsWhiteSpace(sb[^1])) sb.Length--;
                sb.Append('\n').Append(scoped && Root().IsMatch(body) ? Exact : Typical);
            }
            if (end < sql.Length) sb.Append(';');
            from = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>A statement that reads: it begins SELECT, WITH or INSERT. Anything else (DECLARE, IF, CREATE, DROP, SET) takes no hint.</summary>
    [GeneratedRegex(@"\A\s*(?:SELECT|WITH|INSERT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reads();

    [GeneratedRegex(@"\bOPTION\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Hinted();

    /// <summary>A statement inside a manager's part: it names the root, or the list of the people in scope.</summary>
    [GeneratedRegex(@"(?:@root|#scope)(?![\w@#$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Root();

    /// <summary>The text with every string literal, quoted name and comment replaced by spaces, so a semicolon or a word inside one is not read as SQL.</summary>
    static string Blank(string sql)
    {
        var c = sql.ToCharArray();
        for (var i = 0; i < c.Length; i++)
        {
            if (c[i] == '\'')
            {
                var j = i + 1;
                while (j < c.Length) { if (c[j] == '\'') { if (j + 1 < c.Length && c[j + 1] == '\'') j++; else break; } j++; }
                for (var k = i; k <= j && k < c.Length; k++) c[k] = ' ';
                i = j;
            }
            else if (c[i] == '-' && i + 1 < c.Length && c[i + 1] == '-')
            {
                while (i < c.Length && c[i] != '\n') c[i++] = ' ';
            }
            else if (c[i] == '/' && i + 1 < c.Length && c[i + 1] == '*')
            {
                var j = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j < 0 ? c.Length - 1 : j + 1;
                for (var k = i; k <= j; k++) if (c[k] != '\n') c[k] = ' ';
                i = j;
            }
            else if (c[i] == '[')
            {
                var j = sql.IndexOf(']', i + 1);
                if (j < 0) break;
                for (var k = i; k <= j; k++) c[k] = ' ';
                i = j;
            }
        }
        return new string(c);
    }
}
