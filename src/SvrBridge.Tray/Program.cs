namespace SvrBridge.Tray;

internal static class Program
{
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
            catch
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        return 0;
    }
}
