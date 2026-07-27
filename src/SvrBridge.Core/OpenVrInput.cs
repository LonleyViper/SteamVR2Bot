using System.Runtime.InteropServices;
using System.Text.Json;

namespace SvrBridge.Core;

public sealed class OpenVrInput : IOpenVrSession
{
    private const string ActionSetPath = "/actions/svrbridge";
    private const string ButtonOnePath = "/actions/svrbridge/in/button_one";
    private const string ButtonTwoPath = "/actions/svrbridge/in/button_two";
    private const int OverlayGlobalPriorityMin = 16_777_216;
    private static readonly PhysicalActionDefinition[] PhysicalActions =
    [
        new(ControllerHand.Left, 1, "/actions/svrbridge/in/left_menu"),
        new(ControllerHand.Right, 1, "/actions/svrbridge/in/right_menu"),
        new(ControllerHand.Left, 2, "/actions/svrbridge/in/left_grip"),
        new(ControllerHand.Right, 2, "/actions/svrbridge/in/right_grip"),
        new(ControllerHand.Left, 33, "/actions/svrbridge/in/left_trigger"),
        new(ControllerHand.Right, 33, "/actions/svrbridge/in/right_trigger"),
        new(ControllerHand.Left, 32, "/actions/svrbridge/in/left_trackpad"),
        new(ControllerHand.Right, 32, "/actions/svrbridge/in/right_trackpad")
    ];

    private readonly nint _library;
    private readonly VrShutdownInternal _shutdown;
    private readonly Action<string> _log;
    private readonly VrInputFunctions _input;
    private readonly VrSystemFunctions? _system;
    private readonly VrOverlayFunctions? _overlay;
    private readonly bool _supportsBindingInspection;
    private readonly ulong _actionSet;
    private readonly ulong _buttonOne;
    private readonly ulong _buttonTwo;
    private readonly IReadOnlyDictionary<(ControllerHand Hand, uint Button), ulong>
        _physicalButtons;
    private readonly VrActiveActionSet[] _activeSets;
    private ulong _dashboardHandle;
    private ulong _dashboardThumbnailHandle;
    private bool _dashboardThumbnailInitialized;
    private readonly DashboardPointerTracker _dashboardPointer = new();
    private long _lastDashboardScrollLogAt = long.MinValue;
    private bool _disposed;

    public OpenVrInput(
        string? configuredDllPath,
        string actionManifestPath,
        Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        _log = log;
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
            var tablePointer = GetInputTable(
                getInterface,
                log,
                out _supportsBindingInspection);
            _input = Marshal.PtrToStructure<VrInputFunctions>(tablePointer);
            _system = TryGetSystemTable(getInterface, log);
            _overlay = TryGetOverlayTable(getInterface, log);

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

            _actionSet = actionSet;
            _buttonOne = GetActionHandle(ButtonOnePath);
            _buttonTwo = GetActionHandle(ButtonTwoPath);
            _physicalButtons = PhysicalActions.ToDictionary(
                definition => (definition.Hand, definition.Button),
                definition => GetActionHandle(definition.ActionPath));

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

        var (leftButtons, rightButtons) = ReadControllerButtons();
        return new InputSnapshot(
            ReadDigital(_buttonOne) || IsPressed(leftButtons, 2),
            ReadDigital(_buttonTwo) || IsPressed(rightButtons, 33),
            leftButtons,
            rightButtons);
    }

