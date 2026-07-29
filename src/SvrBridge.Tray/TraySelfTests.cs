namespace SvrBridge.Tray;

internal static class TraySelfTests
{
    public static void Run()
    {
        TestVrActionBrowser();
        TestVrScrollLimiter();
        TestPackagedViveBinding();
        TestDashboardBottomBarLayout();
        TestRenamedDataDirectoryMigration();

        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"svr-bridge-settings-test-{Guid.NewGuid():N}");
        var missingLegacySettings = Path.Combine(testDirectory, "missing-legacy.json");

        try
        {
            var store = new UserSettingsStore(testDirectory, missingLegacySettings);
            var expected = new UserSettings
            {
                StreamerBotAddress = "ws://192.168.1.50:8080/",
                ActionName = "Friendly VR action",
                ActionId = "a0ff6f91-a51e-4b7d-948b-5e03ff4a82f0",
                Password = "self-test-secret",
                GestureMode = SvrBridge.Core.ChordMode.Simultaneous,
                Shortcuts =
                [
                    new SvrBridge.Core.ShortcutConfig
                    {
                        Id = "settings-round-trip",
                        Name = "Friendly VR shortcut",
                        SafetyInput =
                            SvrBridge.Core.ControllerInputBinding.SteamVrSafety,
                        ActionInput =
                            SvrBridge.Core.ControllerInputBinding.SteamVrAction,
                        Gesture = new SvrBridge.Core.ChordConfig
                        {
                            Mode = SvrBridge.Core.ChordMode.Simultaneous,
                            WindowMs = 300,
                            CooldownMs = 250
                        },
                        ActionName = "Friendly VR action",
                        ActionId = "a0ff6f91-a51e-4b7d-948b-5e03ff4a82f0"
                    }
                ],
                StartBridgeWhenAppOpens = true,
                EventStreamEnabled = true
            };

            store.Save(expected);
            var settingsJson = File.ReadAllText(
                Path.Combine(testDirectory, "settings.json"));
            Assert(
                !settingsJson.Contains(expected.Password, StringComparison.Ordinal),
                "The password was saved as readable text.");

            var actual = store.Load();
            var actualShortcut = actual.GetShortcuts().Single();
            var expectedShortcut = expected.GetShortcuts().Single();
            Assert(
                actual.StreamerBotAddress == expected.StreamerBotAddress
                && actual.Password == expected.Password
                && actual.StartBridgeWhenAppOpens
                && actual.EventStreamEnabled
                && actualShortcut.Id == expectedShortcut.Id
                && actualShortcut.Name == expectedShortcut.Name
                && actualShortcut.SafetyInput == expectedShortcut.SafetyInput
                && actualShortcut.ActionInput == expectedShortcut.ActionInput
                && actualShortcut.ActionId == expectedShortcut.ActionId
                && actualShortcut.ActionName == expectedShortcut.ActionName
                && actualShortcut.Gesture.Mode == expectedShortcut.Gesture.Mode
                && actualShortcut.Gesture.HoldMs == expectedShortcut.Gesture.HoldMs,
                "Protected multi-shortcut settings did not round-trip.");

            UserSettingsStore.ValidateForSave(
                expected with
                {
                    Shortcuts =
                    [
                        expectedShortcut with
                        {
                            SafetyInput =
                                SvrBridge.Core.ControllerInputBinding.Physical(
                                    SvrBridge.Core.ControllerHand.Left,
                                    1,
                                    "Left Menu Button"),
                            ActionInput =
                                SvrBridge.Core.ControllerInputBinding.Physical(
                                    SvrBridge.Core.ControllerHand.Left,
                                    1,
                                    "Left Menu Button"),
                            Gesture = new SvrBridge.Core.ChordConfig
                            {
                                Mode = SvrBridge.Core.ChordMode.LongPress,
                                HoldMs = 2000,
                                CooldownMs = 250,
                                WindowMs = 2000
                            }
                        }
                    ]
                });

            var logDirectory = Path.Combine(testDirectory, "logs");
            var log = new StructuredActivityLog(logDirectory);
            log.Write(
                "self_test",
                "A safe message with password=self-test-secret",
                SvrBridge.Core.BridgeLogLevel.Warning);
            var logText = File.ReadAllText(
                Directory.GetFiles(logDirectory, "*.jsonl").Single());
            Assert(
                !logText.Contains("self-test-secret", StringComparison.Ordinal),
                "The structured log exposed a secret value.");
            Assert(
                logText.Contains("\"event\":\"self_test\"", StringComparison.Ordinal),
                "The structured log omitted its event name.");

            // A settings file written before the event feed existed has no such
            // field, and must still load — with the feed off, which is what
            // its absence means.
            File.WriteAllText(
                Path.Combine(testDirectory, "settings.json"),
                """
                {
                  "StreamerBotAddress": "ws://127.0.0.1:8080/1",
                  "ActionName": "Legacy action",
                  "ActionId": "legacy-id",
                  "ProtectedPassword": "",
                  "GestureMode": 1,
                  "StartBridgeWhenAppOpens": true
                }
                """);
            Assert(
                !store.Load().EventStreamEnabled,
                "An upgraded settings file turned the event feed on by itself.");

            AssertThrows(
                () => UserSettingsStore.Validate(
                    expected with { StreamerBotAddress = "http://not-a-websocket" }),
                "A non-WebSocket address was accepted.");
            AssertThrows(
                () => UserSettingsStore.Validate(
                    expected with
                    {
                        Shortcuts =
                        [
                            expected.GetShortcuts()[0] with
                            {
                                ActionName = "",
                                ActionId = ""
                            }
                        ]
                    }),
                "Empty action settings were accepted.");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static void TestVrActionBrowser()
    {
        var actions = Enumerable.Range(1, 8)
            .Select(index => new SvrBridge.Core.StreamerBotAction(
                $"scene-{index}",
                $"Scene {index:00}",
                "Scenes"))
            .Concat(
            [
                new SvrBridge.Core.StreamerBotAction(
                    "sound-1",
                    "Air horn",
                    "Sounds"),
                new SvrBridge.Core.StreamerBotAction(
                    "ungrouped-1",
                    "Emergency stop",
                    "None")
            ])
            .ToArray();
        var browser = new VrActionBrowser(actions);

        Assert(browser.IsShowingGroups, "The VR action browser did not start at groups.");
        Assert(browser.TotalItemCount == 3, "The VR action browser grouped actions incorrectly.");
        Assert(
            browser.VisibleRows.Select(row => row.Label)
                .SequenceEqual(["Scenes", "Sounds", "Ungrouped"]),
            "VR action groups were not sorted with Ungrouped last.");

        Assert(
            browser.OpenRow(0) is null && !browser.IsShowingGroups,
            "Opening a VR action group did not show its actions.");
        Assert(
            browser.TotalItemCount == 8
            && browser.VisibleRows[0].Label == "Scene 01",
            "The selected VR action group showed the wrong actions.");
        Assert(
            browser.ScrollPage(1)
            && browser.FirstVisibleItemNumber == 3
            && browser.LastVisibleItemNumber == 8,
            "VR action paging did not clamp to the end of the list.");
        Assert(
            browser.OpenRow(5)?.Id == "scene-8",
            "The VR action browser selected the wrong scrolled action.");
        Assert(
            browser.BackToGroups()
            && browser.FirstVisibleItemNumber == 1,
            "Returning to VR action groups did not reset scrolling.");

        var previewPath = VrDashboardRenderer.RenderActionPicker(browser);
        using var preview = new Bitmap(previewPath);
        Assert(
            preview.Width == 1400 && preview.Height == 900,
            "The grouped VR action picker rendered at the wrong size.");

        var gestureTypePath = VrDashboardRenderer.RenderGestureTypePicker(false);
        Assert(
            !gestureTypePath.Equals(
                previewPath,
                StringComparison.OrdinalIgnoreCase),
            "Dashboard pages reused an image path while SteamVR could still be loading it.");
        using var quickInputPreview = new Bitmap(gestureTypePath);
        Assert(
            quickInputPreview.Width == 1400 && quickInputPreview.Height == 900,
            "The VR gesture type picker rendered at the wrong size.");

        var doublePressInput =
            SvrBridge.Core.ControllerInputBinding.Physical(
                SvrBridge.Core.ControllerHand.Left,
                1,
                "Left Menu Button");
        var doublePressShortcut = new SvrBridge.Core.ShortcutConfig
        {
            Id = "double-press-preview",
            Name = "Toggle microphone",
            SafetyInput = doublePressInput,
            ActionInput = doublePressInput,
            Gesture = new SvrBridge.Core.ChordConfig
            {
                Mode = SvrBridge.Core.ChordMode.DoublePress,
                WindowMs = 500,
                CooldownMs = 250
            },
            ActionName = "Toggle microphone",
            ActionId = "toggle-microphone"
        };
        doublePressShortcut.Validate();
        Assert(
            doublePressShortcut.FriendlyGesture == "Double press Left Menu Button",
            "The double-press gesture did not have a friendly description.");
        var listPath = VrDashboardRenderer.Render([doublePressShortcut]);
        using var listPreview = new Bitmap(listPath);
        Assert(
            listPreview.Width == 1400 && listPreview.Height == 900,
            "The editable VR shortcut list rendered at the wrong size.");

        var gesturePath = VrDashboardRenderer.RenderTolerancePicker(
            SvrBridge.Core.ChordMode.DoublePress,
            500);
        using var gesturePreview = new Bitmap(gesturePath);
        Assert(
            gesturePreview.Width == 1400 && gesturePreview.Height == 900,
            "The VR tolerance slider rendered at the wrong size.");

        var handPath = VrDashboardRenderer.RenderInputRecorder(
            SvrBridge.Core.ChordMode.Simultaneous,
            SvrBridge.Core.ControllerSetup.Unknown,
            doublePressInput);
        using var handPreview = new Bitmap(handPath);
        Assert(
            handPreview.Width == 1400 && handPreview.Height == 900,
            "The VR input recorder rendered at the wrong size.");

        var buttonPath = VrDashboardRenderer.RenderShortcutReview(
            SvrBridge.Core.ChordMode.DoublePress,
            doublePressInput,
            doublePressInput,
            new SvrBridge.Core.StreamerBotAction(
                "toggle-microphone",
                "Toggle microphone",
                "VR"),
            500,
            2000,
            false);
        using var buttonPreview = new Bitmap(buttonPath);
        Assert(
            buttonPreview.Width == 1400 && buttonPreview.Height == 900,
            "The VR shortcut review rendered at the wrong size.");
    }

    private static void TestRenamedDataDirectoryMigration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"svr-bridge-rename-test-{Guid.NewGuid():N}");
        var legacy = Path.Combine(root, "SVR Bridge");
        var current = Path.Combine(root, AppPaths.ProductName);

