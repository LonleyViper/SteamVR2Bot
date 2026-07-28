using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SvrBridge.Core;

public sealed class OpenVrInput : IOpenVrSession
{
    private const string ActionSetPath = "/actions/svrbridge";
    private const string ButtonOnePath = "/actions/svrbridge/in/button_one";
    private const string ButtonTwoPath = "/actions/svrbridge/in/button_two";

    // k_nActionSetOverlayGlobalPriorityMin. The 2026-07-27 headset runs showed
    // SteamVR accepting the band maximum and deactivating this action set under
    // dashboard focus regardless, so there is nothing to gain by raising it.
    private const int OverlayGlobalPriorityMin = 16_777_216;
    private const int VrEventQuit = 700;
    private const int MaxTrackedDevices = 64;
    private const uint InvalidDeviceIndex = uint.MaxValue;
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
    private readonly InputProbe _probe;
    private readonly TrackedDevicePose[] _poses = new TrackedDevicePose[MaxTrackedDevices];
    private ControllerSetup _lastSetup = ControllerSetup.Unknown;
    private uint _leftDeviceIndex = InvalidDeviceIndex;
    private uint _rightDeviceIndex = InvalidDeviceIndex;
    private bool _probeMotion;
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
        _probe = new InputProbe(log);
        var dllPath = ResolveOpenVrDll(configuredDllPath);
        log($"OpenVR DLL: {dllPath}");

        _library = NativeLibrary.Load(dllPath);
        var init = LoadExport<VrInitInternal>(_library, "VR_InitInternal");
        _shutdown = LoadExport<VrShutdownInternal>(_library, "VR_ShutdownInternal");
        var getInterface = LoadExport<VrGetGenericInterface>(_library, "VR_GetGenericInterface");

