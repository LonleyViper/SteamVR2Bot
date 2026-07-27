namespace SvrBridge.Tray;

/// <summary>
/// Per-user storage locations, and the one-time move from the folder the app
/// used before it was renamed to SteamVR2Bot.
/// </summary>
internal static class AppPaths
{
    public const string ProductName = "SteamVR2Bot";
    private const string LegacyFolderName = "SVR Bridge";

    private static readonly Lazy<string> ResolvedDirectory = new(
        () => Resolve(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Settings, logs, and dashboard images all live here.</summary>
    public static string DataDirectory => ResolvedDirectory.Value;

    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

    /// <summary>
    /// Returns the folder to use under <paramref name="localAppData"/>, moving
    /// the pre-rename folder across the first time it is found. Resolving must
    /// happen before anything creates the new folder, or the move has nowhere
    /// to land and the saved shortcuts stay stranded.
    /// </summary>
    internal static string Resolve(string localAppData)
    {
        var current = Path.Combine(localAppData, ProductName);
        var legacy = Path.Combine(localAppData, LegacyFolderName);
        if (Directory.Exists(current) || !Directory.Exists(legacy))
        {
            return current;
        }

        try
        {
            // Carries the saved shortcuts, the Streamer.bot connection, and the
            // Windows-protected password across the rename in one step.
            Directory.Move(legacy, current);
            return current;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // The tray and its OpenVR worker can race here, and the log file is
            // often still open. Whichever folder actually holds the data wins;
            // starting empty would look exactly like losing the user's setup.
            return Directory.Exists(current) ? current : legacy;
        }
    }
}