    public ControllerSetup GetControllerSetup()
    {
        ThrowIfDisposed();
        if (!_supportsBindingInspection)
        {
            return ControllerSetup.Unknown with
            {
                FriendlySummary =
                    "Controller binding inspection requires a newer SteamVR input interface."
            };
        }

        var controllers = GetControllers();
        var safety = GetBindings(_buttonOne, "Safety Button").FirstOrDefault()
                     ?? GetBindings(
                             _physicalButtons[(ControllerHand.Left, 2)],
                             "Left Grip")
                         .FirstOrDefault();
        var action = GetBindings(_buttonTwo, "Action Button").FirstOrDefault()
                     ?? GetBindings(
                             _physicalButtons[(ControllerHand.Right, 33)],
                             "Right Trigger")
                         .FirstOrDefault();

        if (controllers.Count == 0)
        {
            return new ControllerSetup(
                controllers,
                safety,
                action,
                BindingAvailability.Unknown,
                "Turn on both VR controllers to confirm their active bindings.",
                false);
        }

        if (safety is null || action is null)
        {
            var missing = safety is null && action is null
                ? "Both controller inputs need a SteamVR binding."
                : safety is null
                    ? "Choose a SteamVR binding for the Safety Button."
                    : "Choose a SteamVR binding for the Action Button.";
            return new ControllerSetup(
                controllers,
                safety,
                action,
                BindingAvailability.NeedsSetup,
                missing,
                false);
        }

        var validatedVivePreset =
            controllers.Any(controller =>
                controller.ControllerType.Equals(
                    "vive_controller",
                    StringComparison.OrdinalIgnoreCase))
            && safety.DevicePath.Contains("/left", StringComparison.OrdinalIgnoreCase)
            && safety.InputPath.Contains("/grip", StringComparison.OrdinalIgnoreCase)
            && action.DevicePath.Contains("/right", StringComparison.OrdinalIgnoreCase)
            && action.InputPath.Contains("/trigger", StringComparison.OrdinalIgnoreCase);

        return new ControllerSetup(
            controllers,
            safety,
            action,
            BindingAvailability.Ready,
            $"Hold {FriendlyBindingName(safety)}, then press {FriendlyBindingName(action)}.",
            validatedVivePreset);
    }

    public void OpenBindingUi()
    {
        ThrowIfDisposed();
        if (!_supportsBindingInspection)
        {
            throw new InvalidOperationException(
                "This SteamVR version cannot open controller bindings from SVR Bridge.");
        }

        var appKey = Marshal.StringToCoTaskMemUTF8("ie.lonelyviper.svrbridge.poc");
        try
        {
            EnsureSuccess(
                _input.OpenBindingUi(appKey, _actionSet, 0, true),
                "OpenBindingUI");
        }
        finally
        {
            Marshal.FreeCoTaskMem(appKey);
        }
    }

    public void ShowDashboard(string imagePath) =>
        UpdateDashboard(imagePath, activate: true);

    public void UpdateDashboard(string imagePath, bool activate)
    {
        ThrowIfDisposed();
        if (_overlay is null)
        {
            throw new InvalidOperationException(
                "This SteamVR version did not expose dashboard overlays.");
        }

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("VR dashboard image not found.", imagePath);
        }

        var key = Marshal.StringToCoTaskMemUTF8("ie.lonelyviper.svrbridge.dashboard");
        var name = Marshal.StringToCoTaskMemUTF8("SVR Bridge");
        var image = Marshal.StringToCoTaskMemUTF8(imagePath);
        try
        {
            if (_dashboardHandle == 0)
            {
                EnsureOverlaySuccess(
                    _overlay.Value.CreateDashboardOverlay(
                        key,
                        name,
                        ref _dashboardHandle,
                        ref _dashboardThumbnailHandle),
                    "CreateDashboardOverlay");
                EnsureOverlaySuccess(
                    _overlay.Value.SetOverlayWidthInMeters(_dashboardHandle, 2.2f),
                    "SetOverlayWidthInMeters");
                EnsureOverlaySuccess(
                    _overlay.Value.SetOverlayInputMethod(_dashboardHandle, 1),
                    "SetOverlayInputMethod");
                EnsureOverlaySuccess(
                    _overlay.Value.SetOverlayFlag(
                        _dashboardHandle,
                        1 << 6,
                        true),
                    "SetOverlayFlag(SendVRDiscreteScrollEvents)");
                var mouseScale = new HmdVector2 { X = 1400, Y = 900 };
                EnsureOverlaySuccess(
                    _overlay.Value.SetOverlayMouseScale(
                        _dashboardHandle,
                        ref mouseScale),
                    "SetOverlayMouseScale");
            }

            EnsureOverlaySuccess(
                _overlay.Value.SetOverlayFromFile(_dashboardHandle, image),
                "SetOverlayFromFile");
            if (!_dashboardThumbnailInitialized)
            {
                EnsureOverlaySuccess(
                    _overlay.Value.SetOverlayFromFile(_dashboardThumbnailHandle, image),
                    "SetOverlayFromFile(thumbnail)");
                _dashboardThumbnailInitialized = true;
            }

            if (activate)
            {
                _overlay.Value.ShowDashboard(key);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(image);
        }
    }

