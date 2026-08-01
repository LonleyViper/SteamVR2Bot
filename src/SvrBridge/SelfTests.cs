using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SvrBridge.Core;

namespace SvrBridge;

internal static class SelfTests
{
    public static async Task RunAsync()
    {
        TestModifierChord();
        TestSimultaneousChord();
        TestSinglePress();
        TestLongPress();
        TestDoublePress();
        TestCooldown();
        TestPhysicalControllerInputs();
        TestAvailableControllerInputs();
        TestAvailableInputsPerFamily();
        TestFriendlyNamesForFaceButtonsAndStick();
        TestFaceButtonActionsMapToExpectedButtonNumbers();
        TestIndexAndTouchBindingFilesAreConsistentWithActionsManifest();
        TestDashboardPointerTracking();
        TestInputProbe();
        TestBodyFrame();
        TestAuthenticationHash();
        TestStreamerBotEventPayload();
        TestTwitchChatMessageMapper();
        TestTwitchEmoteCatalog();
        TestStreamerBotEventTemplateResolvesDottedPaths();
        TestStreamerBotEventCatalogParsesGetEventsResponse();
        TestStreamerBotEventCatalogSpacesRunTogetherEventNames();
        TestStreamerBotEventSearchFiltersAndCapsResults();
        TestStreamerBotSourceChipHandlesAnySourceDeterministically();
        await TestStreamerBotRoundTripAsync();
        await TestStreamerBotReconnectAsync();
        await TestStreamerBotRestartRecoveryAsync();
        await TestUnconfirmedDeliveryIsNotRetriedAsync();
        await TestStreamerBotEventStreamAsync();
        await TestStreamerBotEventStreamPendingRequestsAsync();
        await TestStreamerBotEventStreamSubscribesToEnabledEventsAsync();
        await TestStreamerBotEventStreamFallsBackWhenGetEventsFailsAtConnectAsync();
        await TestStreamerBotEventStreamGetEventsFailureDoesNotStopTheFeedAsync();
        await TestSteamVrSessionRestartAsync();
        await TestRuntimeStartsWithNoShortcutsAsync();
        await TestRuntimeStartsWithMalformedStreamerBotAddressAsync();
        await TestWorkerCrashedExceptionStopsWithoutRetryingAsync();
        Console.WriteLine(
            "SELF-TEST PASS: chord detection, physical controller mapping, authentication, SteamVR worker " +
            "recovery, Streamer.bot restart recovery, no-duplicate delivery, " +
            "event payload parsing, event/response routing, and DoAction round trip.");
    }

    private static void TestPhysicalControllerInputs()
    {
        var snapshot = new InputSnapshot(
            false,
            false,
            LeftButtons: 1UL << 2,
            RightButtons: 1UL << 33);
        var leftGrip = ControllerInputBinding.Physical(
            ControllerHand.Left,
            2,
            "Left Grip");
        var rightTrigger = ControllerInputBinding.Physical(
            ControllerHand.Right,
            33,
            "Right Trigger");
        var wrongHand = ControllerInputBinding.Physical(
            ControllerHand.Left,
            33,
            "Left Trigger");

        Assert(leftGrip.IsPressed(snapshot), "Left Grip was not read from the controller mask.");
        Assert(
            rightTrigger.IsPressed(snapshot),
            "Right Trigger was not read from the controller mask.");
        Assert(
            !wrongHand.IsPressed(snapshot),
            "A button on the wrong controller was treated as pressed.");
    }

    private static void TestAvailableControllerInputs()
    {
        var setup = new ControllerSetup(
            [
                new ControllerDevice(
                    "vive_controller",
                    "HTC Vive controllers",
                    "Left",
                    "Vive Controller MV"),
                new ControllerDevice(
                    "vive_controller",
                    "HTC Vive controllers",
                    "Right",
                    "Vive Controller MV")
            ],
            null,
            null,
            BindingAvailability.Ready,
            "Vive controllers detected.",
            true);
        var left = ControllerInputs.AvailableInputs(ControllerHand.Left, setup);

        Assert(left.Count == 4, "The Vive input picker returned the wrong input count.");
        Assert(
            left[0].Id == "left:1"
            && left[0].FriendlyName == "Left Menu Button",
            "The Vive input picker did not put Left Menu first.");
        Assert(
            left.Any(input => input.Id == "left:33"
                              && input.FriendlyName == "Left Trigger"),
            "The Vive input picker omitted Left Trigger.");
    }

    private static void TestAvailableInputsPerFamily()
    {
        var cases = new[]
        {
            (Type: "vive_controller", Hand: ControllerHand.Left, Buttons: new uint[] { 1, 2, 33, 32 }),
            (Type: "vive_controller", Hand: ControllerHand.Right, Buttons: new uint[] { 1, 2, 33, 32 }),
            (Type: "knuckles", Hand: ControllerHand.Left, Buttons: new uint[] { 2, 33, 32, 7, 34 }),
            (Type: "knuckles", Hand: ControllerHand.Right, Buttons: new uint[] { 2, 33, 32, 7, 34 }),
            (Type: "oculus_touch", Hand: ControllerHand.Left, Buttons: new uint[] { 1, 2, 33, 32, 7, 34 }),
            (Type: "oculus_touch", Hand: ControllerHand.Right, Buttons: new uint[] { 2, 33, 32, 7, 34 }),
            (Type: "unrecognised_controller", Hand: ControllerHand.Left, Buttons: new uint[] { 2, 33, 32 }),
            (Type: "unrecognised_controller", Hand: ControllerHand.Right, Buttons: new uint[] { 2, 33, 32 })
        };

        foreach (var testCase in cases)
        {
            var inputs = ControllerInputs.AvailableInputs(
                testCase.Hand,
                ControllerSetupFor(testCase.Type));
            var expectedIds = testCase.Buttons.Select(
                button => $"{testCase.Hand.ToString().ToLowerInvariant()}:{button}");

            Assert(
                inputs.Select(input => input.Id).SequenceEqual(expectedIds),
                $"{testCase.Type} {testCase.Hand} inputs did not match the exact ordered capability set.");
        }

        var index = ControllerSetupFor("knuckles");
        Assert(
            !ControllerInputs.AvailableInputs(ControllerHand.Left, index).Any(input => input.Id == "left:1")
            && !ControllerInputs.AvailableInputs(ControllerHand.Right, index).Any(input => input.Id == "right:1"),
            "Index offered an application-menu input even though Knuckles has none.");

        var touch = ControllerSetupFor("oculus_touch");
        Assert(
            ControllerInputs.AvailableInputs(ControllerHand.Left, touch).Any(input => input.Id == "left:1"),
            "Touch omitted the left application-menu input.");
        Assert(
            !ControllerInputs.AvailableInputs(ControllerHand.Right, touch).Any(input => input.Id == "right:1"),
            "Touch offered the reserved right Oculus/system button.");
    }

    private static void TestFriendlyNamesForFaceButtonsAndStick()
    {
        var index = ControllerSetupFor("knuckles");
        Assert(
            ControllerInputs.FriendlyName(ControllerHand.Left, 7, index) == "Left A Button"
            && ControllerInputs.FriendlyName(ControllerHand.Left, 34, index) == "Left B Button"
            && ControllerInputs.FriendlyName(ControllerHand.Right, 7, index) == "Right A Button"
            && ControllerInputs.FriendlyName(ControllerHand.Right, 34, index) == "Right B Button",
            "Index face-button labels were not A/B on both hands.");
        Assert(
            ControllerInputs.FriendlyName(ControllerHand.Left, 32, index) == "Left Thumbstick",
            "Index button 32 was not labelled as a thumbstick.");

        var touch = ControllerSetupFor("oculus_touch");
        Assert(
            ControllerInputs.FriendlyName(ControllerHand.Left, 7, touch) == "Left X Button"
            && ControllerInputs.FriendlyName(ControllerHand.Left, 34, touch) == "Left Y Button"
            && ControllerInputs.FriendlyName(ControllerHand.Right, 7, touch) == "Right A Button"
            && ControllerInputs.FriendlyName(ControllerHand.Right, 34, touch) == "Right B Button",
            "Touch face-button labels did not preserve the X/Y-left and A/B-right asymmetry.");
        Assert(
            ControllerInputs.FriendlyName(ControllerHand.Right, 32, touch) == "Right Thumbstick",
            "Touch button 32 was not labelled as a thumbstick.");

        var vive = ControllerSetupFor("vive_controller");
        var unknown = ControllerSetupFor("unrecognised_controller");
        Assert(
            ControllerInputs.FriendlyName(ControllerHand.Left, 32, vive) == "Left Trackpad",
            "Vive button 32 stopped using trackpad terminology.");
        Assert(
            ControllerInputs.FriendlyName(ControllerHand.Left, 32, unknown)
            == "Left Thumbstick / Trackpad",
            "An unknown controller guessed one specific button-32 control type.");
    }

    private static void TestFaceButtonActionsMapToExpectedButtonNumbers()
    {
        var expected = new[]
        {
            (Path: "/actions/svrbridge/in/left_face1", Hand: ControllerHand.Left, Button: 7U),
            (Path: "/actions/svrbridge/in/left_face2", Hand: ControllerHand.Left, Button: 34U),
            (Path: "/actions/svrbridge/in/right_face1", Hand: ControllerHand.Right, Button: 7U),
            (Path: "/actions/svrbridge/in/right_face2", Hand: ControllerHand.Right, Button: 34U)
        };

        foreach (var expectedAction in expected)
        {
            var (left, right) = OpenVrInput.MapPhysicalActionStates(
                (_, _, actionPath, _) => actionPath == expectedAction.Path);
            var expectedLeft = expectedAction.Hand == ControllerHand.Left
                ? 1UL << (int)expectedAction.Button
                : 0;
            var expectedRight = expectedAction.Hand == ControllerHand.Right
                ? 1UL << (int)expectedAction.Button
                : 0;

            Assert(
                left == expectedLeft && right == expectedRight,
                $"Digital action {expectedAction.Path} did not set only {expectedAction.Hand} button {expectedAction.Button}.");

            var snapshot = new InputSnapshot(false, false, left, right);
            var binding = ControllerInputBinding.Physical(
                expectedAction.Hand,
                expectedAction.Button,
                "test face button");
            Assert(
                binding.IsPressed(snapshot),
                $"The bit set by {expectedAction.Path} did not reach ControllerInputBinding.IsPressed.");

            var recorded = ControllerInputs.PressedInputs(snapshot, ControllerSetupFor("knuckles"));
            Assert(
                recorded.Count == 1 && recorded[0].Id == binding.Id,
                $"The bit set by {expectedAction.Path} did not reach physical-input recording.");
        }
    }

