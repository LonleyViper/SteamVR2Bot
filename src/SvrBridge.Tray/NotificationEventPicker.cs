using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>
/// Chooses which Streamer.bot events become headset notifications - §B2 of
/// the Phase 7 plan, rebuilt.
/// <para>
/// <b>Inverted from the two versions live testing rejected.</b> Both of those
/// showed the <em>available</em> events and asked the user to find theirs in
/// the list. A live <c>GetEvents</c> capture puts that list at 467 events
/// across 44 sources - 137 under Twitch alone - so browsing to find "Follow"
/// was poor even before the several hundred live <see cref="CheckBox"/>es it
/// took to draw made the window freeze on every keystroke.
/// </para>
/// <para>
/// This shows what is <em>enabled</em> first, because a user has perhaps four
/// alerts on and that short list is what they look at day to day, and treats
/// adding one as a search rather than a browse. The freeze cannot recur by
/// construction rather than by remembering to suspend layout: results are
/// filtered as data and capped at
/// <see cref="StreamerBotEventSearch.DefaultResultLimit"/> before a single
/// control is built, so the control count has no relationship to the catalog
/// size. The search is debounced so a fast typist gets one rebuild rather
/// than one per keystroke.
/// </para>
/// <para>
/// Nothing here is enabled by default, and a key stays enabled even if
/// <c>GetEvents</c> stops reporting it - see <see cref="EnabledKeys"/>.
/// </para>
/// </summary>
internal sealed class NotificationEventPicker : UserControl
{
    /// <summary>
    /// Long enough that a fast typist's whole word costs one rebuild, short
    /// enough to feel immediate when they stop.
    /// </summary>
    private const int SearchDebounceMilliseconds = 150;

    private const int RowHeight = 30;
    private const int ChipWidth = 38;
    private const int ListWidth = 560;

    /// <summary>
    /// Tallest either list grows before it starts scrolling. Both size
    /// themselves to their contents up to this, so one enabled alert gets a
    /// one-row box rather than a mostly-empty panel with a scrollbar on it.
    /// </summary>
    private const int MaxListHeight = 260;

    private static readonly Color MutedText = Color.FromArgb(92, 101, 112);
    private static readonly Color RowBorder = Color.FromArgb(226, 230, 235);

    // Shared and never disposed by the rows that use them. A Label does not
    // own its Font, so a font allocated per row would leak a GDI handle on
    // every rebuild - and these lists rebuild on every debounced keystroke.
    // Sizes are spelled out rather than derived from this control's own Font
    // so they do not depend on when the parent's font reaches it.
    private static readonly Font ChipFont = new("Segoe UI", 8F, FontStyle.Bold);
    private static readonly Font SmallFont = new("Segoe UI", 9F);
    private static readonly Font HeadingFont = new("Segoe UI", 10F, FontStyle.Bold);

    private readonly TextBox _search = new();

    /// <summary>
    /// Both lists lay their rows out rather than positioning them by hand.
    /// The hand-positioned version could be scrolled up into blank space:
    /// a child's <see cref="Control.Location"/> inside an
    /// <see cref="ScrollableControl.AutoScroll"/> container is relative to
    /// the <em>scrolled</em> origin, so rebuilding the rows while the panel
    /// happened to be scrolled placed them all below the top by however far
    /// it had been scrolled, and the scroll extents grew to match. Letting
    /// the layout own the positions removes the whole class of bug rather
    /// than papering over one instance of it - and at a couple of dozen rows
    /// the layout cost is nothing. The rejected design's freeze came from
    /// building hundreds of controls, not from this.
    /// </summary>
    private readonly FlowLayoutPanel _enabledList = NewListPanel();

    private readonly FlowLayoutPanel _results = NewListPanel();
    private readonly Label _resultCount = new();
    private readonly System.Windows.Forms.Timer _searchDebounce = new();

    private IReadOnlyList<StreamerBotEventDescriptor> _catalog = [];

    /// <summary>
    /// The enabled "Source.Type" keys, in the order they were added.
    /// <para>
    /// Held here rather than read back off the rendered rows, which is what
    /// lets a saved key survive a <c>GetEvents</c> response that no longer
    /// contains it. Streamer.bot only reports the events its currently
    /// installed integrations expose, so a key can disappear because an
    /// integration was momentarily unavailable or the fetch failed outright -
    /// dropping the user's choice on that basis would silently turn their
    /// alerts off and give them nothing to look at to work out why.
    /// </para>
    /// </summary>
    private readonly List<string> _enabled = [];

    /// <summary>Raised when the enabled set changes, so the form can save. Not raised while <see cref="SetEnabledKeys"/> is applying saved settings.</summary>
    public event Action? EnabledKeysChanged;

