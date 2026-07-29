namespace SvrBridge.Core;

/// <summary>
/// A bounded, oldest-first buffer of chat messages, written by the event
/// stream's consumption loop and read by the render thread - different
/// threads per §B6 of the chat plan, so every operation takes a lock.
/// <para>
/// The public surface is a snapshot plus a version counter rather than direct
/// enumeration. A reader can then tell "nothing changed since I last
/// repainted" from an integer comparison alone, which is what lets the
/// throttle in <see cref="ChatRepaintThrottle"/> stay cheap to poll every
/// 10 ms without copying the buffer on every tick.
/// </para>
/// </summary>
public sealed class ChatRingBuffer
{
    /// <summary>Roughly what fits a 512x768 texture at readable size, per §B3.</summary>
    public const int DefaultCapacity = 40;

    private readonly object _gate = new();
    private readonly Queue<StreamerBotEventPayload> _messages = new();
    private readonly int _capacity;
    private long _version;

    public ChatRingBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "A chat ring buffer needs a positive capacity.");
        }

        _capacity = capacity;
    }

    /// <summary>
    /// Bumped once per <see cref="Append"/>. Never zero after the first
    /// message, so a reader that starts with no prior version (0) always
    /// owes an initial repaint.
    /// </summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>
    /// Adds a message, evicting the oldest one first if already at capacity.
    /// Safe to call from any thread.
    /// </summary>
    public void Append(StreamerBotEventPayload message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (_messages.Count >= _capacity)
            {
                _messages.Dequeue();
            }

            _messages.Enqueue(message);
            _version++;
        }
    }

    /// <summary>A copy of the current messages, oldest first, newest last.</summary>
    public IReadOnlyList<StreamerBotEventPayload> Snapshot()
    {
        lock (_gate)
        {
            return _messages.ToArray();
        }
    }

    /// <summary>
    /// The snapshot and the version it was taken at, read under one lock so
    /// the two can never disagree about which messages a version number
    /// describes.
    /// </summary>
    public (IReadOnlyList<StreamerBotEventPayload> Messages, long Version) SnapshotWithVersion()
    {
        lock (_gate)
        {
            return (_messages.ToArray(), _version);
        }
    }
}