        try
        {
            // A user upgrading from the old name: settings, protected password,
            // and logs all sitting in the pre-rename folder.
            Directory.CreateDirectory(Path.Combine(legacy, "Logs"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"kept\":true}");
            File.WriteAllText(Path.Combine(legacy, "Logs", "old.jsonl"), "entry");

            Assert(
                AppPaths.Resolve(root) == current,
                "The rename did not move the user's data to the new folder.");
            Assert(
                File.ReadAllText(Path.Combine(current, "settings.json"))
                    == "{\"kept\":true}",
                "The rename lost the saved settings.");
            Assert(
                File.Exists(Path.Combine(current, "Logs", "old.jsonl")),
                "The rename lost the existing logs.");
            Assert(
                !Directory.Exists(legacy),
                "The rename left the old folder behind to be found again later.");

            // Resolving again must be a no-op rather than a second move.
            Assert(
                AppPaths.Resolve(root) == current,
                "Resolving the data folder a second time did not stay put.");

            // A fresh install has no old folder and must not invent one.
            var emptyRoot = Path.Combine(root, "fresh");
            Directory.CreateDirectory(emptyRoot);
            Assert(
                AppPaths.Resolve(emptyRoot)
                    == Path.Combine(emptyRoot, AppPaths.ProductName),
                "A fresh install did not use the new folder name.");

            // If both exist the new one wins; the old must never be preferred.
            var bothRoot = Path.Combine(root, "both");
            Directory.CreateDirectory(Path.Combine(bothRoot, "SVR Bridge"));
            Directory.CreateDirectory(Path.Combine(bothRoot, AppPaths.ProductName));
            Assert(
                AppPaths.Resolve(bothRoot)
                    == Path.Combine(bothRoot, AppPaths.ProductName),
                "A leftover old folder took precedence over the current one.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void TestDashboardBottomBarLayout()
    {
        var rows = new[]
        {
            ("tolerance", VrDashboardLayout.Tolerance),
            ("recorder", VrDashboardLayout.RecordInput),
            ("action picker", VrDashboardLayout.ActionPicker),
            ("review", VrDashboardLayout.Review)
        };

        foreach (var (name, row) in rows)
        {
            Assert(
                row[0].Left == 60 && row[^1].Right == 1340,
                $"The {name} bottom bar did not span the page.");

            for (var index = 0; index < row.Length; index++)
            {
                var button = row[index];
                Assert(
                    button.Top == VrDashboardLayout.BarY
                    && button.Height == VrDashboardLayout.BarHeight,
                    $"A {name} button left the bottom bar.");
                Assert(
                    button.Width >= 200,
                    $"A {name} button is too narrow to hit with a laser.");
                Assert(
                    index == 0 || button.Left > row[index - 1].Right,
                    $"The {name} buttons overlap.");

                // Centre, both edges, and the gap that follows must all resolve
                // to this button, or the drawn button and the click disagree.
                Assert(
                    VrDashboardLayout.IndexAt(row, button.Left + (button.Width / 2f)) == index
                    && VrDashboardLayout.IndexAt(row, button.Left) == index
                    && VrDashboardLayout.IndexAt(row, button.Right - 1) == index,
                    $"A click on a {name} button resolved to a different button.");
            }

            Assert(
                VrDashboardLayout.IndexAt(row, 0) == 0
                && VrDashboardLayout.IndexAt(row, 1400) == row.Length - 1,
                $"A {name} click outside the buttons did not clamp into the bar.");
        }

        // Cancel is first and Save is last so an accidental laser slip on the
        // review page cannot discard the shortcut the user just built.
        Assert(
            VrDashboardLayout.IndexAt(VrDashboardLayout.Review, 100) == 0
            && VrDashboardLayout.Review[^1].Left
                - VrDashboardLayout.Review[0].Right >= 700,
            "Cancel and Save are not at opposite ends of the review bar.");
    }

    private static void TestVrScrollLimiter()
    {
        var limiter = new VrDashboardScrollLimiter(500);
        Assert(limiter.TryAccept(1000), "The first VR scroll was suppressed.");
        Assert(!limiter.TryAccept(1499), "A VR scroll burst was not throttled.");
        Assert(limiter.TryAccept(1500), "A later VR scroll was incorrectly suppressed.");

        var burstLimiter = new VrDashboardScrollLimiter(500);
        var accepted = Enumerable.Range(0, 31)
            .Count(index => burstLimiter.TryAccept(index * 60L));
        Assert(
            accepted == 4,
            "A VR scroll burst would still cause too many dashboard redraws.");
    }

    private static void TestPackagedViveBinding()
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "actions.json")));
        using var binding = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "bindings_vive_controller.json")));

        var actionNames = manifest.RootElement
            .GetProperty("actions")
            .EnumerateArray()
            .Select(action => action.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        var outputs = binding.RootElement
            .GetProperty("bindings")
            .GetProperty("/actions/svrbridge")
            .GetProperty("sources")
            .EnumerateArray()
            .Select(source =>
                source.GetProperty("inputs")
                    .GetProperty("click")
                    .GetProperty("output")
                    .GetString())
            .ToArray();

        string[] expected =
        [
            "/actions/svrbridge/in/left_menu",
            "/actions/svrbridge/in/right_menu",
            "/actions/svrbridge/in/left_grip",
            "/actions/svrbridge/in/right_grip",
            "/actions/svrbridge/in/left_trigger",
            "/actions/svrbridge/in/right_trigger",
            "/actions/svrbridge/in/left_trackpad",
            "/actions/svrbridge/in/right_trackpad"
        ];
        Assert(
            expected.All(action => actionNames.Contains(action))
            && expected.All(action => outputs.Contains(action, StringComparer.Ordinal)),
            "The packaged Vive binding does not expose every selectable Vive input.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"TRAY SELF-TEST FAIL: {message}");
        }
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException($"TRAY SELF-TEST FAIL: {message}");
    }
}
