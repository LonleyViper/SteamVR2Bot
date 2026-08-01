using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>
/// Registers the application manifest through IVRApplications. vrpathreg.exe
/// manages driver paths only and cannot register application manifests.
/// </summary>
public static class SteamVrApplications
{
    private const string ApplicationsInterfaceVersion = "IVRApplications_006";
    private const string AppKey = "ie.lonelyviper.svrbridge.poc";
    private static readonly Uri SteamVrWebRoot = new("http://127.0.0.1:27062/");

    public static void Register(
        string applicationManifestPath,
        string actionManifestPath,
        Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        if (!File.Exists(applicationManifestPath))
        {
            throw new FileNotFoundException("SteamVR application manifest not found.", applicationManifestPath);
        }

        // Resolve this as part of registration so an actionable error is reported
        // before asking SteamVR to load the application manifest.
        _ = actionManifestPath;
        var dllPath = OpenVrInput.ResolveOpenVrDll(configuredPath: null);
        log($"OpenVR DLL: {dllPath}");

        var library = NativeLibrary.Load(dllPath);
        try
        {
            var init = LoadExport<VrInitInternal>(library, "VR_InitInternal");
            var shutdown = LoadExport<VrShutdownInternal>(library, "VR_ShutdownInternal");
            var getInterface = LoadExport<VrGetGenericInterface>(library, "VR_GetGenericInterface");
            var initError = 0;
            _ = init(ref initError, VrApplicationType.Utility);
            if (initError != 0)
            {
                throw new InvalidOperationException(
                    $"OpenVR utility initialization failed with error {initError}.");
            }

            try
            {
                var interfaceError = 0;
                var tablePointer = getInterface($"FnTable:{ApplicationsInterfaceVersion}", ref interfaceError);
                if (tablePointer == nint.Zero || interfaceError != 0)
                {
                    throw new InvalidOperationException(
                        $"SteamVR did not expose {ApplicationsInterfaceVersion} (error {interfaceError}).");
                }

                var applications = Marshal.PtrToStructure<VrApplicationsFunctions>(tablePointer);
                var manifestPointer = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(applicationManifestPath));
                try
                {
                    var result = applications.AddApplicationManifest(manifestPointer, false);
                    if (result != 0)
                    {
                        throw new InvalidOperationException(
                            $"AddApplicationManifest failed with SteamVR application error {result}.");
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(manifestPointer);
                }

                // Ours is registered first so a failure here can never leave
                // SteamVR with no registration at all.
                RemoveStaleRegistrations(
                    applications,
                    Path.GetFullPath(applicationManifestPath),
                    log);
                EnableAutoLaunch(applications, log);
            }
            finally
            {
                shutdown();
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }

        log($"Registered SteamVR application manifest: {Path.GetFullPath(applicationManifestPath)}");
    }

    public static async Task SelectPackagedViveBindingAsync(
        string bindingPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(bindingPath))
        {
            throw new FileNotFoundException(
                "Packaged Vive controller binding not found.",
                bindingPath);
        }

        using var client = new HttpClient { BaseAddress = SteamVrWebRoot };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "input/selectconfig.action");
        request.Headers.Referrer = new Uri(
            SteamVrWebRoot,
            $"dashboard/controllerbinding.html?app={AppKey}");
        request.Headers.TryAddWithoutValidation("Origin", SteamVrWebRoot.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        var payload = JsonSerializer.Serialize(
            new
            {
                app_key = AppKey,
                controller_type = "vive_controller",
                url = new Uri(Path.GetFullPath(bindingPath)).AbsoluteUri
            });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        using var result = JsonDocument.Parse(responseText);
        if (!result.RootElement.TryGetProperty("success", out var success)
            || !success.GetBoolean())
        {
            var detail = result.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : "SteamVR did not confirm the controller input map.";
            throw new InvalidOperationException(detail);
        }
    }

