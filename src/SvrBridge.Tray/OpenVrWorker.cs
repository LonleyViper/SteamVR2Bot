using System.Collections.Concurrent;
using System.Diagnostics;
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

internal sealed class OpenVrWorkerSession : IOpenVrSession
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
    private InputSnapshot _snapshot;
    private ControllerSetup _setup = ControllerSetup.Unknown;
    private Exception? _failure;
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

    public void ShowDashboard(string imagePath)
        => ShowDashboard(imagePath, [], []);

    public void ShowDashboard(
        string imagePath,
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
                    imagePath,
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

    public IReadOnlyList<string> DrainDeletedShortcutIds()
    {
        var result = new List<string>();
        while (_deletedShortcutIds.TryDequeue(out var shortcutId))
        {
            result.Add(shortcutId);
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
                              "Could not locate the SVR Bridge executable.");
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
                    "Could not locate the SVR Bridge worker assembly.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--openvr-worker");
        startInfo.ArgumentList.Add("--action-manifest");
        startInfo.ArgumentList.Add(actionManifest);
        startInfo.ArgumentList.Add("--poll-interval");
        startInfo.ArgumentList.Add(config.PollIntervalMs.ToString());
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
        while (await _process.StandardError.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                _log($"SteamVR input worker: {line}");
            }
        }
    }

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

            using var openVr = new OpenVrInput(
                openVrDll,
                actionManifest,
                message => Emit(new OpenVrWorkerMessage("log", Message: message)));
            var commands = Channel.CreateUnbounded<OpenVrWorkerCommand>();
            VrDashboardController? dashboard = null;
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

                    if (command.Kind == "showDashboard")
                    {
                        string? error = null;
                        try
                        {
                            dashboard = new VrDashboardController(
                                openVr,
                                command.Shortcuts ?? [],
                                command.Actions ?? [],
                                command.Activate,
                                shortcut => Emit(
                                    new OpenVrWorkerMessage(
                                        "shortcutCreated",
                                        ShortcutCreated: shortcut)),
                                shortcutId => Emit(
                                    new OpenVrWorkerMessage(
                                        "shortcutDeleted",
                                        ShortcutDeletedId: shortcutId)),
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
    string? ImagePath = null,
    IReadOnlyList<ShortcutConfig>? Shortcuts = null,
    IReadOnlyList<StreamerBotAction>? Actions = null,
    bool Activate = true);

internal sealed record OpenVrWorkerMessage(
    string Kind,
    InputSnapshot? Snapshot = null,
    ControllerSetup? Setup = null,
    string? Message = null,
    string? RequestId = null,
    string? Error = null,
    ShortcutConfig? ShortcutCreated = null,
    string? ShortcutDeletedId = null);
