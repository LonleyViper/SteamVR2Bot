namespace SvrBridge.Core;

/// <summary>How one event source identifies itself in the picker: a short label and a colour.</summary>
/// <param name="Source">The source string exactly as <c>GetEvents</c> reported it, for the chip's tooltip and grouping.</param>
/// <param name="Abbreviation">One or two characters derived from <paramref name="Source"/> - "TW", "YT", "SE".</param>
/// <param name="Colour">"#RRGGBB", the same form every other colour in this app is carried as.</param>
public sealed record StreamerBotSourceBadge(string Source, string Abbreviation, string Colour);

/// <summary>
/// Gives every event source a chip the picker can draw next to an event's
/// name, so a row reads as "this one is Twitch" at a glance.
/// <para>
/// Derived entirely from the source string <c>GetEvents</c> reported, with no
/// list of known platforms anywhere. That is the point rather than a
/// simplification: §5c's governing principle is that this app must not carry
/// a hardcoded platform list, and a live capture from Streamer.bot 1.0.4
/// showed 44 distinct sources - Twitch, Kick, YouTube and Trovo alongside
/// Elgato, MeldStudio, DonorDrive, ThrowingSystem and Pallygg. A source
/// nobody anticipated, including one added to Streamer.bot after this was
/// written, gets a stable readable chip the same way the well-known ones do.
/// </para>
/// <para>
/// Colours come from a small fixed palette indexed by a stable hash of the
/// source name, so a given source always draws the same colour, in this
/// session and the next. <b>Not</b> <see cref="object.GetHashCode"/>: .NET
/// randomises string hashing per process, which would repaint every chip a
/// different colour on each launch.
/// </para>
/// <para>
/// The live <c>GetEvents</c> response carries no icon or image field of any
/// kind (see <see cref="StreamerBotEventCatalog"/>), so any real platform
/// logo has to be an embedded resource this app ships itself - never a
/// network fetch. <see cref="StreamerBotSourceChip"/> deliberately does not
/// know whether such an icon exists: the Tray layer looks one up by source
/// name and falls back to this badge when there is none, which is why adding
/// a logo later is dropping a file in rather than editing a lookup here.
/// </para>
/// </summary>
public static class StreamerBotSourceChip
{
    /// <summary>
    /// Deliberately small and chosen for legibility against white text at
    /// chip size rather than for resembling any particular brand - a chip is
    /// an identifier, not a logo, and picking "Twitch purple" for Twitch
    /// would mean carrying the platform list this type exists to avoid.
    /// </summary>
    private static readonly string[] Palette =
    [
        "#6441A5",
        "#1F8A70",
        "#B4531E",
        "#2D6CB5",
        "#8B2F62",
        "#3F6E37",
        "#A33A3A",
        "#5B4B8A",
        "#1F6F7A",
        "#7A5C1F"
    ];

    /// <summary>The chip for one source. An empty or whitespace source still yields a drawable badge rather than throwing - it is a string off the wire, not something this app controls.</summary>
    public static StreamerBotSourceBadge BadgeFor(string? source)
    {
        var trimmed = (source ?? "").Trim();
        return new StreamerBotSourceBadge(
            trimmed,
            AbbreviationFor(trimmed),
            ColourFor(trimmed));
    }

    /// <summary>
    /// "#RRGGBB" from <see cref="Palette"/>, stable for a given source across
    /// runs. Case-insensitive, so a source that changes capitalisation
    /// between Streamer.bot versions does not change colour.
    /// </summary>
    public static string ColourFor(string? source)
    {
        var key = (source ?? "").Trim().ToLowerInvariant();
        return Palette[(int)(StableHash(key) % (uint)Palette.Length)];
    }

    /// <summary>
    /// One or two characters standing in for the source name: its capitals
    /// when it has more than one ("YouTube" gives "YT", "StreamElements"
    /// gives "SE"), otherwise its first two letters ("Twitch" gives "TW",
    /// "Kick" gives "KI"). A source with nothing usable in it gives "?" so
    /// the row still draws.
    /// </summary>
    public static string AbbreviationFor(string? source)
    {
        var trimmed = (source ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "?";
        }

        var capitals = new List<char>(2);
        foreach (var character in trimmed)
        {
            if (char.IsUpper(character))
            {
                capitals.Add(character);
                if (capitals.Count == 2)
                {
                    break;
                }
            }
        }

        if (capitals.Count == 2)
        {
            return new string([capitals[0], capitals[1]]);
        }

        var letters = trimmed.Where(char.IsLetterOrDigit).Take(2).ToArray();
        return letters.Length > 0
            ? new string(letters).ToUpperInvariant()
            : "?";
    }

    /// <summary>
    /// FNV-1a. Any stable hash would do; what matters is that it is <em>not</em>
    /// <see cref="string.GetHashCode()"/>, whose per-process randomisation
    /// would give the same source a different colour on every launch.
    /// </summary>
    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return hash;
    }
}
