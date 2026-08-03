using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>Deterministic tab and scrollback state, independent of WPF/OpenVR.</summary>
internal sealed class ChatWorkspaceViewport
{
    // The chat card has 728 vertical pixels available. Thirty-two normal
    // chat/event rows use that space rather than leaving the lower quarter
    // empty, without bringing back paging chrome.
    public const int PageSize = 32;
    public const int ScrollStep = 3;
    private int _chatOffsetFromLatest;
    private int _eventOffsetFromLatest;
    private int _knownChatCount;
    private int _knownEventCount;

    public ChatWorkspaceTab ActiveTab { get; private set; } = ChatWorkspaceTab.Chat;
    public bool CanNext => OffsetFor(ActiveTab) > 0;

    public void Reconcile(ChatWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _chatOffsetFromLatest = ReconcileOffset(_chatOffsetFromLatest, _knownChatCount, snapshot.Chat.Count);
        _eventOffsetFromLatest = ReconcileOffset(_eventOffsetFromLatest, _knownEventCount, snapshot.Events.Count);
        _knownChatCount = snapshot.Chat.Count;
        _knownEventCount = snapshot.Events.Count;
    }

    public void Select(ChatWorkspaceTab tab) => ActiveTab = tab;
    public void Previous(ChatWorkspaceSnapshot snapshot) => SetOffset(ActiveTab, Math.Min(OffsetFor(ActiveTab) + PageSize, MaxOffset(CountFor(snapshot, ActiveTab))));
    public void Next() => SetOffset(ActiveTab, Math.Max(0, OffsetFor(ActiveTab) - PageSize));
    public void Latest() => SetOffset(ActiveTab, 0);

    /// <summary>Moves the reading window toward older entries by a small controller-scroll increment.</summary>
    public bool ScrollOlder(ChatWorkspaceSnapshot snapshot) =>
        MoveBy(snapshot, ScrollStep);

    /// <summary>Moves the reading window toward newer entries by a small controller-scroll increment.</summary>
    public bool ScrollNewer(ChatWorkspaceSnapshot snapshot) =>
        MoveBy(snapshot, -ScrollStep);

    public IReadOnlyList<ChatWorkspaceEntry> VisibleEntries(ChatWorkspaceSnapshot snapshot)
    {
        var entries = ActiveTab == ChatWorkspaceTab.Chat ? snapshot.Chat : snapshot.Events;
        var offset = Math.Min(OffsetFor(ActiveTab), MaxOffset(entries.Count));
        var take = Math.Min(PageSize, entries.Count);
        return entries.Skip(Math.Max(0, entries.Count - take - offset)).Take(take).ToArray();
    }

    public bool CanPrevious(ChatWorkspaceSnapshot snapshot) => OffsetFor(ActiveTab) < MaxOffset(CountFor(snapshot, ActiveTab));
    public double ScrollFraction(ChatWorkspaceSnapshot snapshot)
    {
        var maximum = MaxOffset(CountFor(snapshot, ActiveTab));
        return maximum == 0 ? 0 : OffsetFor(ActiveTab) / (double)maximum;
    }
    internal int OffsetFromLatest(ChatWorkspaceTab tab) => OffsetFor(tab);

    private bool MoveBy(ChatWorkspaceSnapshot snapshot, int delta)
    {
        var current = OffsetFor(ActiveTab);
        var next = Math.Clamp(current + delta, 0, MaxOffset(CountFor(snapshot, ActiveTab)));
        if (next == current)
        {
            return false;
        }

        SetOffset(ActiveTab, next);
        return true;
    }

    private static int ReconcileOffset(int offset, int knownCount, int currentCount)
    {
        var additions = Math.Max(0, currentCount - knownCount);
        return offset == 0 ? 0 : Math.Min(offset + additions, MaxOffset(currentCount));
    }

    private int OffsetFor(ChatWorkspaceTab tab) => tab == ChatWorkspaceTab.Chat ? _chatOffsetFromLatest : _eventOffsetFromLatest;
    private void SetOffset(ChatWorkspaceTab tab, int value)
    {
        if (tab == ChatWorkspaceTab.Chat) _chatOffsetFromLatest = value;
        else _eventOffsetFromLatest = value;
    }

    private static int CountFor(ChatWorkspaceSnapshot snapshot, ChatWorkspaceTab tab) => tab == ChatWorkspaceTab.Chat ? snapshot.Chat.Count : snapshot.Events.Count;
    private static int MaxOffset(int count) => Math.Max(0, count - PageSize);
}
