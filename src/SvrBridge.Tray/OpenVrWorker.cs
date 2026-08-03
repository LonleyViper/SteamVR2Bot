using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class OpenVrWorkerSessionFactory : IOpenVrSessionFactory
{
    public Task<IOpenVrSession> ConnectAsync(
        AppConfig config,
        string actionManifest,
        Action<string> log,
        CancellationToken cancellationToken) =>
        OpenVrWorkerSession.StartAsync(
            config,
            actionManifest,
            log,
            cancellationToken);
}

internal sealed partial class OpenVrWorkerSession : IOpenVrSession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _stateGate = new();
    private readonly object _commandGate = new();
    private readonly Process _process;
    private readonly Action<string> _log;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>>
        _commandResults = new();
    private readonly ConcurrentQueue<ShortcutConfig> _createdShortcuts = new();
    private readonly ConcurrentQueue<string> _deletedShortcutIds = new();
    private readonly ConcurrentQueue<VrSettingsSnapshot> _vrSettingsChanges = new();
    private InputSnapshot _snapshot;
    private ControllerSetup _setup = ControllerSetup.Unknown;
    private Exception? _failure;
    private volatile bool _quitRequested;
    private bool _disposed;

    private OpenVrWorkerSession(Process process, Action<string> log)
    {
        _process = process;
        _log = log;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnWorkerExited;
    }

    public static async Task<IOpenVrSession> StartAsync(
        AppConfig config,
        string actionManifest,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(config, actionManifest);
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the SteamVR input worker.");
        }

        var session = new OpenVrWorkerSession(process, log);
        session.BeginReading();
        try
        {
            await session._ready.Task.WaitAsync(cancellationToken);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public InputSnapshot Poll()
    {
        lock (_stateGate)
        {
            ThrowIfUnavailable();
            return _snapshot;
        }
    }

    public ControllerSetup GetControllerSetup()
    {
        lock (_stateGate)
        {
            ThrowIfUnavailable();
            return _setup;
        }
    }

    public void OpenBindingUi()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandResults.TryAdd(requestId, result))
        {
            throw new InvalidOperationException("Could not prepare the SteamVR input command.");
        }

        try
        {
            SendCommand(new OpenVrWorkerCommand("openBindings", requestId));
            var error = result.Task.WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter()
                .GetResult();
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }
        }
        finally
        {
            _commandResults.TryRemove(requestId, out _);
        }
    }

    public void SetTestOverlayEnabled(bool enabled)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandResults.TryAdd(requestId, result))
        {
            throw new InvalidOperationException("Could not prepare the VR test overlay command.");
        }

        try
        {
            SendCommand(new OpenVrWorkerCommand("testOverlay", requestId, Enabled: enabled));
            var error = result.Task.WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter()
                .GetResult();
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }
        }
        finally
        {
            _commandResults.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Fire-and-forget, unlike the other commands: the caller is the event
    /// feed's consumption loop, and waiting here for a SteamVR round trip per
    /// notification would stall it against a backlog the worker's own bounded
    /// queue already exists to absorb.
    /// </summary>
    public void ShowNotification(StreamerBotEventPayload payload) =>
        SendCommand(new OpenVrWorkerCommand("notify", Payload: payload));

    /// <summary>
    /// Fire-and-forget, for the same reason as <see cref="ShowNotification"/>:
    /// the caller is the event feed's consumption loop, and the chat window's
    /// own <see cref="ChatRingBuffer"/> is what absorbs a burst, not this call.
    /// </summary>
    public void ShowChatMessage(StreamerBotEventPayload payload) =>
        SendCommand(new OpenVrWorkerCommand("chat", Payload: payload));

    /// <summary>
    /// Fire-and-forget, for the same reason as <see cref="ShowChatMessage"/>:
    /// the caller is a background fetch in the tray process, not a user
    /// action waiting on a result.
    /// </summary>
    public void SetEmoteCatalog(IReadOnlyDictionary<string, string> catalog) =>
        SendCommand(new OpenVrWorkerCommand("emoteCatalog", EmoteCatalog: catalog));

    /// <summary>
    /// Developer-only probe: turns the chat window's laser input on for a
    /// bounded window that ends by itself. Fire-and-forget - the log records
    /// both ends of it, and the headset is the actual result.
    /// </summary>
    public void StartChatInputProbe() =>
        SendCommand(new OpenVrWorkerCommand("chatInputProbe"));

    /// <summary>
    /// Developer-only override: puts the SteamVR dashboard back on
    /// <c>SetOverlayTexture</c>. Fire-and-forget - the worker logs which path
    /// it is on, and the headset is the actual result.
    /// </summary>
    public void SetDashboardTexturePathEnabled(bool enabled) =>
        SendCommand(new OpenVrWorkerCommand("dashboardTexturePath", Enabled: enabled));

    /// <summary>
    /// Developer-only override: switches the chat window, notifications and
    /// the VR test overlay onto the persistent-texture path, together - off
    /// by default. Fire-and-forget, for the same reason as
    /// <see cref="SetDashboardTexturePathEnabled"/>.
    /// </summary>
    public void SetOverlayTexturePathEnabled(bool enabled) =>
        SendCommand(new OpenVrWorkerCommand("overlayTexturePath", Enabled: enabled));

    /// <summary>
    /// Fire-and-forget, for the same reason as <see cref="SetEmoteCatalog"/>:
    /// the caller (<c>TrayApplicationContext.SaveAndApplySettingsAsync</c>)
    /// only sends this when it has already decided a restart is unnecessary,
    /// so there is no result to wait on here - the worker's own
    /// <c>"vrSettingsChanged"</c> echo back is what confirms it landed.
    /// </summary>
    public void ApplySettingsChange(VrSettingsSnapshot settings) =>
        SendCommand(new OpenVrWorkerCommand("applySettings", VrSettings: settings));

    /// <summary>
    /// Fire-and-forget, for the same reason as <see cref="ShowChatMessage"/>:
    /// the caller is the event feed's consumption loop, and the worker's own
    /// <see cref="SurfaceOverrideState"/> per surface is what remembers the
    /// resulting override, not this call.
    /// </summary>
    public void ApplyControlCommand(StreamerBotEventPayload payload) =>
        SendCommand(new OpenVrWorkerCommand("control", Payload: payload));

    public void ShowDashboard(
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate = true)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandResults.TryAdd(requestId, result))
        {
            throw new InvalidOperationException("Could not prepare the SteamVR dashboard.");
        }

        try
        {
            SendCommand(
                new OpenVrWorkerCommand(
                    "showDashboard",
                    requestId,
                    shortcuts,
                    actions,
                    activate));
            var error = result.Task.WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter()
                .GetResult();
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }
        }
        finally
        {
            _commandResults.TryRemove(requestId, out _);
        }
    }

    public IReadOnlyList<ShortcutConfig> DrainCreatedShortcuts()
    {
        var result = new List<ShortcutConfig>();
        while (_createdShortcuts.TryDequeue(out var shortcut))
        {
            result.Add(shortcut);
        }

        return result;
    }

    public bool IsQuitRequested() => _quitRequested;

    public IReadOnlyList<string> DrainDeletedShortcutIds()
    {
        var result = new List<string>();
        while (_deletedShortcutIds.TryDequeue(out var shortcutId))
        {
            result.Add(shortcutId);
        }

        return result;
    }

    /// <summary>Drains settings changes made from the VR settings page - see <c>OpenVrWorker.ApplyVrSettingsChange</c>.</summary>
    public IReadOnlyList<VrSettingsSnapshot> DrainVrSettingsChanges()
    {
        var result = new List<VrSettingsSnapshot>();
        while (_vrSettingsChanges.TryDequeue(out var settings))
        {
            result.Add(settings);
        }

        return result;
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            if (!_process.HasExited)
            {
                lock (_commandGate)
                {
                    _process.StandardInput.WriteLine(
                        JsonSerializer.Serialize(
                            new OpenVrWorkerCommand("shutdown"),
                            JsonOptions));
                    _process.StandardInput.Flush();
                }

                if (!_process.WaitForExit(1000))
                {
                    _process.Kill(entireProcessTree: false);
                    _process.WaitForExit(1000);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // SteamVR may have already terminated the worker.
        }
        finally
        {
            lock (_stateGate)
            {
                _disposed = true;
            }

            foreach (var result in _commandResults.Values)
            {
                result.TrySetException(
                    new ObjectDisposedException(nameof(OpenVrWorkerSession)));
            }

            _process.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        AppConfig config,
        string actionManifest)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException(
                              "Could not locate the SteamVR2Bot executable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException(
                    "Could not locate the SteamVR2Bot worker assembly.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--openvr-worker");
        startInfo.ArgumentList.Add("--action-manifest");
        startInfo.ArgumentList.Add(actionManifest);
        startInfo.ArgumentList.Add("--poll-interval");
        startInfo.ArgumentList.Add(config.PollIntervalMs.ToString());
        // Baked in at spawn rather than pushed at runtime, so an anchor
        // change from the desktop takes effect on the next worker restart -
        // the same as an address or password change already does. See
        // AppConfig.ChatAnchor.
        startInfo.ArgumentList.Add("--chat-anchor-mode");
        startInfo.ArgumentList.Add(((int)config.ChatAnchor.Mode).ToString());
        startInfo.ArgumentList.Add("--chat-anchor-hand");
        startInfo.ArgumentList.Add(((int)config.ChatAnchor.Hand).ToString());
        startInfo.ArgumentList.Add("--notification-anchor-mode");
        startInfo.ArgumentList.Add(((int)config.NotificationAnchor.Mode).ToString());
        startInfo.ArgumentList.Add("--notification-anchor-hand");
        startInfo.ArgumentList.Add(((int)config.NotificationAnchor.Hand).ToString());
        // One argument rather than six numbers, so a placement can never be
        // half-applied by a partially-updated spawn - see
        // OverlayPlacement.ToArgument.
        startInfo.ArgumentList.Add("--chat-placement");
        startInfo.ArgumentList.Add(config.ChatPlacement.ToArgument());
        // Same "baked in at spawn" reasoning as the anchor args above - see
        // AppConfig.ChatOpacity.
        startInfo.ArgumentList.Add("--chat-enabled");
        startInfo.ArgumentList.Add(config.ChatEnabled.ToString());
        startInfo.ArgumentList.Add("--notifications-enabled");
        startInfo.ArgumentList.Add(config.NotificationsEnabled.ToString());
        startInfo.ArgumentList.Add("--chat-opacity");
        startInfo.ArgumentList.Add(config.ChatOpacity.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--chat-size-scale");
        startInfo.ArgumentList.Add(config.ChatSizeScale.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--gaze-sensitivity");
        startInfo.ArgumentList.Add(((int)config.GazeSensitivity).ToString());
        startInfo.ArgumentList.Add("--chat-gaze-scale");
        startInfo.ArgumentList.Add(config.ChatGazeScaleEnabled.ToString());
        startInfo.ArgumentList.Add("--chat-gaze-reference");
        startInfo.ArgumentList.Add(OpenVrWorker.SerialiseGazeReference(config.ChatGazeReference));
        startInfo.ArgumentList.Add("--notification-opacity");
        startInfo.ArgumentList.Add(config.NotificationOpacity.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--notification-size-scale");
        startInfo.ArgumentList.Add(config.NotificationSizeScale.ToString(CultureInfo.InvariantCulture));
        // One argument rather than twelve numbers, same reasoning as
        // --chat-placement above.
        startInfo.ArgumentList.Add("--notification-placement");
        startInfo.ArgumentList.Add(config.NotificationPlacement.ToArgument());
        // One JSON argument rather than seven flags, for the same reason -
        // an appearance bundle can never be half-applied by a
        // partially-updated spawn. See NotificationAppearanceSettings.
        startInfo.ArgumentList.Add("--notification-appearance");
        startInfo.ArgumentList.Add(JsonSerializer.Serialize(config.NotificationAppearance));
        if (!string.IsNullOrWhiteSpace(config.OpenVrDllPath))
        {
            startInfo.ArgumentList.Add("--openvr-dll");
            startInfo.ArgumentList.Add(config.OpenVrDllPath);
        }

        return startInfo;
    }

    private void BeginReading()
    {
        _ = Task.Run(ReadMessagesAsync);
        _ = Task.Run(ReadErrorsAsync);
    }

    private async Task ReadMessagesAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                OpenVrWorkerMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<OpenVrWorkerMessage>(
                        line,
                        JsonOptions);
                }
                catch (JsonException exception)
                {
                    SetFailure(
                        new InvalidDataException(
                            "SteamVR input worker returned invalid data.",
                            exception));
                    return;
                }

                if (message is null)
                {
                    continue;
                }

                HandleMessage(message);
            }
        }
        catch (Exception exception) when (!_disposed)
        {
            SetFailure(exception);
        }
    }

    private async Task ReadErrorsAsync()
    {
        string? crashLine = null;
        while (await _process.StandardError.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                _log($"SteamVR input worker: {line}");
            }

            // .NET's default unhandled-exception handler writes exactly this
            // prefix before the crashing exception's own ToString() - the
            // one reliable signal on this stream that the worker hit a bug
            // that will recur identically on the next attempt, rather than
            // SteamVR merely closing the pipe on it.
            if (crashLine is null
                && line.StartsWith("Unhandled exception.", StringComparison.Ordinal))
            {
                crashLine = line;
            }
        }

        // Waiting for end-of-stream rather than reacting to the line the
        // instant it arrives, and rather than relying on Process.Exited's
        // timing relative to output draining (undocumented, and not worth
        // depending on): the pipe cannot reach EOF until every byte the
        // worker wrote has been delivered here, so this is the point the
        // capture above is known to be complete.
        if (crashLine is not null)
        {
            SetFailure(new WorkerCrashedException(DescribeCrash(crashLine)));
        }
    }

    /// <summary>
    /// Turns the worker's crash line into something a user can act on.
    /// Internal rather than private so TraySelfTests can prove both the
    /// named-DLL and generic-fallback wording without spawning a real
    /// worker process.
    /// </summary>
    internal static string DescribeCrash(string crashLine)
    {
        if (crashLine.Contains("DllNotFoundException", StringComparison.Ordinal))
        {
            var dllMatch = DllNameInMessage().Match(crashLine);
            var component = dllMatch.Success
                ? dllMatch.Groups[1].Value
                : "one of its required native components";
            return $"SteamVR2Bot could not load {component} and cannot continue. " +
                   "This normally means SteamVR2Bot.exe is running without the rest " +
                   "of its published folder - run it from inside the complete " +
                   "extracted publish folder, not moved out on its own.";
        }

        return "The SteamVR input worker crashed and cannot continue: " +
               crashLine["Unhandled exception. ".Length..];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"'([^']+\.dll)'")]
    private static partial System.Text.RegularExpressions.Regex DllNameInMessage();

    private void HandleMessage(OpenVrWorkerMessage message)
    {
        switch (message.Kind)
        {
            case "ready":
                lock (_stateGate)
                {
                    _snapshot = message.Snapshot ?? default;
                    _setup = message.Setup ?? ControllerSetup.Unknown;
                }

                _log("SteamVR input worker connected.");
                _ready.TrySetResult();
                break;
            case "snapshot":
                if (message.Snapshot is { } snapshot)
                {
                    lock (_stateGate)
                    {
                        _snapshot = snapshot;
                    }
                }

                break;
            case "setup":
                if (message.Setup is { } setup)
                {
                    lock (_stateGate)
                    {
                        _setup = setup;
                    }
                }

                break;
            case "log":
                if (!string.IsNullOrWhiteSpace(message.Message))
                {
                    _log(message.Message);
                }

                break;
            case "commandResult":
                if (message.RequestId is not null
                    && _commandResults.TryGetValue(message.RequestId, out var result))
                {
                    result.TrySetResult(message.Error);
                }

                break;
            case "quit":
                // The worker exits immediately after sending this, so record it
                // before the resulting process-exit failure lands.
                _quitRequested = true;
                _log("SteamVR asked SteamVR2Bot to close.");
                break;
            case "failure":
                SetFailure(
                    new InvalidOperationException(
                        message.Error ?? "SteamVR input worker failed."));
                break;
            case "shortcutCreated":
                if (message.ShortcutCreated is { } shortcut)
                {
                    _createdShortcuts.Enqueue(shortcut);
                }

                break;
            case "shortcutDeleted":
                if (!string.IsNullOrWhiteSpace(message.ShortcutDeletedId))
                {
                    _deletedShortcutIds.Enqueue(message.ShortcutDeletedId);
                }

                break;
            case "vrSettingsChanged":
                if (message.VrSettingsChanged is { } vrSettings)
                {
                    _vrSettingsChanges.Enqueue(vrSettings);
                }

                break;
        }
    }

    private void OnWorkerExited(object? sender, EventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        int? exitCode = null;
        try
        {
            exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // The process exit raced this notification.
        }

        SetFailure(
            new InvalidOperationException(
                exitCode is null
                    ? "SteamVR closed the input worker."
                    : $"SteamVR input worker exited with code {exitCode}."));
    }

    private void SetFailure(Exception exception)
    {
        lock (_stateGate)
        {
            _failure ??= exception;
        }

        _ready.TrySetException(exception);
        foreach (var result in _commandResults.Values)
        {
            result.TrySetException(exception);
        }
    }

    private void SendCommand(OpenVrWorkerCommand command)
    {
        lock (_commandGate)
        {
            lock (_stateGate)
            {
                ThrowIfUnavailable();
            }

            _process.StandardInput.WriteLine(
                JsonSerializer.Serialize(command, JsonOptions));
            _process.StandardInput.Flush();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null)
        {
            throw new InvalidOperationException(
                "SteamVR input worker is unavailable.",
                _failure);
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException("SteamVR input worker has stopped.");
        }
    }
}

