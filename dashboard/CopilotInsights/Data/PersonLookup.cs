using CopilotInsights.Infrastructure;

namespace CopilotInsights.Data;

/// <summary>Whose names a page's name box may offer as the viewer types.</summary>
public enum LookupKind
{
    /// <summary>The page has no name box.</summary>
    None,
    /// <summary>Managers with a team large enough to show, in the viewer's part of the organization.</summary>
    Managers,
    /// <summary>People with Copilot records, in the viewer's part of the organization.</summary>
    People,
    /// <summary>Everyone in the directory: for the Admin pages, where an administrator names a person to grant or check.</summary>
    Directory,
}

public sealed record PersonMatch(string Upn, string? Name, string? Department, int PeopleBelow);

/// <summary>
/// The search behind every box that takes a person. Nothing is searched until three letters are typed, here as in
/// the browser, and a name matches where its first name or its last name begins with them: "mor" finds Avery Morgan
/// and Morgan Petrov, and not Seymour Hill. A sign-in matches from its start too. The page's own scope and role
/// decide who can be found, as they decide who the page shows.
/// </summary>
public static class PersonLookup
{
    /// <summary>The fewest letters a search needs.</summary>
    public const int MinLetters = 3;

    /// <summary>The most names offered at once.</summary>
    public const int Most = 20;

    /// <summary>What was typed, as it is searched: trimmed, one space between words; null when under three letters or digits.</summary>
    public static string? Term(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var t = string.Join(' ', typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length > 100) t = t[..100];
        return t.Count(char.IsLetterOrDigit) >= MinLetters ? t : null;
    }

    /// <summary>The term with LIKE's own characters made literal, for ESCAPE '\'.</summary>
    public static string Escape(string term) => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    /// <summary>
    /// Whether a name matches: a word of it begins with the term (a first name, a last name, either part of "Last,
    /// First"), or the whole name does when the term is more than one word. The rule the SQL below applies, here for
    /// the tests.
    /// </summary>
    public static bool Matches(string? name, string term) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.StartsWith(term, StringComparison.OrdinalIgnoreCase) || name.Contains(" " + term, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a match is offered as: the name, the department, and for a manager the size of the team.</summary>
    public static string Label(PersonMatch p, bool team) =>
        (p.Name ?? p.Upn) + (string.IsNullOrWhiteSpace(p.Department) ? "" : ", " + p.Department) +
        (team ? $" ({p.PeopleBelow:#,0} {(p.PeopleBelow == 1 ? "person" : "people")})" : "");

    public static async Task<List<PersonMatch>> FindAsync(Db db, ReportContext c, LookupKind kind, string? typed)
    {
        if (kind == LookupKind.None || Term(typed) is not { } term) return [];
        var e = Escape(term);
        var args = c.Args(new { starts = e + "%", word = "% " + e + "%" });
        const string Named = @"({0} LIKE @starts ESCAPE '\' OR {0} LIKE @word ESCAPE '\' OR {1} LIKE @starts ESCAPE '\')";
        return kind switch
        {
            LookupKind.People => await db.QueryAsync<PersonMatch>($@"
SELECT TOP ({Most}) du.UserPrincipalName AS Upn, du.DisplayName AS Name, du.Department, 0 AS PeopleBelow
FROM bi.DimUser AS du {c.Scope("du.UserKey")}
WHERE du.UserKey > 1 AND {string.Format(Named, "du.DisplayName", "du.UserPrincipalName")}
ORDER BY du.DisplayName, du.UserPrincipalName;", args),
            LookupKind.Managers => await db.QueryAsync<PersonMatch>($@"
SELECT TOP ({Most}) p.Upn, p.DisplayName AS Name, p.Department, p.PeopleBelow
FROM dash.Person AS p {c.PersonScope("p.Upn")}
WHERE p.PeopleBelow > 0 AND p.PeopleBelow + 1 >= @minGroup AND {string.Format(Named, "p.DisplayName", "p.Upn")}
ORDER BY p.DisplayName, p.Upn;", args),
            _ => await db.QueryAsync<PersonMatch>($@"
SELECT TOP ({Most}) p.Upn, p.DisplayName AS Name, p.Department, p.PeopleBelow
FROM dash.Person AS p
WHERE {string.Format(Named, "p.DisplayName", "p.Upn")}
ORDER BY p.DisplayName, p.Upn;", args),
        };
    }
}
