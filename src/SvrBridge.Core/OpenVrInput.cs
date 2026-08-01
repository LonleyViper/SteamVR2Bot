using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SvrBridge.Core;

public sealed class OpenVrInput : IOpenVrSession, IVrOverlayApi
{
    private const string ActionSetPath = "/actions/svrbridge";
    private const string ButtonOnePath = "/actions/svrbridge/in/button_one";
    private const string ButtonTwoPath = "/actions/svrbridge/in/button_two";

    // The ordinary priority band, and it must stay there.
    //
    // openvr.h defines k_nActionSetOverlayGlobalPriorityMin (16_777_216): any
    // action set at or above it takes input away from the scene application.
    // Priority is per-action-set, not per-action, and the packaged binding
    // claims grip, trigger, trackpad and menu on both hands unconditionally -
    // regardless of what the user has actually mapped to a shortcut. So sitting
    // in that band does not politely observe those controls, it takes all eight
    // away from whatever game is running. That is what killed grip in
    // Contractors VR and Showdown on 2026-07-29, and it costs a long bisect to
    // rediscover: the app's own log shows the edges arriving normally, because
    // it is this app receiving them that is the bug, and SteamVR's Controller
    // Binding UI looks correct, because the config is fine and it is live
    // routing that breaks.
    //
    // Nothing is lost by staying here. Re-tested in-headset at this priority on
    // 2026-07-29: shortcuts still fire from inside a running game, and the game
    // receives grip at the same time. Raising it buys no edge this app needs.
    private const int ActionSetPriority = 0;
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
        new(ControllerHand.Right, 32, "/actions/svrbridge/in/right_trackpad"),
        // Index A/B and Touch X/Y share these two slots - see
        // ControllerInputs.FriendlyName, which is what actually decides
        // which physical label button 7/34 reads as for a given hand and
        // controller type. The action names stay neutral ("face1"/"face2")
        // on purpose: one pair of actions serves every family that has a
        // face-button pair, rather than one named for Index and a duplicate
        // named for Touch.
        new(ControllerHand.Left, 7, "/actions/svrbridge/in/left_face1"),
        new(ControllerHand.Left, 34, "/actions/svrbridge/in/left_face2"),
        new(ControllerHand.Right, 7, "/actions/svrbridge/in/right_face1"),
        new(ControllerHand.Right, 34, "/actions/svrbridge/in/right_face2")
    ];

    /// <summary>
    /// The physical-input to action-path wiring <see cref="ReadControllerButtons"/>
    /// uses to assemble each hand's bitmask - the same array, not a parallel
    /// copy. Public and data-only (no native call) so a self-test can prove a
    /// new action path is wired to the exact button number
    /// <see cref="ControllerInputs.AvailableInputs"/> and
    /// <see cref="ControllerInputs.FriendlyName"/> already expect, without a
    /// live OpenVR session.
    /// </summary>
    public static IReadOnlyList<(ControllerHand Hand, uint Button, string ActionPath)> PhysicalActionMap { get; } =
        Array.ConvertAll(
            PhysicalActions,
            definition => (definition.Hand, definition.Button, definition.ActionPath));

    /// <summary>
    /// Reads the declared physical actions and assembles the exact per-hand
    /// bitmasks consumed by recording and gesture detection. This is the
    /// production mapping seam used by <see cref="ReadControllerButtons"/>;
    /// it is public and native-free so the self-test can drive one digital
    /// action at a time and prove its resulting button number.
    /// </summary>
    public static (ulong Left, ulong Right) MapPhysicalActionStates(
        Func<ControllerHand, uint, string, string, bool> isPressed)
    {
        ulong left = 0;
        ulong right = 0;
        foreach (var definition in PhysicalActions)
        {
            if (!isPressed(
                    definition.Hand,
                    definition.Button,
                    definition.ActionPath,
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

    /// <summary>
    /// Applies the physical-input fallback that keeps the packaged default
    /// gesture working even when the legacy logical actions are unbound:
    /// left grip is Button One and right trigger is Button Two. The runtime
    /// and binding-consistency self-test share this exact method.
    /// </summary>
    public static (bool ButtonOne, bool ButtonTwo) MapDefaultGestureActions(
        bool buttonOne,
        bool buttonTwo,
        ulong leftButtons,
        ulong rightButtons) =>
        (buttonOne || IsPressed(leftButtons, 2),
         buttonTwo || IsPressed(rightButtons, 33));

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
    private const string DashboardOverlayKey = "ie.lonelyviper.svrbridge.dashboard";

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
                    ? "Could not reach SteamVR. Either it is not running, or it is running as "
                      + "administrator while this app is not - launch both elevated, or neither, "
                      + "and make sure SteamVR is fully started first."
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
                    Priority = ActionSetPriority
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

    /// <summary>
    /// One tracked device's world pose from the most recent
    /// <see cref="Poll"/>, in OpenVR's own 3x4 layout.
    /// <para>
    /// Deliberately separate from <see cref="LatestMotion"/> rather than added
    /// to it. <see cref="MotionSample"/> is the gesture recognizers' input: a
    /// yaw-only body frame carrying positions and velocities and no device
    /// rotation at all, shaped that way on purpose so a level sweep does not
    /// read as a diagonal one when the wearer glances down. Grabbing a panel
    /// needs the opposite - the raw pose, rotation included - and bending the
    /// gesture frame to also serve that would put a hardware-validated
    /// recognition path at risk for a UI feature. This reads the same pose
    /// array the sample is built from and converts nothing.
    /// </para>
    /// <para>
    /// Only meaningful while <see cref="MotionSamplingEnabled"/> is on, which
    /// is exactly when a panel that can be grabbed exists. Stale otherwise.
    /// </para>
    /// </summary>
    public bool TryGetDevicePose(uint deviceIndex, out VrOverlayTransform pose)
    {
        pose = VrOverlayTransform.Identity;
        if (deviceIndex >= (uint)_poses.Length)
        {
            return false;
        }

        var device = _poses[deviceIndex];
        if (!device.IsUsable)
        {
            return false;
        }

        // A field-for-field copy, not a conversion: HmdMatrix34 and
        // VrOverlayTransform are the same layout by construction - see
        // VrOverlayTransform's own remarks on why it mirrors OpenVR rather
        // than reusing Matrix4x4.
        var matrix = device.DeviceToAbsoluteTracking;
        pose = new VrOverlayTransform(
            matrix.M00, matrix.M01, matrix.M02, matrix.M03,
            matrix.M10, matrix.M11, matrix.M12, matrix.M13,
            matrix.M20, matrix.M21, matrix.M22, matrix.M23);
        return true;
    }

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
        var (buttonOne, buttonTwo) = MapDefaultGestureActions(
            ReadDigital(_buttonOne, "button_one"),
            ReadDigital(_buttonTwo, "button_two"),
            leftButtons,
            rightButtons);
        var snapshot = new InputSnapshot(
            buttonOne,
            buttonTwo,
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

    /// <summary>
    /// Creates and configures the dashboard overlay if it does not exist yet,
    /// and loads its taskbar thumbnail once. Idempotent - every page render
    /// calls it, and after the first it does nothing.
    /// <para>
    /// Separated from the texture upload because the dashboard no longer has
    /// a single "update from this file" call. The page pixels now go through
    /// <see cref="SetDashboardTexture"/> or
    /// <see cref="SetDashboardD3D11Texture"/>, the same two paths every other
    /// overlay uses.
    /// </para>
    /// </summary>
    public void EnsureDashboardCreated()
    {
        ThrowIfDisposed();
        if (_overlay is null)
        {
            throw new InvalidOperationException(
                "This SteamVR version did not expose dashboard overlays.");
        }

        if (_dashboardHandle != 0)
        {
            return;
        }

        var key = Marshal.StringToCoTaskMemUTF8(DashboardOverlayKey);
        var name = Marshal.StringToCoTaskMemUTF8("SteamVR2Bot");
        try
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
        finally
        {
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(name);
        }

        InitializeDashboardThumbnail();
    }

    /// <summary>
    /// Loads the static app icon into the dashboard's taskbar thumbnail, once.
    /// <para>
    /// <b>Deliberately still <c>SetOverlayFromFile</c>.</b> This is a genuinely
    /// file-based, one-time load of an icon on disk - not a repaint - so none
    /// of the reasons the page path was converted apply to it. It is also the
    /// one place where feeding the current page would be actively wrong: the
    /// taskbar strip at the bottom of the SteamVR dashboard shows this while
    /// the app is running, and it must stay the app icon rather than a
    /// screenshot of the shortcut list.
    /// </para>
    /// <para>
    /// A missing icon file now simply leaves the thumbnail unset. The old code
    /// fell back to the current page image, which is precisely the bug above;
    /// with the page no longer on disk there is nothing to fall back to, and
    /// nothing worth falling back to either.
    /// </para>
    /// </summary>
    private void InitializeDashboardThumbnail()
    {
        if (_dashboardThumbnailInitialized)
        {
            return;
        }

        _dashboardThumbnailInitialized = true;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "SteamVR2Bot.png");
        if (!File.Exists(iconPath))
        {
            return;
        }

        var thumbnailImage = Marshal.StringToCoTaskMemUTF8(iconPath);
        try
        {
            EnsureOverlaySuccess(
                _overlay!.Value.SetOverlayFromFile(_dashboardThumbnailHandle, thumbnailImage),
                "SetOverlayFromFile(thumbnail)");
        }
        finally
        {
            Marshal.FreeCoTaskMem(thumbnailImage);
        }
    }

    /// <summary>
    /// Uploads a dashboard page from straight-alpha RGBA bytes - the fallback
    /// path, used when no Direct3D device is available. Blinks on every write,
    /// like every CPU upload.
    /// </summary>
    public void SetDashboardTexture(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ThrowIfDisposed();
        RequireDashboard();

        var required = (long)width * height * 4;
        if (rgba.Length < required)
        {
            throw new ArgumentException(
                $"The dashboard buffer holds {rgba.Length} bytes but {width}x{height} needs {required}.",
                nameof(rgba));
        }

        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            EnsureOverlaySuccess(
                _overlay!.Value.SetOverlayRaw(
                    _dashboardHandle,
                    pin.AddrOfPinnedObject(),
                    (uint)width,
                    (uint)height,
                    4),
                "SetOverlayRaw(dashboard)");
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>
    /// Uploads a dashboard page from a persistent Direct3D 11 texture - the
    /// blink-free path.
    /// <para>
    /// Note this is a <em>dashboard</em> overlay handle, from
    /// <c>CreateDashboardOverlay</c>, where the spike only ever proved
    /// <c>SetOverlayTexture</c> against a regular one. Nothing in
    /// <c>openvr.h</c> distinguishes them for this call - a dashboard overlay
    /// handle is a <c>VROverlayHandle_t</c> like any other - but that is an
    /// argument, not a test, so it is confirmed in the headset matrix.
    /// </para>
    /// </summary>
    public void SetDashboardD3D11Texture(nint nativeD3D11Texture)
    {
        ThrowIfDisposed();
        RequireDashboard();
        if (nativeD3D11Texture == nint.Zero)
        {
            throw new ArgumentException(
                "The native D3D11 texture pointer is null.",
                nameof(nativeD3D11Texture));
        }

        ((IVrOverlayApi)this).SetOverlayTexture(_dashboardHandle, nativeD3D11Texture);
    }

    /// <summary>Brings the SteamVR dashboard to this app's page.</summary>
    public void ShowDashboardOverlay()
    {
        ThrowIfDisposed();
        RequireDashboard();

        var key = Marshal.StringToCoTaskMemUTF8(DashboardOverlayKey);
        try
        {
            _overlay!.Value.ShowDashboard(key);
        }
        finally
        {
            Marshal.FreeCoTaskMem(key);
        }
    }

    private void RequireDashboard()
    {
        if (_overlay is null || _dashboardHandle == 0)
        {
            throw new InvalidOperationException(
                "The SteamVR dashboard overlay has not been created yet.");
        }
    }

    /// <summary>True when this SteamVR version exposed the overlay interface.</summary>
    public bool SupportsOverlaySurfaces => _overlay is not null;

    /// <summary>
    /// Creates a standalone overlay - not a dashboard one, so it is visible
    /// whether or not the SteamVR dashboard is open.
    /// <para>
    /// Each call returns an independent surface. Keys must be unique within the
    /// process; reusing one that is still alive fails with <c>KeyInUse</c>.
    /// </para>
    /// </summary>
    public VrOverlaySurface CreateOverlaySurface(string key, string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_overlay is null)
        {
            throw new InvalidOperationException(
                "This SteamVR version did not expose overlays.");
        }

        IVrOverlayApi api = this;
        return new VrOverlaySurface(api, key, api.CreateOverlay(key, name));
    }

    /// <summary>
    /// Looks up an overlay this process did not create the handle for. Used by
    /// the self-test to prove that the CreateOverlay and FindOverlay indices
    /// address the functions they claim to.
    /// </summary>
    public bool TryFindOverlayHandle(string key, out ulong handle)
    {
        ThrowIfDisposed();
        handle = 0;
        return _overlay is not null && ((IVrOverlayApi)this).TryFindOverlay(key, out handle);
    }

    /// <summary>
    /// The tracked device index for one hand, re-resolved on every call.
    /// <para>
    /// Returning a fresh answer rather than a cached one is the entire point.
    /// Tracked device indices are not stable across controller sleep,
    /// reconnect, or a battery change, and a role can come back unassigned.
    /// An overlay bound to a stale index detaches with no error reported
    /// anywhere, so callers are expected to ask again and re-apply the
    /// transform when the answer changes.
    /// </para>
    /// </summary>
    /// <returns>Null when no controller currently holds that role.</returns>
    public uint? TryGetControllerDeviceIndex(ControllerHand hand)
    {
        ThrowIfDisposed();
        RefreshControllerIndices();
        var index = hand == ControllerHand.Left ? _leftDeviceIndex : _rightDeviceIndex;
        return index == InvalidDeviceIndex ? null : index;
    }

    ulong IVrOverlayApi.CreateOverlay(string key, string name)
    {
        ulong handle = 0;
        var keyPointer = Marshal.StringToCoTaskMemUTF8(key);
        var namePointer = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            EnsureOverlaySuccess(
                _overlay!.Value.CreateOverlay(keyPointer, namePointer, ref handle),
                $"CreateOverlay({key})");
        }
        finally
        {
            Marshal.FreeCoTaskMem(keyPointer);
            Marshal.FreeCoTaskMem(namePointer);
        }

        return handle;
    }

    bool IVrOverlayApi.TryFindOverlay(string key, out ulong handle)
    {
        ulong found = 0;
        var keyPointer = Marshal.StringToCoTaskMemUTF8(key);
        try
        {
            // UnknownOverlay is the ordinary "no such key" answer, not a
            // failure, so it is the one error this does not throw on.
            var error = _overlay!.Value.FindOverlay(keyPointer, ref found);
            if (error == VrOverlayError.UnknownOverlay)
            {
                handle = 0;
                return false;
            }

            EnsureOverlaySuccess(error, $"FindOverlay({key})");
        }
        finally
        {
            Marshal.FreeCoTaskMem(keyPointer);
        }

        handle = found;
        return found != 0;
    }

    void IVrOverlayApi.DestroyOverlay(ulong handle) =>
        EnsureOverlaySuccess(_overlay!.Value.DestroyOverlay(handle), "DestroyOverlay");

    void IVrOverlayApi.SetOverlayRaw(
        ulong handle,
        nint buffer,
        uint width,
        uint height,
        uint bytesPerPixel) =>
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayRaw(handle, buffer, width, height, bytesPerPixel),
            "SetOverlayRaw");

    void IVrOverlayApi.SetOverlayTexture(ulong handle, nint nativeD3D11Texture)
    {
        var texture = new VrTexture
        {
            Handle = nativeD3D11Texture,
            Type = VrTextureType.DirectX,
            ColorSpace = VrColorSpace.Auto
        };
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayTexture(handle, ref texture),
            "SetOverlayTexture");
    }

    void IVrOverlayApi.SetOverlayWidthInMeters(ulong handle, float widthInMeters) =>
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayWidthInMeters(handle, widthInMeters),
            "SetOverlayWidthInMeters");

    void IVrOverlayApi.SetOverlayInputMethod(ulong handle, int inputMethod) =>
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayInputMethod(handle, inputMethod),
            "SetOverlayInputMethod");

    void IVrOverlayApi.SetOverlayFlag(ulong handle, int flag, bool enabled) =>
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayFlag(handle, flag, enabled),
            $"SetOverlayFlag({flag})");

    void IVrOverlayApi.SetOverlayMouseScale(ulong handle, float width, float height)
    {
        var mouseScale = new HmdVector2 { X = width, Y = height };
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayMouseScale(handle, ref mouseScale),
            "SetOverlayMouseScale");
    }

    /// <summary>
    /// Reads this overlay's queue with the same 64-byte <c>VREvent_t</c>
    /// layout the dashboard path already uses: a 16-byte header
    /// (eventType, trackedDeviceIndex, eventAgeSeconds, pad) followed by the
    /// data union, which puts <c>VREvent_Mouse_t.x</c> at offset 16 and
    /// <c>.y</c> at offset 20.
    /// <para>
    /// Non-pointer events are dropped rather than surfaced. The queue also
    /// carries overlay lifecycle events, and a caller that had to filter them
    /// itself would need this struct layout too.
    /// </para>
    /// </summary>
    void IVrOverlayApi.PollOverlayMouseEvents(ulong handle, List<OverlayMouseEvent> into)
    {
        if (_overlay is null)
        {
            return;
        }

        const int eventBufferSize = 64;
        var eventBuffer = Marshal.AllocCoTaskMem(eventBufferSize);
        try
        {
            while (_overlay.Value.PollNextOverlayEvent(handle, eventBuffer, eventBufferSize))
            {
                var kind = Marshal.ReadInt32(eventBuffer) switch
                {
                    300 => OverlayMouseEventKind.Move,
                    301 => OverlayMouseEventKind.ButtonDown,
                    302 => OverlayMouseEventKind.ButtonUp,
                    304 => OverlayMouseEventKind.FocusLeave,
                    _ => (OverlayMouseEventKind?)null
                };
                if (kind is not { } eventKind)
                {
                    continue;
                }

                // FocusLeave carries VREvent_Overlay_t, not VREvent_Mouse_t -
                // an overlay handle sits where x/y do - so its coordinates are
                // reported as zero rather than as reinterpreted bytes that
                // would hit-test to a real rectangle.
                var isMouse = eventKind != OverlayMouseEventKind.FocusLeave;
                into.Add(
                    new OverlayMouseEvent(
                        eventKind,
                        isMouse ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(eventBuffer, 16)) : 0f,
                        isMouse ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(eventBuffer, 20)) : 0f,
                        (uint)Marshal.ReadInt32(eventBuffer, 4)));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(eventBuffer);
        }
    }

    void IVrOverlayApi.ShowOverlay(ulong handle) =>
        EnsureOverlaySuccess(_overlay!.Value.ShowOverlay(handle), "ShowOverlay");

    void IVrOverlayApi.HideOverlay(ulong handle) =>
        EnsureOverlaySuccess(_overlay!.Value.HideOverlay(handle), "HideOverlay");

    bool IVrOverlayApi.IsOverlayVisible(ulong handle) =>
        _overlay is not null && _overlay.Value.IsOverlayVisible(handle);

    void IVrOverlayApi.SetOverlayAlpha(ulong handle, float alpha) =>
        EnsureOverlaySuccess(_overlay!.Value.SetOverlayAlpha(handle, alpha), "SetOverlayAlpha");

    void IVrOverlayApi.SetOverlaySortOrder(ulong handle, uint sortOrder) =>
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlaySortOrder(handle, sortOrder),
            "SetOverlaySortOrder");

    void IVrOverlayApi.SetOverlayCurvature(ulong handle, float curvature) =>
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayCurvature(handle, curvature),
            "SetOverlayCurvature");

    void IVrOverlayApi.SetOverlayTransformTrackedDeviceRelative(
        ulong handle,
        uint deviceIndex,
        VrOverlayTransform transform)
    {
        // Field-for-field rather than a reinterpret cast: the two types happen
        // to have the same layout today, and relying on that would break
        // silently if either ever gained a member.
        var native = new HmdMatrix34
        {
            M00 = transform.M00,
            M01 = transform.M01,
            M02 = transform.M02,
            M03 = transform.M03,
            M10 = transform.M10,
            M11 = transform.M11,
            M12 = transform.M12,
            M13 = transform.M13,
            M20 = transform.M20,
            M21 = transform.M21,
            M22 = transform.M22,
            M23 = transform.M23
        };
        EnsureOverlaySuccess(
            _overlay!.Value.SetOverlayTransformTrackedDeviceRelative(
                handle,
                deviceIndex,
                ref native),
            "SetOverlayTransformTrackedDeviceRelative");
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

    /// <summary>
    /// Binds the <c>IVROverlay</c> function table by raw vtable index.
    /// <para>
    /// <b>These indices are load-bearing in a way most constants are not.</b> A
    /// wrong index does not throw - it calls a different function with
    /// mismatched arguments, which is an access violation inside SteamVR at
    /// best and silent memory corruption at worst. Nothing here validates them
    /// at runtime, so they have to be right by derivation.
    /// </para>
    /// <para>
    /// <b>How they were derived</b> (repeat this, do not guess individually):
    /// the <c>IVROverlay</c> virtual method declarations in <c>openvr.h</c> were
    /// enumerated once, in declaration order, which is the vtable order. The
    /// header used was ValveSoftware/openvr <c>headers/openvr.h</c> at the
    /// revision whose <c>IVROverlay_Version</c> is literally
    /// <c>"IVROverlay_028"</c> - the same string requested below - with
    /// SHA-256 <c>1E6ED571 99896CC1 F7C5484E 50FA1895 5E97BE15 BE690BEB
    /// 28D998C8 77EAD7FD</c>. That revision also carries
    /// <c>IVRSystem_026</c> and <c>IVRInput_011</c>, which are the two other
    /// versions this file requests, so all three agree on one header.
    /// </para>
    /// <para>
    /// <b>Why the derivation can be trusted:</b> the enumeration was
    /// cross-checked against the ten indices that were already validated
    /// against real hardware - SetOverlayFlag 11, SetOverlayWidthInMeters 22,
    /// PollNextOverlayEvent 48, SetOverlayInputMethod 50, SetOverlayMouseScale
    /// 52, SetOverlayFromFile 63, CreateDashboardOverlay 67, IsDashboardVisible
    /// 68, IsActiveDashboardOverlay 69, ShowDashboard 72. All ten landed
    /// exactly where the enumeration predicted. Ten independent agreements
    /// spread across the table make an off-by-one or a wrong header version
    /// impossible to miss, so the eleven indices added alongside them are
    /// trustworthy for the same reason.
    /// </para>
    /// <para>
    /// To re-verify after a SteamVR update: enumerate the header again and
    /// check the same ten anchors. If even one moves, the interface version
    /// changed and every index here is suspect - do not patch one entry.
    /// </para>
    /// <para>
    /// <b>Re-verified once, for <c>SetOverlayTexture</c> (2026-07-30).</b> The
    /// header was fetched again and its SHA-256 matched the one above
    /// byte for byte, so it is the same revision rather than merely a
    /// same-version one. Enumerating <c>IVROverlay</c> afresh reproduced all
    /// <em>twenty-one</em> indices already listed below - the ten hardware-
    /// validated anchors and the eleven added alongside them - with no
    /// movement anywhere, which brackets index 60 on both sides
    /// (<c>SetOverlayMouseScale</c> 52 below it, <c>SetOverlayRaw</c> 62 and
    /// <c>SetOverlayFromFile</c> 63 immediately above). Between the last
    /// validated index below it and the first above it the enumeration places
    /// exactly the declarations the header shows, ending
    /// <c>SetOverlayTexture</c> 60, <c>ClearOverlayTexture</c> 61,
    /// <c>SetOverlayRaw</c> 62 - so an off-by-one at 60 would have had to
    /// shift 62 too, and it did not.
    /// </para>
    /// </summary>
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

        return new VrOverlayFunctions
        {
            // The ten anchors, unchanged and hardware-validated.
            SetOverlayFlag = GetTableDelegate<SetOverlayFlagDelegate>(pointer, 11),
            SetOverlayWidthInMeters =
                GetTableDelegate<SetOverlayWidthInMetersDelegate>(pointer, 22),
            PollNextOverlayEvent =
                GetTableDelegate<PollNextOverlayEventDelegate>(pointer, 48),
            SetOverlayInputMethod =
                GetTableDelegate<SetOverlayInputMethodDelegate>(pointer, 50),
            SetOverlayMouseScale =
                GetTableDelegate<SetOverlayMouseScaleDelegate>(pointer, 52),
            SetOverlayFromFile = GetTableDelegate<SetOverlayFromFileDelegate>(pointer, 63),
            CreateDashboardOverlay =
                GetTableDelegate<CreateDashboardOverlayDelegate>(pointer, 67),
            IsDashboardVisible = GetTableDelegate<IsDashboardVisibleDelegate>(pointer, 68),
            IsActiveDashboardOverlay =
                GetTableDelegate<IsActiveDashboardOverlayDelegate>(pointer, 69),
            ShowDashboard = GetTableDelegate<ShowDashboardDelegate>(pointer, 72),

            // Added for the overlay substrate, from the same single pass.
            FindOverlay = GetTableDelegate<FindOverlayDelegate>(pointer, 0),
            CreateOverlay = GetTableDelegate<CreateOverlayDelegate>(pointer, 1),
            DestroyOverlay = GetTableDelegate<DestroyOverlayDelegate>(pointer, 3),
            SetOverlayAlpha = GetTableDelegate<SetOverlayAlphaDelegate>(pointer, 16),
            SetOverlaySortOrder = GetTableDelegate<SetOverlaySortOrderDelegate>(pointer, 20),
            SetOverlayCurvature = GetTableDelegate<SetOverlayCurvatureDelegate>(pointer, 24),
            SetOverlayTransformTrackedDeviceRelative =
                GetTableDelegate<SetOverlayTransformTrackedDeviceRelativeDelegate>(pointer, 35),
            ShowOverlay = GetTableDelegate<ShowOverlayDelegate>(pointer, 43),
            HideOverlay = GetTableDelegate<HideOverlayDelegate>(pointer, 44),
            IsOverlayVisible = GetTableDelegate<IsOverlayVisibleDelegate>(pointer, 45),
            SetOverlayRaw = GetTableDelegate<SetOverlayRawDelegate>(pointer, 62),

            // Added by the D3D11 texture spike, from a fresh pass over the
            // same header - see the re-verification note in the doc comment.
            SetOverlayTexture = GetTableDelegate<SetOverlayTextureDelegate>(pointer, 60)
        };
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
        => MapPhysicalActionStates(
            (hand, button, _, probeName) =>
                ReadDigital(_physicalButtons[(hand, button)], probeName));

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

        // The value SteamVR actually returns for this case
        // (VRInitError_Init_NoServerForBackgroundApp in openvr.h) is 121, not
        // 312. That mismatch meant the friendly "SteamVR is not running."
        // message below never fired against a real SteamVR - callers saw the
        // raw "OpenVR initialization failed: 121 (121)." fallback instead,
        // including the specific case this exists for: SteamVR launched
        // elevated (Run as administrator) while this app is not, which
        // blocks the client from reaching SteamVR's IPC server even though
        // SteamVR is genuinely running.
        NoServerForBackgroundApp = 121
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

    /// <summary>
    /// <c>EVROverlayError</c> from the same header the vtable indices come from.
    /// Spelled out rather than left as <c>None = 0</c> because these are the
    /// only readable signal when an overlay call fails: "InvalidHandle" points
    /// at a lifetime bug, "KeyInUse" at a duplicate surface, and
    /// "OverlayLimitExceeded" at surfaces that were never destroyed. A bare
    /// number would send the next person back to the header.
    /// </summary>
    private enum VrOverlayError
    {
        None = 0,
        UnknownOverlay = 10,
        InvalidHandle = 11,
        PermissionDenied = 12,
        OverlayLimitExceeded = 13,
        WrongVisibilityType = 14,
        KeyTooLong = 15,
        NameTooLong = 16,
        KeyInUse = 17,
        WrongTransformType = 18,
        InvalidTrackedDevice = 19,
        InvalidParameter = 20,
        ThumbnailCantBeDestroyed = 21,
        ArrayTooSmall = 22,
        RequestFailed = 23,
        InvalidTexture = 24,
        UnableToLoadFile = 25,
        KeyboardAlreadyInUse = 26,
        NoNeighbor = 27,
        TooManyMaskPrimitives = 29,
        BadMaskPrimitive = 30,
        TextureAlreadyLocked = 31,
        TextureLockCapacityReached = 32,
        TextureNotLocked = 33,
        TimedOut = 34
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError FindOverlayDelegate(
        nint overlayKey,
        ref ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError CreateOverlayDelegate(
        nint overlayKey,
        nint overlayName,
        ref ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError DestroyOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError ShowOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError HideOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsOverlayVisibleDelegate(ulong overlayHandle);

    /// <summary>
    /// The reason this module exists. The dashboard path encodes a PNG, writes
    /// it to disk and hands SteamVR a path; this takes the pixels directly, so
    /// a repaint costs a memory copy instead of an encode, a file write and a
    /// decode. The buffer must stay alive and pinned for the duration of the
    /// call - SteamVR reads it synchronously but keeps no reference.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayRawDelegate(
        ulong overlayHandle,
        nint buffer,
        uint width,
        uint height,
        uint bytesPerPixel);

    /// <summary>
    /// Mirrors <c>Texture_t</c>: a native texture handle plus the two enums
    /// that say how to read it. 16 bytes on x64 - an 8-byte pointer and two
    /// 4-byte enums - with no padding, so sequential layout is exact.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VrTexture
    {
        public nint Handle;
        public VrTextureType Type;
        public VrColorSpace ColorSpace;
    }

    /// <summary>Only the one member this app can produce; see <c>ETextureType</c>.</summary>
    private enum VrTextureType
    {
        /// <summary><c>Handle</c> is an <c>ID3D11Texture2D*</c>.</summary>
        DirectX = 0
    }

    /// <summary>
    /// <c>EColorSpace</c>. <see cref="Auto"/> means gamma for 8-bit-per-channel
    /// formats and linear otherwise, which for a <c>R8G8B8A8_UNORM</c> texture
    /// resolves to gamma - the same interpretation
    /// <see cref="SetOverlayRawDelegate"/> already applies to the identical
    /// bytes, so the two upload paths agree on colour without either side
    /// converting.
    /// </summary>
    private enum VrColorSpace
    {
        Auto = 0,
        Gamma = 1,
        Linear = 2
    }

    /// <summary>
    /// The GPU counterpart of <see cref="SetOverlayRawDelegate"/>, and the
    /// reason the D3D11 spike exists. <c>SetOverlayRaw</c> takes width, height
    /// and stride on every call, so SteamVR treats each call as a fresh
    /// texture to allocate and upload; this hands over a texture SteamVR keeps
    /// a reference to, which the caller then rewrites in place.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayTextureDelegate(
        ulong overlayHandle,
        ref VrTexture texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayAlphaDelegate(
        ulong overlayHandle,
        float alpha);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlaySortOrderDelegate(
        ulong overlayHandle,
        uint sortOrder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayCurvatureDelegate(
        ulong overlayHandle,
        float curvature);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate VrOverlayError SetOverlayTransformTrackedDeviceRelativeDelegate(
        ulong overlayHandle,
        uint trackedDevice,
        ref HmdMatrix34 transform);

    /// <summary>
    /// The <c>IVROverlay</c> function table, addressed by raw vtable index.
    /// <para>
    /// Named rather than positional deliberately. A positional record of
    /// twenty-one delegates makes every construction site depend on argument
    /// order, and two adjacent entries with the same shape - say
    /// <see cref="ShowOverlayDelegate"/> and <see cref="HideOverlayDelegate"/>,
    /// both <c>ulong -&gt; error</c> - would swap silently with no compiler
    /// complaint and no crash, just an overlay that hides when asked to show.
    /// Names make that class of mistake impossible to write.
    /// </para>
    /// <para>
    /// <c>required</c> is what stops a member being forgotten: with this many
    /// entries an omission would otherwise leave a null delegate that only
    /// fails when that one feature is first used.
    /// </para>
    /// </summary>
    private readonly record struct VrOverlayFunctions
    {
        public required SetOverlayWidthInMetersDelegate SetOverlayWidthInMeters { get; init; }
        public required SetOverlayFlagDelegate SetOverlayFlag { get; init; }
        public required PollNextOverlayEventDelegate PollNextOverlayEvent { get; init; }
        public required SetOverlayInputMethodDelegate SetOverlayInputMethod { get; init; }
        public required SetOverlayMouseScaleDelegate SetOverlayMouseScale { get; init; }
        public required SetOverlayFromFileDelegate SetOverlayFromFile { get; init; }
        public required CreateDashboardOverlayDelegate CreateDashboardOverlay { get; init; }
        public required IsDashboardVisibleDelegate IsDashboardVisible { get; init; }
        public required IsActiveDashboardOverlayDelegate IsActiveDashboardOverlay { get; init; }
        public required ShowDashboardDelegate ShowDashboard { get; init; }
        public required FindOverlayDelegate FindOverlay { get; init; }
        public required CreateOverlayDelegate CreateOverlay { get; init; }
        public required DestroyOverlayDelegate DestroyOverlay { get; init; }
        public required ShowOverlayDelegate ShowOverlay { get; init; }
        public required HideOverlayDelegate HideOverlay { get; init; }
        public required IsOverlayVisibleDelegate IsOverlayVisible { get; init; }
        public required SetOverlayRawDelegate SetOverlayRaw { get; init; }
        public required SetOverlayTextureDelegate SetOverlayTexture { get; init; }
        public required SetOverlayAlphaDelegate SetOverlayAlpha { get; init; }
        public required SetOverlaySortOrderDelegate SetOverlaySortOrder { get; init; }
        public required SetOverlayCurvatureDelegate SetOverlayCurvature { get; init; }
        public required SetOverlayTransformTrackedDeviceRelativeDelegate
            SetOverlayTransformTrackedDeviceRelative
        { get; init; }
    }

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