        var initError = VrInitError.None;
        _ = init(ref initError, VrApplicationType.Background);
        if (initError != VrInitError.None)
        {
            NativeLibrary.Free(_library);
            throw new InvalidOperationException(
                initError == VrInitError.NoServerForBackgroundApp
                    ? "SteamVR is not running."
                    : $"OpenVR initialization failed: {initError} ({(int)initError}).");
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

    /// <summary>
    /// Turns the read-only input probe on or off. The probe only logs; it never
    /// changes how input is delivered, so it is safe to leave wired in.
    /// </summary>
    public void SetInputProbeEnabled(bool enabled)
    {
        _probe.SetEnabled(enabled, Environment.TickCount64);
        _probeMotion = enabled;
    }

    /// <summary>
    /// Samples head and controller poses on every <see cref="Poll"/>. Motion
    /// recognition needs this permanently on; the probe turns it on by itself
    /// while the recorder page is showing.
    /// </summary>
    public bool MotionSamplingEnabled { get; set; }

    /// <summary>
    /// The most recent motion sample, in the wearer's body frame. Stale when
    /// motion sampling is off.
    /// </summary>
    public MotionSample LatestMotion { get; private set; }

    public InputSnapshot Poll()
    {
        ThrowIfDisposed();

        _probe.BeginPoll();
        EnsureSuccess(
            _input.UpdateActionState(
                _activeSets,
                (uint)Marshal.SizeOf<VrActiveActionSet>(),
                (uint)_activeSets.Length),
            "UpdateActionState");

        ProbeDashboardState();
        if (MotionSamplingEnabled || _probeMotion)
        {
            LatestMotion = SampleMotion(Environment.TickCount64);
            _probe.ObserveMotion(LatestMotion);
        }

        var (leftButtons, rightButtons) = ReadControllerButtons();
        var snapshot = new InputSnapshot(
            ReadDigital(_buttonOne, "button_one") || IsPressed(leftButtons, 2),
            ReadDigital(_buttonTwo, "button_two") || IsPressed(rightButtons, 33),
            leftButtons,
            rightButtons);
        _probe.EndPoll(Environment.TickCount64);
        return snapshot;
    }

    /// <summary>
    /// Reads head and controller poses and expresses the hands in the wearer's
    /// body frame.
    /// <para>
    /// This deliberately uses <c>IVRSystem.GetDeviceToAbsoluteTrackingPose</c>
    /// rather than IVRInput pose actions. Poses are a tracking query, not
    /// input, so they keep arriving while SteamVR's dashboard owns input focus
    /// — the exact condition that starves the digital action path. It also
    /// needs no action-manifest or binding changes.
    /// </para>
    /// </summary>
    private MotionSample SampleMotion(long nowMs)
    {
        if (_system is null || _system.Value.GetDeviceToAbsoluteTrackingPose is null)
        {
            return MotionSample.Untracked(nowMs);
        }

        // Zero prediction: a predicted pose is smoothed towards where the
        // device is expected to be, which blunts exactly the direction changes
        // a gesture recognizer is looking for.
        _system.Value.GetDeviceToAbsoluteTrackingPose(
            TrackingUniverseOrigin.Standing,
            0f,
            _poses,
            (uint)_poses.Length);

        var head = _poses[0];
        if (!head.IsUsable)
        {
            return MotionSample.Untracked(nowMs);
        }

        var frame = BodyFrame.FromHead(head.Position, head.Forward, head.Up);
        RefreshControllerIndices();

        var tracking = MotionTracking.Head;
        if (TryReadHand(_leftDeviceIndex, frame, out var leftPosition, out var leftVelocity))
        {
            tracking |= MotionTracking.Left;
        }

        if (TryReadHand(_rightDeviceIndex, frame, out var rightPosition, out var rightVelocity))
        {
            tracking |= MotionTracking.Right;
        }

        return new MotionSample(
            nowMs,
            tracking,
            leftPosition,
            leftVelocity,
            rightPosition,
            rightVelocity,
            head.Position.Y);
    }

    private bool TryReadHand(
        uint deviceIndex,
        BodyFrame frame,
        out Vector3 position,
        out Vector3 velocity)
    {
        position = Vector3.Zero;
        velocity = Vector3.Zero;
        if (deviceIndex >= (uint)_poses.Length)
        {
            return false;
        }

        var pose = _poses[deviceIndex];
        if (!pose.IsUsable)
        {
            return false;
        }

        position = frame.ToLocalPoint(pose.Position);
        velocity = frame.ToLocalDirection(pose.Velocity.ToVector());
        return true;
    }

    /// <summary>
    /// Keeps the cached left/right device indices current. The common case is
    /// two cheap role lookups; the full scan only runs when a controller is
    /// swapped, reassigned, or wakes up.
    /// </summary>
    private void RefreshControllerIndices()
    {
        if (_system is null
            || (HasRole(_leftDeviceIndex, TrackedControllerRole.LeftHand)
                && HasRole(_rightDeviceIndex, TrackedControllerRole.RightHand)))
        {
            return;
        }

        _leftDeviceIndex = InvalidDeviceIndex;
        _rightDeviceIndex = InvalidDeviceIndex;
        for (uint index = 0; index < (uint)_poses.Length; index++)
        {
            switch (_system.Value.GetControllerRoleForTrackedDeviceIndex(index))
            {
                case TrackedControllerRole.LeftHand
                    when _leftDeviceIndex == InvalidDeviceIndex:
                    _leftDeviceIndex = index;
                    break;
                case TrackedControllerRole.RightHand
                    when _rightDeviceIndex == InvalidDeviceIndex:
                    _rightDeviceIndex = index;
                    break;
            }
        }
    }

    private bool HasRole(uint deviceIndex, TrackedControllerRole role) =>
        _system is not null
        && deviceIndex < (uint)_poses.Length
        && _system.Value.GetControllerRoleForTrackedDeviceIndex(deviceIndex) == role;

    private void ProbeDashboardState()
    {
        if (!_probe.Enabled || _overlay is null || _dashboardHandle == 0)
        {
            return;
        }

        _probe.ObserveDashboard(
            _overlay.Value.IsDashboardVisible(),
            _overlay.Value.IsActiveDashboardOverlay(_dashboardHandle));
    }

    public ControllerSetup GetControllerSetup()
    {
        ThrowIfDisposed();
        _lastSetup = BuildControllerSetup();
        return _lastSetup;
    }

    private ControllerSetup BuildControllerSetup()
    {
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
                "This SteamVR version cannot open controller bindings from SteamVR2Bot.");
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
        var name = Marshal.StringToCoTaskMemUTF8("SteamVR2Bot");
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
                // The taskbar strip at the bottom of the SteamVR dashboard shows
                // this thumbnail while the app is running. It must stay the
                // static app icon rather than whatever page is currently
                // rendered, or it flips to a screenshot of the shortcut list.
                var iconPath = Path.Combine(AppContext.BaseDirectory, "SteamVR2Bot.png");
                var thumbnailSource = File.Exists(iconPath) ? iconPath : imagePath;
                var thumbnailImage = thumbnailSource == imagePath
                    ? image
                    : Marshal.StringToCoTaskMemUTF8(thumbnailSource);
                try
                {
                    EnsureOverlaySuccess(
                        _overlay.Value.SetOverlayFromFile(_dashboardThumbnailHandle, thumbnailImage),
                        "SetOverlayFromFile(thumbnail)");
                }
                finally
                {
                    if (thumbnailImage != image)
                    {
                        Marshal.FreeCoTaskMem(thumbnailImage);
                    }
                }

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

    public bool IsDashboardActive =>
        _overlay is not null
        && _dashboardHandle != 0
        && _overlay.Value.IsDashboardVisible()
        && _overlay.Value.IsActiveDashboardOverlay(_dashboardHandle);

    /// <summary>
    /// True once SteamVR has asked every application to quit. Draining the
    /// system queue here cannot swallow dashboard input, because overlay
    /// events are delivered on a separate per-overlay queue.
    /// </summary>
    public bool IsQuitRequested()
    {
        ThrowIfDisposed();
        if (_system?.PollNextEvent is not { } pollNextEvent)
        {
            return false;
        }

        const int eventBufferSize = 64;
        var eventBuffer = Marshal.AllocCoTaskMem(eventBufferSize);
        try
        {
            while (pollNextEvent(eventBuffer, eventBufferSize))
            {
                if (Marshal.ReadInt32(eventBuffer) == VrEventQuit)
                {
                    _log("SteamVR is shutting down.");
                    return true;
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(eventBuffer);
        }

        return false;
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
                LogDashboardLifecycleEvent(eventType);
                ProbeOverlayEvent(eventType, eventBuffer);
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

    /// <summary>
    /// Reports one raw dashboard overlay event to the probe. VREvent_t is a
    /// 16-byte header (eventType, trackedDeviceIndex, eventAgeSeconds, pad)
    /// followed by the data union, so VREvent_Controller_t.button sits at
    /// offset 16 and the device index at offset 4.
    /// </summary>
    private void ProbeOverlayEvent(int eventType, nint eventBuffer)
    {
        if (!_probe.Enabled)
        {
            return;
        }

        var deviceIndex = (uint)Marshal.ReadInt32(eventBuffer, 4);
        var isButtonEvent = InputProbe.OverlayEventName(eventType) is not null;
        var button = isButtonEvent
            ? (uint)Marshal.ReadInt32(eventBuffer, 16)
            : 0;
        _probe.ObserveOverlayEvent(
            eventType,
            deviceIndex,
            button,
            isButtonEvent ? DescribeOverlayButton(deviceIndex, button) : "");
    }

    private string DescribeOverlayButton(uint deviceIndex, uint button)
    {
        // Events can carry k_unTrackedDeviceIndexInvalid; keep the lookup inside
        // the same device range the rest of this class scans.
        if (_system is null || deviceIndex >= 64)
        {
            return $"button {button}, no controller";
        }

        return _system.Value.GetControllerRoleForTrackedDeviceIndex(deviceIndex) switch
        {
            TrackedControllerRole.LeftHand => ControllerInputs.FriendlyName(
                ControllerHand.Left,
                button,
                _lastSetup),
            TrackedControllerRole.RightHand => ControllerInputs.FriendlyName(
                ControllerHand.Right,
                button,
                _lastSetup),
            _ => $"button {button}, unassigned hand"
        };
    }

    private void LogDashboardLifecycleEvent(int eventType)
    {
        var message = eventType switch
        {
            500 => "SteamVR dashboard overlay shown.",
            501 => "SteamVR dashboard overlay hidden.",
            502 => "SteamVR dashboard activated.",
            503 => "SteamVR dashboard deactivated.",
            508 => "SteamVR dashboard image loaded.",
            517 => "SteamVR dashboard image failed to load.",
            534 => "SteamVR dashboard overlay closed.",
            _ => null
        };
        if (message is not null)
        {
            _log(message);
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
            "Could not locate openvr_api.dll. Start SteamVR once, copy the DLL beside SteamVR2Bot, " +
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
            GetTableDelegate<IsDashboardVisibleDelegate>(pointer, 68),
            GetTableDelegate<IsActiveDashboardOverlayDelegate>(pointer, 69),
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

    private bool ReadDigital(ulong handle, string? probeName = null)
    {
        var data = new InputDigitalActionData();
        var error = _input.GetDigitalActionData(
            handle,
            ref data,
            (uint)Marshal.SizeOf<InputDigitalActionData>(),
            0);

        if (probeName is not null)
        {
            _probe.ObserveAction(
                new ProbeActionState(
                    probeName,
                    (int)error,
                    data.Active,
                    data.State,
                    data.Changed,
                    data.ActiveOrigin));
        }

        if (error == VrInputError.NoData)
        {
            return false;
        }

        EnsureSuccess(error, "GetDigitalActionData");
        return data.Active && data.State;
    }

    private (ulong Left, ulong Right) ReadControllerButtons()
    {
        ulong left = 0;
        ulong right = 0;
        foreach (var definition in PhysicalActions)
        {
            if (!ReadDigital(
                    _physicalButtons[(definition.Hand, definition.Button)],
                    definition.ProbeName))
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
        // Background never starts SteamVR, and does not hold it open once
        // everything else quits. The app now auto-launches with SteamVR and
        // exits with it, so booting SteamVR from the desktop would fight both
        // halves of that lifecycle.
        Background = 3
    }

    private enum VrInitError
    {
        None = 0,
        NoServerForBackgroundApp = 312
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

        // Index 12. Everything up to GetStringTrackedDeviceProperty is already
        // proven correct by live controller detection, so this entry is inside
        // the validated prefix of the table.
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public GetDeviceToAbsoluteTrackingPoseDelegate? GetDeviceToAbsoluteTrackingPose;

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

        // VREvent_Quit arrives on the system queue, which is separate from the
        // dashboard overlay queue, so noticing SteamVR shut down needs this
        // entry rather than PollNextOverlayEvent.
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public PollNextEventDelegate? PollNextEvent;

        private nint PollNextEventWithPose;
        private nint PollNextEventWithPoseAndOverlays;
        private nint GetEventTypeNameFromEnum;
        private nint GetHiddenAreaMesh;
        private nint GetEyeTrackedFoveationCenter;
        private nint GetEyeTrackedFoveationCenterForProjection;
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
    private delegate void GetDeviceToAbsoluteTrackingPoseDelegate(
        TrackingUniverseOrigin origin,
        float predictedSecondsToPhotonsFromNow,
        [In, Out] TrackedDevicePose[] poses,
        uint poseCount);

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
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool PollNextEventDelegate(
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
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsDashboardVisibleDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsActiveDashboardOverlayDelegate(ulong overlayHandle);

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
        IsDashboardVisibleDelegate IsDashboardVisible,
        IsActiveDashboardOverlayDelegate IsActiveDashboardOverlay,
        ShowDashboardDelegate ShowDashboard);

    private readonly record struct PhysicalActionDefinition(
        ControllerHand Hand,
        uint Button,
        string ActionPath)
    {
        /// <summary>Short log label, for example "left_grip".</summary>
        public string ProbeName => ActionPath[(ActionPath.LastIndexOf('/') + 1)..];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdVector2
    {
        public float X;
        public float Y;
    }

    private enum TrackingUniverseOrigin
    {
        Seated = 0,
        Standing = 1,
        RawAndUncalibrated = 2
    }

    private enum TrackingResult
    {
        Uninitialized = 1,
        CalibratingInProgress = 100,
        CalibratingOutOfRange = 101,
        RunningOk = 200,
        RunningOutOfRange = 201,
        FallbackRotationOnly = 300
    }

    /// <summary>Row-major 3x4 transform; the fourth column is translation.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HmdMatrix34
    {
        public float M00;
        public float M01;
        public float M02;
        public float M03;
        public float M10;
        public float M11;
        public float M12;
        public float M13;
        public float M20;
        public float M21;
        public float M22;
        public float M23;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdVector3
    {
        public float X;
        public float Y;
        public float Z;

        public readonly Vector3 ToVector() => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackedDevicePose
    {
        public HmdMatrix34 DeviceToAbsoluteTracking;
        public HmdVector3 Velocity;
        public HmdVector3 AngularVelocity;
        public TrackingResult Result;

        // Held as bytes rather than bool so the array stays blittable and can
        // be pinned. Marshalled bools would force an element-by-element copy of
        // all 64 poses on every poll.
        public byte PoseIsValid;
        public byte DeviceIsConnected;

        public readonly bool IsUsable =>
            PoseIsValid != 0
            && DeviceIsConnected != 0
            && Result == TrackingResult.RunningOk;

        public readonly Vector3 Position => new(
            DeviceToAbsoluteTracking.M03,
            DeviceToAbsoluteTracking.M13,
            DeviceToAbsoluteTracking.M23);

        /// <summary>World direction the device faces; OpenVR looks down -Z.</summary>
        public readonly Vector3 Forward => new(
            -DeviceToAbsoluteTracking.M02,
            -DeviceToAbsoluteTracking.M12,
            -DeviceToAbsoluteTracking.M22);

        public readonly Vector3 Up => new(
            DeviceToAbsoluteTracking.M01,
            DeviceToAbsoluteTracking.M11,
            DeviceToAbsoluteTracking.M21);
    }

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
