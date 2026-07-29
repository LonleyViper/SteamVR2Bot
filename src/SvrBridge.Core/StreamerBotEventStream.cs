using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace SvrBridge.Core;

/// <summary>What the desktop window can say about the event feed.</summary>
public enum StreamerBotStreamState
{
    Stopped,
    Connecting,
    Connected,
    Reconnecting
}

/// <summary>
/// One payload as it arrived. The arrival time is stamped here rather than
/// where the payload is consumed, because a burst of chat is drained after the
/// fact and would otherwise all carry the same timestamp.
/// </summary>
public sealed record StreamerBotEvent(
    DateTimeOffset ReceivedAt,
    StreamerBotEventPayload Payload);

/// <summary>
/// The long-lived half of the Streamer.bot connection: it subscribes to
/// <c>General.Custom</c> and publishes whatever arrives.
/// <para>
/// This is deliberately a separate type from <see cref="StreamerBotClient"/>
/// rather than a feature added to it. That client is built fresh per delivery
/// and reads the next frame as the response to what it just sent; an
/// unsolicited chat message landing between a DoAction and its acknowledgement
/// would be returned as the action result. Streamer.bot only sends events to
/// connections that subscribed, so keeping the subscription on a second socket
/// removes that failure by construction instead of by careful locking.
/// </para>
/// <para>
/// The two also want opposite policies. Action delivery retries three times and
/// then gives up, because silently resending may run the user's action twice.
/// Here a missed event is harmless and a permanently absent feed is not, so
/// this reconnects forever and drops what it could not deliver.
/// </para>
/// </summary>
public sealed class StreamerBotEventStream : IAsyncDisposable
{
    /// <summary>Backoff between connection attempts; the last entry repeats forever.</summary>
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // A single broadcast large enough to matter is a bug in the sending
    // action, not a message worth rendering. Bounding the accumulator keeps
    // one bad action from growing the process instead of dropping a frame.
    private const int MaximumFrameBytes = 1024 * 1024;

    // Bounded and oldest-first: if the consumer stalls, the newest chat is
    // what the user wants on their wrist, and unbounded growth behind a stalled
    // UI thread is the failure mode that would take the whole app down.
    private const int EventCapacity = 256;

    private readonly StreamerBotConfig _config;
    private readonly Action<BridgeActivity> _log;
    private readonly Channel<StreamerBotEvent> _events =
        Channel.CreateBounded<StreamerBotEvent>(
            new BoundedChannelOptions(EventCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });
    private readonly ConcurrentDictionary<string, PendingRequest> _pending =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _startGate = new();

    private Task? _pump;
    // Written by the pump, read by callers of SendRequestAsync and by disposal.
    private volatile ClientWebSocket? _socket;
    // Written by the pump thread as the connection changes, read by the UI
    // thread through State. Without volatile the reader may keep a stale copy
    // in a register indefinitely and show "Connecting" over a live feed.
    private volatile StreamerBotStreamState _state = StreamerBotStreamState.Stopped;
    private bool _disposed;

    public StreamerBotEventStream(StreamerBotConfig config, Action<BridgeActivity>? log = null)
    {
        _config = config;
        _log = log ?? (activity => Console.WriteLine(activity.Message));
    }

    /// <summary>Payloads in arrival order. Completes when the stream is disposed.</summary>
    public ChannelReader<StreamerBotEvent> Events => _events.Reader;

    public StreamerBotStreamState State => _state;

    public event Action<StreamerBotStreamState>? StateChanged;

    /// <summary>
    /// Requests awaiting a response. Exposed so a self-test can prove they are
    /// removed on timeout, cancellation and socket failure as well as success.
    /// </summary>
    public int PendingRequestCount => _pending.Count;

