using System.Reflection;

namespace SvrBridge.Core;

/// <summary>
/// Both shipped executables (the tray app and the console diagnostics tool)
/// are published with <c>PublishSingleFile</c>, which bundles the managed
/// assembly but leaves the None/CopyToOutputDirectory sidecar files -
/// app.vrmanifest, actions.json, bindings_vive_controller.json,
/// SteamVR2Bot.png - sitting loose next to the exe. The release zip carries
/// them correctly, but the release page also links the bare exe on its own
/// (for the "just give me the file" case), and anyone who runs that copy,
/// or drags just the .exe out of the extracted folder, gets
/// FileNotFoundException("SteamVR action/application manifest not found.")
/// on startup. That reads like a SteamVR problem; it is actually a missing
/// file next to the exe.
/// </summary>
/// <remarks>
/// Every file listed here is also embedded into SvrBridge.Core (see
/// SvrBridge.Core.csproj) as a byte-for-byte copy of the one under
/// src\SvrBridge\assets. <see cref="EnsurePresent"/> recreates whichever
/// ones are missing so the app is self-healing even when run as a lone exe.
/// </remarks>
public static class SidecarAssets
{
    private static readonly IReadOnlyDictionary<string, string> EmbeddedResourceNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["app.vrmanifest"] = "SvrBridge.Core.Assets.app.vrmanifest",
            ["actions.json"] = "SvrBridge.Core.Assets.actions.json",
            ["bindings_vive_controller.json"] = "SvrBridge.Core.Assets.bindings_vive_controller.json",
            ["SteamVR2Bot.png"] = "SvrBridge.Core.Assets.SteamVR2Bot.png"
        };

    /// <summary>
    /// Writes any sidecar file missing from <paramref name="baseDirectory"/>
    /// using the copy embedded in this assembly. Existing files are never
    /// touched - this only fills gaps, so a user's customised actions.json
    /// or bindings file is never overwritten. Call this once, as early as
    /// possible in every process entry point (including worker/child
    /// processes launched by re-invoking the same exe), before anything
    /// resolves a path under <paramref name="baseDirectory"/>.
    /// </summary>
    /// <param name="baseDirectory">
    /// Defaults to <see cref="AppContext.BaseDirectory"/>, the directory
    /// containing the running exe.
    /// </param>
    /// <param name="log">
    /// Optional sink for a one-line message per file actually restored.
    /// Silent when everything is already present, which is the common case.
    /// </param>
    public static void EnsurePresent(string? baseDirectory = null, Action<string>? log = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var assembly = typeof(SidecarAssets).Assembly;

        foreach (var (fileName, resourceName) in EmbeddedResourceNames)
        {
            var targetPath = Path.Combine(baseDirectory, fileName);
            if (File.Exists(targetPath))
            {
                continue;
            }

            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is null)
            {
                // A build misconfiguration (the embed and this lookup table
                // drifting apart), not a runtime condition. Skip rather than
                // throw so a packaging bug in the fallback itself can never
                // be the thing that blocks startup.
                continue;
            }

            try
            {
                Directory.CreateDirectory(baseDirectory);
                using var fileStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write);
                resourceStream.CopyTo(fileStream);
                log?.Invoke($"Restored missing {fileName} from the embedded copy.");
            }
            catch (IOException)
            {
                // Lost a race with another process re-invoking this same exe
                // (e.g. an --openvr-worker child started concurrently) that
                // already recreated the file. Nothing left to do.
            }
        }
    }
}
