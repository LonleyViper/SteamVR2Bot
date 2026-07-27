using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class VrActionBrowser
{
    public const int VisibleRowCount = 6;

    private readonly IReadOnlyList<ActionGroup> _groups;
    private ActionGroup? _selectedGroup;
    private int _offset;

    public VrActionBrowser(IReadOnlyList<StreamerBotAction> actions)
    {
        _groups = actions
            .GroupBy(
                action => FriendlyGroupName(action.Group),
                StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new ActionGroup(
                group.Key,
                group
                    .OrderBy(action => action.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()))
            .OrderBy(
                group => group.Name.Equals(
                    "Ungrouped",
                    StringComparison.CurrentCultureIgnoreCase)
                    ? 1
                    : 0)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public bool IsShowingGroups => _selectedGroup is null;

    public string Title =>
        IsShowingGroups ? "Choose an action group" : _selectedGroup!.Name;

    public string Subtitle =>
        IsShowingGroups
            ? $"{_groups.Count} groups • Open one, then scroll with your controller."
            : $"{_selectedGroup!.Actions.Count} actions • Scroll, then choose what this shortcut runs.";

    public string BackLabel => IsShowingGroups ? "Cancel" : "All groups";

    public int TotalItemCount =>
        IsShowingGroups ? _groups.Count : _selectedGroup!.Actions.Count;

    public int FirstVisibleItemNumber => TotalItemCount == 0 ? 0 : _offset + 1;

    public int LastVisibleItemNumber =>
        Math.Min(_offset + VisibleRowCount, TotalItemCount);

    public bool CanScrollUp => _offset > 0;

    public bool CanScrollDown => _offset + VisibleRowCount < TotalItemCount;

    public IReadOnlyList<VrActionBrowserRow> VisibleRows =>
        IsShowingGroups
            ? _groups
                .Skip(_offset)
                .Take(VisibleRowCount)
                .Select(group => new VrActionBrowserRow(
                    group.Name,
                    $"{group.Actions.Count} action{(group.Actions.Count == 1 ? "" : "s")}",
                    null))
                .ToArray()
            : _selectedGroup!.Actions
                .Skip(_offset)
                .Take(VisibleRowCount)
                .Select(action => new VrActionBrowserRow(action.Name, null, action))
                .ToArray();

    public StreamerBotAction? OpenRow(int visibleRowIndex)
    {
        if (visibleRowIndex is < 0 or >= VisibleRowCount)
        {
            return null;
        }

        var itemIndex = _offset + visibleRowIndex;
        if (IsShowingGroups)
        {
            if (itemIndex >= _groups.Count)
            {
                return null;
            }

            _selectedGroup = _groups[itemIndex];
            _offset = 0;
            return null;
        }

        return itemIndex < _selectedGroup!.Actions.Count
            ? _selectedGroup.Actions[itemIndex]
            : null;
    }

    public bool BackToGroups()
    {
        if (IsShowingGroups)
        {
            return false;
        }

        _selectedGroup = null;
        _offset = 0;
        return true;
    }

    public void Reset()
    {
        _selectedGroup = null;
        _offset = 0;
    }

    public bool ScrollRows(int rowDelta)
    {
        var maximumOffset = Math.Max(0, TotalItemCount - VisibleRowCount);
        var nextOffset = Math.Clamp(_offset + rowDelta, 0, maximumOffset);
        if (nextOffset == _offset)
        {
            return false;
        }

        _offset = nextOffset;
        return true;
    }

    public bool ScrollPage(int direction) =>
        ScrollRows(Math.Sign(direction) * VisibleRowCount);

    private static string FriendlyGroupName(string group) =>
        string.IsNullOrWhiteSpace(group)
        || group.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? "Ungrouped"
            : group.Trim();

    private sealed record ActionGroup(
        string Name,
        IReadOnlyList<StreamerBotAction> Actions);
}

internal sealed record VrActionBrowserRow(
    string Label,
    string? Detail,
    StreamerBotAction? Action);