    public bool TryGetDashboardInteraction(out DashboardInteraction interaction)
    {
        interaction = default;
        if (_overlay is null || _dashboardHandle == 0)
        {
            return false;
        }

        // On Windows VREvent_t is 64 bytes: a 16-byte header followed by
        // the 48-byte VREvent_Data_t union.
        const int eventBufferSize = 64;
        var eventBuffer = Marshal.AllocCoTaskMem(eventBufferSize);
        try
        {
            while (_overlay.Value.PollNextOverlayEvent(
                       _dashboardHandle,
                       eventBuffer,
                       eventBufferSize))
            {
                var eventType = Marshal.ReadInt32(eventBuffer);
                var eventX = BitConverter.Int32BitsToSingle(
                    Marshal.ReadInt32(eventBuffer, 16));
                var rawEventY = BitConverter.Int32BitsToSingle(
                    Marshal.ReadInt32(eventBuffer, 20));
                var eventY = eventType is 300 or 301
                    ? 900 - rawEventY
                    : rawEventY;
                if (!_dashboardPointer.Update(
                        eventType,
                        eventX,
                        eventY,
                        out interaction))
                {
                    continue;
                }

                if (interaction.Kind == DashboardInteractionKind.Click)
                {
                    // Valve's dashboard sample uses the last MouseMove position
                    // for button events; the button packet itself is not a
                    // reliable source of x/y coordinates.
                    _log(
                        $"SteamVR dashboard click: " +
                        $"{interaction.X:0}, {interaction.Y:0}.");
                }
                else
                {
                    var nowMs = Environment.TickCount64;
                    if (_lastDashboardScrollLogAt == long.MinValue
                        || nowMs - _lastDashboardScrollLogAt >= 500)
                    {
                        _lastDashboardScrollLogAt = nowMs;
                        _log($"SteamVR dashboard scroll: {interaction.ScrollY:0.##}.");
                    }
                }

                return true;
            }

            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(eventBuffer);
        }
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
        Action<string> log,
        out bool supportsBindingInspection)
    {
        foreach (var version in new[] { "IVRInput_011", "IVRInput_010", "IVRInput_009" })
        {
            var error = VrInitError.None;
            var pointer = getInterface($"FnTable:{version}", ref error);
            if (pointer != nint.Zero && error == VrInitError.None)
            {
                log($"OpenVR input interface: {version}");
                supportsBindingInspection = version == "IVRInput_011";
                return pointer;
            }
        }

        supportsBindingInspection = false;
        throw new InvalidOperationException("SteamVR did not expose a supported IVRInput interface.");
    }

    private static VrSystemFunctions? TryGetSystemTable(
        VrGetGenericInterface getInterface,
        Action<string> log)
    {
        var error = VrInitError.None;
        var pointer = getInterface("FnTable:IVRSystem_026", ref error);
        if (pointer == nint.Zero || error != VrInitError.None)
        {
            log("Controller family detection is unavailable in this SteamVR version.");
            return null;
        }

        return Marshal.PtrToStructure<VrSystemFunctions>(pointer);
    }

