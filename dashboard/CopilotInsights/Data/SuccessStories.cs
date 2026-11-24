namespace CopilotInsights.Data;

/// <summary>
/// The success stories' rules, kept pure so they can be tested on their own: who may do what to a story at each status,
/// and how the weekly report names whoever said it. A story only moves forward: Entered, then Approved, then Sent;
/// Declined and Withdrawn end it. Editing never changes its status, and nothing sends a story back.
/// </summary>
public static class SuccessStories
{
    public const string Entered = "Entered", Approved = "Approved", Sent = "Sent", Declined = "Declined", Withdrawn = "Withdrawn";

    public static readonly IReadOnlyList<string> Statuses = [Entered, Approved, Sent, Declined, Withdrawn];

    /// <summary>The reasons a curator picks from when declining, as the person who entered the story sees them.</summary>
    public static readonly IReadOnlyList<string> DeclineReasons =
        ["Not a Copilot story", "Sensitive content", "A duplicate", "Needs more detail", "Other"];

    public const int QuoteMax = 2000;

    /// <summary>The person who entered it edits it until it is approved; after that only a curator does.</summary>
    public static bool OwnerMayEdit(string status) => status == Entered;

    /// <summary>A curator edits it while it can still be quoted: entered, approved or sent.</summary>
    public static bool CuratorMayEdit(string status) => status is Entered or Approved or Sent;

    /// <summary>The person who entered it withdraws it at any point it is still live, sent included (no more sends).</summary>
    public static bool MayWithdraw(string status) => status is Entered or Approved or Sent;

    public static bool MayApprove(string status) => status == Entered;

    /// <summary>Declined before it is sent; a sent story is withdrawn or deleted instead.</summary>
    public static bool MayDecline(string status) => status is Entered or Approved;

    /// <summary>Ticked for the next report: an approved story, or a sent one again on purpose.</summary>
    public static bool MayInclude(string status) => status is Approved or Sent;

    /// <summary>
    /// The status after an action, or why not: never backwards. Edits and ticks are not actions on the status.
    /// </summary>
    public static string Next(string status, string action) => (status, action) switch
    {
        (Entered, "approve") => Approved,
        (Entered or Approved, "decline") => Declined,
        (Entered or Approved or Sent, "withdraw") => Withdrawn,
        (Approved or Sent, "send") => Sent,
        _ => throw new ArgumentException($"a story that is {status.ToLowerInvariant()} cannot be {Past(action)}."),
    };

    static string Past(string action) => action switch { "approve" => "approved", "decline" => "declined", "withdraw" => "withdrawn", "send" => "sent", _ => action };

    /// <summary>
    /// Who said it, in the weekly report and on the page outside the curators' list: their name where they agreed to be
    /// named and privacy mode is off; else "A colleague in Finance", where Finance has at least the minimum group size;
    /// else "A colleague".
    /// </summary>
    public static string Attribution(string? displayName, string? unit, bool mayName, bool privacyMode, int unitSize, int minGroupSize)
    {
        if (mayName && !privacyMode && !string.IsNullOrWhiteSpace(displayName)) return displayName.Trim() + (string.IsNullOrWhiteSpace(unit) ? "" : ", " + unit.Trim());
        if (!string.IsNullOrWhiteSpace(unit) && unitSize >= Math.Max(1, minGroupSize)) return "A colleague in " + unit.Trim();
        return "A colleague";
    }

    /// <summary>The quote as stored: trimmed, at most <see cref="QuoteMax"/> characters; empty is refused.</summary>
    public static string CleanQuote(string? quote)
    {
        var q = (quote ?? "").Trim();
        if (q.Length == 0) throw new ArgumentException("write the story first.");
        if (q.Length > QuoteMax) throw new ArgumentException($"a story is at most {QuoteMax:#,0} characters; this one is {q.Length:#,0}.");
        return q;
    }

    public static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();

    public static int? Minutes(int? minutes) => minutes switch
    {
        null => null,
        < 0 or > 10_000 => throw new ArgumentException("minutes saved a week is a whole number from 0 to 10,000."),
        _ => minutes,
    };
}