    /// <summary>Starts the receive pump. Calling it twice is a no-op.</summary>
    public void Start()
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pump ??= Task.Run(() => RunAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// Sends a request over the live socket and waits for the frame carrying
    /// the same id. Public for the same reason
    /// <see cref="StreamerBotClient.BuildAuthentication"/> is: the self-tests
    /// need it, and there is nothing unsafe about it.
    /// </summary>
    public Task<JsonDocument> SendRequestAsync(
        string request,
        CancellationToken cancellationToken)
    {
        var socket = _socket
                     ?? throw new InvalidOperationException(
                         "The Streamer.bot event stream is not connected.");
        return SendRequestAsync(socket, request, null, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task? pump;
        lock (_startGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pump = _pump;
            _pump = null;
        }

        _stopping.Cancel();
        _events.Writer.TryComplete();

        // Cancellation reaches a blocked ReceiveAsync only once the socket is
        // torn down, so abort rather than wait for a close handshake that the
        // other side may never answer.
        _socket?.Abort();

        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch (Exception)
            {
                // The pump swallows everything it can; anything left is a
                // shutdown race and must not fault disposal.
            }
        }

        FailPendingRequests(new OperationCanceledException("The event stream was stopped."));
        _stopping.Dispose();
        SetState(StreamerBotStreamState.Stopped);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var failureLogged = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetState(
                    attempt == 0
                        ? StreamerBotStreamState.Connecting
                        : StreamerBotStreamState.Reconnecting);
                await RunConnectionAsync(
                    () =>
                    {
                        attempt = 0;
                        failureLogged = false;
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Only the first failure of a run is worth the user's
                // attention. Streamer.bot being closed for an hour must not
                // fill the activity log with the same line.
                _log(
                    new BridgeActivity(
                        "streamerbot.events_unavailable",
                        $"Streamer.bot event feed unavailable: {exception.Message}",
                        failureLogged ? BridgeLogLevel.Debug : BridgeLogLevel.Warning));
                failureLogged = true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetState(StreamerBotStreamState.Reconnecting);
            var delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
            attempt++;
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunConnectionAsync(
        Action onConnected,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        await socket.ConnectAsync(new Uri(_config.WebSocketUrl), cancellationToken);

        // The handshake is read directly, before the pump starts, because
        // Hello and Authenticate are the one exchange where the next frame
        // really is the answer: no subscription exists yet to interleave with.
        await ShakeHandsAsync(socket, cancellationToken);

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _socket = socket;
        var reader = ReadLoopAsync(socket, connection.Token);
        try
        {
            // The acknowledgement carries nothing worth keeping, and holding it
            // would pin its pooled buffers for the life of the connection.
            (await SendRequestAsync(
                socket,
                "Subscribe",
                new Dictionary<string, object>
                {
                    ["events"] = new Dictionary<string, string[]>
                    {
                        ["General"] = ["Custom"]
                    }
                },
                connection.Token)).Dispose();

            onConnected();
            SetState(StreamerBotStreamState.Connected);
            _log(
                new BridgeActivity(
                    "streamerbot.events_connected",
                    $"Listening for Streamer.bot chat and events at {_config.WebSocketUrl}."));

            await reader;
        }
        finally
        {
            _socket = null;
            connection.Cancel();
            socket.Abort();
            try
            {
                await reader;
            }
            catch (Exception)
            {
                // Already reported by the caller of this method, or a
                // cancellation caused by the teardown immediately above.
            }

            FailPendingRequests(
                new WebSocketException("The Streamer.bot event connection closed."));
        }
    }

    /// <summary>
    /// The same Hello/Authenticate exchange the action client performs.
    /// Duplicated rather than shared: extracting it would mean editing the
    /// validated delivery path to add a feature it must stay ignorant of.
    /// </summary>
    private async Task ShakeHandsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var hello = await ReceiveJsonAsync(socket, cancellationToken);
        if (!hello.RootElement.TryGetProperty("request", out var request)
            || request.GetString() != "Hello")
        {
            throw new InvalidDataException("Streamer.bot did not send the expected Hello message.");
        }

        if (!hello.RootElement.TryGetProperty("authentication", out var authentication))
        {
            return;
        }

        if (string.IsNullOrEmpty(_config.Password))
        {
            throw new InvalidOperationException(
                "Streamer.bot requires authentication but no password is saved.");
        }

        var salt = authentication.GetProperty("salt").GetString()
                   ?? throw new InvalidDataException("Hello authentication salt is missing.");
        var challenge = authentication.GetProperty("challenge").GetString()
                        ?? throw new InvalidDataException("Hello authentication challenge is missing.");
        var id = $"svr-events-auth-{Guid.NewGuid():N}";

        await SendJsonAsync(
            socket,
            new Dictionary<string, object>
            {
                ["request"] = "Authenticate",
                ["id"] = id,
                ["authentication"] =
                    StreamerBotClient.BuildAuthentication(_config.Password, salt, challenge)
            },
            cancellationToken);

        using var response = await ReceiveJsonAsync(socket, cancellationToken);
        EnsureSuccessfulResponse(response.RootElement, id, "Authenticate");
    }

    private async Task<JsonDocument> SendRequestAsync(
        ClientWebSocket socket,
        string request,
        IReadOnlyDictionary<string, object>? arguments,
        CancellationToken cancellationToken)
    {
        var id = $"svr-events-{Guid.NewGuid():N}";
        var payload = new Dictionary<string, object>
        {
            ["request"] = request,
            ["id"] = id
        };
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                payload[key] = value;
            }
        }

        var pending = new PendingRequest();
        _pending[id] = pending;

        JsonDocument? response = null;
        var delivered = false;
        try
        {
            await SendJsonAsync(socket, payload, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            response = await pending.Task.WaitAsync(timeout.Token);
            EnsureSuccessfulResponse(response.RootElement, id, request);
            delivered = true;
            return response;
        }
        finally
        {
            // Every exit removes the entry: a send failure, the timeout, the
            // caller's cancellation, and a dropped socket all land here.
            _pending.TryRemove(id, out _);

            if (!delivered)
            {
                if (response is not null)
                {
                    // Received, then rejected by EnsureSuccessfulResponse.
                    // Nothing downstream will ever release its pooled buffers.
                    response.Dispose();
                }
                else if (!pending.TryAbandon())
                {
                    // We gave up, but the pump had already claimed the request
                    // in that same instant, so it will not dispose the document
                    // itself. The claim is taken before the result is published,
                    // so the document may not have landed yet - await it rather
                    // than test IsCompletedSuccessfully, which is precisely the
                    // check that let this document escape before.
                    await DisposeUnwantedResponseAsync(pending);
                }
            }
        }
    }

    /// <summary>
    /// Releases a response whose requester stopped waiting between the pump
    /// claiming the request and the result being published.
    /// </summary>
    private static async Task DisposeUnwantedResponseAsync(PendingRequest pending)
    {
        try
        {
            (await pending.Task).Dispose();
        }
        catch (Exception)
        {
            // The claim was taken by FailPendingRequests rather than by a
            // delivered frame, so there is no document to release.
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (!cancellationToken.IsCancellationRequested
               && socket.State == WebSocketState.Open)
        {
            using var frame = new MemoryStream();
            var oversized = false;

            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (frame.Length + result.Count > MaximumFrameBytes)
                {
                    oversized = true;
                    continue;
                }

                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (oversized)
            {
                _log(
                    new BridgeActivity(
                        "streamerbot.event_oversized",
                        "Dropped a Streamer.bot broadcast larger than 1 MB.",
                        BridgeLogLevel.Warning));
                continue;
            }

            frame.Position = 0;
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(frame, cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                // Malformed input is the sending action's problem. Ending the
                // pump over it would take the feed down until the next
                // reconnect, which is exactly the wrong trade.
                _log(
                    new BridgeActivity(
                        "streamerbot.event_dropped",
                        $"Ignored an unreadable Streamer.bot frame: {exception.Message}",
                        BridgeLogLevel.Debug));
                continue;
            }

            Dispatch(document);
        }
    }

    /// <summary>
    /// Routes one frame by shape: a reply to a request we are waiting on, an
    /// event, or something this app has no use for.
    /// </summary>
    private void Dispatch(JsonDocument document)
    {
        var handedOff = false;
        try
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && _pending.TryRemove(id.GetString() ?? "", out var pending))
            {
                // The waiting request owns the document from here - unless it
                // has already stopped waiting, in which case the claim fails
                // and the finally below releases the document.
                handedOff = pending.TryDeliver(document);
                return;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("event", out var source)
                && source.ValueKind == JsonValueKind.Object)
            {
                PublishEvent(document.RootElement, source);
                return;
            }

            _log(
                new BridgeActivity(
                    "streamerbot.event_ignored",
                    "Ignored a Streamer.bot frame that was neither a response nor an event.",
                    BridgeLogLevel.Debug));
        }
        finally
        {
            if (!handedOff)
            {
                document.Dispose();
            }
        }
    }

    private void PublishEvent(JsonElement root, JsonElement source)
    {
        var eventSource = source.TryGetProperty("source", out var sourceName)
            ? sourceName.GetString() ?? ""
            : "";
        var eventType = source.TryGetProperty("type", out var typeName)
            ? typeName.GetString() ?? ""
            : "";

        // Only General.Custom is subscribed to, so anything else here means
        // Streamer.bot sent something this app never asked for.
        if (!eventSource.Equals("General", StringComparison.OrdinalIgnoreCase)
            || !eventType.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            _log(
                new BridgeActivity(
                    "streamerbot.event_ignored",
                    $"Ignored an unsubscribed Streamer.bot event: {eventSource}.{eventType}",
                    BridgeLogLevel.Debug));
            return;
        }

        if (!root.TryGetProperty("data", out var data))
        {
            _log(
                new BridgeActivity(
                    "streamerbot.event_dropped",
                    "Dropped a Streamer.bot broadcast with no payload.",
                    BridgeLogLevel.Debug));
            return;
        }

        if (!StreamerBotEventPayload.TryParse(data, out var payload, out var rejection))
        {
            _log(
                new BridgeActivity(
                    "streamerbot.event_dropped",
                    $"Dropped a Streamer.bot broadcast: {rejection}.",
                    BridgeLogLevel.Debug));
            return;
        }

        _events.Writer.TryWrite(new StreamerBotEvent(DateTimeOffset.Now, payload));
    }

    private void FailPendingRequests(Exception reason)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TryFail(reason);
            }
        }
    }

    /// <summary>
    /// One in-flight request, and the arbiter that decides which of the two
    /// threads racing over its response is responsible for disposing it.
    /// <para>
    /// A <see cref="JsonDocument"/> parsed from a stream holds pooled buffers,
    /// so exactly one side must release it. The pump produces the document and
    /// the requester consumes it, but the requester can time out at any moment -
    /// including after the pump has decided to hand the document over. Testing
    /// the task for completion cannot close that window, because the pump is
    /// committed to the handoff before the result becomes observable.
    /// </para>
    /// <para>
    /// The claim flag is taken first and taken once. Whoever loses the exchange
    /// knows the other side owns the document, which turns an unlucky interleave
    /// into an ordinary branch rather than a leak.
    /// </para>
    /// </summary>
    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<JsonDocument> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _claimed;

        public Task<JsonDocument> Task => _completion.Task;

        /// <summary>
        /// Hands a response to the requester. Returns false when the requester
        /// already gave up, leaving the document with the caller to dispose.
        /// </summary>
        public bool TryDeliver(JsonDocument document) =>
            TryClaim() && _completion.TrySetResult(document);

        /// <summary>Reports that no response is coming.</summary>
        public bool TryFail(Exception reason) =>
            TryClaim() && _completion.TrySetException(reason);

        /// <summary>
        /// Called by the requester when it stops waiting. Returns true if it got
        /// there first, meaning no document was ever handed over; false means the
        /// pump is mid-handoff and the requester must release what arrives.
        /// </summary>
        public bool TryAbandon() => TryClaim();

        private bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }

    private void SetState(StreamerBotStreamState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    private static void EnsureSuccessfulResponse(
        JsonElement response,
        string expectedId,
        string operation)
    {
        if (!response.TryGetProperty("id", out var id) || id.GetString() != expectedId)
        {
            throw new InvalidDataException($"{operation} returned an unexpected response id.");
        }

        if (response.TryGetProperty("status", out var status)
            && !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            var error = response.TryGetProperty("error", out var errorValue)
                ? errorValue.ToString()
                : response.ToString();
            throw new InvalidOperationException($"{operation} failed: {error}");
        }
    }

    private static async Task SendJsonAsync(
        ClientWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Streamer.bot closed the WebSocket connection.");
            }

            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
