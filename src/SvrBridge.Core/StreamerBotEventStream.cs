using System.Collections.Concurrent;
using System.Linq;
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
/// How directly-subscribed Streamer.bot events (everything beyond
/// <c>General.Custom</c>/<c>Twitch.ChatMessage</c>) become notifications -
/// per §B2 of the Phase 7 plan.
/// <para>
/// Opt-in, not "subscribe to everything": a live headset session tried the
/// broader design first, and it does not hold up. Streamer.bot's own
/// documentation and API expose <b>no</b> way to ask which events currently
/// have an enabled trigger, and confirmed live: disabling every event in
/// Streamer.bot's own Settings > Events panel did not stop this app
/// receiving them. There is no server-side signal this app can rely on to
/// avoid noise (OBS scene changes and other non-alert plumbing came through
/// indistinguishable from real alerts), so the selection has to live here,
/// client-side - defaulting to nothing enabled, exactly today's behaviour
/// for a settings file predating this feature.
/// </para>
/// </summary>
/// <param name="EnabledEvents">
/// "Source.Type" keys (<see cref="StreamerBotEventDescriptor.Key"/>) the
/// wearer has explicitly turned on. Never includes <c>General.Custom</c> or
/// <c>Twitch.ChatMessage</c> - those two are subscribed unconditionally and
/// have nothing to do with this list.
/// </param>
/// <param name="Templates">Per-event template override, keyed the same way. An event with no entry here uses <see cref="DefaultTemplate"/>.</param>
/// <param name="DefaultTemplate">Resolved against an enabled event with no entry in <see cref="Templates"/>.</param>
/// <param name="ShowTestEvents">
/// Whether an event whose <c>data.isTest</c> is <c>true</c> still produces a
/// notification. Defaults to showing them, per §B2: a wearer firing a test
/// from Streamer.bot wants to see the result.
/// </param>
public sealed record NotificationEventSettings(
    IReadOnlyCollection<string> EnabledEvents,
    IReadOnlyDictionary<string, string> Templates,
    string DefaultTemplate,
    bool ShowTestEvents)
{
    public const string GenericDefaultTemplate = "New event: {event}";

    /// <summary>No extra events enabled - exactly today's behaviour, for a caller that has not opted into any.</summary>
    public static readonly NotificationEventSettings None = new(
        [],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        GenericDefaultTemplate,
        true);
}

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
    private readonly NotificationEventSettings _notificationEvents;
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

    public StreamerBotEventStream(
        StreamerBotConfig config,
        Action<BridgeActivity>? log = null,
        NotificationEventSettings? notificationEvents = null)
    {
        _config = config;
        _notificationEvents = notificationEvents ?? NotificationEventSettings.None;
        _log = log ?? (activity => Console.WriteLine(activity.Message));
    }

    /// <summary>
    /// Asks the connected Streamer.bot instance what it can emit, for the
    /// Notifications tab's toggle list - see <see cref="StreamerBotEventCatalog"/>.
    /// Callers must treat a thrown exception the same way §B2 requires: fall
    /// back to whatever selection is already saved rather than taking the
    /// feed down.
    /// </summary>
    public async Task<IReadOnlyList<StreamerBotEventDescriptor>> GetEventsAsync(
        CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync("GetEvents", cancellationToken);
        return StreamerBotEventCatalog.Parse(response.RootElement);
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
            // Every event Streamer.bot can emit becomes a notification, per
            // §B2's revised design - the user-facing rationale is that
            // Streamer.bot only ever forwards an event while at least one
            // local trigger for it is enabled (confirmed live for
            // Twitch.ChatMessage in Phase 3; the same gate applies to every
            // other event), so subscribing broadly is self-limiting rather
            // than a firehose. A GetEvents failure must not take down the
            // feed, so a catalog fetch failure here falls back to the two
            // events this app always wants rather than aborting the connection.
            var catalog = await TryFetchEventCatalogAsync(socket, connection.Token);

            // The acknowledgement carries nothing worth keeping, and holding it
            // would pin its pooled buffers for the life of the connection.
            (await SendRequestAsync(
                socket,
                "Subscribe",
                new Dictionary<string, object>
                {
                    ["events"] = BuildSubscribeEvents(catalog)
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

        if (!root.TryGetProperty("data", out var data))
        {
            _log(
                new BridgeActivity(
                    "streamerbot.event_dropped",
                    "Dropped a Streamer.bot broadcast with no payload.",
                    BridgeLogLevel.Debug));
            return;
        }

        // General.Custom carries a hand-authored payload already in this
        // app's own shape; Twitch.ChatMessage carries Streamer.bot's parsed
        // Twitch event and needs the platform-specific mapper. Anything else
        // means Streamer.bot sent something this app never subscribed to -
        // see BuildSubscribeEvents, which only ever asks for
        // NotificationEventSettings.EnabledEvents beyond those two.
        bool mapped;
        StreamerBotEventPayload? payload;
        string rejection;
        if (eventSource.Equals("General", StringComparison.OrdinalIgnoreCase)
            && eventType.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            mapped = StreamerBotEventPayload.TryParse(data, out payload, out rejection);
        }
        else if (eventSource.Equals("Twitch", StringComparison.OrdinalIgnoreCase)
            && eventType.Equals("ChatMessage", StringComparison.OrdinalIgnoreCase))
        {
            mapped = TwitchChatMessageMapper.TryMap(data, out payload, out rejection);
        }
        else if (IsEnabledNotificationEvent(eventSource, eventType))
        {
            // Direct subscription per §B2: no Streamer.bot-side authoring at
            // all, unlike General.Custom above. The template resolver is
            // deliberately generic rather than a mapper per event type - see
            // StreamerBotEventTemplate.
            mapped = TryBuildNotificationPayload(eventSource, eventType, data, out payload, out rejection);
        }
        else
        {
            _log(
                new BridgeActivity(
                    "streamerbot.event_ignored",
                    $"Ignored an unsubscribed Streamer.bot event: {eventSource}.{eventType}",
                    BridgeLogLevel.Debug));
            return;
        }

        if (!mapped)
        {
            _log(
                new BridgeActivity(
                    "streamerbot.event_dropped",
                    $"Dropped a Streamer.bot broadcast: {rejection}.",
                    BridgeLogLevel.Debug));
            return;
        }

        _events.Writer.TryWrite(new StreamerBotEvent(DateTimeOffset.Now, payload!));
    }

    /// <summary>
    /// Asks Streamer.bot what it can emit, right after authenticating and
    /// before subscribing - purely to populate the Notifications tab's event
    /// list and to validate <see cref="NotificationEventSettings.EnabledEvents"/>
    /// against what actually still exists. A fetch failure here must not
    /// take down the feed - it falls back to an empty catalog, which means
    /// only the two events this app always wants get subscribed until the
    /// next successful fetch, and the connection still succeeds.
    /// </summary>
    private async Task<IReadOnlyList<StreamerBotEventDescriptor>> TryFetchEventCatalogAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendRequestAsync(socket, "GetEvents", null, cancellationToken);
            return StreamerBotEventCatalog.Parse(response.RootElement);
        }
        catch (Exception exception)
        {
            _log(
                new BridgeActivity(
                    "streamerbot.events_catalog_failed",
                    $"Could not load the Streamer.bot event list at connect: {exception.Message}. "
                    + "Falling back to General.Custom and Twitch.ChatMessage only.",
                    BridgeLogLevel.Warning));
            return [];
        }
    }

    /// <summary>
    /// The <c>events</c> argument for the <c>Subscribe</c> request: the two
    /// this app always wants, plus whichever of <paramref name="catalog"/>'s
    /// events §B2's toggles enabled.
    /// <para>
    /// Deliberately <b>not</b> "subscribe to everything <paramref name="catalog"/>
    /// reports" - that was tried and rejected live. Streamer.bot exposes no
    /// way to ask which events currently have an enabled trigger (confirmed:
    /// disabling every event in its own Settings > Events panel did not stop
    /// this app receiving them), so a broad subscription pulls in non-alert
    /// plumbing - OBS scene changes and the like - indistinguishable from a
    /// real alert. Filtering has to live here, client-side.
    /// </para>
    /// <para>
    /// General.Custom stays the escape hatch for SB-side-filtered alerts and
    /// notifications - see NotificationOverlay - and Twitch.ChatMessage is
    /// the direct route added per §B6 so chat works with no relay action
    /// required; Streamer.bot still owns the entire platform integration
    /// either way, since this is the parsed output of its own connection,
    /// not anything raw from the platform itself.
    /// </para>
    /// </summary>
    private Dictionary<string, string[]> BuildSubscribeEvents(IReadOnlyList<StreamerBotEventDescriptor> catalog)
    {
        var bySource = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = ["Custom"],
            ["Twitch"] = ["ChatMessage"]
        };

        var enabled = new HashSet<string>(_notificationEvents.EnabledEvents, StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in catalog)
        {
            if (!enabled.Contains(descriptor.Key))
            {
                continue;
            }

            if (!bySource.TryGetValue(descriptor.Source, out var types))
            {
                types = [];
                bySource[descriptor.Source] = types;
            }

            if (!types.Contains(descriptor.Type, StringComparer.OrdinalIgnoreCase))
            {
                types.Add(descriptor.Type);
            }
        }

        return bySource.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether §B2's toggles asked for this event - never true for the two subscribed unconditionally above.</summary>
    private bool IsEnabledNotificationEvent(string source, string type) =>
        _notificationEvents.EnabledEvents.Contains($"{source}.{type}", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a notification payload from a directly-subscribed event by
    /// resolving its configured (or generic default) template against the
    /// event's own <c>data</c> - see <see cref="StreamerBotEventTemplate"/>.
    /// The only rejection case is a test-fired event the wearer has asked not
    /// to see; a malformed or unexpected shape simply resolves its missing
    /// pieces to empty text rather than failing, per the template resolver's
    /// own contract.
    /// </summary>
    private bool TryBuildNotificationPayload(
        string source,
        string type,
        JsonElement data,
        out StreamerBotEventPayload? payload,
        out string rejection)
    {
        payload = null;
        var isTest = data.ValueKind == JsonValueKind.Object
                     && data.TryGetProperty("isTest", out var testFlag)
                     && testFlag.ValueKind == JsonValueKind.True;
        if (isTest && !_notificationEvents.ShowTestEvents)
        {
            rejection = "a test-fired event was suppressed by settings";
            return false;
        }

        var eventLabel = $"{source}.{type}";
        var template = _notificationEvents.Templates.TryGetValue(eventLabel, out var custom)
                        && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : _notificationEvents.DefaultTemplate;

        payload = new StreamerBotEventPayload
        {
            Target = StreamerBotEventTarget.Notification,
            Text = StreamerBotEventTemplate.Resolve(template, data, eventLabel)
        };
        rejection = "";
        return true;
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
