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
        TestOverlayTransformComposition();
        TestOverlayHandleRoundTrip();
        TestNotificationPlayerQueueing();
        TestNotificationDurationClampingEndToEnd();
        TestNotificationAlphaCurve();
        TestWpfRenderThreadStartsAndShutsDownCleanly();
        TestNotificationPixelFormatConversion();
        TestChatRingBufferEviction();
        TestChatRepaintThrottleCoalescesBurst();
        TestChatGazeHysteresisNoOscillationAtBoundary();
        TestChatRenderWrapsLongUnbrokenString();
        TestChatRenderHandlesEmptyUsernameAndColour();
        TestChatRenderStylesEmoteTokensDistinctly();
        TestChatImageCacheFetchesDecodesAndCaches();
        TestChatImageCacheRetriesAfterAFailedFetch();
        TestChatRenderEmbedsCachedEmoteImage();
        TestChatRenderEmbedsCachedBadgeImage();
        TestChatRenderEmbedsMultipleBadges();
        TestChatRingBufferThreadSafeConcurrentAccess();

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

    /// <summary>
    /// Proves the <c>IVROverlay</c> vtable indices address the functions they
    /// claim to, by round-tripping one throwaway overlay:
    /// <c>CreateOverlay</c> returns a handle, <c>FindOverlay</c> maps the key
    /// back to the same handle, <c>DestroyOverlay</c> removes it, and
    /// <c>FindOverlay</c> then reports it gone.
    /// <para>
    /// <b>This is the whole point of the test.</b> A wrong vtable index does not
    /// throw - it calls a different function with mismatched arguments, which is
    /// an access violation inside SteamVR at best and silent memory corruption
    /// at worst, discovered mid-stream in front of an audience. Four calls that
    /// agree with each other cannot all be pointing at the wrong entries, so
    /// this converts that failure mode into a loud one at startup.
    /// </para>
    /// <para>
    /// Skipped when SteamVR is not running, because it is real hardware
    /// interaction rather than a pure unit test and the suite has to pass on a
    /// desktop with no headset. A skip is not a pass - the manual live test in
    /// <c>LIVE_TEST_RESULTS.md</c> is what covers the case where it never ran.
    /// </para>
    /// </summary>
    private static void TestOverlayHandleRoundTrip()
    {
        var actionManifest = Path.Combine(AppContext.BaseDirectory, "actions.json");
        if (!File.Exists(actionManifest))
        {
            return;
        }

        SvrBridge.Core.OpenVrInput openVr;
        try
        {
            openVr = new SvrBridge.Core.OpenVrInput(
                configuredDllPath: null,
                actionManifest,
                _ => { });
        }
        catch (Exception)
        {
            // No SteamVR, no openvr_api.dll, or no runtime to talk to. The
            // indices cannot be checked here; the live test covers it.
            return;
        }

        using (openVr)
        {
            if (!openVr.SupportsOverlaySurfaces)
            {
                return;
            }

            // Unique per run so a previous crashed run cannot make this pass or
            // fail for the wrong reason.
            var key = $"ie.lonelyviper.svrbridge.selftest.{Guid.NewGuid():N}";

            Assert(
                !openVr.TryFindOverlayHandle(key, out _),
                "FindOverlay reported an overlay that had never been created.");

            ulong created;
            using (var surface = openVr.CreateOverlaySurface(key, "SteamVR2Bot self-test"))
            {
                created = surface.Handle;
                Assert(
                    created != 0,
                    "CreateOverlay returned a zero overlay handle.");
                Assert(
                    openVr.TryFindOverlayHandle(key, out var found) && found == created,
                    "FindOverlay did not round-trip the key back to the created handle. "
                    + "The IVROverlay vtable indices are wrong for this SteamVR version.");
            }

            Assert(
                !openVr.TryFindOverlayHandle(key, out _),
                "DestroyOverlay left the overlay registered with SteamVR.");
        }
    }

    /// <summary>
    /// Composition has to be right before an overlay can be placed correctly,
    /// and a wrong transform looks like a badly chosen offset rather than a bug.
    /// This runs everywhere, with or without SteamVR.
    /// </summary>
    private static void TestOverlayTransformComposition()
    {
        var identity = SvrBridge.Core.VrOverlayTransform.Identity;
        var translation = SvrBridge.Core.VrOverlayTransform.Translation(1f, 2f, 3f);

        Assert(
            translation * identity == translation && identity * translation == translation,
            "Composing an overlay transform with the identity changed it.");

        // A quarter turn about X maps +Y onto +Z, so a point one metre up ends
        // up one metre back. Translation lives in the fourth column, which is
        // the half most easily got backwards.
        var quarterTurn = SvrBridge.Core.VrOverlayTransform.RotationX(MathF.PI / 2f);
        var rotated = quarterTurn * SvrBridge.Core.VrOverlayTransform.Translation(0f, 1f, 0f);
        Assert(
            MathF.Abs(rotated.M13) < 1e-5f && MathF.Abs(rotated.M23 - 1f) < 1e-5f,
            "Rotating a translated overlay transform did not move the translation with it.");
    }

    /// <summary>
    /// Covers both queue behaviours in one pass: a burst that overflows the
    /// bound drops the oldest items rather than the newest, and whatever
    /// survives plays back in the order it arrived.
    /// </summary>
    private static void TestNotificationPlayerQueueing()
    {
        var player = new SvrBridge.Core.NotificationPlayer();

        // Enqueue capacity + 2 before ever ticking, so every one of them is
        // still queued behind whatever plays first - the shape of a burst
        // "arriving together" per §B3, and the only way to exercise the
        // bound rather than just the FIFO order.
        var titles = Enumerable.Range(0, SvrBridge.Core.NotificationPlayer.QueueCapacity + 2)
            .Select(index => $"n{index}")
            .ToArray();
        foreach (var title in titles)
        {
            player.Enqueue(NotificationFor(title, 500));
        }

        Assert(
            player.QueuedCount == SvrBridge.Core.NotificationPlayer.QueueCapacity,
            "The notification queue did not bound itself to its capacity.");

        var played = new List<string>();
        var now = 0L;
        while (played.Count < SvrBridge.Core.NotificationPlayer.QueueCapacity)
        {
            var frame = player.Tick(now);
            if (frame.IsNewItem && frame.Current is { } current)
            {
                played.Add(current.Title);
            }

            now += 100;
        }

        Assert(
            played.SequenceEqual(titles.Skip(2)),
            "The notification queue did not drop the two oldest items and play the rest in order.");
    }

    /// <summary>
    /// Proves the 500-60000 ms clamp in <c>StreamerBotEventPayload.TryParse</c>
    /// is the duration actually played back, by going through JSON parsing
    /// rather than constructing the payload directly - which is the "end to
    /// end" the verification plan asks for.
    /// </summary>
    private static void TestNotificationDurationClampingEndToEnd()
    {
        Assert(
            SvrBridge.Core.StreamerBotEventPayload.TryParse(
                """{"target":"notification","title":"Too short","text":"x","duration":1}""",
                out var tooShort,
                out _)
            && tooShort.DurationMs == 500,
            "A too-short notification duration was not clamped to the 500 ms minimum.");

        Assert(
            SvrBridge.Core.StreamerBotEventPayload.TryParse(
                """{"target":"notification","title":"Too long","text":"x","duration":999999}""",
                out var tooLong,
                out _)
            && tooLong.DurationMs == 60_000,
            "A too-long notification duration was not clamped to the 60 s maximum.");

        var player = new SvrBridge.Core.NotificationPlayer();
        player.Enqueue(tooShort!);
        player.Tick(0);
        Assert(
            player.Tick(499).Phase != SvrBridge.Core.NotificationPhase.Idle,
            "A notification clamped to 500 ms ended before 500 ms had elapsed.");
        Assert(
            player.Tick(500).Phase == SvrBridge.Core.NotificationPhase.Idle,
            "A notification clamped to 500 ms did not end at 500 ms.");
    }

    /// <summary>
    /// The fade curve must reach exactly 0 the instant a notification starts
    /// and again as it finishes, and exactly 1 while it holds - anything else
    /// is either a flash of full opacity with no fade, or a fade that never
    /// finishes closing.
    /// </summary>
    private static void TestNotificationAlphaCurve()
    {
        var player = new SvrBridge.Core.NotificationPlayer();
        player.Enqueue(NotificationFor("alpha", 5000));

        var start = player.Tick(0);
        Assert(
            start.Phase == SvrBridge.Core.NotificationPhase.FadingIn && start.Alpha == 0f,
            "A notification did not start at alpha 0.");

        var middle = player.Tick(2500);
        Assert(
            middle.Phase == SvrBridge.Core.NotificationPhase.Holding && middle.Alpha == 1f,
            "A notification was not fully opaque during its hold.");

        var nearEnd = player.Tick(4999);
        Assert(
            nearEnd.Phase == SvrBridge.Core.NotificationPhase.FadingOut && nearEnd.Alpha < 0.02f,
            "A notification had not faded back down towards 0 by the end of its duration.");

        Assert(
            player.Tick(5000).Phase == SvrBridge.Core.NotificationPhase.Idle,
            "A notification did not end exactly at its duration.");
    }

    private static SvrBridge.Core.StreamerBotEventPayload NotificationFor(string title, int durationMs) =>
        new()
        {
            Target = SvrBridge.Core.StreamerBotEventTarget.Notification,
            Title = title,
            Text = "self-test body",
            DurationMs = durationMs
        };

    /// <summary>
    /// Proves the render thread actually runs dispatched work and that
    /// <see cref="WpfRenderThread.Dispose"/> joins the OS thread rather than
    /// merely asking it to stop - a hung dispatcher shutdown would otherwise
    /// leave a thread behind silently.
    /// </summary>
    private static void TestWpfRenderThreadStartsAndShutsDownCleanly()
    {
        var thread = new WpfRenderThread("self-test render thread");
        try
        {
            Assert(thread.IsRunning, "The WPF render thread did not start.");
            Assert(
                thread.Invoke(() => 21 + 21) == 42,
                "The WPF render thread did not run dispatched work.");
        }
        finally
        {
            thread.Dispose();
        }

        Assert(!thread.IsRunning, "The WPF render thread did not shut down cleanly.");
    }

    /// <summary>
    /// Renders a known semi-transparent colour through the exact production
    /// path - <see cref="WpfOverlayPixelPipeline.RenderToRgba"/> - and checks
    /// the resulting bytes. This is the one bug class that looks plausible on
    /// screen and is invisible in a log: a colour left premultiplied comes out
    /// darkened towards black by roughly its own alpha, which reads as "a
    /// slightly dim swatch" rather than "the conversion is wrong".
    /// </summary>
    private static void TestNotificationPixelFormatConversion()
    {
        using var thread = new WpfRenderThread("self-test pixel pipeline");
        const int size = 32;
        var pixels = thread.Invoke(() =>
        {
            var swatch = new System.Windows.Controls.Border
            {
                Width = size,
                Height = size,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(128, 200, 50, 10))
            };
            return WpfOverlayPixelPipeline.RenderToRgba(swatch, size, size);
        });

        Assert(
            pixels.Length == size * size * 4,
            "The rendered texture was the wrong size for its declared dimensions.");

        // Sampled well inside the swatch, away from any edge antialiasing.
        var index = (((size / 2) * size) + (size / 2)) * 4;
        Assert(
            pixels[index + 3] == 128,
            "The alpha channel changed during un-premultiplication, which must leave it alone.");
        Assert(
            Math.Abs(pixels[index] - 200) <= 3
            && Math.Abs(pixels[index + 1] - 50) <= 3
            && Math.Abs(pixels[index + 2] - 10) <= 3,
            "The WPF render -> un-premultiply -> channel-swap path did not reproduce the source "
            + $"colour. Got R={pixels[index]} G={pixels[index + 1]} B={pixels[index + 2]} "
            + $"A={pixels[index + 3]}.");
    }

    /// <summary>
    /// A burst arriving out of order would be as wrong as losing messages, so
    /// this checks eviction and order together: the oldest messages must be
    /// the ones dropped, and the survivors must stay in arrival order.
    /// </summary>
    private static void TestChatRingBufferEviction()
    {
        var buffer = new SvrBridge.Core.ChatRingBuffer(capacity: 3);
        buffer.Append(ChatMessageFor("a"));
        buffer.Append(ChatMessageFor("b"));
        buffer.Append(ChatMessageFor("c"));
        buffer.Append(ChatMessageFor("d"));
        buffer.Append(ChatMessageFor("e"));

        var snapshot = buffer.Snapshot();
        Assert(
            snapshot.Select(message => message.Text).SequenceEqual(["c", "d", "e"]),
            "The chat ring buffer did not evict the oldest messages and keep the "
            + "rest in arrival order.");
        Assert(
            buffer.Version == 5,
            "The chat ring buffer version did not count every append, including evicted ones.");
    }

    /// <summary>
    /// Simulates the exact flow <c>ChatOverlay.Tick</c> uses: check the
    /// throttle once per append, as a caller polling once per 10 ms tick
    /// would. A burst of ten appends inside the throttle window must produce
    /// exactly one repaint, per §B2 of the chat plan.
    /// </summary>
    private static void TestChatRepaintThrottleCoalescesBurst()
    {
        var buffer = new SvrBridge.Core.ChatRingBuffer();
        var throttle = new SvrBridge.Core.ChatRepaintThrottle(minimumIntervalMs: 100);

        var repaints = 0;
        for (var index = 0; index < 10; index++)
        {
            buffer.Append(ChatMessageFor($"m{index}"));
            if (throttle.ShouldRepaint(buffer.Version, nowMs: 0))
            {
                repaints++;
                throttle.MarkPainted(buffer.Version, nowMs: 0);
            }
        }

        Assert(
            repaints == 1,
            $"A burst of 10 messages produced {repaints} repaints instead of coalescing into one.");

        buffer.Append(ChatMessageFor("late"));
        Assert(
            throttle.ShouldRepaint(buffer.Version, nowMs: 150),
            "The chat repaint throttle did not allow a repaint once its interval had elapsed.");
    }

    /// <summary>
    /// Proves a sample sitting exactly on either threshold resolves to one
    /// definite state and holds it under repeated identical samples, rather
    /// than toggling - the failure mode a single-threshold design has right
    /// at the boundary.
    /// </summary>
    private static void TestChatGazeHysteresisNoOscillationAtBoundary()
    {
        const float enterAngleDegrees = 20f;
        const float exitAngleDegrees = 35f;
        var hysteresis = new SvrBridge.Core.ChatGazeHysteresis(enterAngleDegrees, exitAngleDegrees);
        var enterCosine = MathF.Cos(enterAngleDegrees * MathF.PI / 180f);
        var exitCosine = MathF.Cos(exitAngleDegrees * MathF.PI / 180f);

        Assert(!hysteresis.IsGazing, "Gaze hysteresis started already gazing.");
        Assert(
            hysteresis.Update(enterCosine),
            "Gaze hysteresis did not enter exactly at its own enter boundary.");

        for (var index = 0; index < 5; index++)
        {
            Assert(
                hysteresis.Update(exitCosine),
                "Gaze hysteresis exited exactly at its own exit boundary instead of holding "
                + "steady, which would read as flicker in the headset.");
        }

        Assert(
            !hysteresis.Update(exitCosine - 0.01f),
            "Gaze hysteresis never exited once the gaze moved clearly past the exit boundary.");
    }

    /// <summary>
    /// A long unbroken run - a URL with no spaces - must be force-broken onto
    /// multiple lines rather than silently overflowing the panel width, per
    /// §B3. Compares its wrapped height against a one-line baseline built the
    /// same way, so the assertion holds regardless of the exact font metrics.
    /// </summary>
    private static void TestChatRenderWrapsLongUnbrokenString()
    {
        using var thread = new WpfRenderThread("self-test chat wrap");
        var longToken = new string('x', 400);

        var (oneLineHeight, wrappedHeight) = thread.Invoke(() =>
        {
            var oneLine = WpfChatRenderer.BuildTextBlock([ChatMessageFor("short")]);
            oneLine.Measure(new System.Windows.Size(
                WpfChatRenderer.PanelWidth,
                double.PositiveInfinity));

            var wrapped = WpfChatRenderer.BuildTextBlock([ChatMessageFor(longToken)]);
            wrapped.Measure(new System.Windows.Size(
                WpfChatRenderer.PanelWidth,
                double.PositiveInfinity));

            return (oneLine.DesiredSize.Height, wrapped.DesiredSize.Height);
        });

        Assert(
            wrappedHeight > oneLineHeight * 1.5,
            "A long unbroken chat message did not grow past one line's height, meaning it "
            + "overflowed the panel width instead of wrapping onto multiple lines.");
    }

    /// <summary>
    /// An empty username and an unparsable colour must fall back to sensible
    /// defaults rather than throwing mid-repaint - a malformed Streamer.bot
    /// action must cost one odd-looking line, not the whole chat window.
    /// </summary>
    private static void TestChatRenderHandlesEmptyUsernameAndColour()
    {
        using var thread = new WpfRenderThread("self-test chat empty fields");
        var messages = new[] { ChatMessageFor("hello", user: "", colour: "not-a-colour") };

        var rendered = thread.Invoke(() =>
        {
            var panel = WpfChatRenderer.BuildPanel(new ChatContent(messages));
            return WpfOverlayPixelPipeline.RenderToRgba(
                panel,
                WpfChatRenderer.PanelWidth,
                WpfChatRenderer.PanelHeight);
        });

        Assert(
            rendered.Length == WpfChatRenderer.PanelWidth * WpfChatRenderer.PanelHeight * 4,
            "Rendering a chat message with an empty username and an invalid colour produced a "
            + "wrongly sized texture instead of falling back to defaults.");
    }

    /// <summary>
    /// A token that exactly matches one of the message's own
    /// <c>EmoteNames</c> must render distinctly from ordinary text - still
    /// plain text, no image, per §B3's deferral of emote images - while an
    /// ordinary word must not be mistaken for one.
    /// </summary>
    private static void TestChatRenderStylesEmoteTokensDistinctly()
    {
        using var thread = new WpfRenderThread("self-test chat emote styling");
        var message = ChatMessageFor("hey Kappa there", emoteNames: ["Kappa"]);

        var (emoteStyle, normalStyle) = thread.Invoke(() =>
        {
            var block = WpfChatRenderer.BuildTextBlock([message]);
            var runs = block.Inlines.OfType<System.Windows.Documents.Run>().ToList();
            var emoteRun = runs.FirstOrDefault(run => run.Text.Trim() == "Kappa");
            var normalRun = runs.FirstOrDefault(run => run.Text.Trim() == "hey");
            return (emoteRun?.FontStyle, normalRun?.FontStyle);
        });

        Assert(
            emoteStyle == System.Windows.FontStyles.Italic,
            "A recognised emote token was not styled distinctly from ordinary text.");
        Assert(
            normalStyle == System.Windows.FontStyles.Normal,
            "An ordinary word was styled as if it were a recognised emote.");
    }

    /// <summary>
    /// The ring buffer is written by the event stream's consumption loop and
    /// read by the render thread - different threads, per §B6 - so this
    /// hammers it from several writer threads while a reader thread
    /// continuously snapshots, the same shape <c>ChatOverlay.Tick</c> and the
    /// channel consumer actually use.
    /// </summary>
    private static void TestChatRingBufferThreadSafeConcurrentAccess()
    {
        var buffer = new SvrBridge.Core.ChatRingBuffer(capacity: 40);
        const int writerCount = 4;
        const int messagesPerWriter = 200;
        Exception? readException = null;
        var readerStop = false;

        var reader = new System.Threading.Thread(() =>
        {
            try
            {
                while (!System.Threading.Volatile.Read(ref readerStop))
                {
                    var (snapshot, _) = buffer.SnapshotWithVersion();
                    if (snapshot.Count > 40)
                    {
                        throw new InvalidOperationException(
                            "A chat ring buffer snapshot exceeded its capacity.");
                    }
                }
            }
            catch (Exception exception)
            {
                readException = exception;
            }
        });
        reader.Start();

        var writers = Enumerable.Range(0, writerCount)
            .Select(writer => new System.Threading.Thread(() =>
            {
                for (var index = 0; index < messagesPerWriter; index++)
                {
                    buffer.Append(ChatMessageFor($"w{writer}-{index}"));
                }
            }))
            .ToArray();

        foreach (var writer in writers)
        {
            writer.Start();
        }

        foreach (var writer in writers)
        {
            writer.Join();
        }

        System.Threading.Volatile.Write(ref readerStop, true);
        reader.Join();

        Assert(readException is null, $"Concurrent chat buffer access threw: {readException}");
        Assert(
            buffer.Version == writerCount * messagesPerWriter,
            "The chat ring buffer lost or double-counted appends made concurrently from "
            + "several threads.");
        Assert(
            buffer.Snapshot().Count == 40,
            "The chat ring buffer did not settle at its capacity after concurrent writes.");
    }

    /// <summary>A minimal, valid 1x1 PNG - enough for a real WPF decode to succeed.</summary>
    private static readonly byte[] OnePixelPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    /// <summary>
    /// A cache miss must never block: it starts a background fetch and
    /// returns immediately, and only a later call sees the decoded, frozen
    /// image. Also proves a completed fetch is served from cache rather than
    /// re-downloaded, and that <c>Version</c> tracks successful decodes.
    /// </summary>
    private static void TestChatImageCacheFetchesDecodesAndCaches()
    {
        var attempts = 0;
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler((_, _) =>
            {
                System.Threading.Interlocked.Increment(ref attempts);
                return Task.FromResult(
                    new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(OnePixelPngBytes)
                    });
            }));
        using var cache = new ChatImageCache(httpClient);
        cache.SetEmoteCatalog(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Kappa"] = "https://example.invalid/kappa.png"
            });

        Assert(
            !cache.TryGet("Kappa", out var immediate) && immediate is null,
            "The emote cache returned an image before any fetch could have completed.");

        Assert(
            WaitForCondition(() => cache.TryGet("Kappa", out _), TimeSpan.FromSeconds(5)),
            "The emote cache never finished fetching and decoding a known emote.");
        Assert(
            cache.TryGet("Kappa", out var cached) && cached is not null,
            "A completed emote fetch was not served from cache on a later call.");
        Assert(cache.Version > 0, "The emote cache version was not bumped after a successful fetch.");
        Assert(
            attempts == 1,
            "The emote cache fetched an already-cached emote again instead of reusing it.");

        Assert(
            !cache.TryGet("UnknownEmote", out var missing) && missing is null,
            "An emote name absent from the catalog unexpectedly returned an image.");
    }

    /// <summary>
    /// A failed fetch must not be cached as a permanent miss - see
    /// <see cref="ChatImageCache"/>'s own remarks on why - so this proves a
    /// later call for the same name retries rather than staying stuck.
    /// </summary>
    private static void TestChatImageCacheRetriesAfterAFailedFetch()
    {
        var attempts = 0;
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler((_, _) =>
            {
                var attempt = System.Threading.Interlocked.Increment(ref attempts);
                return Task.FromResult(
                    attempt == 1
                        ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                        : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(OnePixelPngBytes)
                        });
            }));
        using var cache = new ChatImageCache(httpClient);
        cache.SetEmoteCatalog(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FlakyEmote"] = "https://example.invalid/flaky.png"
            });

        cache.TryGet("FlakyEmote", out _);
        Assert(
            WaitForCondition(
                () => System.Threading.Volatile.Read(ref attempts) >= 1,
                TimeSpan.FromSeconds(5)),
            "The emote cache never attempted its first fetch.");
        // Give the failing attempt a moment to finish clearing its in-flight
        // marker before checking that failure was not cached.
        System.Threading.Thread.Sleep(200);
        Assert(
            !cache.TryGet("FlakyEmote", out var afterFailure) && afterFailure is null,
            "A failed emote fetch was cached as if it had succeeded.");

        Assert(
            WaitForCondition(() => cache.TryGet("FlakyEmote", out _), TimeSpan.FromSeconds(5)),
            "The emote cache did not retry a previously failed fetch on a later request.");
        Assert(
            attempts >= 2,
            "The emote cache did not actually make a second network request after the first failed.");
    }

    /// <summary>
    /// A recognised emote token with an already-cached image must render as
    /// that image (an <c>InlineUIContainer</c>), not as styled text - proving
    /// <see cref="WpfChatRenderer"/> actually prefers the real image over its
    /// text fallback once one is available, rather than always falling back.
    /// </summary>
    private static void TestChatRenderEmbedsCachedEmoteImage()
    {
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler((_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(OnePixelPngBytes)
                    })));
        using var cache = new ChatImageCache(httpClient);
        cache.SetEmoteCatalog(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Kappa"] = "https://example.invalid/kappa.png"
            });
        cache.TryGet("Kappa", out _);
        Assert(
            WaitForCondition(() => cache.TryGet("Kappa", out _), TimeSpan.FromSeconds(5)),
            "The emote cache never finished preparing the image this test depends on.");

        using var thread = new WpfRenderThread("self-test chat emote image");
        var message = ChatMessageFor("hey Kappa there", emoteNames: ["Kappa"]);

        var containerCount = thread.Invoke(() =>
        {
            var block = WpfChatRenderer.BuildTextBlock([message], cache);
            return block.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Count();
        });

        Assert(
            containerCount == 1,
            "A recognised emote token with an already-cached image was not embedded as an image.");
    }

    /// <summary>
    /// A badge with an already-cached image must render as that image, not
    /// the bracketed text label - and a badge with no image URL at all (or
    /// one not yet cached) must still fall back to the label, never to
    /// nothing. Uses <see cref="ChatImageCache.TryGetByUrl"/> directly - the
    /// badge path has no catalog indirection, unlike emotes.
    /// </summary>
    private static void TestChatRenderEmbedsCachedBadgeImage()
    {
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler((_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(OnePixelPngBytes)
                    })));
        using var cache = new ChatImageCache(httpClient);
        cache.TryGetByUrl("https://example.invalid/mod.png", out _);
        Assert(
            WaitForCondition(
                () => cache.TryGetByUrl("https://example.invalid/mod.png", out _),
                TimeSpan.FromSeconds(5)),
            "The badge image cache never finished preparing the image this test depends on.");

        using var thread = new WpfRenderThread("self-test chat badge image");
        var withImage = ChatMessageFor(
            "hi",
            badge: "Mod",
            badgeImageUrl: "https://example.invalid/mod.png");
        var withoutImage = ChatMessageFor("hi", badge: "VIP", badgeImageUrl: "");

        var (imageContainers, fallbackText) = thread.Invoke(() =>
        {
            var withImageBlock = WpfChatRenderer.BuildTextBlock([withImage], cache);
            var withoutImageBlock = WpfChatRenderer.BuildTextBlock([withoutImage], cache);
            var containers = withImageBlock.Inlines
                .OfType<System.Windows.Documents.InlineUIContainer>()
                .Count();
            var fallback = withoutImageBlock.Inlines
                .OfType<System.Windows.Documents.Run>()
                .Any(run => run.Text.Contains("[VIP]"));
            return (containers, fallback);
        });

        Assert(
            imageContainers == 1,
            "A badge with an already-cached image was not embedded as an image.");
        Assert(
            fallbackText,
            "A badge with no image URL did not fall back to its bracketed text label.");
    }

    /// <summary>
    /// A message carrying several badges (broadcaster, Prime, an
    /// unrecognised channel-specific one) must render all of them, not just
    /// the first - the exact bug a live headset session caught: an earlier
    /// version picked one badge from a small hardcoded priority list and
    /// silently discarded the rest.
    /// </summary>
    private static void TestChatRenderEmbedsMultipleBadges()
    {
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler((_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(OnePixelPngBytes)
                    })));
        using var cache = new ChatImageCache(httpClient);
        var urls = new[]
        {
            "https://example.invalid/broadcaster.png",
            "https://example.invalid/prime.png",
            "https://example.invalid/glhf.png"
        };
        foreach (var url in urls)
        {
            cache.TryGetByUrl(url, out _);
        }

        Assert(
            WaitForCondition(
                () => urls.All(url => cache.TryGetByUrl(url, out _)),
                TimeSpan.FromSeconds(5)),
            "The image cache never finished preparing all three badge images this test depends on.");

        using var thread = new WpfRenderThread("self-test chat multiple badges");
        var message = new SvrBridge.Core.StreamerBotEventPayload
        {
            Target = SvrBridge.Core.StreamerBotEventTarget.Chat,
            User = "user",
            Text = "hi",
            Badges =
            [
                new SvrBridge.Core.ChatBadge("Broadcaster", urls[0]),
                new SvrBridge.Core.ChatBadge("Prime", urls[1]),
                new SvrBridge.Core.ChatBadge("glhf-pledge", urls[2])
            ]
        };

        var containerCount = thread.Invoke(() =>
        {
            var block = WpfChatRenderer.BuildTextBlock([message], cache);
            return block.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Count();
        });

        Assert(
            containerCount == 3,
            $"Expected all 3 badges on the message to render as images, but only {containerCount} did.");
    }

    private static bool WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            System.Threading.Thread.Sleep(20);
        }

        return condition();
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, System.Threading.CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private static SvrBridge.Core.StreamerBotEventPayload ChatMessageFor(
        string text,
        string user = "user",
        string colour = "",
        string badge = "",
        string badgeImageUrl = "",
        IReadOnlyList<string>? emoteNames = null) =>
        new()
        {
            Target = SvrBridge.Core.StreamerBotEventTarget.Chat,
            User = user,
            Colour = colour,
            Badge = badge,
            BadgeImageUrl = badgeImageUrl,
            Text = text,
            EmoteNames = emoteNames ?? []
        };

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
