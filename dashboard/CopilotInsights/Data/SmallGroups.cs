namespace CopilotInsights.Data;

/// <summary>
/// Groups smaller than the minimum group size, kept off a breakdown on their own: one rule for every page,
/// digest and API call. The groups left over must together reach the minimum too, or they are one small group under
/// another name: "Smaller groups, 1 person" was the head of the organization, and a list that left a small group out
/// beside the total gave it back by subtraction. So when the small groups together are still under the minimum, the
/// smallest groups shown join them until they reach it. When everyone together is under it, they are all one row,
/// which is the total the page shows anyway.
/// </summary>
public static class SmallGroups
{
    /// <summary>The groups shown on their own, and the ones that are not, which together reach <paramref name="min"/> where they can.</summary>
    public static (List<T> Shown, List<T> Combined) Split<T>(IEnumerable<T> rows, Func<T, int> people, int min)
    {
        var shown = rows.ToList();
        var combined = shown.Where(r => people(r) < min).ToList();
        shown.RemoveAll(r => people(r) < min);
        if (combined.Count > 0)
            while (combined.Sum(people) < min && shown.Count > 0)
            {
                var least = shown.MinBy(people)!;
                shown.Remove(least);
                combined.Add(least);
            }
        return (shown, combined);
    }

    /// <summary>The groups shown on their own, then one row for the rest (made by <paramref name="combine"/>) when there is any.</summary>
    public static List<T> Fold<T>(IEnumerable<T> rows, Func<T, int> people, Func<IReadOnlyList<T>, T> combine, int min, out int folded)
    {
        var (shown, combined) = Split(rows, people, min);
        folded = combined.Count;
        if (combined.Count > 0) shown.Add(combine(combined));
        return shown;
    }

    /// <summary>The groups shown on their own, for a list that leaves the rest out.</summary>
    public static List<T> Shown<T>(IEnumerable<T> rows, Func<T, int> people, int min) => Split(rows, people, min).Shown;
}
