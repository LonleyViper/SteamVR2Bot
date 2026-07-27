using System.Runtime.InteropServices;

namespace SvrBridge.Core;

/// <summary>
/// Registers the application manifest through IVRApplications. vrpathreg.exe
/// manages driver paths only and cannot register application manifests.
/// </summary>
public static class SteamVrApplications
{
    private const string ApplicationsInterfaceVersion = "IVRApplications_006";

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

    [StructLayout(LayoutKind.Sequential)]
    private struct VrApplicationsFunctions
    {
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public AddApplicationManifestDelegate AddApplicationManifest;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddApplicationManifestDelegate(
        nint applicationManifestPath,
        [MarshalAs(UnmanagedType.I1)] bool temporary);
}