    private static void TestIndexAndTouchBindingFilesAreConsistentWithActionsManifest()
    {
        var assetsDirectory = AppContext.BaseDirectory;
        using var actionsDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(assetsDirectory, "actions.json")));
        var actionPaths = actionsDocument.RootElement
            .GetProperty("actions")
            .EnumerateArray()
            .Select(action => action.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var defaultBindings = actionsDocument.RootElement
            .GetProperty("default_bindings")
            .EnumerateArray()
            .ToDictionary(
                binding => binding.GetProperty("controller_type").GetString()!,
                binding => binding.GetProperty("binding_url").GetString()!,
                StringComparer.Ordinal);
        Assert(
            defaultBindings.TryGetValue("vive_controller", out var viveFile)
            && viveFile == "bindings_vive_controller.json"
            && defaultBindings.TryGetValue("knuckles", out var indexFile)
            && indexFile == "bindings_index_controller.json"
            && defaultBindings.TryGetValue("oculus_touch", out var touchFile)
            && touchFile == "bindings_oculus_touch.json",
            "actions.json did not register the exact Vive, Index, and Touch default binding files.");

        var expectedMappings = new Dictionary<string, (string Path, string Output)[]>(StringComparer.Ordinal)
        {
            ["bindings_vive_controller.json"] =
            [
                ("/user/hand/left/input/application_menu", "/actions/svrbridge/in/left_menu"),
                ("/user/hand/right/input/application_menu", "/actions/svrbridge/in/right_menu"),
                ("/user/hand/left/input/grip", "/actions/svrbridge/in/left_grip"),
                ("/user/hand/right/input/grip", "/actions/svrbridge/in/right_grip"),
                ("/user/hand/left/input/trigger", "/actions/svrbridge/in/left_trigger"),
                ("/user/hand/right/input/trigger", "/actions/svrbridge/in/right_trigger"),
                ("/user/hand/left/input/trackpad", "/actions/svrbridge/in/left_trackpad"),
                ("/user/hand/right/input/trackpad", "/actions/svrbridge/in/right_trackpad")
            ],
            ["bindings_index_controller.json"] =
            [
                ("/user/hand/left/input/grip", "/actions/svrbridge/in/left_grip"),
                ("/user/hand/right/input/grip", "/actions/svrbridge/in/right_grip"),
                ("/user/hand/left/input/trigger", "/actions/svrbridge/in/left_trigger"),
                ("/user/hand/right/input/trigger", "/actions/svrbridge/in/right_trigger"),
                ("/user/hand/left/input/thumbstick", "/actions/svrbridge/in/left_trackpad"),
                ("/user/hand/right/input/thumbstick", "/actions/svrbridge/in/right_trackpad"),
                ("/user/hand/left/input/a", "/actions/svrbridge/in/left_face1"),
                ("/user/hand/left/input/b", "/actions/svrbridge/in/left_face2"),
                ("/user/hand/right/input/a", "/actions/svrbridge/in/right_face1"),
                ("/user/hand/right/input/b", "/actions/svrbridge/in/right_face2")
            ],
            ["bindings_oculus_touch.json"] =
            [
                ("/user/hand/left/input/system", "/actions/svrbridge/in/left_menu"),
                ("/user/hand/left/input/grip", "/actions/svrbridge/in/left_grip"),
                ("/user/hand/right/input/grip", "/actions/svrbridge/in/right_grip"),
                ("/user/hand/left/input/trigger", "/actions/svrbridge/in/left_trigger"),
                ("/user/hand/right/input/trigger", "/actions/svrbridge/in/right_trigger"),
                ("/user/hand/left/input/joystick", "/actions/svrbridge/in/left_trackpad"),
                ("/user/hand/right/input/joystick", "/actions/svrbridge/in/right_trackpad"),
                ("/user/hand/left/input/x", "/actions/svrbridge/in/left_face1"),
                ("/user/hand/left/input/y", "/actions/svrbridge/in/left_face2"),
                ("/user/hand/right/input/a", "/actions/svrbridge/in/right_face1"),
                ("/user/hand/right/input/b", "/actions/svrbridge/in/right_face2")
            ]
        };

        var controllerTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bindings_vive_controller.json"] = "vive_controller",
            ["bindings_index_controller.json"] = "knuckles",
            ["bindings_oculus_touch.json"] = "oculus_touch"
        };

        foreach (var (fileName, expected) in expectedMappings)
        {
            var (controllerType, sources) = ReadBindingFile(
                Path.Combine(assetsDirectory, fileName));
            Assert(
                controllerType == controllerTypes[fileName],
                $"{fileName} declared controller type '{controllerType}' instead of '{controllerTypes[fileName]}'.");
            Assert(
                sources.Count == expected.Length,
                $"{fileName} contained an unexpected number of bound inputs.");
            Assert(
                sources.All(source => actionPaths.Contains(source.Output)),
                $"{fileName} referenced a misspelled or nonexistent action path.");

            foreach (var mapping in expected)
            {
                Assert(
                    sources.Any(source =>
                        source.Path == mapping.Path && source.Output == mapping.Output),
                    $"{fileName} did not bind {mapping.Path} to {mapping.Output}.");
            }

            // The manifest's legacy button_one/button_two actions may remain
            // unbound: Poll applies the same default through the physical
            // left-grip/right-trigger actions. Drive that exact production
            // fallback here so every family proves the same gesture rather
            // than merely containing two plausible-looking JSON entries.
            var defaultOutputs = new HashSet<string>(
                sources
                    .Where(source =>
                        source.Path == "/user/hand/left/input/grip"
                        || source.Path == "/user/hand/right/input/trigger")
                    .Select(source => source.Output),
                StringComparer.Ordinal);
            var masks = OpenVrInput.MapPhysicalActionStates(
                (_, _, actionPath, _) => defaultOutputs.Contains(actionPath));
            var defaultGesture = OpenVrInput.MapDefaultGestureActions(
                false,
                false,
                masks.Left,
                masks.Right);
            Assert(
                defaultGesture.ButtonOne && defaultGesture.ButtonTwo,
                $"{fileName} did not produce Button One from left grip and Button Two from right trigger.");
        }

        var indexSources = ReadBindingFile(
            Path.Combine(assetsDirectory, "bindings_index_controller.json")).Sources;
        AssertBindingParameters(
            indexSources,
            "/user/hand/left/input/grip",
            "button",
            "0.8",
            "0.65",
            "force");
        AssertBindingParameters(
            indexSources,
            "/user/hand/right/input/grip",
            "button",
            "0.8",
            "0.65",
            "force");
        Assert(
            indexSources.Where(source => source.Path.EndsWith("/input/trigger", StringComparison.Ordinal))
                .All(source => source.Mode == "trigger"),
            "Index triggers did not use their genuine trigger click output.");

        var touchSources = ReadBindingFile(
            Path.Combine(assetsDirectory, "bindings_oculus_touch.json")).Sources;
        AssertBindingParameters(
            touchSources,
            "/user/hand/left/input/grip",
            "button",
            "0.65",
            "0.5");
        AssertBindingParameters(
            touchSources,
            "/user/hand/right/input/grip",
            "button",
            "0.65",
            "0.5");
        AssertBindingParameters(
            touchSources,
            "/user/hand/left/input/trigger",
            "button",
            "0.65",
            "0.6");
        AssertBindingParameters(
            touchSources,
            "/user/hand/right/input/trigger",
            "button",
            "0.65",
            "0.6");
        Assert(
            touchSources.All(source => source.Path != "/user/hand/right/input/system"),
            "The Touch binding claimed the reserved right Oculus/system button.");
    }

    private static ControllerSetup ControllerSetupFor(string controllerType) =>
        new(
            [
                new ControllerDevice(controllerType, controllerType, "Left", controllerType),
                new ControllerDevice(controllerType, controllerType, "Right", controllerType)
            ],
            null,
            null,
            BindingAvailability.Ready,
            $"{controllerType} controllers detected.",
            controllerType == "vive_controller");

    private static (string ControllerType, IReadOnlyList<ParsedBindingSource> Sources)
        ReadBindingFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var sources = new List<ParsedBindingSource>();
        foreach (var source in root
                     .GetProperty("bindings")
                     .GetProperty("/actions/svrbridge")
                     .GetProperty("sources")
                     .EnumerateArray())
        {
            var sourcePath = source.GetProperty("path").GetString()!;
            var mode = source.GetProperty("mode").GetString()!;
            var parameters = source.TryGetProperty("parameters", out var parametersElement)
                ? parametersElement.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.ToString(),
                    StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var input in source.GetProperty("inputs").EnumerateObject())
            {
                if (input.Value.TryGetProperty("output", out var output))
                {
                    sources.Add(
                        new ParsedBindingSource(
                            sourcePath,
                            mode,
                            output.GetString()!,
                            parameters));
                }
            }
        }

        return (root.GetProperty("controller_type").GetString()!, sources);
    }

    private static void AssertBindingParameters(
        IReadOnlyList<ParsedBindingSource> sources,
        string path,
        string mode,
        string activation,
        string deactivation,
        string? forceInput = null)
    {
        var source = sources.Single(candidate => candidate.Path == path);
        Assert(source.Mode == mode, $"{path} used binding mode {source.Mode} instead of {mode}.");
        Assert(
            source.Parameters.TryGetValue("click_activate_threshold", out var actualActivation)
            && actualActivation == activation
            && source.Parameters.TryGetValue("click_deactivate_threshold", out var actualDeactivation)
            && actualDeactivation == deactivation,
            $"{path} did not preserve its documented {activation}/{deactivation} hysteresis thresholds.");
        Assert(
            double.Parse(deactivation, System.Globalization.CultureInfo.InvariantCulture)
            < double.Parse(activation, System.Globalization.CultureInfo.InvariantCulture),
            $"{path} did not deactivate below its activation threshold.");
        if (forceInput is not null)
        {
            Assert(
                source.Parameters.TryGetValue("force_input", out var actualForceInput)
                && actualForceInput == forceInput,
                $"{path} did not use the Index controller's force input.");
        }
    }

    private sealed record ParsedBindingSource(
        string Path,
        string Mode,
        string Output,
        IReadOnlyDictionary<string, string> Parameters);

    private static void TestDashboardPointerTracking()
    {
        var tracker = new DashboardPointerTracker();
        Assert(
            !tracker.Update(300, 640, 720, out _),
            "A dashboard mouse move was treated as a click.");
        Assert(
            !tracker.Update(301, 0, 0, out _),
            "A dashboard mouse-down event activated the page before release.");
        Assert(
            !tracker.Update(300, 700, 760, out _),
            "Dragging across the dashboard was treated as a click.");
        Assert(
            tracker.Update(302, 0, 0, out var click),
            "A dashboard mouse-up event was not recognized.");
        Assert(
            click.Kind == DashboardInteractionKind.Click
            && click.X == 700
            && click.Y == 760,
            "Dashboard click did not use the release-time pointer position.");
        Assert(
            tracker.Update(305, 0, -1, out var scroll)
            && scroll.Kind == DashboardInteractionKind.Scroll
            && scroll.ScrollY == -1,
            "A dashboard scroll event was not recognized.");
    }

    private static void TestInputProbe()
    {
        var lines = new List<string>();
        var probe = new InputProbe(lines.Add);
        var pressed = new ProbeActionState("left_grip", 0, true, true, true, 0x2A);

        probe.ObserveAction(pressed);
        probe.ObserveDashboard(true, true);
        probe.ObserveOverlayEvent(200, 3, 2, "Left Grip");
        Assert(
            lines.Count == 0,
            "The input probe logged while it was disabled.");

        probe.SetEnabled(true, 0);
        Assert(
            probe.Enabled && lines.Count == 1,
            "Enabling the input probe was not announced exactly once.");

        lines.Clear();
        probe.BeginPoll();
        probe.ObserveDashboard(true, true);
        probe.ObserveAction(pressed);
        probe.EndPoll(10);
        Assert(
            lines.Count == 2
            && lines[0].Contains("visible=1 active=1", StringComparison.Ordinal)
            && lines[1].Contains(
                "left_grip: err=0 active=1 state=1 changed=1 origin=0x2A",
                StringComparison.Ordinal),
            "The input probe did not report the first observation of each signal.");

        lines.Clear();
        probe.BeginPoll();
        probe.ObserveDashboard(true, true);
        probe.ObserveAction(pressed);
        probe.EndPoll(20);
        Assert(
            lines.Count == 0,
            "The input probe repeated an unchanged state instead of logging transitions.");

        lines.Clear();
        probe.BeginPoll();
        probe.ObserveDashboard(true, false);
        probe.ObserveAction(pressed with { State = false, Changed = false });
        probe.EndPoll(30);
        Assert(
            lines.Count == 2
            && lines[0].Contains("visible=1 active=0", StringComparison.Ordinal)
            && lines[1].Contains("state=0 changed=0", StringComparison.Ordinal),
            "The input probe missed a state transition.");

        lines.Clear();
        probe.ObserveOverlayEvent(200, 3, 2, "Left Grip");
        probe.ObserveOverlayEvent(201, 3, 2, "Left Grip");
        probe.ObserveOverlayEvent(300, 3, 0, "");
        probe.ObserveOverlayEvent(300, 3, 0, "");
        Assert(
            lines.Count == 3
            && lines[0].Contains(
                "ButtonPress: device=3 button=2 (Left Grip)",
                StringComparison.Ordinal)
            && lines[1].Contains("ButtonUnpress", StringComparison.Ordinal)
            && lines[2].Contains("event 300", StringComparison.Ordinal),
            "The input probe did not decode controller button events or throttle the rest.");

        lines.Clear();
        probe.BeginPoll();
        probe.ObserveAction(pressed);
        probe.EndPoll(2000);
        Assert(
            lines.Any(line => line.Contains(
                "actions 1/1 active, pressed left_grip",
                StringComparison.Ordinal))
            && lines.All(line => !line.Contains("legacy", StringComparison.OrdinalIgnoreCase)),
            "The input probe did not emit its once-per-second summary.");

        lines.Clear();
        lines.Clear();
        probe.ObserveMotion(
            MotionSample.Untracked(100) with { Tracking = MotionTracking.All });
        probe.ObserveMotion(
            MotionSample.Untracked(110) with { Tracking = MotionTracking.All });
        probe.ObserveMotion(
            MotionSample.Untracked(120) with { Tracking = MotionTracking.Head });
        Assert(
            lines.Count == 2
            && lines[0].Contains("tracking: All", StringComparison.Ordinal)
            && lines[1].Contains("tracking: Head", StringComparison.Ordinal),
            "The input probe logged poses per poll instead of on tracking changes.");

        lines.Clear();
        probe.SetEnabled(false, 3000);
        probe.BeginPoll();
        probe.ObserveAction(pressed);
        probe.ObserveMotion(MotionSample.Untracked(4000));
        probe.EndPoll(9000);
        Assert(
            lines.Count == 1 && !probe.Enabled,
            "Disabling the input probe did not stop it logging.");
    }

    private static void TestBodyFrame()
    {
        // Facing OpenVR's -Z with the head at the origin.
        var level = BodyFrame.FromHead(
            Vector3.Zero,
            -Vector3.UnitZ,
            Vector3.UnitY);
        AssertVector(
            level.ToLocalPoint(new Vector3(0, 0, -1)),
            new Vector3(0, 0, 1),
            "A point ahead of the wearer was not one metre forward in body space.");
        AssertVector(
            level.ToLocalPoint(new Vector3(1, 0, 0)),
            new Vector3(1, 0, 0),
            "A point beside the wearer was not one metre right in body space.");

        // Turned 90 degrees to the left, so the wearer's right is world -Z.
        var turned = BodyFrame.FromHead(
            Vector3.Zero,
            -Vector3.UnitX,
            Vector3.UnitY);
        AssertVector(
            turned.ToLocalPoint(new Vector3(0, 0, -0.5f)),
            new Vector3(0.5f, 0, 0),
            "Body space did not follow the wearer's yaw.");

        // Head pitched down 45 degrees. Pitch must not rotate the gesture
        // space, otherwise glancing down turns a level sweep into a diagonal.
        const float diagonal = 0.70710678f;
        var pitched = BodyFrame.FromHead(
            Vector3.Zero,
            new Vector3(0, -diagonal, -diagonal),
            new Vector3(0, diagonal, -diagonal));
        AssertVector(
            pitched.ToLocalPoint(new Vector3(1, 0, 0)),
            new Vector3(1, 0, 0),
            "Head pitch leaked into the body frame.");

        // Looking straight down and straight up leave no yaw in the forward
        // axis; the head's own up axis still carries it.
        var down = BodyFrame.FromHead(
            Vector3.Zero,
            -Vector3.UnitY,
            -Vector3.UnitZ);
        AssertVector(
            down.Forward,
            -Vector3.UnitZ,
            "Looking straight down lost the wearer's facing.");
        var up = BodyFrame.FromHead(
            Vector3.Zero,
            Vector3.UnitY,
            Vector3.UnitZ);
        AssertVector(
            up.Forward,
            -Vector3.UnitZ,
            "Looking straight up lost the wearer's facing.");

        // A velocity must not pick up the head's position.
        var offset = BodyFrame.FromHead(
            new Vector3(5, 1.7f, 3),
            -Vector3.UnitZ,
            Vector3.UnitY);
        AssertVector(
            offset.ToLocalDirection(Vector3.UnitY),
            Vector3.UnitY,
            "Standing away from the origin distorted a velocity.");
        AssertVector(
            offset.ToLocalPoint(new Vector3(5, 1.7f, 3)),
            Vector3.Zero,
            "The wearer's own head was not the body-space origin.");
    }

    private static void AssertVector(Vector3 actual, Vector3 expected, string message) =>
        Assert(
            Vector3.Distance(actual, expected) < 1e-4f,
            $"{message} Expected {expected}, saw {actual}.");

    private static void TestModifierChord()
    {
        var detector = new ChordDetector(new ChordConfig
        {
            Mode = ChordMode.Modifier,
            WindowMs = 2000,
            CooldownMs = 0
        });

        Assert(!detector.Update(true, false, 0), "Modifier alone must not fire.");
        Assert(detector.Update(true, true, 500), "Trigger press while modifier held must fire.");
        Assert(!detector.Update(true, true, 510), "Held chord must fire once.");
        Assert(!detector.Update(true, false, 520), "Trigger release must not fire.");
        Assert(detector.Update(true, true, 530), "Trigger may be pressed again while modifier remains held.");
    }

    private static void TestSimultaneousChord()
    {
        var detector = new ChordDetector(new ChordConfig
        {
            Mode = ChordMode.Simultaneous,
            WindowMs = 200,
            CooldownMs = 0
        });

        Assert(!detector.Update(true, false, 100), "First simultaneous button must not fire.");
        Assert(detector.Update(true, true, 250), "Buttons inside the window must fire.");

        var tooSlow = new ChordDetector(new ChordConfig
        {
            Mode = ChordMode.Simultaneous,
            WindowMs = 200,
            CooldownMs = 0
        });
        Assert(!tooSlow.Update(true, false, 100), "First slow button must not fire.");
        Assert(!tooSlow.Update(true, true, 301), "Buttons outside the window must not fire.");
    }

    private static void TestSinglePress()
    {
        var detector = new ChordDetector(new ChordConfig
        {
            Mode = ChordMode.SinglePress,
            CooldownMs = 0
        });

        Assert(detector.Update(true, true, 100), "Single press did not fire.");
        Assert(!detector.Update(true, true, 150), "Held single press fired more than once.");
        Assert(!detector.Update(false, false, 200), "Single press fired on release.");
        Assert(detector.Update(true, true, 300), "Single press did not re-arm.");

        var hotReloaded = new ChordDetector(
            new ChordConfig
            {
                Mode = ChordMode.SinglePress,
                CooldownMs = 0
            },
            requireReleaseBeforeArmed: true);
        Assert(
            !hotReloaded.Update(true, true, 100),
            "A held input fired immediately after a hot reload.");
        Assert(
            !hotReloaded.Update(false, false, 150),
            "Release after a hot reload fired.");
        Assert(
            hotReloaded.Update(true, true, 200),
            "Hot-reloaded input did not arm after release.");
    }

    private static void TestCooldown()
    {
        var detector = new ChordDetector(new ChordConfig
        {
            Mode = ChordMode.Modifier,
            WindowMs = 2000,
            CooldownMs = 500
        });

        Assert(!detector.Update(true, false, 0), "Modifier alone must not fire.");
        Assert(detector.Update(true, true, 100), "First chord must fire.");
        Assert(!detector.Update(true, false, 150), "Release must not fire.");
        Assert(!detector.Update(true, true, 300), "Cooldown must suppress an early repeat.");
        Assert(!detector.Update(true, false, 550), "Release must not fire.");
        Assert(detector.Update(true, true, 600), "Chord must fire after cooldown.");
    }

    private static void TestLongPress()
    {
        var detector = new ChordDetector(new ChordConfig
        {
            Mode = ChordMode.LongPress,
            HoldMs = 1000,
            CooldownMs = 0
        });

        Assert(!detector.Update(true, false, 100), "Long press fired immediately.");
        Assert(!detector.Update(true, false, 1099), "Long press fired before its duration.");
        Assert(detector.Update(true, false, 1100), "Long press did not fire at its duration.");
        Assert(!detector.Update(true, false, 1500), "Held long press fired more than once.");
        Assert(!detector.Update(false, false, 1600), "Long press fired on release.");
        Assert(!detector.Update(true, false, 1700), "Second long press fired immediately.");
        Assert(detector.Update(true, false, 2700), "Second long press did not re-arm.");
    }

    private static void TestDoublePress()
    {
        var detector = new ChordDetector(new ChordConfig
        {
            Mode = ChordMode.DoublePress,
            WindowMs = 500,
            CooldownMs = 0
        });

        Assert(!detector.Update(true, true, 100), "First press of a double press fired.");
        Assert(!detector.Update(false, false, 150), "Release between presses fired.");
        Assert(detector.Update(true, true, 400), "Second press inside the window did not fire.");
        Assert(!detector.Update(true, true, 450), "Held second press fired more than once.");
        Assert(!detector.Update(false, false, 500), "Double press fired on release.");
        Assert(!detector.Update(true, true, 1100), "A press outside the window fired.");
        Assert(!detector.Update(false, false, 1150), "Release after a late press fired.");
        Assert(detector.Update(true, true, 1450), "A new double press pair did not fire.");
    }

    private static void TestAuthenticationHash()
    {
        const string expected = "zTM5ki6L2vVvBQiTG9ckH1Lh64AbnCf6XZ226UmnkIA=";
        var actual = StreamerBotClient.BuildAuthentication("password", "salt", "challenge");
        Assert(actual == expected, "Authentication hash changed unexpectedly.");
    }

    private static void TestStreamerBotEventPayload()
    {
        var chat = ParsePayload(
            """
            {"v":1,"target":"Chat","user":"Nightbot","colour":"#aabbcc",
             "badge":"mod","text":"hello there","futureField":{"ignored":true}}
            """);
        Assert(
            chat.Version == 1
            && chat.Target == StreamerBotEventTarget.Chat
            && chat.User == "Nightbot"
            && chat.Colour == "#AABBCC"
            && chat.Badge == "mod"
            && chat.Text == "hello there",
            "A General.Custom chat payload did not parse.");

        // The Streamer.bot side will evolve faster than the app, so an
        // unversioned payload has to keep working rather than be rejected.
        var unversioned = ParsePayload("""{"target":"chat","text":"no version"}""");
        Assert(
            unversioned.Version == StreamerBotEventPayload.CurrentVersion
            && unversioned.Colour == "",
            "A payload with no version was not treated as version 1.");

        var notification = ParsePayload(
            """{"target":"notification","title":"Raid","text":"20 viewers","duration":2500}""");
        Assert(
            notification.Target == StreamerBotEventTarget.Notification
            && notification.DurationMs == 2500,
            "A notification payload lost its duration.");
        Assert(
            ParsePayload("""{"target":"notification"}""").DurationMs
                == StreamerBotEventPayload.DefaultDurationMs
            && ParsePayload("""{"target":"notification","duration":0}""").DurationMs > 0
            && ParsePayload("""{"target":"notification","duration":9999999}""").DurationMs
                <= 60_000,
            "A missing or absurd notification duration was not made safe.");

        Assert(
            ParsePayload("""{"target":"control","command":" show "}""").Command == "show",
            "A control payload lost its command.");

        Assert(
            ParsePayload("""{"target":"control","command":"clear"}""").Surface
                == ControlSurface.Chat,
            "A control payload with no surface field did not default to chat.");
        Assert(
            ParsePayload("""{"target":"control","command":"clear","surface":"notifications"}""")
                .Surface == ControlSurface.Notifications,
            "A control payload did not recognise the notifications surface.");
        Assert(
            ParsePayload("""{"target":"control","command":"clear","surface":"hologram"}""").Surface
                == ControlSurface.Chat,
            "An unrecognised surface value was not treated as the chat default.");

        var anchorCommand = ParsePayload(
            """{"target":"control","command":"anchor","mode":"Head","hand":"Right"}""");
        Assert(
            anchorCommand.RequestedAnchorMode == OverlayAnchorMode.Head
            && anchorCommand.RequestedAnchorHand == OverlayAnchorHand.Right,
            "An anchor control payload did not parse its mode and hand.");
        Assert(
            ParsePayload("""{"target":"control","command":"anchor"}""").RequestedAnchorMode is null,
            "A missing anchor mode was not left null.");
        Assert(
            ParsePayload("""{"target":"control","command":"anchor","mode":"sideways"}""")
                .RequestedAnchorMode is null,
            "An unrecognised anchor mode was not left null.");

        Assert(
            ParsePayload("""{"target":"chat","text":"Kappa hi","emotes":["Kappa","",42,"PogChamp"]}""")
                .EmoteNames.SequenceEqual(["Kappa", "PogChamp"]),
            "An emotes array did not keep its valid string entries and skip the invalid ones.");
        Assert(
            ParsePayload("""{"target":"chat","text":"hi"}""").EmoteNames.Count == 0,
            "A payload with no emotes field produced a non-empty emote list.");

        Assert(
            ParsePayload(
                """
                {"target":"chat","text":"hi","badges":[
                  {"label":"One","imageUrl":"https://a.test/one.png"},
                  {"label":"NoImage","imageUrl":""},
                  {"imageUrl":"https://a.test/no-label.png"},
                  "not an object"
                ]}
                """).Badges.SequenceEqual(
                [
                    new ChatBadge("One", "https://a.test/one.png"),
                    new ChatBadge("NoImage", "")
                ]),
            "A hand-authored badges array did not keep its valid entries and skip the invalid ones.");

        // An unusable colour must become "no colour given" here; the renderer
        // this feeds is several phases downstream and cannot recover from it.
        Assert(
            ParsePayload("""{"target":"chat","colour":"rebeccapurple","accent":"#12345"}""")
                is { Colour: "", Accent: "" },
            "An unparsable colour was passed through instead of dropped.");

        AssertDropped("{ this is not json", "Malformed JSON was accepted.");
        AssertDropped("", "An empty payload was accepted.");
        AssertDropped("{}", "A payload with no target was accepted.");
        AssertDropped("""{"target":"hologram","text":"x"}""", "An unknown target was accepted.");
        AssertDropped("""{"target":7}""", "A non-string target was accepted.");
        AssertDropped("[1,2,3]", "A payload that was not an object was accepted.");
    }

    /// <summary>
    /// Covers both known Streamer.bot <c>Twitch.ChatMessage</c> schema shapes -
    /// see <see cref="TwitchChatMessageMapper"/>'s own remarks for where each
    /// was confirmed - plus the defensive fallbacks a live payload might still
    /// need: badge derived from the subscriber flag when no badges array
    /// names it, and graceful rejection of a payload with no usable text.
    /// </summary>
    private static void TestTwitchChatMessageMapper()
    {
        var newer = MapTwitchChatMessage(
            """
            {
              "user": {
                "id": "12345",
                "login": "testviewer",
                "name": "TestViewer",
                "role": 2,
                "badges": [
                  { "name": "moderator", "version": "1", "imageUrl": "https://a.test/mod.png" },
                  { "name": "premium", "version": "1", "imageUrl": "https://a.test/prime.png" },
                  { "name": "glhf-pledge", "version": "1", "imageUrl": "https://a.test/glhf.png" }
                ],
                "color": "#FF0000",
                "subscribed": false
              },
              "messageId": "abc123",
              "text": "hello from the newer shape Kappa",
              "emotes": [ { "name": "Kappa", "startIndex": 25, "endIndex": 29 } ]
            }
            """);
        Assert(
            newer is { Target: StreamerBotEventTarget.Chat }
            && newer.User == "TestViewer"
            && newer.Colour == "#FF0000"
            && newer.Badge == "Mod"
            && newer.BadgeImageUrl == "https://a.test/mod.png"
            && newer.Text == "hello from the newer shape Kappa"
            && newer.EmoteNames.SequenceEqual(["Kappa"]),
            "The newer top-level-user Twitch.ChatMessage shape did not map correctly.");
        // The bug this covers: an earlier version picked one badge from four
        // hardcoded categories and silently dropped everything else,
        // including Prime and any channel-specific custom badge.
        Assert(
            newer.Badges.Count == 3
            && newer.Badges[0] == new ChatBadge("Mod", "https://a.test/mod.png")
            && newer.Badges[1] == new ChatBadge("Prime", "https://a.test/prime.png")
            && newer.Badges[2] == new ChatBadge("glhf-pledge", "https://a.test/glhf.png"),
            "Not every badge on the message was kept - Prime or the unrecognised "
            + "channel-specific badge was dropped.");

        var older = MapTwitchChatMessage(
            """
            {
              "message": {
                "userId": "999",
                "username": "oldviewer",
                "displayName": "OldViewer",
                "role": 1,
                "subscriber": true,
                "color": "0000ff",
                "message": "hi from the older shape PogChamp",
                "badges": [ { "name": "subscriber", "version": "6", "imageUrl": "https://b.test/sub.png" } ],
                "emotes": [ { "name": "PogChamp" } ],
                "cheerEmotes": [ { "name": "Cheer100" } ]
              }
            }
            """);
        Assert(
            older is { Target: StreamerBotEventTarget.Chat }
            && older.User == "OldViewer"
            && older.Colour == "#0000FF"
            && older.Badge == "Sub"
            && older.BadgeImageUrl == "https://b.test/sub.png"
            && older.Text == "hi from the older shape PogChamp"
            && older.EmoteNames.SequenceEqual(["PogChamp", "Cheer100"]),
            "The older message-wrapped Twitch.ChatMessage shape did not map its badge image, "
            + "emotes and cheer emotes correctly.");

        // No badges array at all, but the older shape's own subscriber flag
        // is true - the fallback in ReadBadge, not the badge-name scan.
        var subscriberFallback = MapTwitchChatMessage(
            """{"message":{"username":"nobadges","subscriber":true,"message":"hi"}}""");
        Assert(
            subscriberFallback is { Badge: "Sub", BadgeImageUrl: "" },
            "A subscriber flag with no badges array did not fall back to a Sub badge with no image.");

        // No username anywhere and no colour - must still map rather than
        // reject, with sensible empty defaults for the renderer to handle.
        var minimal = MapTwitchChatMessage("""{"text":"just text"}""");
        Assert(
            minimal is { User: "", Colour: "", Badge: "", BadgeImageUrl: "", Text: "just text" },
            "A minimal payload with only text was not mapped with safe defaults.");

        Assert(
            !TwitchChatMessageMapper.TryMap(
                JsonDocument.Parse("{}").RootElement,
                out _,
                out var rejection)
            && rejection.Length > 0,
            "A Twitch chat message with no usable text anywhere was accepted.");
        Assert(
            !TwitchChatMessageMapper.TryMap(
                JsonDocument.Parse("[1,2,3]").RootElement,
                out _,
                out _),
            "A Twitch chat message that was not a JSON object was accepted.");
    }

    /// <summary>
    /// Uses the exact response shape confirmed live against a running
    /// Streamer.bot instance's <c>TwitchGetEmotes</c> request - see
    /// <see cref="TwitchEmoteCatalog"/>'s own remarks.
    /// </summary>
    private static void TestTwitchEmoteCatalog()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id": "req-1",
              "emotes": {
                "userEmotes": [
                  { "name": "Kappa", "type": "twitch_globals", "imageUrl": "https://a.test/kappa.png" },
                  { "name": "AngelThump", "type": "BTTVGlobal", "imageUrl": "https://b.test/angel.png" },
                  { "name": "missingUrl", "type": "twitch_globals" },
                  { "name": "", "imageUrl": "https://c.test/blank.png" },
                  42
                ]
              }
            }
            """);

        var catalog = TwitchEmoteCatalog.Parse(document.RootElement);
        Assert(
            catalog.Count == 2,
            "The Twitch emote catalog did not skip malformed entries and keep only the valid ones.");
        Assert(
            catalog.TryGetImageUrl("Kappa", out var kappaUrl) && kappaUrl == "https://a.test/kappa.png",
            "The Twitch emote catalog lost an official Twitch emote's image URL.");
        Assert(
            catalog.TryGetImageUrl("AngelThump", out var bttvUrl) && bttvUrl == "https://b.test/angel.png",
            "The Twitch emote catalog lost a third-party (BTTV/FFZ/7TV) emote's image URL.");
        Assert(
            !catalog.TryGetImageUrl("missingUrl", out _),
            "An emote entry with no image URL was kept in the catalog.");

        Assert(
            TwitchEmoteCatalog.Parse(JsonDocument.Parse("{}").RootElement).Count == 0,
            "A response with no emotes object did not produce an empty catalog.");
    }

    /// <summary>
    /// §B2's template resolver: a dotted path resolves; a missing path, a
    /// null intermediate (<c>targetUser: null</c>, exactly as Twitch.Follow's
    /// own schema allows it), and a null leaf each resolve to empty rather
    /// than throwing. Also covers the special <c>{event}</c> token and an
    /// unbalanced <c>{</c> with no closing brace.
    /// </summary>
    private static void TestStreamerBotEventTemplateResolvesDottedPaths()
    {
        using var data = JsonDocument.Parse(
            """
            {
              "targetUser": { "name": "Ashling", "id": null },
              "nullTargetUser": null,
              "followedAt": "2026-07-30T12:00:00Z",
              "viewerCount": 42
            }
            """);
        var root = data.RootElement;

        Assert(
            StreamerBotEventTemplate.Resolve("{targetUser.name} just followed!", root, "Twitch.Follow")
            == "Ashling just followed!",
            "A dotted path into a present nested object did not resolve.");
        Assert(
            StreamerBotEventTemplate.Resolve("{targetUser.id}", root, "Twitch.Follow") == "",
            "A null leaf did not resolve to an empty string.");
        Assert(
            StreamerBotEventTemplate.Resolve("{nullTargetUser.name}", root, "Twitch.Follow") == "",
            "A null intermediate segment did not resolve to an empty string.");
        Assert(
            StreamerBotEventTemplate.Resolve("{doesNotExist.name}", root, "Twitch.Follow") == "",
            "A missing path did not resolve to an empty string.");
        Assert(
            StreamerBotEventTemplate.Resolve("{viewerCount} viewers", root, "Twitch.Raid") == "42 viewers",
            "A numeric field did not resolve to its plain string form.");
        Assert(
            StreamerBotEventTemplate.Resolve("New {event}!", root, "Twitch.Raid") == "New Twitch.Raid!",
            "The {event} token did not resolve to the event's own Source.Type label.");
        Assert(
            StreamerBotEventTemplate.Resolve("no tokens here", root, "Twitch.Raid") == "no tokens here",
            "A template with no tokens at all was not passed through unchanged.");
        Assert(
            StreamerBotEventTemplate.Resolve("trailing {unterminated", root, "Twitch.Raid")
            == "trailing {unterminated",
            "An unterminated token (no closing brace) lost its literal tail instead of being copied through.");

        // Alternatives are what let one template cover events that name their
        // actor in different fields, which is the alternative to this app
        // carrying a table of which event uses which.
        Assert(
            StreamerBotEventTemplate.Resolve("{user.name|targetUser.name}", root, "Twitch.Follow")
            == "Ashling",
            "A token's second alternative was not tried after the first resolved to nothing.");
        Assert(
            StreamerBotEventTemplate.Resolve("{targetUser.name|viewerCount}", root, "Twitch.Follow")
            == "Ashling",
            "A later alternative overrode an earlier one that had already resolved.");
        Assert(
            StreamerBotEventTemplate.Resolve("{nope.here|alsoMissing|\"Someone\"}", root, "Twitch.Follow")
            == "Someone",
            "A quoted literal did not act as the last resort when every path was missing.");
        Assert(
            StreamerBotEventTemplate.Resolve("{nope|alsoNope}", root, "Twitch.Follow") == "",
            "A token whose alternatives all failed, with no literal, did not resolve to empty.");

        Assert(
            StreamerBotEventTemplate.Resolve("{eventName}", root, "Twitch.GiftSub") == "Gift Sub",
            "{eventName} did not resolve to the event's own name in readable form.");
        Assert(
            StreamerBotEventTemplate.Resolve("{eventSource}", root, "Twitch.GiftSub") == "Twitch",
            "{eventSource} did not resolve to the source alone.");
        Assert(
            StreamerBotEventTemplate.Resolve("{event}", root, "Twitch.GiftSub") == "Twitch.GiftSub",
            "{event} stopped resolving to the full Source.Type label.");

        // The shipped default, against a payload naming its actor the way
        // Twitch.Follow actually does.
        Assert(
            StreamerBotEventTemplate.Resolve(
                NotificationEventSettings.GenericDefaultTemplate, root, "Twitch.Follow")
                .StartsWith("Ashling", StringComparison.Ordinal),
            "The shipped default template did not lead with the actor it found in the payload.");
        Assert(
            StreamerBotEventTemplate.Resolve(
                NotificationEventSettings.GenericDefaultTemplate,
                JsonDocument.Parse("{}").RootElement,
                "Twitch.Follow")
                .StartsWith("Someone", StringComparison.Ordinal),
            "The shipped default template left a gap instead of its literal when the payload named nobody.");

        TestGenericTemplateAgainstDocumentedPayloadShapes();
    }

    /// <summary>
    /// The shipped default against the payload shapes Streamer.bot actually
    /// documents, rather than against anything invented here.
    /// <para>
    /// Three real shapes, and the reason the default is a chain of
    /// alternatives rather than one field: <c>Twitch.Sub</c> keeps its actor
    /// under <c>user</c>, <c>Twitch.Follow</c> under <c>targetUser</c>, and a
    /// <c>Twitch.PredictionCreated</c> captured live from this very machine
    /// has no actor at all and is snake_case throughout. One field name would
    /// have been right for at most one of them.
    /// </para>
    /// <para>
    /// Real platform payload shapes appear here because this is a test
    /// fixture; production code holds one chain of generic field names and no
    /// per-event knowledge at all.
    /// </para>
    /// </summary>
    private static void TestGenericTemplateAgainstDocumentedPayloadShapes()
    {
        // docs.streamer.bot/api/websocket/events/twitch/follow
        using var follow = JsonDocument.Parse(
            """
            {"broadcaster":null,"isInSharedChat":true,"createdAt":"2026-07-31T15:00:00Z",
             "isTest":false,"targetUser":{"id":"1","login":"ashling","name":"Ashling","type":""},
             "followedAt":"2026-07-31T15:00:00Z"}
            """);
        Assert(
            StreamerBotEventTemplate.Resolve(
                NotificationEventSettings.GenericDefaultTemplate, follow.RootElement, "Twitch.Follow")
            == "Ashling — Follow",
            "The default did not name a follower from the targetUser object Twitch.Follow documents.");

        // docs.streamer.bot/api/websocket/events/twitch/sub - note sub_tier
        // and duration_months sitting beside camelCase systemMessage in the
        // same object, which is why the chain covers both conventions.
        using var sub = JsonDocument.Parse(
            """
            {"user":{"id":"2","login":"viper","name":"Viper","type":""},
             "messageId":null,"systemMessage":null,"isTest":false,
             "createdAt":"2026-07-31T15:00:00Z","sub_tier":"1000","is_prime":true,
             "duration_months":3}
            """);
        Assert(
            StreamerBotEventTemplate.Resolve(
                NotificationEventSettings.GenericDefaultTemplate, sub.RootElement, "Twitch.Sub")
            == "Viper — Sub",
            "The default did not name a subscriber from the user object Twitch.Sub documents.");
        Assert(
            StreamerBotEventTemplate.Resolve(
                "{user.name} subscribed for {duration_months} months at tier {sub_tier}!",
                sub.RootElement,
                "Twitch.Sub")
            == "Viper subscribed for 3 months at tier 1000!",
            "A hand-written template could not reach the snake_case fields in a documented payload.");

        // Captured live from this machine on 2026-07-31: a real prediction,
        // snake_case throughout, and carrying no actor whatsoever.
        using var prediction = JsonDocument.Parse(
            """
            {"locks_at":"2026-07-31T15:33:27Z","id":"2e5b82a4","title":"Poop",
             "outcomes":[{"id":"cf525541","title":"1","color":"blue","users":0,"channel_points":0}],
             "started_at":"2026-07-31T15:32:57Z"}
            """);
        Assert(
            StreamerBotEventTemplate.Resolve(
                NotificationEventSettings.GenericDefaultTemplate,
                prediction.RootElement,
                "Twitch.PredictionCreated")
            == "Poop — Prediction Created",
            "A channel-wide event with no actor did not fall through to its title.");
    }

    /// <summary>
    /// §B2: <c>GetEvents</c>' own reference/events pages are both marked
    /// "Documentation Needed", so this proves the parser against the shape
    /// that mirrors the (documented) Subscribe request's own argument -
    /// source name keyed to an array of event type names - and that a
    /// response this cannot make sense of degrades to an empty list rather
    /// than throwing, since a GetEvents failure must not take down the feed.
    /// </summary>
    private static void TestStreamerBotEventCatalogParsesGetEventsResponse()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "status": "ok",
              "id": "req-1",
              "events": {
                "General": ["Custom"],
                "Twitch": ["Follow", "Raid", "ChatMessage"],
                "YouTube": ["Message"],
                "Broken": "not-an-array",
                "Objects": [{ "type": "Cheer" }, { "name": "Sub" }, { "nothingUseful": true }, 42]
              }
            }
            """);

        var events = StreamerBotEventCatalog.Parse(document.RootElement);
        Assert(
            events.Any(entry => entry is { Source: "Twitch", Type: "Follow" }),
            "A plain string event entry was not parsed.");
        Assert(
            events.Any(entry => entry is { Source: "YouTube", Type: "Message" }),
            "An event under a different source was not parsed.");
        Assert(
            events.Count(entry => entry.Source == "Broken") == 0,
            "A source whose value was not an array produced entries instead of being skipped.");
        Assert(
            events.Any(entry => entry is { Source: "Objects", Type: "Cheer" })
            && events.Any(entry => entry is { Source: "Objects", Type: "Sub" }),
            "An object-shaped event entry (type/name instead of a bare string) was not read.");
        Assert(
            events.Count(entry => entry.Source == "Objects") == 2,
            "A malformed object entry with neither type nor name was not skipped.");
        Assert(
            events.First(entry => entry is { Source: "Twitch", Type: "Follow" }).Key == "Twitch.Follow",
            "StreamerBotEventDescriptor.Key did not build the expected \"Source.Type\" form.");

        Assert(
            StreamerBotEventCatalog.Parse(JsonDocument.Parse("{}").RootElement).Count == 0,
            "A response with no events object did not produce an empty list.");
        Assert(
            StreamerBotEventCatalog.Parse(JsonDocument.Parse("""{"events": "not-an-object"}""").RootElement).Count
            == 0,
            "A response whose events property was not an object did not degrade to an empty list.");
    }

    /// <summary>
    /// A live <c>GetEvents</c> capture (Streamer.bot 1.0.4) showed event names
    /// arrive run-together - <c>GiftSub</c>, <c>HypeTrainLevelUp</c> - not in
    /// the spaced form Streamer.bot's own UI displays, which an earlier
    /// screenshot had made look like the wire format. So the picker has to put
    /// the word breaks back, and this pins that it does so as a transform
    /// rather than a lookup table: every case here is handled by the same
    /// rules, and an event nobody has seen yet gets the same treatment.
    /// </summary>
    private static void TestStreamerBotEventCatalogSpacesRunTogetherEventNames()
    {
        Assert(
            StreamerBotEventCatalog.SpaceCamelCase("GiftSub") == "Gift Sub",
            "An ordinary PascalCase event name did not gain its word break.");
        Assert(
            StreamerBotEventCatalog.SpaceCamelCase("HypeTrainLevelUp") == "Hype Train Level Up",
            "A four-word event name was not fully separated.");
        Assert(
            StreamerBotEventCatalog.SpaceCamelCase("Follow") == "Follow",
            "A single-word event name was altered.");
        Assert(
            StreamerBotEventCatalog.SpaceCamelCase("SevenTVEmoteAdded") == "Seven TV Emote Added",
            "An embedded acronym was split letter by letter instead of kept together.");
        Assert(
            StreamerBotEventCatalog.SpaceCamelCase("") == "",
            "An empty event name did not survive the transform.");
        Assert(
            new StreamerBotEventDescriptor("Twitch", "RewardRedemption").DisplayName
            == "Reward Redemption",
            "StreamerBotEventDescriptor.DisplayName did not use the spaced form.");
    }

    /// <summary>
    /// The picker's whole defence against the freeze that got two earlier
    /// designs rejected: the result set is filtered as data and capped before
    /// any control is built, so the number of rows is bounded by the cap and
    /// not by the catalog. Proven here against a catalog the size of a real
    /// one - the live capture reported 467 events across 44 sources.
    /// <para>
    /// Real platform and event names appear here because this is a test
    /// fixture; production code contains none, which is the point of
    /// searching them by string rather than switching on them.
    /// </para>
    /// </summary>
    private static void TestStreamerBotEventSearchFiltersAndCapsResults()
    {
        var catalog = BuildRealisticEventCatalog();
        Assert(
            catalog.Count >= 187,
            "The realistic catalog fixture is smaller than the event count this design has to survive.");

        var everything = StreamerBotEventSearch.Search(catalog, "");
        Assert(
            everything.MatchCount == catalog.Count,
            "An empty query did not match the whole catalog.");
        Assert(
            everything.Matches.Count == StreamerBotEventSearch.DefaultResultLimit,
            "An empty query rendered more rows than the cap - the freeze this design exists to prevent.");
        Assert(
            everything.Truncated,
            "A capped result set did not report itself as truncated, so the count line would understate it.");

        var follows = StreamerBotEventSearch.Search(catalog, "follow");
        Assert(
            follows.Matches.Any(entry => entry is { Source: "Twitch", Type: "Follow" })
            && follows.Matches.Any(entry => entry is { Source: "Kick", Type: "Follow" }),
            "Searching one word did not find the same event across two different sources.");
        Assert(
            follows.Matches.All(entry =>
                entry.Source.Contains("follow", StringComparison.OrdinalIgnoreCase)
                || entry.Type.Contains("follow", StringComparison.OrdinalIgnoreCase)),
            "A result matched neither the source nor the event name.");

        var narrowed = StreamerBotEventSearch.Search(catalog, "kick follow");
        Assert(
            narrowed.MatchCount < follows.MatchCount && narrowed.MatchCount > 0,
            "Adding a second search term did not narrow the results.");
        Assert(
            narrowed.Matches.All(entry => entry.Source == "Kick"),
            "A second term matching only the source did not constrain the results to it.");
        Assert(
            StreamerBotEventSearch.Search(catalog, "follow kick").MatchCount == narrowed.MatchCount,
            "Search results depended on the order the terms were typed in.");

        // The wire format is run-together and the row on screen is not, so a
        // search that only matched one of them would look broken from
        // whichever side the user happened to type.
        Assert(
            StreamerBotEventSearch.Search(catalog, "giftsub").Matches
                .Any(entry => entry is { Source: "Twitch", Type: "GiftSub" }),
            "Searching the raw run-together name did not find the event.");
        Assert(
            StreamerBotEventSearch.Search(catalog, "gift sub").Matches
                .Any(entry => entry is { Source: "Twitch", Type: "GiftSub" }),
            "Searching the spaced display name did not find the event.");

        // Ranking, not merely filtering - and this is the case that proves
        // why it matters. Sorted alphabetically, "sub" filled the visible
        // twenty with Twitch's EventSub/subscriber-mode plumbing and left
        // Twitch.Sub at position 28, GiftSub at 22: the cap threw away
        // precisely the three events anyone typing that word wants.
        var subs = StreamerBotEventSearch.Search(catalog, "sub");
        Assert(
            subs.Matches[0] is { Source: "Twitch", Type: "Sub" },
            "An event whose name is exactly the query did not rank first.");
        Assert(
            subs.Matches.Any(entry => entry is { Source: "Twitch", Type: "GiftSub" })
            && subs.Matches.Any(entry => entry is { Source: "Twitch", Type: "ReSub" }),
            "Twitch's own sub events were pushed out of the visible results by the cap.");
        Assert(
            IndexOfKey(subs.Matches, "Twitch.GiftSub")
            < IndexOfKey(subs.Matches, "Twitch.BotEventSubConnected"),
            "A word-boundary match (Gift Sub) did not outrank an incidental one (BotEventSubConnected).");
        Assert(
            StreamerBotEventSearch.Search(catalog, "follow").Matches[0].Type == "Follow",
            "An exact name match did not lead the results for a differently-cased query.");

        // A plural search term has to find a singular event name, or "subs"
        // silently returns none of Twitch's three sub events.
        var plural = StreamerBotEventSearch.Search(catalog, "subs");
        Assert(
            plural.Matches[0] is { Source: "Twitch", Type: "Sub" },
            "A plural query did not find the singular event name.");
        Assert(
            plural.MatchCount == subs.MatchCount,
            "A plural query matched a different set from its singular form.");
        Assert(
            StreamerBotEventSearch.Search(catalog, "raid").Matches[0].Type == "Raid",
            "Stripping a plural \"s\" narrowed a query that was never plural.");

        // The one deliberate exception to "no hardcoded event names": a
        // search-only synonym table, because Twitch's bits arrive as Cheer
        // and being told "no results" for "bits" is indistinguishable from
        // the feature being broken. It only ever widens a search - see
        // StreamerBotEventSearch.SynonymGroups.
        var bits = StreamerBotEventSearch.Search(catalog, "bits");
        Assert(
            bits.Matches[0] is { Source: "Twitch", Type: "Cheer" },
            "Searching \"bits\" did not lead with the event Streamer.bot actually calls Cheer.");
        Assert(
            IndexOfKey(bits.Matches, "Twitch.Cheer") < IndexOfKey(bits.Matches, "Twitch.BitsBadgeTier"),
            "An exact synonym match did not outrank a weaker direct match on the literal word.");
        Assert(
            StreamerBotEventSearch.Search(catalog, "host").Matches.Any(entry => entry.Type == "Raid"),
            "A synonym group did not connect the word searched to the word Streamer.bot uses.");

        // The rule the exception must not break: a synonym can reorder
        // results, never gate them. A direct name match always wins.
        Assert(
            StreamerBotEventSearch.Search(catalog, "cheer").Matches[0] is { Type: "Cheer" }
            && StreamerBotEventSearch.Search(catalog, "raid").Matches[0].Type == "Raid"
            && StreamerBotEventSearch.Search(catalog, "follow").Matches[0].Type == "Follow",
            "The synonym table displaced a direct name match from the top of the results.");
        Assert(
            StreamerBotEventSearch.Search(catalog, "somethinghappened").Matches
                .Any(entry => entry.Source == "Zorblatt"),
            "A source outside the synonym table stopped being findable, which is the thing it must never do.");

        Assert(
            StreamerBotEventSearch.Search(catalog, "nothingmatchesthis").MatchCount == 0,
            "A query matching nothing still produced results.");
        Assert(
            StreamerBotEventSearch.Search(catalog, "", limit: 5).Matches.Count == 5,
            "An explicit smaller cap was not honoured.");
        Assert(
            StreamerBotEventSearch.Search([], "follow").TotalCount == 0,
            "An empty catalog did not report a zero total.");
    }

    /// <summary>Where one key sits in a ranked result set, or int.MaxValue when the cap left it out - so an "A outranks B" assertion reads the right way round when B is missing entirely.</summary>
    private static int IndexOfKey(IReadOnlyList<StreamerBotEventDescriptor> matches, string key)
    {
        for (var index = 0; index < matches.Count; index++)
        {
            if (string.Equals(matches[index].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// Proves no hardcoded platform list crept into the chip lookup, using a
    /// source name that exists nowhere: it must resolve to a drawable badge
    /// with a palette colour, exactly as a well-known source does, and give
    /// the same answer on every call and every run. Colour stability is not
    /// cosmetic - <see cref="object.GetHashCode"/> is randomised per process,
    /// so a chip built on it would change colour at each launch.
    /// </summary>
    private static void TestStreamerBotSourceChipHandlesAnySourceDeterministically()
    {
        const string fabricated = "Zorblatt";
        var badge = StreamerBotSourceChip.BadgeFor(fabricated);
        Assert(
            badge.Source == fabricated && badge.Abbreviation.Length > 0,
            "A source nobody anticipated did not produce a usable chip.");
        Assert(
            badge.Colour.StartsWith('#') && badge.Colour.Length == 7,
            "A chip colour was not the \"#RRGGBB\" form the rest of this app uses.");
        Assert(
            StreamerBotSourceChip.ColourFor(fabricated) == badge.Colour
            && StreamerBotSourceChip.ColourFor(fabricated.ToUpperInvariant()) == badge.Colour,
            "The same source resolved to two different colours, so chips would not be stable.");

        // Sources from the live capture, checked only for the generic rules -
        // capitals when there are two, first two letters otherwise. No branch
        // in the resolver knows any of these names.
        Assert(
            StreamerBotSourceChip.AbbreviationFor("YouTube") == "YT"
            && StreamerBotSourceChip.AbbreviationFor("StreamElements") == "SE"
            && StreamerBotSourceChip.AbbreviationFor("Twitch") == "TW"
            && StreamerBotSourceChip.AbbreviationFor("Kick") == "KI",
            "The generic abbreviation rules did not produce the expected short labels.");
        Assert(
            StreamerBotSourceChip.AbbreviationFor("") == "?"
            && StreamerBotSourceChip.AbbreviationFor(null) == "?"
            && StreamerBotSourceChip.BadgeFor(null).Colour.Length == 7,
            "An empty or missing source threw or produced something undrawable.");
    }

    /// <summary>
    /// A catalog the shape and size of a real one, for the search tests and
    /// for the picker's own responsiveness check. Source names and counts are
    /// from a live <c>GetEvents</c> capture (Streamer.bot 1.0.4): 467 events
    /// across 44 sources. The first events under each source are that
    /// source's real names so search assertions mean something; the rest are
    /// filler standing in for the long tail, which is all the picker has to
    /// scroll past anyway.
    /// </summary>
    private static IReadOnlyList<StreamerBotEventDescriptor> BuildRealisticEventCatalog()
    {
        var sources = new (string Source, int Count, string[] Real)[]
        {
            // BotEventSubConnected and ChatSubscriberModeOff are real Twitch
            // events and are here on purpose: they are the incidental "sub"
            // matches that used to crowd Twitch.Sub out of the visible
            // twenty, so the ranking test needs them present to mean anything.
            ("Twitch", 137, ["Follow", "Cheer", "Sub", "ReSub", "GiftSub", "GiftBomb", "Raid",
                "HypeTrainStart", "HypeTrainLevelUp", "RewardRedemption", "ChatMessage", "Whisper",
                "BotEventSubConnected", "BroadcasterEventSubConnected", "ChatSubscriberModeOff",
                "ChatSubscriberModeOn", "SubCounterRollover", "SharedChatSub"]),
            ("Elgato", 90, ["ActionTriggered"]),
            ("YouTube", 29, ["BroadcastStarted", "Message", "SuperChat", "NewSponsor"]),
            ("Kick", 21, ["Follow", "Subscription", "GiftSubscription", "MassGiftSubscription",
                "Resubscription", "ChatMessage", "StreamOnline"]),
            ("Trovo", 16, ["Follow", "Subscription", "GiftSubscription"]),
            ("Misc", 13, ["TimedAction"]),
            ("Fourthwall", 13, ["OrderPlaced"]),
            ("MeldStudio", 12, ["SceneChanged"]),
            ("VTubeStudio", 11, ["ModelLoaded"]),
            ("Obs", 9, ["SceneChanged", "StreamingStarted"]),
            ("CrowdControl", 9, ["EffectRedeemed"]),
            ("ThrowingSystem", 8, ["ObjectThrown"]),
            ("StreamlabsDesktop", 7, ["SceneChanged"]),
            ("Streamlabs", 6, ["Donation"]),
            ("Application", 6, ["Started"]),
            ("StreamElements", 5, ["Tip"]),
            ("Kofi", 5, ["Donation"]),
            ("Patreon", 5, ["PledgeCreated"]),
            ("General", 1, ["Custom"]),
            ("Pallygg", 3, ["Tip"]),
            ("DonorDrive", 3, ["Donation"]),
            ("HypeRate", 4, ["HeartRatePulse", "Connected"]),
            ("StreamDeck", 4, ["ButtonPressed"]),
            ("Zorblatt", 4, ["SomethingHappened"])
        };

        var catalog = new List<StreamerBotEventDescriptor>();
        foreach (var (source, count, real) in sources)
        {
            for (var index = 0; index < count; index++)
            {
                catalog.Add(
                    new StreamerBotEventDescriptor(
                        source,
                        index < real.Length ? real[index] : $"LongTailEvent{index:D3}"));
            }
        }

        return catalog;
    }

    private static StreamerBotEventPayload MapTwitchChatMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert(
            TwitchChatMessageMapper.TryMap(document.RootElement, out var payload, out var rejection),
            $"A Twitch chat message expected to map was rejected: {rejection}.");
        return payload!;
    }

    /// <summary>
    /// Proves the receive pump keeps its two destinations apart, and survives
    /// the payloads a hand-written Streamer.bot action really produces.
    /// </summary>
    private static async Task TestStreamerBotEventStreamAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var activity = new ConcurrentQueue<BridgeActivity>();
        await using var stream = new StreamerBotEventStream(
            new StreamerBotConfig
            {
                WebSocketUrl = $"ws://127.0.0.1:{port}/",
                Password = "password",
                DryRun = false
            },
            activity.Enqueue);
        stream.Start();

        using var socket = await AcceptEventSubscriberAsync(
            listener,
            requireAuthentication: true,
            timeout.Token);

        // Everything a bad action can send, ahead of the one good message: if
        // any of these ends the pump, the valid payload below never arrives.
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{ not json at all"),
            WebSocketMessageType.Text,
            true,
            timeout.Token);
        await SendCustomEventAsync(socket, new { }, timeout.Token);
        await SendCustomEventAsync(
            socket,
            new { target = "hologram", text = "unknown target" },
            timeout.Token);
        await SendJsonAsync(
            socket,
            new { request = "Hello", info = new { name = "A late hello" } },
            timeout.Token);
        await SendCustomEventAsync(
            socket,
            new
            {
                v = 1,
                target = "chat",
                user = "Nightbot",
                colour = "#00FF00",
                text = "first message"
            },
            timeout.Token);

        var first = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            first.Payload.Target == StreamerBotEventTarget.Chat
            && first.Payload.User == "Nightbot"
            && first.Payload.Text == "first message",
            "The event pump did not survive malformed frames ahead of a good one.");

        // A response and an event now interleave on the same socket, which is
        // the case that made a second connection necessary in the first place.
        var request = stream.SendRequestAsync("GetActions", timeout.Token);
        using var requested = await ReceiveJsonAsync(socket, timeout.Token);
        Assert(
            requested.RootElement.GetProperty("request").GetString() == "GetActions",
            "The event stream did not send the requested operation.");
        var requestId = requested.RootElement.GetProperty("id").GetString();

        await SendCustomEventAsync(
            socket,
            new { target = "notification", title = "Raid", text = "20 viewers" },
            timeout.Token);
        await SendJsonAsync(
            socket,
            new { status = "ok", id = requestId, actions = Array.Empty<string>() },
            timeout.Token);

        using var response = await request.WaitAsync(timeout.Token);
        Assert(
            response.RootElement.GetProperty("id").GetString() == requestId,
            "A response frame was not routed back to its request.");

        var second = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            second.Payload.Target == StreamerBotEventTarget.Notification
            && second.Payload.Title == "Raid",
            "An event frame sent mid-request was not routed to the event channel.");
        Assert(
            stream.PendingRequestCount == 0,
            "A completed request was left in the pending table.");

        // The raw platform route added per §B6, exercised over the real
        // socket rather than only through TwitchChatMessageMapper directly -
        // proves Dispatch/PublishEvent actually route Twitch.ChatMessage
        // through the mapper rather than through StreamerBotEventPayload.TryParse.
        await SendTwitchChatMessageEventAsync(
            socket,
            new
            {
                user = new { name = "TwitchViewer", color = "#123456" },
                text = "raw twitch chat message"
            },
            timeout.Token);
        var third = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            third.Payload.Target == StreamerBotEventTarget.Chat
            && third.Payload.User == "TwitchViewer"
            && third.Payload.Colour == "#123456"
            && third.Payload.Text == "raw twitch chat message",
            "A live Twitch.ChatMessage frame was not routed through the mapper to the event channel.");
        Assert(
            activity.Any(entry =>
                entry.EventName == "streamerbot.event_dropped"
                && entry.Level == BridgeLogLevel.Debug),
            "Dropped frames were not reported at debug level.");
    }

    /// <summary>
    /// The pending table is the one place in the event stream that can leak, so
    /// both non-success exits from a request are checked explicitly.
    /// </summary>
    private static async Task TestStreamerBotEventStreamPendingRequestsAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var stream = new StreamerBotEventStream(
            new StreamerBotConfig
            {
                WebSocketUrl = $"ws://127.0.0.1:{port}/",
                DryRun = false
            });
        stream.Start();

        // No password configured and no authentication offered: the other half
        // of the handshake the action client performs.
        var socket = await AcceptEventSubscriberAsync(
            listener,
            requireAuthentication: false,
            timeout.Token);

        using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            giveUp.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                using var abandoned = await stream.SendRequestAsync("GetActions", giveUp.Token);
                throw new InvalidOperationException(
                    "SELF-TEST FAIL: an unanswered request completed.");
            }
            catch (OperationCanceledException)
            {
                // Expected: the mock never answers this one.
            }
        }

        Assert(
            stream.PendingRequestCount == 0,
            "A cancelled request was left in the pending table.");

        var pending = stream.SendRequestAsync("GetActions", timeout.Token);
        using var sent = await ReceiveJsonAsync(socket, timeout.Token);
        socket.Abort();
        socket.Dispose();

        try
        {
            using var lost = await pending.WaitAsync(timeout.Token);
            throw new InvalidOperationException(
                "SELF-TEST FAIL: a request survived the socket it was sent on.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            // Expected: the connection failed underneath the request.
        }

        Assert(
            stream.PendingRequestCount == 0,
            "A request left behind by a dropped socket was not removed.");
    }

    /// <summary>
    /// §B2, opt-in: only the events the wearer explicitly enabled fold into
    /// the Subscribe request alongside the two this app always wants - a
    /// broader "subscribe to everything GetEvents reports" design was tried
    /// live and rejected, since Streamer.bot exposes no way to ask which
    /// events currently have an enabled trigger (confirmed live: disabling
    /// every event in its own Settings > Events panel did not stop delivery)
    /// and a wide-open subscription pulled in non-alert plumbing (OBS scene
    /// changes and the like). An enabled event becomes a notification
    /// through the generic template; an event Streamer.bot reports but the
    /// wearer never enabled (YouTube.Message here) is neither subscribed to
    /// nor turned into a notification even if it somehow arrives anyway.
    /// </summary>
    private static async Task TestStreamerBotEventStreamSubscribesToEnabledEventsAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var notificationEvents = new NotificationEventSettings(
            // Kick.Subscription is enabled but deliberately absent from the
            // catalog below - see the assertion on it further down.
            ["Twitch.Follow", "twitch.follow", "Kick.Subscription"],
            new Dictionary<string, string>(),
            NotificationEventSettings.GenericDefaultTemplate,
            true);

        await using var stream = new StreamerBotEventStream(
            new StreamerBotConfig { WebSocketUrl = $"ws://127.0.0.1:{port}/", DryRun = false },
            notificationEvents: notificationEvents);
        stream.Start();

        var catalog = new { Twitch = new[] { "Follow", "Raid" }, YouTube = new[] { "Message" } };
        var (socket, subscribedEvents) = await AcceptSubscriberCapturingEventsAsync(listener, catalog, timeout.Token);
        using var disposableSocket = socket;

        var general = subscribedEvents.GetProperty("General")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();
        Assert(
            general.Length == 1 && general[0] == "Custom",
            "General.Custom was not subscribed unconditionally alongside the enabled notification events.");

        var twitch = subscribedEvents.GetProperty("Twitch")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();
        Assert(
            twitch.Contains("ChatMessage") && twitch.Contains("Follow"),
            "The enabled notification event was not folded into the Twitch subscription alongside ChatMessage.");
        Assert(
            !twitch.Contains("Raid"),
            "An event GetEvents reported but the wearer never enabled (Twitch.Raid) was subscribed to anyway.");
        Assert(
            twitch.Count(type => string.Equals(type, "Follow", StringComparison.OrdinalIgnoreCase)) == 1,
            "A duplicate/differently-cased enabled event produced more than one Subscribe entry.");
        Assert(
            !subscribedEvents.TryGetProperty("YouTube", out _),
            "A source with nothing enabled (YouTube) still appeared in the Subscribe request.");

        // The wearer enabled Kick.Subscription and this instance's GetEvents
        // did not mention it. Dropping it would look like tidiness and behave
        // like a silent failure: a fetch that failed, or came back while an
        // integration was reloading, would turn their alerts off with nothing
        // on screen to explain why. An event name Streamer.bot does not know
        // simply never fires, which is the far cheaper wrong answer.
        Assert(
            subscribedEvents.TryGetProperty("Kick", out var kick)
            && kick.EnumerateArray().Any(entry => entry.GetString() == "Subscription"),
            "An enabled event this GetEvents response did not report was silently dropped from Subscribe.");

        await SendEventAsync(
            socket,
            "Twitch",
            "Follow",
            new { targetUser = new { name = "Ashling" }, isTest = false },
            timeout.Token);
        var received = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            received.Payload.Target == StreamerBotEventTarget.Notification,
            "A directly-subscribed enabled event did not produce a notification payload.");

        // The headline is the Title; Text carries whatever the viewer
        // themselves typed, and a follow carries nothing, so it stays empty
        // rather than repeating the headline.
        Assert(
            received.Payload.Title.Contains("Ashling"),
            $"The generic template did not name the actor from the payload - got \"{received.Payload.Title}\".");
        Assert(
            received.Payload.Title.Contains("Follow"),
            $"The generic template did not name the event - got \"{received.Payload.Title}\".");
        Assert(
            !received.Payload.Title.Contains("Someone"),
            "The generic template fell back to its literal even though the payload named an actor.");
        Assert(
            received.Payload.Text.Length == 0,
            $"An event carrying no message of its own still filled the message line - got \"{received.Payload.Text}\".");
        Assert(
            received.Payload.Source == "Twitch",
            "The event's source did not reach the payload, so the notification could not show its icon.");

        // A cheer's note and a donation's message are the part worth reading,
        // and they get their own line under the headline rather than being
        // folded into it. Twitch.Cheer documents the field as "text".
        await SendEventAsync(
            socket,
            "Twitch",
            "Follow",
            new { targetUser = new { name = "Ashling" }, text = "have some bits!", bits = 500, isTest = false },
            timeout.Token);
        var withMessage = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            withMessage.Payload.Text == "have some bits!",
            $"The viewer's own message did not reach its own line - got \"{withMessage.Payload.Text}\".");
        Assert(
            withMessage.Payload.Title.Contains("Ashling"),
            "The headline was lost once the payload also carried a message.");

        // Twitch documents systemMessage on Sub/ReSub/GiftSub: a whole
        // sentence it wrote itself. It must win over anything assembled here,
        // and must arrive verbatim - a brace in it is somebody's text, not a
        // token to resolve.
        await SendEventAsync(
            socket,
            "Twitch",
            "Follow",
            new
            {
                systemMessage = "Viper subscribed at Tier 1. They've subscribed for 3 months!",
                targetUser = new { name = "Ashling" },
                isTest = false
            },
            timeout.Token);
        var withSystemMessage = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            withSystemMessage.Payload.Title
            == "Viper subscribed at Tier 1. They've subscribed for 3 months!",
            "The platform's own written-out sentence did not win over this app's assembled wording - "
            + $"got \"{withSystemMessage.Payload.Title}\".");

        await SendEventAsync(
            socket,
            "Twitch",
            "Follow",
            new { systemMessage = "   ", targetUser = new { name = "Ashling" }, isTest = false },
            timeout.Token);
        var blankSystemMessage = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            blankSystemMessage.Payload.Title.Contains("Ashling"),
            "A present-but-blank systemMessage produced an empty notification instead of falling "
            + "through to the generic wording.");

        // Even if Streamer.bot sent one anyway, an event never enabled must
        // not become a notification - the dispatch-side check is what
        // actually enforces this, not just the Subscribe request.
        await SendEventAsync(socket, "Twitch", "Raid", new { viewers = 5 }, timeout.Token);
        await SendCustomEventAsync(
            socket,
            new { target = "chat", user = "Viewer", text = "still alive after an unenabled event" },
            timeout.Token);
        var next = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            next.Payload.Target == StreamerBotEventTarget.Chat,
            "An unenabled event (Twitch.Raid) was turned into a notification instead of being ignored.");
    }

    /// <summary>
    /// §B2: "a GetEvents failure must not take down the feed" - now most
    /// relevant at connect time, since that is when this stream asks it to
    /// build its Subscribe list. A GetEvents failure there falls back to
    /// exactly General.Custom and Twitch.ChatMessage, and the connection
    /// still succeeds and keeps delivering events.
    /// </summary>
    private static async Task TestStreamerBotEventStreamFallsBackWhenGetEventsFailsAtConnectAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var stream = new StreamerBotEventStream(
            new StreamerBotConfig { WebSocketUrl = $"ws://127.0.0.1:{port}/", DryRun = false });
        stream.Start();

        // null catalog tells the mock to answer GetEvents with an error
        // status rather than a catalog, simulating a real GetEvents failure.
        var (socket, subscribedEvents) = await AcceptSubscriberCapturingEventsAsync(listener, null, timeout.Token);
        using var disposableSocket = socket;

        var general = subscribedEvents.GetProperty("General")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();
        var twitch = subscribedEvents.GetProperty("Twitch")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();
        Assert(
            general.Length == 1 && general[0] == "Custom" && twitch.Length == 1 && twitch[0] == "ChatMessage",
            "A GetEvents failure at connect did not fall back to exactly General.Custom and Twitch.ChatMessage.");

        await SendCustomEventAsync(
            socket,
            new { target = "chat", user = "Viewer", text = "still alive" },
            timeout.Token);
        var received = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            received.Payload.Target == StreamerBotEventTarget.Chat && received.Payload.Text == "still alive",
            "The connection did not succeed and keep delivering events after a GetEvents failure at connect.");
    }

    /// <summary>
    /// Separately, an on-demand <see cref="StreamerBotEventStream.GetEventsAsync"/>
    /// call (e.g. the desktop app's "Refresh events" button) that goes
    /// unanswered must not leak a pending-table entry or otherwise disturb
    /// the feed - the same pending-table discipline
    /// <see cref="TestStreamerBotEventStreamPendingRequestsAsync"/> already
    /// covers for other requests.
    /// </summary>
    private static async Task TestStreamerBotEventStreamGetEventsFailureDoesNotStopTheFeedAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var stream = new StreamerBotEventStream(
            new StreamerBotConfig { WebSocketUrl = $"ws://127.0.0.1:{port}/", DryRun = false });
        stream.Start();

        using var socket = await AcceptEventSubscriberAsync(listener, requireAuthentication: false, timeout.Token);

        using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            giveUp.CancelAfter(TimeSpan.FromMilliseconds(300));
            try
            {
                await stream.GetEventsAsync(giveUp.Token);
                throw new InvalidOperationException(
                    "SELF-TEST FAIL: GetEvents completed even though the mock never answered it.");
            }
            catch (OperationCanceledException)
            {
                // Expected: the mock deliberately never answers this one.
            }
        }

        Assert(
            stream.PendingRequestCount == 0,
            "An abandoned GetEvents request was left in the pending table.");

        await SendCustomEventAsync(
            socket,
            new { target = "chat", user = "Viewer", text = "still alive" },
            timeout.Token);
        var received = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            received.Payload.Target == StreamerBotEventTarget.Chat && received.Payload.Text == "still alive",
            "The event feed stopped delivering events after a GetEvents request went unanswered.");
    }

    /// <summary>
    /// Same Hello/GetEvents/Subscribe handshake as
    /// <see cref="AcceptEventSubscriberAsync"/>, but answers GetEvents with
    /// <paramref name="eventsCatalogResponse"/> (or an error status when
    /// null, to simulate a GetEvents failure) and hands the raw Subscribe
    /// <c>events</c> argument back for the caller's own assertions instead of
    /// asserting a fixed shape - needed once that argument became dynamic per
    /// §B2.
    /// </summary>
    private static async Task<(WebSocket Socket, JsonElement Events)> AcceptSubscriberCapturingEventsAsync(
        HttpListener listener,
        object? eventsCatalogResponse,
        CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
        var socket = webSocketContext.WebSocket;

        await SendJsonAsync(
            socket,
            new { request = "Hello", info = new { instanceId = "self-test", name = "Mock Streamer.bot" } },
            cancellationToken);

        using var getEvents = await ReceiveJsonAsync(socket, cancellationToken);
        Assert(
            getEvents.RootElement.GetProperty("request").GetString() == "GetEvents",
            "The event stream did not ask GetEvents before subscribing.");
        var getEventsId = getEvents.RootElement.GetProperty("id").GetString();
        if (eventsCatalogResponse is null)
        {
            await SendJsonAsync(
                socket,
                new { status = "error", id = getEventsId, error = "self-test: GetEvents deliberately failed" },
                cancellationToken);
        }
        else
        {
            await SendJsonAsync(
                socket,
                new { status = "ok", id = getEventsId, events = eventsCatalogResponse },
                cancellationToken);
        }

        using var subscribe = await ReceiveJsonAsync(socket, cancellationToken);
        Assert(
            subscribe.RootElement.GetProperty("request").GetString() == "Subscribe",
            "The event stream did not subscribe.");
        var events = subscribe.RootElement.GetProperty("events").Clone();
        await SendJsonAsync(
            socket,
            new { status = "ok", id = subscribe.RootElement.GetProperty("id").GetString() },
            cancellationToken);

        return (socket, events);
    }

    private static Task SendEventAsync(
        WebSocket socket,
        string source,
        string type,
        object data,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            socket,
            new
            {
                timeStamp = DateTimeOffset.Now.ToString("O"),
                @event = new { source, type },
                data
            },
            cancellationToken);

    private static StreamerBotEventPayload ParsePayload(string json)
    {
        Assert(
            StreamerBotEventPayload.TryParse(json, out var payload, out var rejection),
            $"A valid payload was rejected: {rejection}");
        return payload!;
    }

    private static void AssertDropped(string json, string message)
    {
        Assert(
            !StreamerBotEventPayload.TryParse(json, out _, out var rejection),
            message);
        Assert(rejection.Length > 0, $"{message} (no reason was reported)");
    }

    /// <summary>
    /// Plays the Streamer.bot side of a subscriber connection and hands back
    /// the socket so a test can push frames down it.
    /// </summary>
    private static async Task<WebSocket> AcceptEventSubscriberAsync(
        HttpListener listener,
        bool requireAuthentication,
        CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
        var socket = webSocketContext.WebSocket;

        await SendJsonAsync(
            socket,
            requireAuthentication
                ? new
                {
                    request = "Hello",
                    info = new { instanceId = "self-test", name = "Mock Streamer.bot" },
                    authentication = new { salt = "salt", challenge = "challenge" }
                }
                : (object)new
                {
                    request = "Hello",
                    info = new { instanceId = "self-test", name = "Mock Streamer.bot" }
                },
            cancellationToken);

        if (requireAuthentication)
        {
            using var authentication = await ReceiveJsonAsync(socket, cancellationToken);
            Assert(
                authentication.RootElement.GetProperty("request").GetString() == "Authenticate",
                "The event stream did not authenticate.");
            Assert(
                authentication.RootElement.GetProperty("authentication").GetString()
                == "zTM5ki6L2vVvBQiTG9ckH1Lh64AbnCf6XZ226UmnkIA=",
                "The event stream sent an incorrect authentication value.");
            await SendJsonAsync(
                socket,
                new
                {
                    status = "ok",
                    id = authentication.RootElement.GetProperty("id").GetString()
                },
                cancellationToken);
        }

        // Per §B2's revised design, the stream asks GetEvents before it ever
        // subscribes - answered with an empty catalog here, so every test
        // using this helper keeps asserting the exact same fallback shape
        // (General.Custom + Twitch.ChatMessage only) it always has.
        using var getEvents = await ReceiveJsonAsync(socket, cancellationToken);
        Assert(
            getEvents.RootElement.GetProperty("request").GetString() == "GetEvents",
            "The event stream did not ask GetEvents before subscribing.");
        await SendJsonAsync(
            socket,
            new
            {
                status = "ok",
                id = getEvents.RootElement.GetProperty("id").GetString(),
                events = new { }
            },
            cancellationToken);

        using var subscribe = await ReceiveJsonAsync(socket, cancellationToken);
        Assert(
            subscribe.RootElement.GetProperty("request").GetString() == "Subscribe",
            "The event stream did not subscribe.");
        var events = subscribe.RootElement.GetProperty("events");
        var subscribed = events
            .GetProperty("General")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();
        Assert(
            subscribed.Length == 1 && subscribed[0] == "Custom",
            "The event stream subscribed to something other than General.Custom.");
        var subscribedTwitch = events
            .GetProperty("Twitch")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();
        Assert(
            subscribedTwitch.Length == 1 && subscribedTwitch[0] == "ChatMessage",
            "The event stream did not subscribe to Twitch.ChatMessage alongside General.Custom.");
        await SendJsonAsync(
            socket,
            new { status = "ok", id = subscribe.RootElement.GetProperty("id").GetString() },
            cancellationToken);

        return socket;
    }

    private static Task SendCustomEventAsync(
        WebSocket socket,
        object data,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            socket,
            new
            {
                timeStamp = DateTimeOffset.Now.ToString("O"),
                @event = new { source = "General", type = "Custom" },
                data
            },
            cancellationToken);

    private static Task SendTwitchChatMessageEventAsync(
        WebSocket socket,
        object data,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            socket,
            new
            {
                timeStamp = DateTimeOffset.Now.ToString("O"),
                @event = new { source = "Twitch", type = "ChatMessage" },
                data
            },
            cancellationToken);

    private static async Task TestStreamerBotRoundTripAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = RunMockStreamerBotAsync(
            listener,
            timeout.Token,
            expectedActionName: "Second mapped action");
        var config = new StreamerBotConfig
        {
            WebSocketUrl = $"ws://127.0.0.1:{port}/",
            Password = "password",
            ActionName = "SVR POC Test",
            DryRun = false
        };

        await using (var client = new StreamerBotClient(config))
        {
            var actions = await client.GetActionsAsync(timeout.Token);
            Assert(actions.Count == 1, "Client did not return the mock action.");
            Assert(actions[0].Name == "SVR POC Test", "Client returned the wrong action name.");
            Assert(
                actions[0].Id == "a0ff6f91-a51e-4b7d-948b-5e03ff4a82f0",
                "Client returned the wrong action ID.");
            await client.TriggerAsync(
                "self-test",
                "second-action-id",
                "Second mapped action",
                timeout.Token);
        }

        await server;
    }

    private static async Task TestStreamerBotReconnectAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var config = new StreamerBotConfig
        {
            WebSocketUrl = $"ws://127.0.0.1:{port}/",
            Password = "password",
            ActionName = "SVR POC Test",
            DryRun = false
        };

        await using var client = new StreamerBotClient(config);
        var trigger = client.TriggerAsync("reconnect-test", timeout.Token);
        await Task.Delay(400, timeout.Token);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = RunMockStreamerBotAsync(
            listener,
            timeout.Token,
            expectGetActions: false,
            expectedBinding: "reconnect-test");

        await trigger;
        await server;
    }

    private static async Task TestUnconfirmedDeliveryIsNotRetriedAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = RunUnconfirmedDeliveryServerAsync(listener, timeout.Token);
        var config = new StreamerBotConfig
        {
            WebSocketUrl = $"ws://127.0.0.1:{port}/",
            Password = "password",
            ActionName = "SVR POC Test",
            DryRun = false
        };

        await using var client = new StreamerBotClient(config);
        try
        {
            await client.TriggerAsync("no-retry-test", timeout.Token);
            throw new InvalidOperationException(
                "SELF-TEST FAIL: unconfirmed delivery unexpectedly succeeded.");
        }
        catch (StreamerBotDeliveryException exception)
        {
            Assert(
                exception.DeliveryMayHaveOccurred,
                "Unconfirmed delivery was not marked as potentially delivered.");
        }

        await server;
    }

    private static async Task TestStreamerBotRestartRecoveryAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var config = new StreamerBotConfig
        {
            WebSocketUrl = $"ws://127.0.0.1:{port}/",
            Password = "password",
            ActionName = "SVR POC Test",
            DryRun = false
        };

        using (var firstListener = new HttpListener())
        {
            firstListener.Prefixes.Add($"http://127.0.0.1:{port}/");
            firstListener.Start();
            var firstServer = RunMockStreamerBotAsync(
                firstListener,
                timeout.Token,
                expectGetActions: false,
                expectedBinding: "before-restart");

            await using var client = new StreamerBotClient(config);
            await client.TriggerAsync("before-restart", timeout.Token);
            await firstServer;
            firstListener.Stop();

            try
            {
                await client.TriggerAsync("during-restart", timeout.Token);
                throw new InvalidOperationException(
                    "SELF-TEST FAIL: delivery succeeded while Streamer.bot was stopped.");
            }
            catch (StreamerBotDeliveryException)
            {
                // Expected: this request is either safely unsent or unconfirmed.
            }

            using var secondListener = new HttpListener();
            secondListener.Prefixes.Add($"http://127.0.0.1:{port}/");
            secondListener.Start();
            var secondServer = RunMockStreamerBotAsync(
                secondListener,
                timeout.Token,
                expectGetActions: false,
                expectedBinding: "after-restart");
            await client.TriggerAsync("after-restart", timeout.Token);
            await secondServer;
        }
    }

    private static async Task TestSteamVrSessionRestartAsync()
    {
        var factory = new RestartingOpenVrSessionFactory();
        var engine = new BridgeEngine(factory);
        var reconnectLogged = false;
        engine.Activity += activity =>
        {
            reconnectLogged |= activity.EventName == "steamvr.reconnect";
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var config = new AppConfig
        {
            ActionManifestPath = Path.Combine(
                AppContext.BaseDirectory,
                "actions.json"),
            PollIntervalMs = 1,
            StreamerBot = new StreamerBotConfig
            {
                ActionName = "SVR POC Test",
                DryRun = true
            }
        };

        var run = engine.RunAsync(config, timeout.Token);
        await factory.SecondSessionStarted.Task.WaitAsync(timeout.Token);
        timeout.Cancel();
        await run;

        Assert(factory.ConnectionCount >= 2, "SteamVR session was not recreated.");
        Assert(reconnectLogged, "SteamVR session loss was not logged.");
    }

    /// <summary>
    /// A first-run install has no shortcuts configured yet - the SteamVR
    /// session must still open so the dashboard appears and the in-VR wizard
    /// is reachable to create one, rather than the runtime refusing to start.
    /// Regression test for the shortcut-count check that used to gate
    /// <c>BridgeEngine.RunAsync</c> being reached at all.
    /// </summary>
    private static async Task TestRuntimeStartsWithNoShortcutsAsync()
    {
        var engine = new BridgeEngine(new ReadyOpenVrSessionFactory());
        var readyWithGuidance = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StatusChanged += status =>
        {
            if (status.State == BridgeState.Ready
                && status.Detail.Contains("No shortcuts yet", StringComparison.Ordinal))
            {
                readyWithGuidance.TrySetResult();
            }
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var config = new AppConfig
        {
            ActionManifestPath = Path.Combine(AppContext.BaseDirectory, "actions.json"),
            PollIntervalMs = 1,
            StreamerBot = new StreamerBotConfig { ActionName = "", ActionId = null, DryRun = true },
            Shortcuts = []
        };

        var run = engine.RunAsync(config, timeout.Token);
        await readyWithGuidance.Task.WaitAsync(timeout.Token);
        timeout.Cancel();
        await run;
    }

    /// <summary>
    /// A missing or malformed Streamer.bot address is guidance, not a reason
    /// to keep the SteamVR session from opening - it only ever blocks
    /// delivery of an actual shortcut, which <see cref="StreamerBotClient"/>
    /// only touches when one fires. Regression test for the connection check
    /// that used to gate <c>BridgeEngine.RunAsync</c> being reached at all.
    /// </summary>
    private static async Task TestRuntimeStartsWithMalformedStreamerBotAddressAsync()
    {
        var engine = new BridgeEngine(new ReadyOpenVrSessionFactory());
        var reachedReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StatusChanged += status =>
        {
            if (status.State == BridgeState.Ready)
            {
                reachedReady.TrySetResult();
            }
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var config = new AppConfig
        {
            ActionManifestPath = Path.Combine(AppContext.BaseDirectory, "actions.json"),
            PollIntervalMs = 1,
            StreamerBot = new StreamerBotConfig
            {
                WebSocketUrl = "not-a-websocket",
                ActionName = "",
                ActionId = null,
                DryRun = true
            },
            Shortcuts = []
        };

        var run = engine.RunAsync(config, timeout.Token);
        await reachedReady.Task.WaitAsync(timeout.Token);
        timeout.Cancel();
        await run;
    }

    /// <summary>
    /// Regression test for the bug that shipped a missing WPF native DLL: the
    /// worker crashing with an unhandled exception was indistinguishable from
    /// SteamVR merely being unavailable, so the engine looped the SteamVR
    /// reconnect ladder over a fault that would recur identically on every
    /// attempt, and the activity log filled with "Waiting for SteamVR"
    /// instead of anything the user could act on. Proves
    /// <c>WorkerCrashedException</c> ends <see cref="BridgeEngine.RunAsync"/>
    /// outright - no reconnect delay, no further connection attempt - and
    /// reports it distinctly rather than as a dropped SteamVR connection.
    /// </summary>
    private static async Task TestWorkerCrashedExceptionStopsWithoutRetryingAsync()
    {
        var factory = new CrashingOpenVrSessionFactory();
        var engine = new BridgeEngine(factory);
        BridgeStatus? finalStatus = null;
        var sawSteamVrReconnectWording = false;
        engine.StatusChanged += status =>
        {
            finalStatus = status;
            sawSteamVrReconnectWording |= status.FriendlyName == "Waiting for SteamVR";
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var config = new AppConfig
        {
            ActionManifestPath = Path.Combine(AppContext.BaseDirectory, "actions.json"),
            PollIntervalMs = 1,
            StreamerBot = new StreamerBotConfig { ActionName = "", ActionId = null, DryRun = true },
            Shortcuts = []
        };

        // No cancellation needed: a fixed WorkerCrashedException must make
        // RunAsync return on its own well inside the timeout, not merely
        // survive until one is imposed - that is the entire behaviour under
        // test, so awaiting it directly (rather than racing a signal and
        // cancelling) is the assertion.
        await engine.RunAsync(config, timeout.Token).WaitAsync(TimeSpan.FromSeconds(4));

        Assert(factory.ConnectionCount == 1, "The engine reconnected after a worker crash instead of stopping.");
        Assert(
            !sawSteamVrReconnectWording,
            "A worker crash was reported as an ordinary SteamVR dropout.");
        Assert(
            finalStatus is { State: BridgeState.Error, FriendlyName: "SteamVR2Bot cannot continue" },
            "A worker crash did not leave a distinct, actionable final status.");
        Assert(
            finalStatus!.Detail.Contains("PresentationNative_cor3.dll", StringComparison.Ordinal),
            "The final status dropped the specific detail naming what failed.");
    }

    private static async Task RunMockStreamerBotAsync(
        HttpListener listener,
        CancellationToken cancellationToken,
        bool expectGetActions = true,
        string expectedBinding = "self-test",
        string expectedActionName = "SVR POC Test")
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
        using var socket = webSocketContext.WebSocket;

        await SendJsonAsync(
            socket,
            new
            {
                request = "Hello",
                info = new { instanceId = "self-test", name = "Mock Streamer.bot" },
                authentication = new { salt = "salt", challenge = "challenge" }
            },
            cancellationToken);

        using var authentication = await ReceiveJsonAsync(socket, cancellationToken);
        Assert(
            authentication.RootElement.GetProperty("request").GetString() == "Authenticate",
            "Client did not send Authenticate.");
        Assert(
            authentication.RootElement.GetProperty("authentication").GetString()
            == "zTM5ki6L2vVvBQiTG9ckH1Lh64AbnCf6XZ226UmnkIA=",
            "Client sent an incorrect authentication value.");
        var authenticationId = authentication.RootElement.GetProperty("id").GetString();
        await SendJsonAsync(socket, new { status = "ok", id = authenticationId }, cancellationToken);

        if (expectGetActions)
        {
            using var getActions = await ReceiveJsonAsync(socket, cancellationToken);
            Assert(
                getActions.RootElement.GetProperty("request").GetString() == "GetActions",
                "Client did not send GetActions.");
            var getActionsId = getActions.RootElement.GetProperty("id").GetString();
            await SendJsonAsync(
                socket,
                new
                {
                    count = 1,
                    actions = new[]
                    {
                        new
                        {
                            enabled = true,
                            group = "VR",
                            id = "a0ff6f91-a51e-4b7d-948b-5e03ff4a82f0",
                            name = "SVR POC Test"
                        }
                    },
                    status = "ok",
                    id = getActionsId
                },
                cancellationToken);
        }

        using var action = await ReceiveJsonAsync(socket, cancellationToken);
        Assert(
            action.RootElement.GetProperty("request").GetString() == "DoAction",
            "Client did not send DoAction.");
        Assert(
            action.RootElement.GetProperty("action").GetProperty("name").GetString()
            == expectedActionName,
            "Client sent the wrong action name.");
        Assert(
            action.RootElement.GetProperty("args").GetProperty("binding").GetString()
            == expectedBinding,
            "Client omitted the binding argument.");

        var actionId = action.RootElement.GetProperty("id").GetString();
        await SendJsonAsync(socket, new { status = "ok", id = actionId }, cancellationToken);
    }

    private static async Task RunUnconfirmedDeliveryServerAsync(
        HttpListener listener,
        CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
        using var socket = webSocketContext.WebSocket;

        await SendJsonAsync(
            socket,
            new
            {
                request = "Hello",
                info = new { instanceId = "self-test", name = "Mock Streamer.bot" }
            },
            cancellationToken);

        using var action = await ReceiveJsonAsync(socket, cancellationToken);
        Assert(
            action.RootElement.GetProperty("request").GetString() == "DoAction",
            "Client did not send the unconfirmed DoAction.");
        Assert(
            action.RootElement.GetProperty("args").GetProperty("binding").GetString()
            == "no-retry-test",
            "Client sent the wrong unconfirmed binding.");
        socket.Abort();
    }

    private static async Task SendJsonAsync(
        WebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            Assert(result.MessageType != WebSocketMessageType.Close, "Client closed unexpectedly.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"SELF-TEST FAIL: {message}");
        }
    }

    /// <summary>
    /// A SteamVR session that connects once and reports controllers as ready,
    /// never failing - unlike <see cref="RestartingOpenVrSessionFactory"/>,
    /// which exists to exercise reconnection instead.
    /// </summary>
    private sealed class ReadyOpenVrSessionFactory : IOpenVrSessionFactory
    {
        public Task<IOpenVrSession> ConnectAsync(
            AppConfig config,
            string actionManifest,
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult<IOpenVrSession>(new ReadyOpenVrSession());
    }

    private sealed class ReadyOpenVrSession : IOpenVrSession
    {
        public InputSnapshot Poll() => default;

        public ControllerSetup GetControllerSetup() => new(
            [],
            null,
            null,
            BindingAvailability.Ready,
            "Test controllers ready.",
            false);

        public void OpenBindingUi()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A SteamVR session that connects successfully but then fails its first
    /// poll with <see cref="WorkerCrashedException"/>, standing in for the
    /// real worker crashing on a missing native DLL - never a second
    /// connection, since <c>RunAsync</c> must not retry this.
    /// </summary>
    private sealed class CrashingOpenVrSessionFactory : IOpenVrSessionFactory
    {
        private int _connectionCount;

        public int ConnectionCount => _connectionCount;

        public Task<IOpenVrSession> ConnectAsync(
            AppConfig config,
            string actionManifest,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectionCount);
            return Task.FromResult<IOpenVrSession>(new CrashingOpenVrSession());
        }
    }

    private sealed class CrashingOpenVrSession : IOpenVrSession
    {
        public InputSnapshot Poll() =>
            throw new WorkerCrashedException(
                "SteamVR2Bot could not load PresentationNative_cor3.dll and cannot "
                + "continue. This normally means SteamVR2Bot.exe is running without "
                + "the rest of its published folder - run it from inside the "
                + "complete extracted publish folder, not moved out on its own.");

        public ControllerSetup GetControllerSetup() => new(
            [],
            null,
            null,
            BindingAvailability.Ready,
            "Test controllers ready.",
            false);

        public void OpenBindingUi()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RestartingOpenVrSessionFactory : IOpenVrSessionFactory
    {
        private int _connectionCount;

        public int ConnectionCount => _connectionCount;

        public TaskCompletionSource SecondSessionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IOpenVrSession> ConnectAsync(
            AppConfig config,
            string actionManifest,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            var connection = Interlocked.Increment(ref _connectionCount);
            if (connection >= 2)
            {
                SecondSessionStarted.TrySetResult();
            }

            return Task.FromResult<IOpenVrSession>(
                new RestartingOpenVrSession(failAfterFirstPoll: connection == 1));
        }
    }

    private sealed class RestartingOpenVrSession(bool failAfterFirstPoll)
        : IOpenVrSession
    {
        private int _pollCount;

        public InputSnapshot Poll()
        {
            if (failAfterFirstPoll && Interlocked.Increment(ref _pollCount) > 1)
            {
                throw new InvalidOperationException(
                    "Simulated SteamVR worker termination.");
            }

            return default;
        }

        public ControllerSetup GetControllerSetup() => new(
            [],
            null,
            null,
            BindingAvailability.Unknown,
            "Waiting for test controllers.",
            false);

        public void OpenBindingUi()
        {
        }

        public void Dispose()
        {
        }
    }
}