    private static VrOverlayFunctions? TryGetOverlayTable(
        VrGetGenericInterface getInterface,
        Action<string> log)
    {
        var error = VrInitError.None;
        var pointer = getInterface("FnTable:IVROverlay_028", ref error);
        if (pointer == nint.Zero || error != VrInitError.None)
        {
            log("SteamVR dashboard overlays are unavailable in this SteamVR version.");
            return null;
        }

        return new VrOverlayFunctions(
            GetTableDelegate<SetOverlayWidthInMetersDelegate>(pointer, 22),
            GetTableDelegate<SetOverlayFlagDelegate>(pointer, 11),
            GetTableDelegate<PollNextOverlayEventDelegate>(pointer, 48),
            GetTableDelegate<SetOverlayInputMethodDelegate>(pointer, 50),
            GetTableDelegate<SetOverlayMouseScaleDelegate>(pointer, 52),
            GetTableDelegate<SetOverlayFromFileDelegate>(pointer, 63),
            GetTableDelegate<CreateDashboardOverlayDelegate>(pointer, 67),
            GetTableDelegate<ShowDashboardDelegate>(pointer, 72));
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

    private (ulong Left, ulong Right) ReadControllerButtons()
    {
        var (left, right) = ReadLegacyControllerButtons();
        foreach (var definition in PhysicalActions)
        {
            if (!ReadDigital(_physicalButtons[(definition.Hand, definition.Button)]))
            {
                continue;
            }

            if (definition.Hand == ControllerHand.Left)
            {
                left |= 1UL << (int)definition.Button;
            }
            else
            {
                right |= 1UL << (int)definition.Button;
            }
        }

        return (left, right);
    }

    private (ulong Left, ulong Right) ReadLegacyControllerButtons()
    {
        if (_system is null || _system.Value.GetControllerState is null)
        {
            return (0, 0);
        }

        ulong left = 0;
        ulong right = 0;
        for (uint index = 0; index < 64; index++)
        {
            var role = _system.Value.GetControllerRoleForTrackedDeviceIndex(index);
            if (role is not (TrackedControllerRole.LeftHand or TrackedControllerRole.RightHand)
                || _system.Value.GetTrackedDeviceClass(index) != TrackedDeviceClass.Controller
                || !_system.Value.IsTrackedDeviceConnected(index))
            {
                continue;
            }

            var state = new VrControllerState();
            if (!_system.Value.GetControllerState(
                    index,
                    ref state,
                    (uint)Marshal.SizeOf<VrControllerState>()))
            {
                continue;
            }

            if (role == TrackedControllerRole.LeftHand)
            {
                left = state.ButtonPressed;
            }
            else
            {
                right = state.ButtonPressed;
            }
        }

        return (left, right);
    }

    private static bool IsPressed(ulong buttons, uint button) =>
        (buttons & (1UL << (int)button)) != 0;

    private IReadOnlyList<ControllerDevice> GetControllers()
    {
        if (_system is null)
        {
            return [];
        }

        var controllers = new List<ControllerDevice>();
        for (uint index = 0; index < 64; index++)
        {
            if (_system.Value.GetTrackedDeviceClass(index) != TrackedDeviceClass.Controller
                || !_system.Value.IsTrackedDeviceConnected(index))
            {
                continue;
            }

            var type = ReadDeviceString(index, TrackedDeviceProperty.ControllerType);
            var model = ReadDeviceString(index, TrackedDeviceProperty.ModelNumber);
            var role = _system.Value.GetControllerRoleForTrackedDeviceIndex(index) switch
            {
                TrackedControllerRole.LeftHand => "Left",
                TrackedControllerRole.RightHand => "Right",
                _ => "Controller"
            };
            controllers.Add(
                new ControllerDevice(
                    type,
                    FriendlyControllerName(type, model),
                    role,
                    model));
        }

        return controllers;
    }

    private string ReadDeviceString(uint index, TrackedDeviceProperty property)
    {
        if (_system is null)
        {
            return "";
        }

        const int bufferSize = 1024;
        var buffer = Marshal.AllocCoTaskMem(bufferSize);
        try
        {
            var error = TrackedPropertyError.Success;
            var length = _system.Value.GetStringTrackedDeviceProperty(
                index,
                property,
                buffer,
                bufferSize,
                ref error);
            return error == TrackedPropertyError.Success && length > 1
                ? Marshal.PtrToStringUTF8(buffer) ?? ""
                : "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private IReadOnlyList<ActionBinding> GetBindings(ulong action, string logicalInput)
    {
        if (!_supportsBindingInspection)
        {
            return [];
        }

        const int capacity = 32;
        var bindings = new InputBindingInfo[capacity];
        uint count = 0;
        var error = _input.GetActionBindingInfo(
            action,
            bindings,
            (uint)Marshal.SizeOf<InputBindingInfo>(),
            capacity,
            ref count);

        if (error == VrInputError.NoData)
        {
            return [];
        }

        EnsureSuccess(error, "GetActionBindingInfo");
        return bindings
            .Take((int)Math.Min(count, capacity))
            .Select(binding => new ActionBinding(
                logicalInput,
                binding.DevicePathName ?? "",
                binding.InputPathName ?? "",
                binding.ModeName ?? "",
                binding.SlotName ?? ""))
            .ToArray();
    }

    private static string FriendlyControllerName(string type, string model) =>
        type.ToLowerInvariant() switch
        {
            "vive_controller" => "HTC Vive controllers",
            "knuckles" => "Valve Index controllers",
            "oculus_touch" => "Meta/Oculus Touch controllers",
            "holographic_controller" => "Windows Mixed Reality controllers",
            "vive_cosmos_controller" => "HTC Vive Cosmos controllers",
            "" when !string.IsNullOrWhiteSpace(model) => model,
            "" => "VR controller",
            _ when !string.IsNullOrWhiteSpace(model) => model,
            _ => type.Replace('_', ' ')
        };

    private static string FriendlyBindingName(ActionBinding binding)
    {
        var hand = binding.DevicePath.Contains("/left", StringComparison.OrdinalIgnoreCase)
            ? "Left "
            : binding.DevicePath.Contains("/right", StringComparison.OrdinalIgnoreCase)
                ? "Right "
                : "";
        var path = binding.InputPath.ToLowerInvariant();
        var input = path switch
        {
            _ when path.Contains("/grip") => "Grip",
            _ when path.Contains("/trigger") => "Trigger",
            _ when path.Contains("/trackpad") => "Trackpad",
            _ when path.Contains("/thumbstick") => "Thumbstick",
            _ when path.Contains("/input/a") => "A Button",
            _ when path.Contains("/input/b") => "B Button",
            _ when path.Contains("/input/x") => "X Button",
            _ when path.Contains("/input/y") => "Y Button",
            _ when path.Contains("/menu") => "Menu Button",
            _ => "chosen input"
        };
        return hand + input;
    }

    private static T LoadExport<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static T GetTableDelegate<T>(nint table, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(table, index * IntPtr.Size));

    private static void EnsureSuccess(VrInputError error, string operation)
    {
        if (error != VrInputError.None)
        {
            throw new InvalidOperationException($"{operation} failed: {error} ({(int)error}).");
        }
    }

    private static void EnsureOverlaySuccess(
        VrOverlayError error,
        string operation)
    {
        if (error != VrOverlayError.None)
        {
            throw new InvalidOperationException(
                $"{operation} failed: {error} ({(int)error}).");
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

    private enum VrOverlayError
    {
        None = 0
    }

    private enum TrackedDeviceClass
    {
        Invalid = 0,
        Hmd = 1,
        Controller = 2
    }

    private enum TrackedControllerRole
    {
        Invalid = 0,
        LeftHand = 1,
        RightHand = 2
    }

    private enum TrackedDeviceProperty
    {
        ModelNumber = 1001,
        ControllerType = 7000
    }

    private enum TrackedPropertyError
    {
        Success = 0
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

        private nint GetAnalogActionData;
        private nint GetPoseActionDataRelativeToNow;
        private nint GetPoseActionDataForNextFrame;
        private nint GetSkeletalActionData;
        private nint GetDominantHand;
        private nint SetDominantHand;
        private nint GetEyeTrackingDataRelativeToNow;
        private nint GetEyeTrackingDataForNextFrame;
        private nint GetBoneCount;
        private nint GetBoneHierarchy;
        private nint GetBoneName;
        private nint GetSkeletalReferenceTransforms;
        private nint GetSkeletalTrackingLevel;
        private nint GetSkeletalBoneData;
        private nint GetSkeletalSummaryData;
        private nint GetSkeletalBoneDataCompressed;
        private nint DecompressSkeletalBoneData;
        private nint TriggerHapticVibrationAction;
        private nint GetActionOrigins;
        private nint GetOriginLocalizedName;
        private nint GetOriginTrackedDeviceInfo;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetActionBindingInfoDelegate GetActionBindingInfo;

        private nint ShowActionOrigins;
        private nint ShowBindingsForActionSet;
        private nint GetComponentStateForBinding;
        private nint IsUsingLegacyInput;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public OpenBindingUiDelegate OpenBindingUi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrSystemFunctions
    {
        private nint GetRecommendedRenderTargetSize;
        private nint GetProjectionMatrix;
        private nint GetProjectionRaw;
        private nint ComputeDistortion;
        private nint ComputeDistortionSet;
        private nint GetEyeToHeadTransform;
        private nint GetTimeSinceLastVsync;
        private nint GetD3D9AdapterIndex;
        private nint GetDxgiOutputInfo;
        private nint GetOutputDevice;
        private nint IsDisplayOnDesktop;
        private nint SetDisplayVisibility;
        private nint GetDeviceToAbsoluteTrackingPose;
        private nint GetSeatedZeroPoseToStandingAbsoluteTrackingPose;
        private nint GetRawZeroPoseToStandingAbsoluteTrackingPose;
        private nint GetSortedTrackedDeviceIndicesOfClass;
        private nint GetTrackedDeviceActivityLevel;
        private nint ApplyTransform;
        private nint GetTrackedDeviceIndexForControllerRole;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetControllerRoleForTrackedDeviceIndexDelegate GetControllerRoleForTrackedDeviceIndex;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetTrackedDeviceClassDelegate GetTrackedDeviceClass;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public IsTrackedDeviceConnectedDelegate IsTrackedDeviceConnected;

        private nint GetBoolTrackedDeviceProperty;
        private nint GetFloatTrackedDeviceProperty;
        private nint GetInt32TrackedDeviceProperty;
        private nint GetUint64TrackedDeviceProperty;
        private nint GetMatrix34TrackedDeviceProperty;
        private nint GetArrayTrackedDeviceProperty;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetStringTrackedDevicePropertyDelegate GetStringTrackedDeviceProperty;

        private nint GetPropErrorNameFromEnum;
        private nint PollNextEvent;
        private nint PollNextEventWithPose;
        private nint PollNextEventWithPoseAndOverlays;
        private nint GetEventTypeNameFromEnum;
        private nint GetHiddenAreaMesh;
        private nint GetEyeTrackedFoveationCenter;
        private nint GetEyeTrackedFoveationCenterForProjection;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetControllerStateDelegate? GetControllerState;
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError GetActionBindingInfoDelegate(
        ulong action,
        [In, Out] InputBindingInfo[] bindingInfo,
        uint bindingInfoSize,
        uint bindingInfoCount,
        ref uint returnedBindingInfoCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrInputError OpenBindingUiDelegate(
        nint appKey,
        ulong actionSet,
        ulong device,
        [MarshalAs(UnmanagedType.I1)] bool showOnDesktop);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate TrackedControllerRole GetControllerRoleForTrackedDeviceIndexDelegate(
        uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate TrackedDeviceClass GetTrackedDeviceClassDelegate(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsTrackedDeviceConnectedDelegate(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetStringTrackedDevicePropertyDelegate(
        uint deviceIndex,
        TrackedDeviceProperty property,
        nint value,
        uint bufferSize,
        ref TrackedPropertyError error);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayWidthInMetersDelegate(
        ulong overlayHandle,
        float widthInMeters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayFlagDelegate(
        ulong overlayHandle,
        int overlayFlag,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool PollNextOverlayEventDelegate(
        ulong overlayHandle,
        nint eventBuffer,
        uint eventBufferSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayInputMethodDelegate(
        ulong overlayHandle,
        int inputMethod);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayMouseScaleDelegate(
        ulong overlayHandle,
        ref HmdVector2 mouseScale);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayFromFileDelegate(
        ulong overlayHandle,
        nint filePath);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError CreateDashboardOverlayDelegate(
        nint overlayKey,
        nint friendlyName,
        ref ulong mainHandle,
        ref ulong thumbnailHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ShowDashboardDelegate(nint overlayKey);

    private readonly record struct VrOverlayFunctions(
        SetOverlayWidthInMetersDelegate SetOverlayWidthInMeters,
        SetOverlayFlagDelegate SetOverlayFlag,
        PollNextOverlayEventDelegate PollNextOverlayEvent,
        SetOverlayInputMethodDelegate SetOverlayInputMethod,
        SetOverlayMouseScaleDelegate SetOverlayMouseScale,
        SetOverlayFromFileDelegate SetOverlayFromFile,
        CreateDashboardOverlayDelegate CreateDashboardOverlay,
        ShowDashboardDelegate ShowDashboard);

    private readonly record struct PhysicalActionDefinition(
        ControllerHand Hand,
        uint Button,
        string ActionPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdVector2
    {
        public float X;
        public float Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetControllerStateDelegate(
        uint deviceIndex,
        ref VrControllerState state,
        uint stateSize);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct VrControllerState
    {
        public uint PacketNumber;
        public ulong ButtonPressed;
        public ulong ButtonTouched;
        public VrControllerAxis Axis0;
        public VrControllerAxis Axis1;
        public VrControllerAxis Axis2;
        public VrControllerAxis Axis3;
        public VrControllerAxis Axis4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrControllerAxis
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct InputBindingInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DevicePathName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? InputPathName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? ModeName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? SlotName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? InputSourceType;
    }
}

public readonly record struct InputSnapshot(
    bool ButtonOne,
    bool ButtonTwo,
    ulong LeftButtons = 0,
    ulong RightButtons = 0);

public sealed record RecordedGesture(
    ControllerInputBinding SafetyInput,
    ControllerInputBinding ActionInput,
    string ControllerFamily);
