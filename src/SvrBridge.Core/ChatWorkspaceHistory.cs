namespace SvrBridge.Core;

/// <summary>The two streams shown by the wrist-chat workspace.</summary>
public enum ChatWorkspaceTab { Chat, Events }

/// <summary>A parsed payload and the time the tray-host event stream received it.</summary>
public sealed record ChatWorkspaceEntry(DateTimeOffset ReceivedAt, StreamerBotEventPayload Payload);

/// <summary>A point-in-time, oldest-first copy of both session histories.</summary>
public sealed record ChatWorkspaceSnapshot(
    IReadOnlyList<ChatWorkspaceEntry> Chat,
    IReadOnlyList<ChatWorkspaceEntry> Events,
    long Version = 0);

/// <summary>
/// Bounded, in-memory session history for the wrist-chat workspace. It belongs
/// to the persistent tray host, not a disposable OpenVR worker, and is never
/// written to settings or disk.
/// </summary>
public sealed class ChatWorkspaceHistory
{
    /// <summary>Per-stream capacity, balancing useful scrollback and privacy-conscious memory use.</summary>
    public const int DefaultCapacityPerStream = 250;

    private readonly object _gate = new();
    private readonly Queue<ChatWorkspaceEntry> _chat = new();
    private readonly Queue<ChatWorkspaceEntry> _events = new();
    private readonly int _capacity;
    private long _version;

    public ChatWorkspaceHistory(int capacityPerStream = DefaultCapacityPerStream)
    {
        if (capacityPerStream <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityPerStream), capacityPerStream, "Workspace history needs a positive per-stream capacity.");
        }

        _capacity = capacityPerStream;
    }

    public int CapacityPerStream => _capacity;

    public void Append(ChatWorkspaceTab tab, ChatWorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var target = tab == ChatWorkspaceTab.Chat ? _chat : _events;
            if (target.Count >= _capacity)
            {
                target.Dequeue();
            }

            target.Enqueue(entry);
            _version++;
        }
    }

    public void Clear(ChatWorkspaceTab tab)
    {
        lock (_gate)
        {
            (tab == ChatWorkspaceTab.Chat ? _chat : _events).Clear();
            _version++;
        }
    }

    public void Replace(ChatWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _chat.Clear();
            _events.Clear();
            CopyBounded(snapshot.Chat, _chat);
            CopyBounded(snapshot.Events, _events);
            _version++;
        }
    }

    public ChatWorkspaceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ChatWorkspaceSnapshot(_chat.ToArray(), _events.ToArray(), _version);
        }
    }

    private void CopyBounded(IReadOnlyList<ChatWorkspaceEntry> source, Queue<ChatWorkspaceEntry> target)
    {
        for (var index = Math.Max(0, source.Count - _capacity); index < source.Count; index++)
        {
            target.Enqueue(source[index]);
        }
    }
}
