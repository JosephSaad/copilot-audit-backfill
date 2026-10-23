namespace CopilotInsights.Infrastructure;

/// <summary>
/// What a person is told when something fails: a short reference, never the error itself, which can name servers,
/// databases, tables and accounts. The error goes to the web server's log under the same reference, for an administrator
/// to find. The same for everyone, administrators included.
/// </summary>
public static class Incident
{
    /// <summary>A reference: six letters and digits, unlikely to repeat within a day.</summary>
    public static string NewReference() => Convert.ToHexString(Guid.NewGuid().ToByteArray(), 0, 3);

    /// <summary>Logs the error under a new reference and returns the sentence to show, starting with <paramref name="what"/>.</summary>
    public static string Note(ILogger log, Exception ex, string what = "It failed")
    {
        var reference = NewReference();
        log.LogError(ex, "{What}. Reference {Reference}.", what, reference);
        return Sentence(what, reference);
    }

    /// <summary>The sentence shown. Pure, for the tests.</summary>
    public static string Sentence(string what, string reference) =>
        $"{what.TrimEnd('.', ':', ' ')}. Reference {reference}: an administrator finds the details under it in the web server's log.";
}