    /// <summary>
    /// Drops any other manifest SteamVR still has registered for our app key.
    /// Every build output folder carries its own copy of app.vrmanifest, so
    /// running the app from a second location leaves both registered. Because
    /// the manifest declares a dashboard overlay, SteamVR then auto-launches
    /// the binary from every registered folder, and the user gets a duplicate
    /// window and tray icon whose SteamVR setup fails as it loses the race for
    /// the dashboard overlay.
    /// </summary>
    private static void RemoveStaleRegistrations(
        VrApplicationsFunctions applications,
        string currentManifestPath,
        Action<string> log)
    {
        List<string> stalePaths;
        try
        {
            stalePaths = FindStaleManifestPaths(currentManifestPath);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Cleanup is best effort: registration already succeeded.
            log($"Could not inspect existing SteamVR registrations: {exception.Message}");
            return;
        }

        foreach (var stalePath in stalePaths)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(stalePath);
            try
            {
                var result = applications.RemoveApplicationManifest(pointer);
                log(result == 0
                    ? $"Removed a stale SteamVR registration: {stalePath}"
                    : $"Could not remove the stale SteamVR registration {stalePath} (SteamVR application error {result}).");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    /// <summary>
    /// Puts the app on SteamVR's startup list, so its lifetime matches
    /// SteamVR's: launched with SteamVR, and shut down again on VREvent_Quit.
    /// Requires the manifest to be registered first.
    /// </summary>
    private static void EnableAutoLaunch(
        VrApplicationsFunctions applications,
        Action<string> log)
    {
        var keyPointer = Marshal.StringToCoTaskMemUTF8(AppKey);
        try
        {
            var result = applications.SetApplicationAutoLaunch(keyPointer, true);
            if (result != 0)
            {
                log($"Could not add SteamVR2Bot to SteamVR startup (SteamVR application error {result}).");
                return;
            }

            log(applications.GetApplicationAutoLaunch(keyPointer)
                ? "SteamVR will start SteamVR2Bot automatically."
                : "SteamVR accepted the startup request but did not report it as enabled.");
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException
                                              or MarshalDirectiveException)
        {
            // Startup registration is a convenience: never fail setup over it.
            log($"This SteamVR version did not accept a startup request: {exception.Message}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(keyPointer);
        }
    }

    private static List<string> FindStaleManifestPaths(string currentManifestPath)
    {
        var stale = new List<string>();
        if (ResolveAppConfigPath() is not { } configPath || !File.Exists(configPath))
        {
            return stale;
        }

        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!config.RootElement.TryGetProperty("manifest_paths", out var paths)
            || paths.ValueKind != JsonValueKind.Array)
        {
            return stale;
        }

        foreach (var entry in paths.EnumerateArray())
        {
            if (entry.GetString() is not { } path
                || path.Equals(currentManifestPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A path that no longer exists on disk cannot be a live
            // registration for anything - whatever app it named cannot be
            // launched from there either, by us or by SteamVR. That makes it
            // safe to remove unconditionally, unlike an *existing* file,
            // which still needs the app-key check below so this cannot
            // clobber some other real app's manifest.
            //
            // This matters because it is exactly the case moving install
            // folders produces: register once from Desktop\app.vrmanifest,
            // delete that copy, register again from
            // Desktop\SteamVR2Bot\app.vrmanifest - the old path was
            // previously skipped forever (DeclaresOurAppKey cannot read a
            // file that is gone), so appconfig.json accumulated a dead
            // manifest_paths entry on every reinstall to a new folder.
            if (File.Exists(path) && !DeclaresOurAppKey(path))
            {
                continue;
            }

            stale.Add(path);
        }

        return stale;
    }

    /// <summary>
    /// Only unregister manifests that actually claim our app key: the same
    /// list holds every other SteamVR application on the machine.
    /// </summary>
    private static bool DeclaresOurAppKey(string manifestPath)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("applications", out var applications)
                || applications.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var application in applications.EnumerateArray())
            {
                if (application.TryGetProperty("app_key", out var key)
                    && AppKey.Equals(key.GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A manifest we cannot read is not one we should unregister.
        }

        return false;
    }

    /// <summary>
    /// SteamVR records its registered manifests in appconfig.json, inside the
    /// config folder named by openvrpaths.vrpath.
    /// </summary>
    private static string? ResolveAppConfigPath()
    {
        var vrPaths = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "openvr",
            "openvrpaths.vrpath");
        if (!File.Exists(vrPaths))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(vrPaths));
            if (!document.RootElement.TryGetProperty("config", out var config)
                || config.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in config.EnumerateArray())
            {
                if (entry.GetString() is { } directory)
                {
                    return Path.Combine(directory, "appconfig.json");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Fall through: cleanup is skipped rather than failing registration.
        }

        return null;
    }

    private static T LoadExport<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VrInitInternal(ref int error, VrApplicationType applicationType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VrShutdownInternal();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint VrGetGenericInterface(
        [MarshalAs(UnmanagedType.LPStr)] string interfaceVersion,
        ref int error);

    private enum VrApplicationType
    {
        Utility = 4
    }

    // Field order mirrors the IVRApplications_006 function table, so the
    // unused entries in between must stay as placeholders and nothing may be
    // inserted ahead of a bound delegate.
    [StructLayout(LayoutKind.Sequential)]
    private struct VrApplicationsFunctions
    {
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public AddApplicationManifestDelegate AddApplicationManifest;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public RemoveApplicationManifestDelegate RemoveApplicationManifest;

        private nint IsApplicationInstalled;
        private nint GetApplicationCount;
        private nint GetApplicationKeyByIndex;
        private nint GetApplicationKeyByProcessId;
        private nint LaunchApplication;
        private nint LaunchTemplateApplication;
        private nint LaunchApplicationFromMimeType;
        private nint LaunchDashboardOverlay;
        private nint CancelApplicationLaunch;
        private nint IdentifyApplication;
        private nint GetApplicationProcessId;
        private nint GetApplicationsErrorNameFromEnum;
        private nint GetApplicationPropertyString;
        private nint GetApplicationPropertyBool;
        private nint GetApplicationPropertyUint64;

        // Index 17.
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public SetApplicationAutoLaunchDelegate SetApplicationAutoLaunch;

        // Index 18. Read back purely to prove the two entries above land on the
        // intended slots: SteamVR does not surface the flag in any config file,
        // so a mis-numbered table would otherwise fail silently.
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetApplicationAutoLaunchDelegate GetApplicationAutoLaunch;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddApplicationManifestDelegate(
        nint applicationManifestPath,
        [MarshalAs(UnmanagedType.I1)] bool temporary);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RemoveApplicationManifestDelegate(nint applicationManifestPath);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetApplicationAutoLaunchDelegate(
        nint appKey,
        [MarshalAs(UnmanagedType.I1)] bool autoLaunch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetApplicationAutoLaunchDelegate(nint appKey);
}
