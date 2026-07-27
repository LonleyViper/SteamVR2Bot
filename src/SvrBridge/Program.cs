using System.Diagnostics;
using SvrBridge.Core;

namespace SvrBridge;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                await SelfTests.RunAsync();
                return 0;
            }

            if (args.Contains("--register-steamvr", StringComparer.OrdinalIgnoreCase))
            {
                var actionManifest = OpenVrInput.ResolveActionManifest(
                    GetArgumentValue(args, "--action-manifest"));
                var applicationManifest = GetArgumentValue(args, "--application-manifest")
                                          ?? Path.Combine(AppContext.BaseDirectory, "app.vrmanifest");
                SteamVrApplications.Register(applicationManifest, actionManifest);
                return 0;
            }

            if (args.Contains("--inspect-bindings", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--open-bindings", StringComparer.OrdinalIgnoreCase))
            {
                var actionManifest = OpenVrInput.ResolveActionManifest(
                    GetArgumentValue(args, "--action-manifest"));
                using var openVr = new OpenVrInput(
                    configuredDllPath: null,
                    actionManifest);
                _ = openVr.Poll();

                if (args.Contains("--open-bindings", StringComparer.OrdinalIgnoreCase))
                {
                    openVr.OpenBindingUi();
                    Console.WriteLine("Opened SteamVR controller bindings for SVR Bridge.");
                    return 0;
                }

                var setup = openVr.GetControllerSetup();
                Console.WriteLine($"Binding status: {setup.Availability}");
                Console.WriteLine($"Shortcut: {setup.FriendlySummary}");
                foreach (var controller in setup.Controllers)
                {
                    Console.WriteLine(
                        $"Controller: {controller.Hand} {controller.FriendlyName} " +
                        $"({controller.ControllerType}, {controller.Model})");
                }

                return 0;
            }

            var configPath = GetArgumentValue(args, "--config")
                             ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            EnsureConfigExists(configPath);
            var config = AppConfig.Load(configPath);

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            await using var streamerBot = new StreamerBotClient(config.StreamerBot);

            if (args.Contains("--simulate", StringComparer.OrdinalIgnoreCase))
            {
                await RunSimulationAsync(config, streamerBot, cancellation.Token);
            }
            else
            {
                await RunSteamVrAsync(config, streamerBot, cancellation.Token);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FATAL: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunSteamVrAsync(
        AppConfig config,
        StreamerBotClient streamerBot,
        CancellationToken cancellationToken)
    {
        var actionManifest = OpenVrInput.ResolveActionManifest(config.ActionManifestPath);
        using var openVr = new OpenVrInput(config.OpenVrDllPath, actionManifest);
        var detector = new ChordDetector(config.Chord);
        var stopwatch = Stopwatch.StartNew();
        var previous = new InputSnapshot(false, false);

        Console.WriteLine("SteamVR input active. Press Ctrl+C to stop.");
        Console.WriteLine(
            config.Chord.Mode switch
            {
                ChordMode.SinglePress =>
                    "Gesture: press Button One.",
                ChordMode.Modifier =>
                    "Gesture: hold Button One, then press Button Two.",
                ChordMode.LongPress =>
                    $"Gesture: hold Button One for {config.Chord.HoldMs} ms.",
                ChordMode.DoublePress =>
                    $"Gesture: double press Button One within {config.Chord.WindowMs} ms.",
                _ =>
                    $"Gesture: press both buttons within {config.Chord.WindowMs} ms."
            });

        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = openVr.Poll();

            if (config.LogRawInputChanges && snapshot != previous)
            {
                Console.WriteLine(
                    $"INPUT one={(snapshot.ButtonOne ? "DOWN" : "up")} " +
                    $"two={(snapshot.ButtonTwo ? "DOWN" : "up")}");
                previous = snapshot;
            }

            if (detector.Update(snapshot.ButtonOne, snapshot.ButtonTwo, stopwatch.ElapsedMilliseconds))
            {
                Console.WriteLine("CHORD detected.");
                await streamerBot.TriggerAsync("button_one+button_two", cancellationToken);
            }

            await Task.Delay(config.PollIntervalMs, cancellationToken);
        }
    }

    private static async Task RunSimulationAsync(
        AppConfig config,
        StreamerBotClient streamerBot,
        CancellationToken cancellationToken)
    {
        var detector = new ChordDetector(config.Chord);
        var stopwatch = Stopwatch.StartNew();
        var modifierDown = false;

        Console.WriteLine("Simulation mode:");
        Console.WriteLine("  M = toggle Button One / modifier");
        Console.WriteLine("  T = press and release Button Two / trigger");
        Console.WriteLine("  Q = quit");

        while (!cancellationToken.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Q)
            {
                return;
            }

            if (key == ConsoleKey.M)
            {
                modifierDown = !modifierDown;
                _ = detector.Update(modifierDown, false, stopwatch.ElapsedMilliseconds);
                Console.WriteLine($"Button One / modifier: {(modifierDown ? "DOWN" : "up")}");
                continue;
            }

            if (key != ConsoleKey.T)
            {
                continue;
            }

            var fired = detector.Update(modifierDown, true, stopwatch.ElapsedMilliseconds);
            _ = detector.Update(modifierDown, false, stopwatch.ElapsedMilliseconds + 1);
            Console.WriteLine($"Button Two pressed; chord fired: {fired}");
            if (fired)
            {
                await streamerBot.TriggerAsync("simulated-button_one+button_two", cancellationToken);
            }
        }
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return null;
    }

    private static void EnsureConfigExists(string configPath)
    {
        if (File.Exists(configPath))
        {
            return;
        }

        var examplePath = Path.Combine(AppContext.BaseDirectory, "appsettings.example.json");
        if (!File.Exists(examplePath))
        {
            return;
        }

        File.Copy(examplePath, configPath);
        Console.WriteLine($"Created configuration: {configPath}");
        Console.WriteLine("It defaults to dry-run mode. Edit it before testing Streamer.bot.");
    }
}
