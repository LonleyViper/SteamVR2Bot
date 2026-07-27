using System.Runtime.InteropServices;
using System.Text.Json;

namespace SvrBridge.Core;

public sealed class OpenVrInput : IDisposable
{
    private const string ActionSetPath = "/actions/svrbridge";
    private const string ButtonOnePath = "/actions/svrbridge/in/button_one";
    private const string ButtonTwoPath = "/actions/svrbridge/in/button_two";
    private const int OverlayGlobalPriorityMin = 16_777_216;

    private readonly nint _library;
    private readonly VrShutdownInternal _shutdown;
    private readonly VrInputFunctions _input;
    private readonly ulong _buttonOne;
    private readonly ulong _buttonTwo;
    private readonly VrActiveActionSet[] _activeSets;
    private bool _disposed;

    public OpenVrInput(
        string? configuredDllPath,
        string actionManifestPath,
        Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var dllPath = ResolveOpenVrDll(configuredDllPath);
        log($"OpenVR DLL: {dllPath}");

        _library = NativeLibrary.Load(dllPath);
        var init = LoadExport<VrInitInternal>(_library, "VR_InitInternal");
        _shutdown = LoadExport<VrShutdownInternal>(_library, "VR_ShutdownInternal");
        var getInterface = LoadExport<VrGetGenericInterface>(_library, "VR_GetGenericInterface");

        var initError = VrInitError.None;
        _ = init(ref initError, VrApplicationType.Overlay);
        if (initError != VrInitError.None)
        {
            NativeLibrary.Free(_library);
            throw new InvalidOperationException($"OpenVR initialization failed: {initError} ({(int)initError}).");
        }

        try
        {
            var tablePointer = GetInputTable(getInterface, log);
            _input = Marshal.PtrToStructure<VrInputFunctions>(tablePointer);

            var manifestPointer = Marshal.StringToCoTaskMemUTF8(actionManifestPath);
            try
            {
                EnsureSuccess(
                    _input.SetActionManifestPath(manifestPointer),
                    $"SetActionManifestPath({actionManifestPath})");
            }
            finally
            {
                Marshal.FreeCoTaskMem(manifestPointer);
            }

            ulong actionSet = 0;
            var actionSetPointer = Marshal.StringToCoTaskMemUTF8(ActionSetPath);
            try
            {
                EnsureSuccess(
                    _input.GetActionSetHandle(actionSetPointer, ref actionSet),
                    "GetActionSetHandle");
            }
            finally
            {
                Marshal.FreeCoTaskMem(actionSetPointer);
            }

            _buttonOne = GetActionHandle(ButtonOnePath);
            _buttonTwo = GetActionHandle(ButtonTwoPath);

            _activeSets =
            [
                new VrActiveActionSet
                {
                    ActionSet = actionSet,
                    RestrictedToDevice = 0,
                    SecondaryActionSet = 0,
                    Padding = 0,
                    Priority = OverlayGlobalPriorityMin
                }
            ];
        }
        catch
        {
            _shutdown();
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public InputSnapshot Poll()
    {
        ThrowIfDisposed();

        EnsureSuccess(
            _input.UpdateActionState(
                _activeSets,
                (uint)Marshal.SizeOf<VrActiveActionSet>(),
                (uint)_activeSets.Length),
            "UpdateActionState");

        return new InputSnapshot(ReadDigital(_buttonOne), ReadDigital(_buttonTwo));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _shutdown();
        NativeLibrary.Free(_library);
        _disposed = true;
    }

    public static string ResolveActionManifest(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "actions.json")
            : Path.GetFullPath(configuredPath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("SteamVR action manifest not found.", path);
        }

        return path;
    }

    internal static string ResolveOpenVrDll(string? configuredPath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(Path.GetFullPath(configuredPath));
        }

        var environmentPath = Environment.GetEnvironmentVariable("OPENVR_API_DLL");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            candidates.Add(Path.GetFullPath(environmentPath));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "openvr_api.dll"));

        var vrPathsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "openvr",
            "openvrpaths.vrpath");

        if (File.Exists(vrPathsFile))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(vrPathsFile));
                if (document.RootElement.TryGetProperty("runtime", out var runtimes))
                {
                    foreach (var runtime in runtimes.EnumerateArray())
                    {
                        var root = runtime.GetString();
                        if (!string.IsNullOrWhiteSpace(root))
                        {
                            candidates.Add(Path.Combine(root, "bin", "win64", "openvr_api.dll"));
                        }
                    }
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Could not parse {vrPathsFile}.", exception);
            }
        }

        var result = candidates.FirstOrDefault(File.Exists);
        if (result is not null)
        {
            return result;
        }

        throw new FileNotFoundException(
            "Could not locate openvr_api.dll. Start SteamVR once, copy the DLL beside SVR Bridge, " +
            "set OPENVR_API_DLL, or configure openVrDllPath.");
    }

    private static nint GetInputTable(
        VrGetGenericInterface getInterface,
        Action<string> log)
    {
        foreach (var version in new[] { "IVRInput_011", "IVRInput_010", "IVRInput_009" })
        {
            var error = VrInitError.None;
            var pointer = getInterface($"FnTable:{version}", ref error);
            if (pointer != nint.Zero && error == VrInitError.None)
            {
                log($"OpenVR input interface: {version}");
                return pointer;
            }
        }

        throw new InvalidOperationException("SteamVR did not expose a supported IVRInput interface.");
    }

    private ulong GetActionHandle(string actionPath)
    {
        ulong handle = 0;
        var actionPointer = Marshal.StringToCoTaskMemUTF8(actionPath);
        try
        {
            EnsureSuccess(
                _input.GetActionHandle(actionPointer, ref handle),
                $"GetActionHandle({actionPath})");
        }
        finally
        {
            Marshal.FreeCoTaskMem(actionPointer);
        }

        return handle;
    }

    private bool ReadDigital(ulong handle)
    {
        var data = new InputDigitalActionData();
        var error = _input.GetDigitalActionData(
            handle,
            ref data,
            (uint)Marshal.SizeOf<InputDigitalActionData>(),
            0);

        if (error == VrInputError.NoData)
        {
            return false;
        }

        EnsureSuccess(error, "GetDigitalActionData");
        return data.Active && data.State;
    }

    private static T LoadExport<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static void EnsureSuccess(VrInputError error, string operation)
    {
        if (error != VrInputError.None)
        {
            throw new InvalidOperationException($"{operation} failed: {error} ({(int)error}).");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VrInitInternal(ref VrInitError error, VrApplicationType applicationType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VrShutdownInternal();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint VrGetGenericInterface(
        [MarshalAs(UnmanagedType.LPStr)] string interfaceVersion,
        ref VrInitError error);

    private enum VrApplicationType
    {
        Overlay = 2
    }

    private enum VrInitError
    {
        None = 0
    }

    private enum VrInputError
    {
        None = 0,
        NameNotFound = 1,
        WrongType = 2,
        InvalidHandle = 3,
        InvalidParam = 4,
        NoSteam = 5,
        MaxCapacityReached = 6,
        IpcError = 7,
        NoActiveActionSet = 8,
        InvalidDevice = 9,
        InvalidSkeleton = 10,
        InvalidBoneCount = 11,
        InvalidCompressedData = 12,
        NoData = 13,
        BufferTooSmall = 14,
        MismatchedActionManifest = 15,
        MissingSkeletonData = 16,
        InvalidBoneIndex = 17,
        InvalidPriority = 18,
        PermissionDenied = 19,
        InvalidRenderModel = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrInputFunctions
    {
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public SetActionManifestPathDelegate SetActionManifestPath;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetActionSetHandleDelegate GetActionSetHandle;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetActionHandleDelegate GetActionHandle;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetInputSourceHandleDelegate GetInputSourceHandle;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public UpdateActionStateDelegate UpdateActionState;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetDigitalActionDataDelegate GetDigitalActionData;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError SetActionManifestPathDelegate(nint actionManifestPath);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError GetActionSetHandleDelegate(nint actionSetName, ref ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError GetActionHandleDelegate(nint actionName, ref ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError GetInputSourceHandleDelegate(nint inputSourcePath, ref ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError UpdateActionStateDelegate(
        [In, Out] VrActiveActionSet[] sets,
        uint selectedActionSetSize,
        uint setCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError GetDigitalActionDataDelegate(
        ulong action,
        ref InputDigitalActionData actionData,
        uint actionDataSize,
        ulong restrictToDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct VrActiveActionSet
    {
        public ulong ActionSet;
        public ulong RestrictedToDevice;
        public ulong SecondaryActionSet;
        public uint Padding;
        public int Priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputDigitalActionData
    {
        [MarshalAs(UnmanagedType.I1)]
        public bool Active;

        public ulong ActiveOrigin;

        [MarshalAs(UnmanagedType.I1)]
        public bool State;

        [MarshalAs(UnmanagedType.I1)]
        public bool Changed;

        public float UpdateTime;
    }
}

public readonly record struct InputSnapshot(bool ButtonOne, bool ButtonTwo);
