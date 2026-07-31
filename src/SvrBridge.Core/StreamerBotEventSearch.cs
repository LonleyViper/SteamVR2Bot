namespace SvrBridge.Core;

/// <summary>
/// What one search over the event catalog found: the rows worth rendering,
/// and how many actually matched.
/// </summary>
/// <param name="Matches">
/// The rows to render, already capped at the caller's limit. Never longer
/// than that limit, however many matched.
/// </param>
/// <param name="MatchCount">
/// How many events matched in total, before the cap - what the count line
/// underneath the results reports, so a truncated result set says so instead
/// of quietly showing 20 of 137.
/// </param>
/// <param name="TotalCount">How many events the catalog holds at all.</param>
public sealed record StreamerBotEventSearchResult(
    IReadOnlyList<StreamerBotEventDescriptor> Matches,
    int MatchCount,
    int TotalCount)
{
    /// <summary>Whether <see cref="MatchCount"/> exceeded the cap, so rows were left unrendered.</summary>
    public bool Truncated => MatchCount > Matches.Count;
}

/// <summary>
/// Filters the <c>GetEvents</c> catalog down to the handful of rows the "Add
/// an alert" search shows.
/// <para>
/// This exists as its own pure function, separate from any control, because
/// of how the two rejected versions of this UI failed. Both built one
/// <c>CheckBox</c> per event and toggled <c>Visible</c> to filter - at the 467
/// events a real Streamer.bot instance reports (137 under Twitch alone), that
/// is hundreds of live WinForms controls each triggering its own relayout, and
/// it read as the whole app freezing. Filtering the <em>data</em> and then
/// rendering a capped result set makes that failure impossible by
/// construction rather than by remembering to suspend layout: the control
/// count cannot exceed <see cref="DefaultResultLimit"/> no matter how large
/// the catalog grows.
/// </para>
/// </summary>
public static class StreamerBotEventSearch
{
    /// <summary>
    /// How many result rows to render. Small enough that rebuilding the whole
    /// list on every (debounced) keystroke is cheap, and large enough that a
    /// reasonably specific query rarely needs narrowing further.
    /// </summary>
    public const int DefaultResultLimit = 20;

    /// <summary>
    /// Every event whose source or name contains all of the query's
    /// whitespace-separated terms, ordered by source then name, capped at
    /// <paramref name="limit"/>.
    /// <para>
    /// Terms are matched independently so word order does not matter and
    /// "twitch follow" finds <c>Twitch.Follow</c>. Each term is tried against
    /// the source, the raw event name <em>and</em> its spaced display form, so
    /// both "giftsub" and "gift sub" find <c>Twitch.GiftSub</c> - the wire
    /// format is run-together PascalCase but the row on screen is not, and a
    /// search that only matched one of them would look broken from whichever
    /// side the user typed.
    /// </para>
    /// </summary>
    public static StreamerBotEventSearchResult Search(
        IReadOnlyList<StreamerBotEventDescriptor> catalog,
        string query,
        int limit = DefaultResultLimit)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        limit = Math.Max(0, limit);

        var terms = (query ?? "").Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = new List<StreamerBotEventDescriptor>();
        foreach (var descriptor in catalog)
        {
            if (Matches(descriptor, terms))
            {
                matches.Add(descriptor);
            }
        }

        matches.Sort(static (left, right) =>
        {
            var bySource = string.Compare(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);
            return bySource != 0
                ? bySource
                : string.Compare(left.Type, right.Type, StringComparison.OrdinalIgnoreCase);
        });

        return new StreamerBotEventSearchResult(
            matches.Count <= limit ? matches : matches.GetRange(0, limit),
            matches.Count,
            catalog.Count);
    }

    /// <summary>An empty query matches everything - the results list is the browse view until somebody types.</summary>
    private static bool Matches(StreamerBotEventDescriptor descriptor, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!descriptor.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !descriptor.Type.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !descriptor.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
