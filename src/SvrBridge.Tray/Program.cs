using System.Runtime.InteropServices;
using System.Text.Json;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal static class Program
{
    // Worker child processes (--openvr-worker) are excluded: they are
    // headless and can coexist one-per-tray-instance without contending for
    // this lock.
    private const string SingleInstanceMutexName = "SvrBridge.Tray.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Must run before any branch below: --openvr-worker re-invokes this
        // same exe as a child process, and every other path resolves a
        // sidecar file (app.vrmanifest, actions.json,
        // bindings_vive_controller.json) under AppContext.BaseDirectory.
        // Recreating whatever is missing here means a bare, sidecar-less
        // exe self-heals instead of failing with a "manifest not found"
        // error that reads like a SteamVR problem.
        SidecarAssets.EnsurePresent();

        if (args.Contains("--openvr-worker", StringComparer.OrdinalIgnoreCase))
        {
            return OpenVrWorker.RunAsync(args).GetAwaiter().GetResult();
        }

        if (args.Contains("--inspect-twitch-emotes", StringComparer.OrdinalIgnoreCase))
        {
            return InspectTwitchEmotesAsync().GetAwaiter().GetResult();
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                TraySelfTests.Run();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        // Held for the process lifetime: SteamVR can relaunch the registered
        // binary (e.g. clicking the app's dashboard icon) while the tray app
        // is already running. A second full instance can't claim the same
        // OpenVR dashboard overlay and application registration, so it just
        // surfaces a confusing "SteamVR setup failed" tray icon. Bring the
        // existing window forward instead of starting a duplicate.
        using var singleInstance = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateRunningInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        return 0;
    }

    /// <summary>
    /// A throwaway diagnostic, not a product feature: connects with the
    /// same saved Streamer.bot address and password the tray app already
    /// uses, sends one <c>TwitchGetEmotes</c> request, and prints the raw
    /// response. Exists purely to answer, against a real running
    /// Streamer.bot instance, whether that request returns usable emote
    /// image URLs before any rendering work is built on top of it.
    /// </summary>
    private static async Task<int> InspectTwitchEmotesAsync()
    {
        var settings = new UserSettingsStore().Load();
        var config = settings.ToAppConfig().StreamerBot;
        Console.WriteLine($"Connecting to {config.WebSocketUrl} ...");

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new StreamerBotEventStream(
            config,
            activity => Console.WriteLine(activity.Message));
        stream.StateChanged += state =>
        {
            if (state == StreamerBotStreamState.Connected)
            {
                connected.TrySetResult();
            }
        };
        stream.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (timeout.Token.Register(() => connected.TrySetCanceled()))
        {
            try
            {
                await connected.Task;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Timed out waiting to connect to Streamer.bot.");
                return 1;
            }
        }

        Console.WriteLine("Connected. Requesting TwitchGetEmotes...");
        try
        {
            using var response = await stream.SendRequestAsync("TwitchGetEmotes", timeout.Token);
            Console.WriteLine(
                JsonSerializer.Serialize(
                    response.RootElement,
                    new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"TwitchGetEmotes failed: {exception.Message}");
            return 1;
        }
    }

    private static void ActivateRunningInstance()
    {
        var handle = NativeMethods.FindWindow(null, "SteamVR2Bot — VR shortcuts");
        if (handle == nint.Zero)
        {
            return;
        }

        const int showNormal = 1;
        NativeMethods.ShowWindow(handle, showNormal);
        NativeMethods.SetForegroundWindow(handle);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern nint FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(nint hWnd);
    }
}
