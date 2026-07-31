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
    /// side the user typed. A term that matches nothing is retried without a
    /// trailing "s", so "subs" finds <c>Twitch.Sub</c>.
    /// </para>
    /// <para>
    /// Results are <b>ranked, not merely filtered</b>, which matters entirely
    /// because of the cap. Sorted alphabetically, "sub" put
    /// <c>Twitch.BotEventSubConnected</c> and <c>Twitch.ChatSubscriberModeOff</c>
    /// inside the visible twenty and left <c>Twitch.Sub</c> at position 28,
    /// <c>Twitch.GiftSub</c> at 22 - the three events anyone searching that
    /// word actually wants were the ones the cap threw away. An exact name
    /// match sorts first, then a name starting with the term, then a name with
    /// a <em>word</em> starting with it, then any substring, and last a row
    /// that only matched on its source. Ties break on the shorter name, since
    /// <c>Sub</c> is a likelier target than <c>BotEventSubConnected</c> for
    /// someone who typed "sub".
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

        var matches = new List<(StreamerBotEventDescriptor Descriptor, int Rank)>();
        foreach (var descriptor in catalog)
        {
            var rank = RankOf(descriptor, terms);
            if (rank is not null)
            {
                matches.Add((descriptor, rank.Value));
            }
        }

        // An empty query is the browse view rather than a search, and every
        // row ranks identically - so it sorts plainly by source and name,
        // where ranking by length would just look arbitrary.
        matches.Sort(terms.Length == 0 ? CompareAlphabetically : CompareByRank);

        var ordered = matches.Select(match => match.Descriptor).ToList();
        return new StreamerBotEventSearchResult(
            ordered.Count <= limit ? ordered : ordered.GetRange(0, limit),
            ordered.Count,
            catalog.Count);
    }

    private static readonly Comparison<(StreamerBotEventDescriptor Descriptor, int Rank)> CompareAlphabetically =
        static (left, right) =>
        {
            var bySource = string.Compare(
                left.Descriptor.Source, right.Descriptor.Source, StringComparison.OrdinalIgnoreCase);
            return bySource != 0
                ? bySource
                : string.Compare(
                    left.Descriptor.Type, right.Descriptor.Type, StringComparison.OrdinalIgnoreCase);
        };

    private static readonly Comparison<(StreamerBotEventDescriptor Descriptor, int Rank)> CompareByRank =
        static (left, right) =>
        {
            if (left.Rank != right.Rank)
            {
                return left.Rank.CompareTo(right.Rank);
            }

            if (left.Descriptor.Type.Length != right.Descriptor.Type.Length)
            {
                return left.Descriptor.Type.Length.CompareTo(right.Descriptor.Type.Length);
            }

            return CompareAlphabetically(left, right);
        };

    // Lower is better. Kept as named constants because the ordering they
    // impose is the whole reason the cap does not hide the obvious answer.
    private const int RankExactName = 0;
    private const int RankNameStartsWith = 1;
    private const int RankWordStartsWith = 2;
    private const int RankNameContains = 3;
    private const int RankSourceOnly = 4;

    /// <summary>
    /// The row's rank, or null when it does not match at all. Every term must
    /// match something (so a second term still narrows), but the rank is the
    /// <em>best</em> any single term achieved against the event's own name -
    /// searching "kick follow" should rank <c>Kick.Follow</c> on the strength
    /// of "follow" hitting the name exactly, not drag it down because "kick"
    /// only matched the source.
    /// </summary>
    private static int? RankOf(StreamerBotEventDescriptor descriptor, string[] terms)
    {
        if (terms.Length == 0)
        {
            return RankExactName;
        }

        var best = int.MaxValue;
        foreach (var term in terms)
        {
            var rank = RankTerm(descriptor, term) ?? RankTerm(descriptor, WithoutPluralS(term));
            if (rank is null)
            {
                return null;
            }

            best = Math.Min(best, rank.Value);
        }

        return best;
    }

    private static int? RankTerm(StreamerBotEventDescriptor descriptor, string? term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return null;
        }

        foreach (var name in (string[])[descriptor.Type, descriptor.DisplayName])
        {
            if (name.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                return RankExactName;
            }
        }

        foreach (var name in (string[])[descriptor.Type, descriptor.DisplayName])
        {
            if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                return RankNameStartsWith;
            }
        }

        // The display form is the run-together name already broken into
        // words, so a word-boundary test on it is what makes "sub" rank
        // GiftSub ("Gift Sub") above BotEventSubConnected's later position
        // without knowing anything about either event.
        foreach (var word in descriptor.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                return RankWordStartsWith;
            }
        }

        if (descriptor.Type.Contains(term, StringComparison.OrdinalIgnoreCase)
            || descriptor.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return RankNameContains;
        }

        return descriptor.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
            ? RankSourceOnly
            : null;
    }

    /// <summary>
    /// Drops one trailing "s" so a plural search term still finds a singular
    /// event name - "subs" finds <c>Sub</c>, "bits" finds <c>BitsBadgeTier</c>
    /// either way. Only widens what matches, never narrows it, and only for
    /// terms long enough that the stem is still meaningful.
    /// </summary>
    private static string? WithoutPluralS(string term) =>
        term.Length > 3 && term.EndsWith('s') ? term[..^1] : null;
}