    /// <summary>Raised by the "Refresh events" button - the catalog comes from the form, which owns the connection.</summary>
    public event Action? RefreshRequested;

    public NotificationEventPicker()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0);

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };

        layout.Controls.Add(SectionHeading("Alerts shown in the headset"));
        _enabledList.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(_enabledList);

        layout.Controls.Add(SectionHeading("Add an alert"));
        _search.Width = ListWidth;
        _search.PlaceholderText = "Search events - try a platform, or what happens (follow, sub, raid)…";
        _search.TextChanged += (_, _) => RestartSearchDebounce();
        layout.Controls.Add(_search);

        _results.Margin = new Padding(0, 6, 0, 4);
        layout.Controls.Add(_results);

        _resultCount.AutoSize = true;
        _resultCount.ForeColor = MutedText;
        _resultCount.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(_resultCount);

        var refresh = new Button();
        ConfigureButton(refresh, "Refresh events from Streamer.bot");
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        layout.Controls.Add(refresh);

        _searchDebounce.Interval = SearchDebounceMilliseconds;
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RenderResults();
        };

        Controls.Add(layout);
        RenderEnabled();
        RenderResults();
    }

    /// <summary>The enabled "Source.Type" keys, for <see cref="UserSettings.EnabledEvents"/>.</summary>
    public IReadOnlyList<string> EnabledKeys => _enabled.ToArray();

    /// <summary>Replaces the enabled set from saved settings, without raising <see cref="EnabledKeysChanged"/> back at the caller that is applying them.</summary>
    public void SetEnabledKeys(IEnumerable<string> keys)
    {
        _enabled.Clear();
        foreach (var key in keys)
        {
            var trimmed = (key ?? "").Trim();
            if (trimmed.Length > 0 && !_enabled.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                _enabled.Add(trimmed);
            }
        }

        RenderEnabled();
        RenderResults();
    }

    /// <summary>
    /// Replaces the searchable catalog from a live <c>GetEvents</c> response.
    /// Does not touch the enabled set: an event the response no longer
    /// mentions stays enabled and keeps its row, marked as unreported rather
    /// than deleted.
    /// </summary>
    public void SetCatalog(IReadOnlyList<StreamerBotEventDescriptor> catalog)
    {
        _catalog = catalog;
        RenderEnabled();
        RenderResults();
    }

    private void RestartSearchDebounce()
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>
    /// Types <paramref name="query"/> and renders the results without waiting
    /// for the debounce. Exists for the self-tests, which run on a thread with
    /// no message loop for a <see cref="System.Windows.Forms.Timer"/> to tick
    /// on - the freeze that got two earlier designs rejected is exactly the
    /// kind of thing that has to stay covered by an automated test, so the
    /// seam is worth it.
    /// </summary>
    internal void ApplySearchNow(string query)
    {
        _search.Text = query;
        _searchDebounce.Stop();
        RenderResults();
    }

    /// <summary>
    /// How many result rows are actually built. The number this design exists
    /// to bound: it must track the cap, never the catalog size.
    /// </summary>
    internal int RenderedResultRowCount => _results.Controls.OfType<Panel>().Count();

    /// <summary>How many enabled rows are built - one per enabled key, however large the catalog is.</summary>
    internal int RenderedEnabledRowCount => _enabledList.Controls.OfType<Panel>().Count();

    /// <summary>The enabled list: one row per enabled key, newest last, each with an X that removes it.</summary>
    private void RenderEnabled()
    {
        _enabledList.SuspendLayout();
        try
        {
            DisposeChildren(_enabledList);
            if (_enabled.Count == 0)
            {
                // Invites the search below rather than apologising - there is
                // nothing wrong with having no alerts on, it is the state
                // every install starts in.
                _enabledList.Controls.Add(EmptyStateLabel("No alerts yet. Search below to add one."));
                return;
            }

            foreach (var key in _enabled)
            {
                var row = BuildRow(DescriptorFor(key), out var actionColumn);

                var remove = new Button
                {
                    Text = "✕",
                    Width = 34,
                    Height = 24,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(150, 45, 45),
                    Location = new Point(actionColumn, 3)
                };
                remove.FlatAppearance.BorderColor = RowBorder;
                var removedKey = key;
                remove.Click += (_, _) => Remove(removedKey);
                row.Controls.Add(remove);

                if (!IsInCatalog(key))
                {
                    // Kept, not dropped - see the remarks on _enabled. Said
                    // out loud so a user whose alert stopped arriving has
                    // something to see rather than a silently shorter list.
                    row.Controls.Add(new Label
                    {
                        Text = "not reported",
                        AutoSize = true,
                        ForeColor = MutedText,
                        Font = SmallFont,
                        Location = new Point(actionColumn - 96, 7)
                    });
                }

                _enabledList.Controls.Add(row);
            }
        }
        finally
        {
            _enabledList.ResumeLayout(true);
            FitListToContent(_enabledList);
        }
    }

    /// <summary>
    /// The search results: at most
    /// <see cref="StreamerBotEventSearch.DefaultResultLimit"/> rows, built
    /// from an already-filtered list. The whole point of this control is that
    /// this method never builds a control per catalog entry.
    /// </summary>
    private void RenderResults()
    {
        var found = StreamerBotEventSearch.Search(_catalog, _search.Text);

        _results.SuspendLayout();
        try
        {
            DisposeChildren(_results);
            if (found.Matches.Count == 0)
            {
                _results.Controls.Add(
                    EmptyStateLabel(
                        _catalog.Count == 0
                            ? "No events loaded yet. Connect to Streamer.bot, then refresh below."
                            : "Nothing matches that search."));
            }

            foreach (var descriptor in found.Matches)
            {
                var row = BuildRow(descriptor, out var actionColumn);
                if (IsEnabled(descriptor.Key))
                {
                    // Greyed "added" rather than dropping the row: hiding it
                    // would make searching "follow" straight after adding
                    // Twitch Follow look like the add had failed.
                    row.Controls.Add(new Label
                    {
                        Text = "added",
                        AutoSize = true,
                        ForeColor = MutedText,
                        // Same column as the Add button it stands in for, so
                        // a list mixing the two does not look ragged.
                        Location = new Point(actionColumn - 20, 7)
                    });
                }
                else
                {
                    var add = new Button
                    {
                        Text = "Add",
                        Width = 60,
                        Height = 24,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.White,
                        Location = new Point(actionColumn - 26, 3)
                    };
                    add.FlatAppearance.BorderColor = RowBorder;
                    var addedKey = descriptor.Key;
                    add.Click += (_, _) => Add(addedKey);
                    row.Controls.Add(add);
                }

                _results.Controls.Add(row);
            }
        }
        finally
        {
            _results.ResumeLayout(true);
            FitListToContent(_results);
        }

        _resultCount.Text = DescribeCount(found);
    }

    /// <summary>
    /// A list panel that lays its own rows out top-to-bottom. Not
    /// hand-positioned: see the remarks on <see cref="_enabledList"/> for the
    /// blank-scroll-space bug that came of doing it by hand.
    /// </summary>
    private static FlowLayoutPanel NewListPanel() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Width = ListWidth,
        Height = MaxListHeight,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        Padding = new Padding(6, 6, 6, 6)
    };

    /// <summary>
    /// Shrinks a list to the height its rows actually need, up to
    /// <see cref="MaxListHeight"/>, and puts the scroll position back to the
    /// top. Without the reset, removing the alert you were scrolled down to
    /// leaves the view parked past the end of a now-shorter list - which is
    /// the same blank space, arrived at from the other direction.
    /// </summary>
    private static void FitListToContent(FlowLayoutPanel list)
    {
        var content = list.Controls.Cast<Control>().Sum(child => child.Height + child.Margin.Vertical);
        list.Height = Math.Clamp(
            content + list.Padding.Vertical + 2,
            RowHeight + list.Padding.Vertical + 2,
            MaxListHeight);
        list.AutoScrollPosition = new Point(0, 0);
    }

    private static Label EmptyStateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = MutedText,
        Margin = new Padding(6, 6, 0, 0)
    };

    /// <summary>
    /// The line under the results. Says how many matched rather than silently
    /// showing the first twenty of a hundred, and only tells the user to keep
    /// typing when doing so would actually change what they see.
    /// </summary>
    private static string DescribeCount(StreamerBotEventSearchResult found)
    {
        if (found.TotalCount == 0)
        {
            return "No events loaded.";
        }

        var matched = $"{found.MatchCount} of {found.TotalCount} events match.";
        return found.Truncated
            ? $"{matched} Showing the first {found.Matches.Count} - keep typing to narrow."
            : matched;
    }

    /// <summary>
    /// A chip, the event's name, and its source - the shared skeleton of both
    /// lists' rows. <paramref name="actionColumn"/> is where the caller's own
    /// button or label goes. The row carries no <see cref="Control.Location"/>
    /// of its own; its list positions it.
    /// </summary>
    private Panel BuildRow(StreamerBotEventDescriptor descriptor, out int actionColumn)
    {
        var row = new Panel
        {
            Width = ListWidth - 40,
            Height = RowHeight,
            Margin = new Padding(0),
            Tag = descriptor.Key
        };

        row.Controls.Add(BuildSourceGlyph(descriptor.Source));
        row.Controls.Add(new Label
        {
            Text = descriptor.DisplayName,
            AutoSize = true,
            Location = new Point(ChipWidth + 16, 7)
        });
        row.Controls.Add(new Label
        {
            Text = descriptor.Source,
            AutoSize = true,
            ForeColor = MutedText,
            Location = new Point(300, 7)
        });

        actionColumn = 460;
        return row;
    }

    /// <summary>
    /// The source's embedded icon if this build ships one, otherwise its
    /// deterministic coloured chip. Neither branch knows the name of any
    /// particular platform - see <see cref="SourceIconResource"/> and
    /// <see cref="StreamerBotSourceChip"/>.
    /// </summary>
    private static Control BuildSourceGlyph(string source)
    {
        var badge = StreamerBotSourceChip.BadgeFor(source);
        var icon = SourceIconResource.TryResolve(source);
        if (icon is not null)
        {
            return new PictureBox
            {
                Image = icon,
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = ChipWidth,
                Height = 22,
                Location = new Point(4, 4)
            };
        }

        return new Label
        {
            Text = badge.Abbreviation,
            Width = ChipWidth,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = ParseHexColour(badge.Colour),
            ForeColor = Color.White,
            Font = ChipFont,
            Location = new Point(4, 4)
        };
    }

    /// <summary>
    /// The catalog's own descriptor for a key, or one reconstructed from the
    /// key itself when the catalog has never mentioned it - so an enabled
    /// alert still draws its name and chip after a failed or narrower
    /// <c>GetEvents</c>, rather than vanishing from the list the user manages
    /// it in.
    /// </summary>
    private StreamerBotEventDescriptor DescriptorFor(string key)
    {
        foreach (var descriptor in _catalog)
        {
            if (string.Equals(descriptor.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor;
            }
        }

        var separator = key.IndexOf('.');
        return separator > 0
            ? new StreamerBotEventDescriptor(key[..separator], key[(separator + 1)..])
            : new StreamerBotEventDescriptor("", key);
    }

    private bool IsInCatalog(string key) =>
        _catalog.Any(descriptor => string.Equals(descriptor.Key, key, StringComparison.OrdinalIgnoreCase));

    private bool IsEnabled(string key) => _enabled.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Enables one event, as the row's Add button does. Internal so a self-test can exercise it without synthesising a click.</summary>
    internal void Add(string key)
    {
        if (IsEnabled(key))
        {
            return;
        }

        _enabled.Add(key);
        RenderEnabled();
        RenderResults();
        EnabledKeysChanged?.Invoke();
    }

    /// <summary>Disables one event, as the row's X does. Internal for the same reason <see cref="Add"/> is.</summary>
    internal void Remove(string key)
    {
        var removed = _enabled.RemoveAll(
            existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return;
        }

        RenderEnabled();
        RenderResults();
        EnabledKeysChanged?.Invoke();
    }

    /// <summary>
    /// Clearing a WinForms container does not dispose what was in it, and
    /// both lists here are rebuilt on every keystroke's worth of debounce -
    /// leaking a row's worth of GDI handles each time would eventually take
    /// the process out, so the children go with the clear.
    /// <para>
    /// A <see cref="PictureBox"/> has its image detached first. Its icon is
    /// the single cached instance <see cref="SourceIconResource"/> hands to
    /// every row drawing that source, so letting a disposing row take it
    /// would blank that platform's icon everywhere for the rest of the run.
    /// </para>
    /// </summary>
    private static void DisposeChildren(Control container)
    {
        var children = container.Controls.Cast<Control>().ToArray();
        container.Controls.Clear();
        foreach (var child in children)
        {
            DetachSharedImages(child);
            child.Dispose();
        }
    }

    private static void DetachSharedImages(Control control)
    {
        if (control is PictureBox picture)
        {
            picture.Image = null;
        }

        foreach (Control child in control.Controls)
        {
            DetachSharedImages(child);
        }
    }

    private static Color ParseHexColour(string value)
    {
        var text = value.TrimStart('#');
        return text.Length == 6
               && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var packed)
            ? Color.FromArgb(255, (packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF)
            : Color.FromArgb(92, 101, 112);
    }

    private static Label SectionHeading(string text) => new()
    {
        Text = text,
        Font = HeadingFont,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4)
    };

    private static void ConfigureButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(112, 34);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Color.White;
        button.Margin = new Padding(0, 0, 0, 4);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchDebounce.Dispose();
        }

        base.Dispose(disposing);
    }
}