internal static class OpenVrWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var actionManifest = GetArgumentValue(args, "--action-manifest")
                                 ?? throw new InvalidDataException(
                                     "The SteamVR input worker needs an action manifest.");
            var openVrDll = GetArgumentValue(args, "--openvr-dll");
            var pollInterval = int.TryParse(
                GetArgumentValue(args, "--poll-interval"),
                out var parsedInterval)
                ? Math.Clamp(parsedInterval, 1, 1000)
                : 10;
            var chatDefaultAnchor = new OverlayAnchor(
                ParseEnumArgument(
                    GetArgumentValue(args, "--chat-anchor-mode"),
                    OverlayAnchorMode.Controller),
                ParseEnumArgument(
                    GetArgumentValue(args, "--chat-anchor-hand"),
                    OverlayAnchorHand.Left));
            var notificationDefaultAnchor = new OverlayAnchor(
                ParseEnumArgument(
                    GetArgumentValue(args, "--notification-anchor-mode"),
                    OverlayAnchorMode.Head),
                ParseEnumArgument(
                    GetArgumentValue(args, "--notification-anchor-hand"),
                    OverlayAnchorHand.Left));
            // Transient overrides a Streamer.bot control command places on
            // top of the saved defaults above - see SurfaceOverrideState's
            // own remarks for why the saved default itself is never mutated
            // here. Remembered even before either overlay exists (a "hide"
            // sent ahead of the first chat message should still apply once
            // it is created), the same reason latestEmoteCatalog below exists.
            var chatOverride = new SurfaceOverrideState(chatDefaultAnchor);
            var notificationOverride = new SurfaceOverrideState(notificationDefaultAnchor);

            // Opacity, size and gaze sensitivity have no Streamer.bot
            // override mechanism (Phase 4's control commands only cover
            // anchor/show/hide/clear) - only the VR settings page and the
            // desktop can change them, so plain mutable locals are enough;
            // there is no "saved default vs transient override" distinction
            // to track for these the way SurfaceOverrideState tracks anchor.
            // The chat window's own placement is the one setting this worker
            // is authoritative for between restarts: the wearer drags it in
            // the headset, so it changes here first and is reported back to
            // the tray for persistence, rather than only ever arriving from
            // there. See OnChatPlacementDragged.
            var chatPlacement = OverlayPlacement.Parse(GetArgumentValue(args, "--chat-placement"));
            var chatEnabled = ParseBoolArgument(GetArgumentValue(args, "--chat-enabled"));
            var chatOpacity = ParseDoubleArgument(GetArgumentValue(args, "--chat-opacity"), 0.95);
            var chatSizeScale = ParseDoubleArgument(GetArgumentValue(args, "--chat-size-scale"), 1.0);
            var gazeSensitivity = ParseEnumArgument(
                GetArgumentValue(args, "--gaze-sensitivity"),
                GazeSensitivity.Normal);
            var chatGazeScaleEnabled = ParseBoolArgument(GetArgumentValue(args, "--chat-gaze-scale"));
            var chatGazeReference = ParseGazeReferenceArgument(
                GetArgumentValue(args, "--chat-gaze-reference"));
            var notificationsEnabled = ParseBoolArgument(GetArgumentValue(args, "--notifications-enabled"));
            var notificationOpacity = ParseDoubleArgument(GetArgumentValue(args, "--notification-opacity"), 1.0);
            var notificationSizeScale =
                ParseDoubleArgument(GetArgumentValue(args, "--notification-size-scale"), 1.0);
            var notificationPlacement =
                OverlayPlacement.Parse(GetArgumentValue(args, "--notification-placement"));
            var notificationAppearance =
                ParseNotificationAppearanceArgument(GetArgumentValue(args, "--notification-appearance"));

            using var openVr = new OpenVrInput(
                openVrDll,
                actionManifest,
                message => Emit(new OpenVrWorkerMessage("log", Message: message)));
            var commands = Channel.CreateUnbounded<OpenVrWorkerCommand>();
            VrDashboardController? dashboard = null;
            // Off unless the user turns it on from the tray menu, and off again
            // on every worker restart. It is a development aid.
            VrTestOverlay? testOverlay = null;
            // Created lazily by the first "notify" command rather than here,
            // so a worker that never receives one never reserves the overlay
            // key or stands up a render thread for nothing.
            NotificationOverlay? notificationOverlay = null;
            // Created lazily by the first "chat" command, for the same
            // reason - and gated behind a user setting one layer up, so most
            // workers never create this at all.
            ChatOverlay? chatOverlay = null;
            // An "emoteCatalog" command can arrive before the first chat
            // message does (it is fetched as soon as the event stream
            // connects), i.e. before chatOverlay exists to receive it. Kept
            // here so it can be applied the moment chatOverlay is created,
            // rather than being silently lost.
            IReadOnlyDictionary<string, string>? latestEmoteCatalog = null;
            // One Direct3D device for every overlay in this worker - the
            // dashboard, chat, notifications and the test overlay. Created
            // eagerly rather than on first upload so its own log line lands at
            // start-up, where a machine with no usable GPU is worth noticing,
            // rather than in the middle of the first chat burst. Textures stay
            // per-overlay; only the device is shared.
            using var textureDevice = new D3D11OverlayDevice(
                message => Emit(new OpenVrWorkerMessage("log", Message: message)));

            // Off unless a developer opts in from the tray menu - see the
            // "overlayTexturePath" command below. Read by every overlay's
            // TryCreate below, and re-applied to whichever of the three
            // already exist so opting in also affects surfaces created before
            // the toggle was flipped.
            var overlayTexturePathEnabled = false;

            // Shared by the "notify"/"chat" command handlers (lazy, on first
            // message - unchanged from before Phase 4b) and the VR settings
            // page's on/off toggle (eager, so the wearer sees the surface
            // appear immediately without leaving the page - see §B4 of the
            // Phase 4b plan). A no-op if the overlay already exists either way.
            void EnsureNotificationOverlay()
            {
                if (notificationOverlay is not null)
                {
                    return;
                }

                notificationOverlay = NotificationOverlay.TryCreate(
                    openVr,
                    textureDevice,
                    notificationOverride.EffectiveAnchor,
                    notificationPlacement,
                    notificationOpacity,
                    notificationSizeScale,
                    notificationAppearance,
                    OnNotificationPlacementDragged,
                    message => Emit(new OpenVrWorkerMessage("log", Message: message)));
                if (notificationOverlay is not null)
                {
                    // Needed for the §B1 positioning frame's grab-to-place -
                    // TryGetDevicePose reads nothing unless this is on, which
                    // otherwise only EnsureChatOverlay ever set. Without it, a
                    // grab silently reads an untracked pose from both
                    // devices and reports "a controller is not tracking"
                    // regardless of whether one actually is.
                    openVr.MotionSamplingEnabled = true;
                    notificationOverlay.SetTexturePathEnabled(overlayTexturePathEnabled);
                    if (notificationOverride.Hidden)
                    {
                        notificationOverlay.SetHidden(true);
                    }
                }
            }

            // Turns the notification positioning frame on or off - §B1 of
            // the Phase 7 plan. Called directly from VrDashboardController's
            // click handling, since that controller already runs on this
            // same thread inside this same process - no worker command or
            // IPC round trip needed, the same reasoning ApplyVrSettingsChange
            // already relies on for a VR-page edit.
            void SetNotificationPositioningEnabled(bool enabled)
            {
                // Forced into existence even if the wearer never turned
                // notifications on: positioning them ahead of enabling them
                // is legitimate, and the toggle is not gated behind that
                // setting.
                EnsureNotificationOverlay();
                notificationOverlay?.SetPositioningEnabled(enabled);
            }

            void EnsureChatOverlay()
            {
                if (chatOverlay is not null)
                {
                    return;
                }

                chatOverlay = ChatOverlay.TryCreate(
                    openVr,
                    textureDevice,
                    chatOverride.EffectiveAnchor,
                    chatPlacement,
                    chatOpacity,
                    chatSizeScale,
                    gazeSensitivity,
                    chatGazeScaleEnabled,
                    chatGazeReference,
                    OnChatPlacementDragged,
                    OnChatGazeCalibrated,
                    message => Emit(new OpenVrWorkerMessage("log", Message: message)));

                if (chatOverlay is not null)
                {
                    chatOverlay.SetTexturePathEnabled(overlayTexturePathEnabled);
                    if (chatOverride.Hidden)
                    {
                        chatOverlay.SetHidden(true);
                    }

                    // Poses are needed for gaze detection from the moment the
                    // window exists; left off until then so a worker that
                    // never creates this overlay never pays for pose
                    // sampling either.
                    openVr.MotionSamplingEnabled = true;

                    // Applies a catalog that may have arrived before this
                    // overlay existed to receive it - see latestEmoteCatalog
                    // above.
                    if (latestEmoteCatalog is not null)
                    {
                        chatOverlay.SetEmoteCatalog(latestEmoteCatalog);
                    }

                }
            }

            // This worker's own live state, which is always at least as fresh
            // as anything the tray could send: a VR settings edit updates it
            // immediately, a hand drag updates it immediately, and a desktop
            // edit only ever reaches this worker as a live "applySettings"
            // command or a full restart with new spawn args.
            VrSettingsSnapshot CurrentVrSettings() => new(
                chatEnabled,
                chatOverride.SavedDefault,
                chatPlacement,
                chatOpacity,
                chatSizeScale,
                gazeSensitivity,
                chatGazeScaleEnabled,
                chatGazeReference,
                notificationsEnabled,
                notificationOverride.SavedDefault,
                notificationOpacity,
                notificationSizeScale,
                notificationPlacement,
                notificationAppearance);

            // The wearer let go of the chat window's move handle. The overlay
            // has already been sitting at this offset for the whole drag, so
            // there is nothing to apply here - only to remember and report.
            void OnChatPlacementDragged(OverlayPlacement placement)
            {
                chatPlacement = placement;
                chatGazeReference = GazeReference.None;
                chatOverlay?.SetGazeReference(chatGazeReference);
                // Placing the window by hand is as explicit a user edit as
                // changing the anchor control on the settings page, so it
                // takes the same side of the §B5 rule and clears any active
                // Streamer.bot anchor override. Without this, a later "reset"
                // command could move a hand-placed window to a different
                // device and leave its offset behind.
                chatOverride.AdoptEffectiveAnchorAsSavedDefault();
                var settings = CurrentVrSettings();
                // The settings page holds its own copy and would otherwise
                // keep offering a reset for a window it still believed was at
                // the default. Guarded because this runs inside the chat
                // window's own Tick, whose caller responds to an exception by
                // turning chat off - a dashboard repaint failing must not
                // cost the wearer the window they were just moving.
                try
                {
                    dashboard?.UpdateSettings(settings);
                }
                catch (Exception exception)
                {
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: "The VR settings page could not be refreshed after the chat "
                                     + $"window was moved: {exception.Message}"));
                }

                Emit(new OpenVrWorkerMessage("vrSettingsChanged", VrSettingsChanged: settings));
            }

            void OnChatGazeCalibrated(GazeReference reference)
            {
                chatGazeReference = reference;
                var settings = CurrentVrSettings();
                // This callback runs inside ChatOverlay.Tick. A dashboard
                // repaint failure must not bubble out and disable chat, and
                // completion needs one texture replacement rather than an
                // UpdateSettings repaint followed by a second repaint merely
                // to clear the transient “Calibrating…” label.
                try
                {
                    dashboard?.OnChatGazeCalibrationFinished(settings);
                }
                catch (Exception exception)
                {
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: "The VR settings page could not be refreshed after gaze "
                                     + $"calibration: {exception.Message}"));
                }

                Emit(new OpenVrWorkerMessage("vrSettingsChanged", VrSettingsChanged: settings));
            }

            // The wearer let go of the notification positioning frame's grab
            // - see OnChatPlacementDragged, which this mirrors exactly.
            void OnNotificationPlacementDragged(OverlayPlacement placement)
            {
                notificationPlacement = placement;
                notificationOverride.AdoptEffectiveAnchorAsSavedDefault();
                var settings = CurrentVrSettings();
                try
                {
                    dashboard?.UpdateSettings(settings);
                }
                catch (Exception exception)
                {
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: "The VR settings page could not be refreshed after notifications "
                                     + $"were moved: {exception.Message}"));
                }

                Emit(new OpenVrWorkerMessage("vrSettingsChanged", VrSettingsChanged: settings));
            }

            // Applies a change reported by either settings surface live, in
            // this same process and thread, and reports it onward to the tray
            // for persistence. A VR-originated click has already updated and
            // repainted its controller state. A desktop-originated command
            // asks this function to refresh that controller from the applied
            // values so its next click cannot send a stale snapshot back.
            void ApplyVrSettingsChange(
                VrSettingsSnapshot newSettings,
                bool refreshDashboard = false)
            {
                if (newSettings.ChatEnabled != chatEnabled)
                {
                    chatEnabled = newSettings.ChatEnabled;
                    if (chatEnabled)
                    {
                        EnsureChatOverlay();
                    }
                    else
                    {
                        chatOverlay?.Dispose();
                        chatOverlay = null;
                    }
                }

                if (newSettings.NotificationsEnabled != notificationsEnabled)
                {
                    notificationsEnabled = newSettings.NotificationsEnabled;
                    if (notificationsEnabled)
                    {
                        EnsureNotificationOverlay();
                    }
                    else
                    {
                        notificationOverlay?.Dispose();
                        notificationOverlay = null;
                    }
                }

                chatOpacity = newSettings.ChatOpacity;
                chatSizeScale = newSettings.ChatSizeScale;
                gazeSensitivity = newSettings.GazeSensitivity;
                chatGazeScaleEnabled = newSettings.ChatGazeScaleEnabled;
                chatGazeReference = newSettings.ChatGazeReference.IsUsable
                    ? newSettings.ChatGazeReference
                    : GazeReference.None;
                notificationOpacity = newSettings.NotificationOpacity;
                notificationSizeScale = newSettings.NotificationSizeScale;
                // Normally unchanged - the settings page's only control for
                // this is the reset button - but applied the same way as
                // everything else so that reset lands live, without the wearer
                // having to take the headset off to see it.
                if (!chatPlacement.Equals(newSettings.ChatPlacement))
                {
                    chatPlacement = newSettings.ChatPlacement;
                    chatOverlay?.SetPlacement(chatPlacement);
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: chatPlacement.Equals(OverlayPlacement.Default)
                                ? "The chat window went back to its default position."
                                : "The chat window moved to a saved position."));
                }

                // Same reasoning as the chat placement block above - normally
                // unchanged, but applied the same way as everything else so a
                // reset from the Notifications tab lands live.
                if (!notificationPlacement.Equals(newSettings.NotificationPlacement))
                {
                    notificationPlacement = newSettings.NotificationPlacement;
                    notificationOverlay?.SetPlacement(notificationPlacement);
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: notificationPlacement.Equals(OverlayPlacement.Default)
                                ? "Notifications went back to their default position."
                                : "Notifications moved to a saved position."));
                }

                notificationAppearance = newSettings.NotificationAppearance;

                chatOverlay?.SetOpacity(chatOpacity);
                chatOverlay?.SetSizeScale(chatSizeScale);
                chatOverlay?.SetGazeSensitivity(gazeSensitivity);
                chatOverlay?.SetGazeScaleEnabled(chatGazeScaleEnabled);
                chatOverlay?.SetGazeReference(chatGazeReference);
                notificationOverlay?.SetOpacity(notificationOpacity);
                notificationOverlay?.SetSizeScale(notificationSizeScale);
                notificationOverlay?.ApplyAppearance(notificationAppearance);

                // SetSavedDefaultAnchor also clears any active Streamer.bot
                // anchor override - an explicit VR edit wins, per §B5 of the
                // Phase 4b plan.
                chatOverride.SetSavedDefaultAnchor(newSettings.ChatAnchor);
                chatOverlay?.SetAnchorOverride(chatOverride.EffectiveAnchor);
                notificationOverride.SetSavedDefaultAnchor(newSettings.NotificationAnchor);
                notificationOverlay?.SetAnchorOverride(notificationOverride.EffectiveAnchor);

                var appliedSettings = CurrentVrSettings();
                if (refreshDashboard)
                {
                    try
                    {
                        dashboard?.UpdateSettings(appliedSettings);
                    }
                    catch (Exception exception)
                    {
                        Emit(
                            new OpenVrWorkerMessage(
                                "log",
                                Message: "The VR settings page could not be refreshed after a desktop "
                                         + $"settings change: {exception.Message}"));
                    }
                }

                Emit(new OpenVrWorkerMessage("vrSettingsChanged", VrSettingsChanged: appliedSettings));
            }

            _ = Task.Run(() => ReadCommandsAsync(commands.Writer));

            var snapshot = openVr.Poll();
            var setup = openVr.GetControllerSetup();
            Emit(new OpenVrWorkerMessage("ready", snapshot, setup));
            var setupSignature = SetupSignature(setup);
            var nextSetupRefresh = Stopwatch.GetTimestamp()
                                   + (long)(Stopwatch.Frequency * 2d);

            while (true)
            {
                while (commands.Reader.TryRead(out var command))
                {
                    if (command.Kind == "shutdown")
                    {
                        return 0;
                    }

                    if (command.Kind == "openBindings")
                    {
                        string? error = null;
                        try
                        {
                            openVr.OpenBindingUi();
                        }
                        catch (Exception exception)
                        {
                            error = exception.Message;
                        }

                        Emit(
                            new OpenVrWorkerMessage(
                                "commandResult",
                                RequestId: command.RequestId,
                                Error: error));
                    }

                    if (command.Kind == "notify" && command.Payload is { } notification)
                    {
                        try
                        {
                            EnsureNotificationOverlay();
                            notificationOverlay?.Enqueue(notification);
                        }
                        catch (Exception exception)
                        {
                            // Fire-and-forget: there is no requester waiting on
                            // a commandResult, so the only way to report this
                            // is the log, and it must not take the worker down.
                            Emit(
                                new OpenVrWorkerMessage(
                                    "log",
                                    Message: $"A notification could not be shown: {exception.Message}"));
                        }
                    }

                    if (command.Kind == "chat" && command.Payload is { } chatMessage)
                    {
                        try
                        {
                            EnsureChatOverlay();
                            chatOverlay?.Enqueue(chatMessage);
                        }
                        catch (Exception exception)
                        {
                            // Fire-and-forget, for the same reason as "notify"
                            // above: there is no requester waiting on this.
                            Emit(
                                new OpenVrWorkerMessage(
                                    "log",
                                    Message: $"A chat message could not be shown: {exception.Message}"));
                        }
                    }

                    if (command.Kind == "chatInputProbe")
                    {
                        // Environment.TickCount64, matching the clock Tick is
                        // called with below - the probe's expiry compares the
                        // two, so a different clock here would never expire it
                        // or would expire it instantly.
                        // Only meaningful once the chat window exists; there is
                        // nothing to point a controller at before that.
                        chatOverlay?.StartInputProbe(Environment.TickCount64);
                    }

                    if (command.Kind == "dashboardTexturePath")
                    {
                        // Developer-only override, for re-checking the
                        // dashboard-overlay finding after a SteamVR update.
                        dashboard?.SetTexturePathEnabled(command.Enabled);
                    }

                    if (command.Kind == "overlayTexturePath")
                    {
                        // Developer-only opt-in, off by default - see
                        // overlayTexturePathEnabled above. Applied to
                        // whichever of the three already exist; the rest pick
                        // it up from that same variable when EnsureXOverlay
                        // or the "testOverlay" command creates them.
                        overlayTexturePathEnabled = command.Enabled;
                        chatOverlay?.SetTexturePathEnabled(command.Enabled);
                        notificationOverlay?.SetTexturePathEnabled(command.Enabled);
                        testOverlay?.SetTexturePathEnabled(command.Enabled);
                        Emit(
                            new OpenVrWorkerMessage(
                                "log",
                                Message: command.Enabled
                                    ? "Chat, notifications and the test overlay are using SetOverlayTexture (developer override)."
                                    : "Chat, notifications and the test overlay are using SetOverlayRaw."));
                    }

                    if (command.Kind == "applySettings" && command.VrSettings is { } desktopSettings)
                    {
                        // A desktop-made appearance/anchor/enable change,
                        // pushed the opposite direction from a VR settings-
                        // page edit but applied through the exact same
                        // function - see ApplyVrSettingsChange. Lets the
                        // tray avoid a full worker restart for a change that
                        // does not need one; TrayApplicationContext only
                        // sends this when nothing else changed.
                        try
                        {
                            ApplyVrSettingsChange(desktopSettings, refreshDashboard: true);
                        }
                        catch (Exception exception)
                        {
                            Emit(
                                new OpenVrWorkerMessage(
                                    "log",
                                    Message: $"A desktop settings change could not be applied: {exception.Message}"));
                        }
                    }

                    if (command.Kind == "emoteCatalog" && command.EmoteCatalog is { } emoteCatalog)
                    {
                        // Recorded even when chatOverlay does not exist yet -
                        // see latestEmoteCatalog above - and applied
                        // immediately when it does. No commandResult and no
                        // try/catch beyond what SetEmoteCatalog itself
                        // already guards internally: this only ever replaces
                        // an in-memory lookup, it cannot fail in a way worth
                        // reporting.
                        latestEmoteCatalog = emoteCatalog;
                        chatOverlay?.SetEmoteCatalog(emoteCatalog);
                    }

                    if (command.Kind == "control" && command.Payload is { } controlPayload)
                    {
                        try
                        {
                            ApplyControlCommand(controlPayload);
                        }
                        catch (Exception exception)
                        {
                            Emit(
                                new OpenVrWorkerMessage(
                                    "log",
                                    Message: $"A control command could not be applied: {exception.Message}"));
                        }

                        void ApplyControlCommand(StreamerBotEventPayload payload)
                        {
                            var command = payload.Command.Trim().ToLowerInvariant();
                            var isChat = payload.Surface != ControlSurface.Notifications;
                            var surfaceName = isChat ? "chat" : "notifications";

                            if (command == "clear")
                            {
                                // A one-shot action against a surface's own
                                // backlog, not a lasting override - see
                                // SurfaceOverrideState's own remarks.
                                if (isChat)
                                {
                                    chatOverlay?.ClearMessages();
                                }
                                else
                                {
                                    notificationOverlay?.ClearQueue();
                                }

                                return;
                            }

                            if (command is not ("show" or "hide" or "anchor" or "reset"))
                            {
                                Emit(
                                    new OpenVrWorkerMessage(
                                        "log",
                                        Message: $"An unrecognised control command (\"{payload.Command}\") "
                                                 + $"for {surfaceName} was ignored."));
                                return;
                            }

                            if (command == "anchor" && payload.RequestedAnchorMode is null)
                            {
                                Emit(
                                    new OpenVrWorkerMessage(
                                        "log",
                                        Message: $"An anchor control command for {surfaceName} was ignored: "
                                                 + "no valid mode was given."));
                                return;
                            }

                            var state = isChat ? chatOverride : notificationOverride;
                            state.Apply(payload);
                            if (isChat)
                            {
                                chatOverlay?.SetAnchorOverride(state.EffectiveAnchor);
                                chatOverlay?.SetHidden(state.Hidden);
                            }
                            else
                            {
                                notificationOverlay?.SetAnchorOverride(state.EffectiveAnchor);
                                notificationOverlay?.SetHidden(state.Hidden);
                            }
                        }
                    }

                    if (command.Kind == "testOverlay")
                    {
                        string? error = null;
                        try
                        {
                            testOverlay?.Dispose();
                            testOverlay = null;
                            if (command.Enabled)
                            {
                                testOverlay = VrTestOverlay.TryCreate(
                                    openVr,
                                    textureDevice,
                                    message => Emit(
                                        new OpenVrWorkerMessage("log", Message: message)));
                                testOverlay?.SetTexturePathEnabled(overlayTexturePathEnabled);
                            }
                            else
                            {
                                Emit(
                                    new OpenVrWorkerMessage(
                                        "log",
                                        Message: "The VR test overlay is off."));
                            }
                        }
                        catch (Exception exception)
                        {
                            error = exception.Message;
                        }

                        Emit(
                            new OpenVrWorkerMessage(
                                "commandResult",
                                RequestId: command.RequestId,
                                Error: error));
                    }

                    if (command.Kind == "showDashboard")
                    {
                        string? error = null;
                        try
                        {
                            // Built from this worker's own live state, not
                            // anything passed on the command - see
                            // CurrentVrSettings.
                            var vrSettings = CurrentVrSettings();

                            dashboard = new VrDashboardController(
                                openVr,
                                textureDevice,
                                command.Shortcuts ?? [],
                                command.Actions ?? [],
                                command.Activate,
                                vrSettings,
                                shortcut => Emit(
                                    new OpenVrWorkerMessage(
                                        "shortcutCreated",
                                        ShortcutCreated: shortcut)),
                                shortcutId => Emit(
                                    new OpenVrWorkerMessage(
                                        "shortcutDeleted",
                                        ShortcutDeletedId: shortcutId)),
                                settings => ApplyVrSettingsChange(settings),
                                SetNotificationPositioningEnabled,
                                () =>
                                {
                                    if (!chatEnabled)
                                    {
                                        Emit(new OpenVrWorkerMessage(
                                            "log",
                                            Message: "Turn chat on before calibrating its gaze fade."));
                                        return false;
                                    }

                                    EnsureChatOverlay();
                                    return chatOverlay?.StartGazeCalibration(Environment.TickCount64) == true;
                                },
                                message => Emit(
                                    new OpenVrWorkerMessage(
                                        "log",
                                        Message: message)));
                        }
                        catch (Exception exception)
                        {
                            error = exception.Message;
                        }

                        Emit(
                            new OpenVrWorkerMessage(
                                "commandResult",
                                RequestId: command.RequestId,
                                Error: error));
                    }
                }

                if (openVr.IsQuitRequested())
                {
                    // Report before exiting: the parent must tell this apart
                    // from a worker crash, which it would otherwise retry.
                    // Destroying the overlays first keeps their keys from
                    // outliving the process that owns them.
                    testOverlay?.Dispose();
                    notificationOverlay?.Dispose();
                    chatOverlay?.Dispose();
                    dashboard?.Dispose();
                    Emit(new OpenVrWorkerMessage("quit"));
                    return 0;
                }

                var current = openVr.Poll();
                try
                {
                    dashboard?.Tick(current, setup);
                }
                catch (Exception exception)
                {
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message:
                            $"SteamVR dashboard interaction was ignored after an error: {exception.Message}"));
                }

                try
                {
                    testOverlay?.Tick(openVr);
                }
                catch (Exception exception)
                {
                    // A development aid must never be able to take the input
                    // worker down, so it is dropped rather than retried.
                    testOverlay?.Dispose();
                    testOverlay = null;
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: $"The VR test overlay was turned off after an error: {exception.Message}"));
                }

                try
                {
                    // A true no-op whenever nothing is queued or showing - see
                    // NotificationOverlay.Tick - so this costs nothing on the
                    // 10 ms poll loop between notifications.
                    notificationOverlay?.Tick(openVr, Environment.TickCount64);
                }
                catch (Exception exception)
                {
                    notificationOverlay?.Dispose();
                    notificationOverlay = null;
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: $"Notifications were turned off after an error: {exception.Message}"));
                }

                try
                {
                    // Re-resolves the wrist attachment and advances the
                    // gaze-scale animation every tick; the text repaint
                    // inside is throttled on its own, per §B2.
                    chatOverlay?.Tick(openVr, Environment.TickCount64);
                }
                catch (Exception exception)
                {
                    chatOverlay?.Dispose();
                    chatOverlay = null;
                    Emit(
                        new OpenVrWorkerMessage(
                            "log",
                            Message: $"Chat was turned off after an error: {exception.Message}"));
                }

                if (current != snapshot)
                {
                    snapshot = current;
                    Emit(new OpenVrWorkerMessage("snapshot", Snapshot: snapshot));
                }

                if (Stopwatch.GetTimestamp() >= nextSetupRefresh)
                {
                    setup = openVr.GetControllerSetup();
                    var signature = SetupSignature(setup);
                    if (signature != setupSignature)
                    {
                        setupSignature = signature;
                        Emit(new OpenVrWorkerMessage("setup", Setup: setup));
                    }

                    nextSetupRefresh = Stopwatch.GetTimestamp()
                                       + (long)(Stopwatch.Frequency * 2d);
                }

                await Task.Delay(pollInterval);
            }
        }
        catch (Exception exception)
        {
            Emit(new OpenVrWorkerMessage("failure", Error: exception.Message));
            return 1;
        }
    }

    private static async Task ReadCommandsAsync(
        ChannelWriter<OpenVrWorkerCommand> writer)
    {
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var command = JsonSerializer.Deserialize<OpenVrWorkerCommand>(
                    line,
                    JsonOptions);
                if (command is not null)
                {
                    await writer.WriteAsync(command);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed parent commands; the parent can retry or restart us.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static void Emit(OpenVrWorkerMessage message)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
        Console.Out.Flush();
    }

    private static string SetupSignature(ControllerSetup setup) =>
        string.Join(
            "|",
            setup.Availability,
            setup.FriendlySummary,
            setup.SafetyInput,
            setup.ActionInput,
            string.Join(
                ",",
                setup.Controllers.Select(controller =>
                    $"{controller.Hand}:{controller.ControllerType}:{controller.Model}")));

    private static TEnum ParseEnumArgument<TEnum>(string? raw, TEnum fallback)
        where TEnum : struct, Enum =>
        int.TryParse(raw, out var value) && Enum.IsDefined(typeof(TEnum), value)
            ? (TEnum)(object)value
            : fallback;

    private static bool ParseBoolArgument(string? raw) => bool.TryParse(raw, out var value) && value;

    private static double ParseDoubleArgument(string? raw, double fallback) =>
        double.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    internal static string SerialiseGazeReference(GazeReference reference) =>
        reference.IsUsable
            ? string.Join(",", reference.Right.ToString(CultureInfo.InvariantCulture), reference.Up.ToString(CultureInfo.InvariantCulture), reference.Forward.ToString(CultureInfo.InvariantCulture))
            : "";

    internal static GazeReference ParseGazeReferenceArgument(string? raw)
    {
        var parts = raw?.Split(',');
        if (parts is not { Length: 3 }
            || !float.TryParse(parts[0], CultureInfo.InvariantCulture, out var right)
            || !float.TryParse(parts[1], CultureInfo.InvariantCulture, out var up)
            || !float.TryParse(parts[2], CultureInfo.InvariantCulture, out var forward))
        {
            return GazeReference.None;
        }

        var reference = new GazeReference(right, up, forward);
        return reference.IsUsable ? reference : GazeReference.None;
    }

    /// <summary>
    /// Unparseable - or absent, for a worker spawned by an older build - falls
    /// back to <see cref="NotificationAppearanceSettings.Default"/> whole,
    /// same reasoning as <see cref="OverlayPlacement.Parse"/>: a half-read
    /// bundle is worse than the proven default.
    /// </summary>
    private static NotificationAppearanceSettings ParseNotificationAppearanceArgument(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NotificationAppearanceSettings.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationAppearanceSettings>(raw)
                   ?? NotificationAppearanceSettings.Default;
        }
        catch (JsonException)
        {
            return NotificationAppearanceSettings.Default;
        }
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

internal sealed record OpenVrWorkerCommand(
    string Kind,
    string? RequestId = null,
    IReadOnlyList<ShortcutConfig>? Shortcuts = null,
    IReadOnlyList<StreamerBotAction>? Actions = null,
    bool Activate = true,
    bool Enabled = false,
    StreamerBotEventPayload? Payload = null,
    IReadOnlyDictionary<string, string>? EmoteCatalog = null,
    VrSettingsSnapshot? VrSettings = null);

internal sealed record OpenVrWorkerMessage(
    string Kind,
    InputSnapshot? Snapshot = null,
    ControllerSetup? Setup = null,
    string? Message = null,
    string? RequestId = null,
    string? Error = null,
    ShortcutConfig? ShortcutCreated = null,
    string? ShortcutDeletedId = null,
    VrSettingsSnapshot? VrSettingsChanged = null);
