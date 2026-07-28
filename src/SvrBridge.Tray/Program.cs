using System.Runtime.InteropServices;

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
        if (args.Contains("--openvr-worker", StringComparer.OrdinalIgnoreCase))
        {
            return OpenVrWorker.RunAsync(args).GetAwaiter().GetResult();
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
