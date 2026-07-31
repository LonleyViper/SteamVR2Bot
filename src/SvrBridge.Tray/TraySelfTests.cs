using System.Numerics;

namespace SvrBridge.Tray;

internal static class TraySelfTests
{
    public static void Run()
    {
        TestVrActionBrowser();
        TestVrScrollLimiter();
        TestPackagedViveBinding();
        TestDashboardBottomBarLayout();
        TestSettingsPageLayoutRectangles();
        TestRenamedDataDirectoryMigration();
        TestOverlayTransformComposition();
        TestOverlayHandleRoundTrip();
        TestNotificationPlayerQueueing();
        TestNotificationDurationClampingEndToEnd();
        TestNotificationAlphaCurve();
        TestNotificationTransitionAnimatorConvergesAndStopsIssuingCalls();
        TestNotificationTemplateLoaderDegradesGracefully();
        TestNotificationTemplateLoaderDownscalesAnOversizedImage();
        TestNotificationPayloadPrecedenceResolvesSettingsDefaults();
        TestWpfRenderThreadStartsAndShutsDownCleanly();
        TestNotificationPixelFormatConversion();
        TestChatRingBufferEviction();
        TestChatRepaintThrottleCoalescesBurst();
        TestDashboardRepaintCoordinatorCoalescesBurst();
        TestChatGazeHysteresisNoOscillationAtBoundary();
        TestGazeScaleAnimationConvergesAndStopsIssuingCalls();
        TestChatRenderWrapsLongUnbrokenString();
        TestChatRenderHandlesEmptyUsernameAndColour();
        TestChatRenderStylesEmoteTokensDistinctly();
        TestChatImageCacheFetchesDecodesAndCaches();
        TestChatImageCacheRetriesAfterAFailedFetch();
        TestChatRenderEmbedsCachedEmoteImage();
        TestChatRenderEmbedsCachedBadgeImage();
        TestChatRenderEmbedsMultipleBadges();
        TestChatRingBufferThreadSafeConcurrentAccess();
        TestOverlayAnchorOffsetsMatchProvenTransforms();
        TestOverlayPlacementDefaultMatchesProvenTransforms();
        TestRigidInverseRoundTrips();
        TestPanelViewMeasuresTheWindowRatherThanItsAnchor();
        TestPanelVisibilityGateHidesTurnedAwayAndDistantPanels();
        TestDegeneratePlacementFallsBackInsteadOfVanishing();
        TestOverlayDragCarriesRotationAsWellAsPosition();
        TestOverlayPlacementArgumentRoundTrip();
        TestChatHandleHitTestMatchesTheDrawnRectangle();
        TestChatHoverRepaintsOncePerRectangleCrossed();
        TestChatInputIsRejectedOutsideTheGazedState();
        TestOnlyTheHandleStartsAndEndsAGrab();
        TestHandPlacedOffsetClearsAStreamerBotOverride();
        TestSurfaceOverrideStateAppliesAndResetsControlCommands();
        TestChatDeveloperInjectorProducesExpectedMessages();
        TestChatCommandJsonRoundTripPreservesBadgesAndEmotes();
        TestChatPlacementSurvivesTheWorkerMessageChannel();
        TestMergeVrSettingsSnapshotPersistsEveryField();
        TestRequiresRuntimeRestartDistinguishesLiveAppliableChanges();
        TestOverlayTextureCopyRespectsAnOverWideRowPitch();
        TestOverlaySourceFormatsConvertToTheSameRgba();
        TestOverlayUploadFallsBackOnDeviceLossAndRecovers();
        TestOverlayUploadDefaultsOffUntilExplicitlyEnabled();
        TestD3D11OverlayTextureRoundTripsRgbaWithoutSwappingChannels();
        TestSourceIconResourceFindsEveryEmbeddedIcon();
        TestNotificationEventPickerStaysBoundedAtARealCatalogSize();
        TestNotificationEventPickerRoundTripsAndKeepsUnreportedEvents();
        TestNotificationRendersAtTheConfiguredSizeWithItsIcon();

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
                EventStreamEnabled = true,
                ChatOpacity = 0.8,
                ChatSizeScale = 1.2,
                GazeSensitivity = SvrBridge.Core.GazeSensitivity.Tight,
                // Non-default, so a dropped field cannot pass by accident.
                ChatGazeScaleEnabled = true,
                NotificationOpacity = 0.7,
                NotificationSizeScale = 0.6,
                // Deliberately not the default in either mode: a placement
                // that happened to equal OverlayPlacement.Default would
                // round-trip even if the field were never written at all.
                ChatPlacement = new SvrBridge.Core.OverlayPlacement(
                    SvrBridge.Core.VrOverlayTransform.Translation(0.03f, 0.11f, -0.19f)
                    * SvrBridge.Core.VrOverlayTransform.RotationY(0.4f),
                    SvrBridge.Core.VrOverlayTransform.Translation(-0.05f, -0.2f, -0.8f)
                    * SvrBridge.Core.VrOverlayTransform.RotationZ(-0.25f))
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
            Assert(
                actual.ChatOpacity == expected.ChatOpacity
                && actual.ChatSizeScale == expected.ChatSizeScale
                && actual.GazeSensitivity == expected.GazeSensitivity
                && actual.ChatGazeScaleEnabled == expected.ChatGazeScaleEnabled
                && actual.NotificationOpacity == expected.NotificationOpacity
                && actual.NotificationSizeScale == expected.NotificationSizeScale,
                "The Phase 4b appearance/gaze settings did not round-trip.");
            Assert(
                actual.ChatPlacement.Equals(expected.ChatPlacement),
                "A hand-dragged chat window position did not survive a save and load.");

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
            var upgraded = store.Load();
            Assert(
                upgraded.ChatOpacity == 0.95
                && upgraded.ChatSizeScale == 1.0
                && upgraded.GazeSensitivity == SvrBridge.Core.GazeSensitivity.Normal
                && upgraded.NotificationOpacity == 1.0
                && upgraded.NotificationSizeScale == 1.0,
                "A settings file written before Phase 4b did not default to today's hardcoded appearance.");
            // Deliberately the opposite of this file's usual migration rule -
            // see UserSettings.ChatGazeScaleEnabled for why the grow-on-gaze
            // animation is the one setting an upgrade is allowed to change.
            Assert(
                !upgraded.ChatGazeScaleEnabled,
                "The grow-on-gaze animation no longer defaults to off.");
            Assert(
                upgraded.ChatPlacement.Equals(SvrBridge.Core.OverlayPlacement.Default),
                "A settings file written before Phase 5 did not default to the hardware-proven chat placement.");

            // The file this field's own shape change left behind: present,
            // well-formed JSON, and all zeros, because the properties it was
            // written with no longer exist. It must load as the proven
            // placement - a zero transform collapses the chat window to
            // nothing, with no error anywhere to say so.
            File.WriteAllText(
                Path.Combine(testDirectory, "settings.json"),
                """
                {
                  "StreamerBotAddress": "ws://127.0.0.1:8080/1",
                  "ProtectedPassword": "",
                  "ChatEnabled": true,
                  "ChatPlacement": {
                    "ControllerOffset": {
                      "M00": 0, "M01": 0, "M02": 0, "M03": 0,
                      "M10": 0, "M11": 0, "M12": 0, "M13": 0,
                      "M20": 0, "M21": 0, "M22": 0, "M23": 0
                    },
                    "HeadOffset": {
                      "M00": 0, "M01": 0, "M02": 0, "M03": 0,
                      "M10": 0, "M11": 0, "M12": 0, "M13": 0,
                      "M20": 0, "M21": 0, "M22": 0, "M23": 0
                    }
                  }
                }
                """);
            Assert(
                store.Load().ChatPlacement.Equals(SvrBridge.Core.OverlayPlacement.Default),
                "A zeroed saved placement loaded as-is, which puts no chat window in the headset at all.");

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

        var preview = VrDashboardRenderer.RenderActionPicker(browser);
        AssertDashboardPage(preview, "The grouped VR action picker");

        var gestureTypePreview = VrDashboardRenderer.RenderGestureTypePicker(false);
        AssertDashboardPage(gestureTypePreview, "The VR gesture type picker");

        // Replaces an older check that two pages never reused an image path
        // while SteamVR could still be loading one. There are no image files
        // any more - pages are uploaded as pixels - so the equivalent question
        // is whether two different pages actually produce different pixels.
        Assert(
            !preview.Rgba.AsSpan().SequenceEqual(gestureTypePreview.Rgba),
            "Two different dashboard pages rendered byte-identical textures.");

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
        AssertDashboardPage(
            VrDashboardRenderer.Render([doublePressShortcut]),
            "The editable VR shortcut list");

        AssertDashboardPage(
            VrDashboardRenderer.RenderTolerancePicker(
                SvrBridge.Core.ChordMode.DoublePress,
                500),
            "The VR tolerance slider");

        AssertDashboardPage(
            VrDashboardRenderer.RenderInputRecorder(
                SvrBridge.Core.ChordMode.Simultaneous,
                SvrBridge.Core.ControllerSetup.Unknown,
                doublePressInput),
            "The VR input recorder");

        var review = VrDashboardRenderer.RenderShortcutReview(
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
        AssertDashboardPage(review, "The VR shortcut review");
    }

    /// <summary>
    /// Every dashboard page must come back at the fixed page size with a
    /// tightly packed straight-alpha RGBA buffer to match. The size is not
    /// cosmetic: <c>VrDashboardLayout</c>'s rectangles and hit testing, and the
    /// mouse scale <c>OpenVrInput.EnsureDashboardCreated</c> sets, are all
    /// written in this coordinate space.
    /// </summary>
    private static void AssertDashboardPage(RenderedPanel page, string description)
    {
        Assert(
            page.Width == VrDashboardRenderer.PageWidth
            && page.Height == VrDashboardRenderer.PageHeight,
            $"{description} rendered at {page.Width}x{page.Height} rather than "
            + $"{VrDashboardRenderer.PageWidth}x{VrDashboardRenderer.PageHeight}.");
        Assert(
            page.Rgba.Length == page.Width * page.Height * 4,
            $"{description} returned {page.Rgba.Length} bytes for a "
            + $"{page.Width}x{page.Height} RGBA texture.");
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

    /// <summary>
    /// The tab strip (three tabs since Phase 6: Shortcuts, Chat,
    /// Notifications) and the Chat/Notifications settings pages' segmented
    /// controls follow the same shared-rectangle-table rule as the bottom bar
    /// tested above: every control the renderer draws is declared once in
    /// <see cref="VrDashboardLayout"/> and hit-tests to itself, and no two
    /// controls on the same page overlap.
    /// </summary>
    private static void TestSettingsPageLayoutRectangles()
    {
        var segmentedRows = new (string Name, Rectangle[] Row)[]
        {
            ("tabs", VrDashboardLayout.Tabs),
            ("chat anchor mode", VrDashboardLayout.ChatAnchorMode),
            ("chat anchor hand", VrDashboardLayout.ChatAnchorHand),
            ("notification anchor mode", VrDashboardLayout.NotificationAnchorMode),
            ("notification anchor hand", VrDashboardLayout.NotificationAnchorHand),
            ("gaze sensitivity", VrDashboardLayout.GazeSensitivity)
        };

        foreach (var (name, row) in segmentedRows)
        {
            for (var index = 0; index < row.Length; index++)
            {
                var button = row[index];
                Assert(
                    button.Width >= 200,
                    $"A {name} button is too narrow to hit with a laser.");
                Assert(
                    index == 0 || button.Left > row[index - 1].Right,
                    $"The {name} buttons overlap.");

                // Centre, both edges, and the gap that follows must all
                // resolve to this button, or the drawn button and the click
                // disagree - the same property TestDashboardBottomBarLayout
                // proves for the wizard's own bottom bar.
                Assert(
                    VrDashboardLayout.IndexAt(row, button.Left + (button.Width / 2f)) == index
                    && VrDashboardLayout.IndexAt(row, button.Left) == index
                    && VrDashboardLayout.IndexAt(row, button.Right - 1) == index,
                    $"A click on a {name} button resolved to a different button.");
            }
        }

        // Each surface's toggle and its two segmented controls sit in the
        // same visual row, on the same Y band, so a Y-based dispatch alone
        // cannot tell them apart - they must not overlap along X either.
        Assert(
            !VrDashboardLayout.ChatAnchorMode[^1].IntersectsWith(VrDashboardLayout.ChatAnchorHand[0])
            && !VrDashboardLayout.ChatAnchorHand[^1].IntersectsWith(VrDashboardLayout.ChatToggle),
            "The chat controls row overlaps itself.");
        Assert(
            !VrDashboardLayout.NotificationAnchorMode[^1].IntersectsWith(
                VrDashboardLayout.NotificationAnchorHand[0])
            && !VrDashboardLayout.NotificationAnchorHand[^1].IntersectsWith(
                VrDashboardLayout.NotificationToggle),
            "The notifications controls row overlaps itself.");

        // The opacity and size sliders share a row the same way.
        Assert(
            !VrDashboardLayout.ChatOpacityTrack.IntersectsWith(VrDashboardLayout.ChatSizeTrack),
            "The chat opacity and size sliders overlap.");
        Assert(
            !VrDashboardLayout.NotificationOpacityTrack.IntersectsWith(
                VrDashboardLayout.NotificationSizeTrack),
            "The notification opacity and size sliders overlap.");

        // Every control on the settings page must stay on the canvas and
        // clear of the tab strip at the top.
        Rectangle[] allControls =
        [
            VrDashboardLayout.ChatToggle,
            VrDashboardLayout.NotificationToggle,
            VrDashboardLayout.ChatOpacityTrack,
            VrDashboardLayout.ChatSizeTrack,
            VrDashboardLayout.NotificationOpacityTrack,
            VrDashboardLayout.NotificationSizeTrack,
            .. VrDashboardLayout.ChatAnchorMode,
            .. VrDashboardLayout.ChatAnchorHand,
            .. VrDashboardLayout.NotificationAnchorMode,
            .. VrDashboardLayout.NotificationAnchorHand,
            .. VrDashboardLayout.GazeSensitivity,
            VrDashboardLayout.ResetPlacement,
            VrDashboardLayout.GazeScaleToggle
        ];
        foreach (var control in allControls)
        {
            Assert(
                control.Top >= VrDashboardLayout.TabStripY + VrDashboardLayout.TabStripHeight
                && control.Bottom <= 900
                && control.Left >= 0
                && control.Right <= 1400,
                "A settings-page control falls outside the canvas or under the tab strip.");
        }

        // The reset control sits below the gaze-sensitivity row - the last
        // row above it on the Chat page since Phase 6 split Chat and
        // Notifications into separate tabs. A Y-band dispatch cannot tell
        // two rows apart if they touch, and this page has no bottom bar to
        // bound it from below.
        Assert(
            VrDashboardLayout.ResetPlacement.Top
            >= VrDashboardLayout.GazeSensitivityY + VrDashboardLayout.SettingsRowHeight,
            "The chat placement reset overlaps the gaze-sensitivity row above it.");

        // Chat and Notifications became separate pages in Phase 6, so their
        // first two rows intentionally now share the same Y - each page
        // starts fresh below its own tab strip rather than the Notifications
        // page continuing to sit lower down where it used to share a page
        // with Chat. Pinned down so this reads as a deliberate choice, not
        // leftover drift, the same way VrDashboardLayout's own comment on
        // NotificationControlsY explains it.
        Assert(
            VrDashboardLayout.NotificationControlsY == VrDashboardLayout.ChatControlsY
            && VrDashboardLayout.NotificationSlidersY == VrDashboardLayout.ChatSlidersY,
            "The Chat and Notifications pages' first two rows drifted apart even though each now starts fresh below its own tab strip.");

        // The reset button and the grow-on-gaze toggle share the bottom row,
        // so a Y-band dispatch alone cannot tell them apart - they must not
        // overlap along X either, exactly like the surface rows above.
        Assert(
            !VrDashboardLayout.ResetPlacement.IntersectsWith(VrDashboardLayout.GazeScaleToggle),
            "The placement reset and the grow-on-gaze toggle overlap.");
        Assert(
            VrDashboardLayout.GazeScaleToggle.Left == VrDashboardLayout.ChatToggle.Left
            && VrDashboardLayout.GazeScaleToggle.Width == VrDashboardLayout.ChatToggle.Width,
            "The grow-on-gaze toggle is not aligned with the column of on/off toggles above it.");

        // The List page's row count dropped from 6 to 5 to make room for the
        // tab strip - its last row must still clear the bottom bar.
        var lastListRowBottom = VrDashboardLayout.ListRowsStartY
                                 + ((VrDashboardLayout.ListVisibleRowCount - 1) * VrDashboardLayout.ListRowHeight)
                                 + 92;
        Assert(
            lastListRowBottom < VrDashboardLayout.BarY,
            "The shortcut list's last row now overlaps the bottom bar.");
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
    /// Guards the Phase 4 anchor refactor (§B2): the offsets
    /// <see cref="SvrBridge.Core.OverlayAnchor"/> hands out must stay bit-for-bit
    /// the same as the wrist and head transforms Phase 1/2/3 already proved in
    /// the headset, not merely "close" values reinvented for the occasion.
    /// </summary>
    private static void TestOverlayAnchorOffsetsMatchProvenTransforms()
    {
        var provenWristOffset =
            SvrBridge.Core.VrOverlayTransform.Translation(0f, 0.06f, -0.12f)
            * SvrBridge.Core.VrOverlayTransform.RotationX(-0.6f);
        var provenHeadOffset =
            SvrBridge.Core.VrOverlayTransform.Translation(0f, -0.12f, -0.6f);

        Assert(
            SvrBridge.Core.OverlayAnchor.ControllerOffset.Equals(provenWristOffset),
            "The controller-anchor offset no longer matches the wrist transform Phase 1/3 proved in the headset.");
        Assert(
            SvrBridge.Core.OverlayAnchor.HeadOffset.Equals(provenHeadOffset),
            "The head-anchor offset no longer matches the transform Phase 2 proved in the headset.");

        var chatDefault = new SvrBridge.Core.OverlayAnchor(
            SvrBridge.Core.OverlayAnchorMode.Controller,
            SvrBridge.Core.OverlayAnchorHand.Left);
        var notificationDefault = SvrBridge.Core.OverlayAnchor.Head;
        Assert(
            chatDefault.Offset.Equals(provenWristOffset)
            && notificationDefault.Offset.Equals(provenHeadOffset),
            "The default chat/notification anchors resolve to the wrong offset.");
    }

    /// <summary>
    /// The reset control's whole promise: an install that has never dragged
    /// the window, and one that has dragged it and pressed reset, both land on
    /// the exact transform hardware testing validated - not an approximation
    /// of it. Asserted as transform equality rather than by eye, because
    /// <see cref="SvrBridge.Core.OverlayPlacement"/> reconstructs the tilt
    /// from a constant rather than storing it, and a wrong sign there would be
    /// invisible in a settings file and obvious only in a headset.
    /// </summary>
    private static void TestOverlayPlacementDefaultMatchesProvenTransforms()
    {
        var defaults = SvrBridge.Core.OverlayPlacement.Default;
        Assert(
            defaults.ToTransform(SvrBridge.Core.OverlayAnchorMode.Controller)
                .Equals(SvrBridge.Core.OverlayAnchor.ControllerOffset),
            "The default controller placement is no longer the proven wrist transform.");
        Assert(
            defaults.ToTransform(SvrBridge.Core.OverlayAnchorMode.Head)
                .Equals(SvrBridge.Core.OverlayAnchor.HeadOffset),
            "The default head placement is no longer the proven head transform.");

        // Placing one mode must not disturb the other: the two are independent
        // saved settings, and switching anchor mode has to land somewhere the
        // wearer already chose for that mode.
        var placed = SvrBridge.Core.VrOverlayTransform.Translation(0.2f, 0.3f, -0.4f)
                     * SvrBridge.Core.VrOverlayTransform.RotationY(0.5f);
        var moved = defaults.With(SvrBridge.Core.OverlayAnchorMode.Controller, placed);
        Assert(
            moved.HeadOffset.Equals(defaults.HeadOffset)
            && moved.ControllerOffset.Equals(placed),
            "Placing one anchor mode's offset changed the other mode's.");
        Assert(
            SvrBridge.Core.OverlayPlacement.Default.Equals(defaults),
            "Reset no longer restores the hardware-proven default exactly.");

        // A tracking dropout can produce a NaN, and SteamVR accepts one without
        // complaint - the panel simply vanishes to somewhere it can never be
        // pointed at to drag it back.
        var wild = defaults.With(
            SvrBridge.Core.OverlayAnchorMode.Controller,
            SvrBridge.Core.VrOverlayTransform.Translation(float.NaN, 0f, 0f));
        Assert(
            wild.ToTransform(SvrBridge.Core.OverlayAnchorMode.Controller)
                .Equals(SvrBridge.Core.OverlayAnchor.ControllerOffset),
            "A non-finite placement was applied instead of falling back to the proven default.");

        var far = defaults.With(
            SvrBridge.Core.OverlayAnchorMode.Head,
            SvrBridge.Core.VrOverlayTransform.Translation(0f, 900f, -900f));
        var clamped = far.ToTransform(SvrBridge.Core.OverlayAnchorMode.Head);
        Assert(
            clamped.M13 == SvrBridge.Core.OverlayPlacement.LimitMeters
            && clamped.M23 == -SvrBridge.Core.OverlayPlacement.LimitMeters,
            "An out-of-range placement was not brought back within reach.");
    }

    /// <summary>
    /// A zero placement must never reach SteamVR, at any layer.
    /// <para>
    /// This is a regression test for a real failure, and for the reason it was
    /// hard to spot. A settings file written before this field changed shape -
    /// three numbers per anchor mode, rather than a full transform -
    /// deserialises to an all-zero matrix. That is not absent, not a JSON
    /// error, and not non-finite, so every check that existed at the time let
    /// it through. SteamVR then accepts it without an error and collapses the
    /// overlay quad to nothing: no exception, no log line, no misplaced panel,
    /// just no chat window at all. The only defence is refusing a rotation
    /// block that is not a rotation.
    /// </para>
    /// </summary>
    private static void TestDegeneratePlacementFallsBackInsteadOfVanishing()
    {
        var zero = default(SvrBridge.Core.OverlayPlacement);
        Assert(
            !zero.ControllerOffset.IsUsable() && !zero.HeadOffset.IsUsable(),
            "An all-zero transform was judged usable - it collapses the overlay to nothing.");

        // The layer that keeps the panel on screen.
        Assert(
            zero.ToTransform(SvrBridge.Core.OverlayAnchorMode.Controller)
                .Equals(SvrBridge.Core.OverlayAnchor.ControllerOffset)
            && zero.ToTransform(SvrBridge.Core.OverlayAnchorMode.Head)
                .Equals(SvrBridge.Core.OverlayAnchor.HeadOffset),
            "A zero placement was handed to SteamVR instead of the proven default.");

        // The layer that stops it being written back to disk as though the
        // wearer had chosen it.
        Assert(
            zero.Sanitised().Equals(SvrBridge.Core.OverlayPlacement.Default),
            "A zero placement survived sanitising.");
        Assert(
            SvrBridge.Core.OverlayPlacement.Parse(zero.ToArgument())
                .Equals(SvrBridge.Core.OverlayPlacement.Default),
            "A zero placement survived the worker's command line.");

        // One half bad, one half good - the shape a partially-written or
        // hand-edited file takes. The good half must be kept.
        var placed = SvrBridge.Core.VrOverlayTransform.Translation(0.2f, 0.1f, -0.3f)
                     * SvrBridge.Core.VrOverlayTransform.RotationY(0.4f);
        var half = new SvrBridge.Core.OverlayPlacement(placed, default);
        Assert(
            half.Sanitised().ControllerOffset.Equals(placed)
            && half.Sanitised().HeadOffset.Equals(SvrBridge.Core.OverlayAnchor.HeadOffset),
            "Sanitising one bad half discarded the good one.");

        // A real dragged placement must not be mistaken for garbage: it is
        // tracked poses composed with an inverse, so it carries float error.
        var dragged = SvrBridge.Core.VrOverlayTransform.Translation(0.4f, -1.1f, 2.3f)
                      * SvrBridge.Core.VrOverlayTransform.RotationX(0.6f)
                      * SvrBridge.Core.VrOverlayTransform.RotationY(-1.2f)
                      * SvrBridge.Core.VrOverlayTransform.RotationZ(0.3f);
        Assert(
            dragged.IsUsable() && (dragged * dragged.InverseRigid() * dragged).IsUsable(),
            "A legitimately composed transform was rejected as degenerate.");
    }

    /// <summary>
    /// Gaze must follow the <em>window</em>, not the device it hangs off.
    /// <para>
    /// The first version measured the direction to the anchor controller in a
    /// yaw-only body frame, which was indistinguishable from measuring the
    /// window while the window was welded 12 cm off the wrist - and wrong the
    /// moment the wearer could drag it elsewhere. The reported symptom was
    /// that the grow-and-shrink trigger "does not adjust with the
    /// positioning", which is exactly this: the window moved and the thing
    /// being measured did not.
    /// </para>
    /// </summary>
    private static void TestPanelViewMeasuresTheWindowRatherThanItsAnchor()
    {
        // Head at the origin, looking down -Z, which is where OpenVR puts a
        // device's forward axis.
        var head = SvrBridge.Core.VrOverlayTransform.Identity;

        var ahead = SvrBridge.Core.VrOverlayTransform.Translation(0f, 0f, -1f);
        var aheadView = SvrBridge.Core.PanelView.From(head, ahead);
        Assert(
            Close(aheadView.GazeDot, 1f) && Close(aheadView.DistanceMeters, 1f),
            $"A panel straight ahead measured as gaze {aheadView.GazeDot}, {aheadView.DistanceMeters} m.");

        // Same distance, off to the side: looked away from, not at.
        var beside = SvrBridge.Core.VrOverlayTransform.Translation(1f, 0f, 0f);
        Assert(
            Close(SvrBridge.Core.PanelView.From(head, beside).GazeDot, 0f),
            "A panel at ninety degrees did not measure as being looked away from.");

        // And behind.
        var behind = SvrBridge.Core.VrOverlayTransform.Translation(0f, 0f, 1f);
        Assert(
            Close(SvrBridge.Core.PanelView.From(head, behind).GazeDot, -1f),
            "A panel directly behind did not measure as being looked away from.");

        // Pitch counts, unlike the yaw-only body frame this replaced: a panel
        // low down is not being looked at by someone staring straight ahead.
        var low = SvrBridge.Core.VrOverlayTransform.Translation(0f, -1f, -1f);
        var lowDot = SvrBridge.Core.PanelView.From(head, low).GazeDot;
        Assert(
            lowDot > 0.6f && lowDot < 0.8f,
            $"A panel 45 degrees below the eye line measured {lowDot}; pitch is being ignored.");

        // Facing is a separate question from gaze. An overlay's texture faces
        // its own +Z, so a panel placed in front of the wearer with no
        // rotation already faces back at them.
        Assert(
            Close(aheadView.FacingDot, 1f),
            "A panel placed in front of the wearer did not measure as facing them.");
        var turnedAway = SvrBridge.Core.VrOverlayTransform.Translation(0f, 0f, -1f)
                         * SvrBridge.Core.VrOverlayTransform.RotationY(MathF.PI);
        Assert(
            SvrBridge.Core.PanelView.From(head, turnedAway).FacingDot < -0.9f,
            "A panel turned to face away was still measured as facing the wearer.");

        // The two really are independent: this one is looked straight at and
        // is edge-on, which is the case worth hiding.
        var edgeOn = SvrBridge.Core.VrOverlayTransform.Translation(0f, 0f, -1f)
                     * SvrBridge.Core.VrOverlayTransform.RotationY(MathF.PI / 2f);
        var edgeView = SvrBridge.Core.PanelView.From(head, edgeOn);
        Assert(
            Close(edgeView.GazeDot, 1f) && MathF.Abs(edgeView.FacingDot) < 0.01f,
            "An edge-on panel being stared at was not distinguished from one facing the wearer.");

        // The proven wrist placement, on a level controller half a metre in
        // front and below the head, must still read as facing the wearer -
        // this is the case the -0.6 rad tilt exists for, and a sign error in
        // the normal would hide the window at its own default placement.
        var wrist = SvrBridge.Core.VrOverlayTransform.Translation(0.1f, -0.5f, -0.4f);
        var wristPanel = wrist * SvrBridge.Core.OverlayAnchor.ControllerOffset;
        Assert(
            SvrBridge.Core.PanelView.From(head, wristPanel).FacingDot > 0.5f,
            "The default wrist placement measured as facing away, which would hide it on sight.");
    }

    /// <summary>
    /// The hide rules, including the hysteresis that stops a panel flickering
    /// at either boundary and the asymmetry that stops it flickering at both
    /// at once.
    /// </summary>
    private static void TestPanelVisibilityGateHidesTurnedAwayAndDistantPanels()
    {
        var gate = new SvrBridge.Core.PanelVisibilityGate();
        Assert(gate.IsVisible, "A fresh visibility gate started hidden.");
        Assert(gate.Update(1f, 0.5f), "A panel facing the wearer at arm's length was hidden.");

        // Turned nearly edge-on: hidden.
        Assert(!gate.Update(0.05f, 0.5f), "A panel turned away was not hidden.");

        // Coming back needs more than just crossing the same line again, or
        // pose noise at the boundary flickers it.
        Assert(!gate.Update(0.25f, 0.5f), "A panel came back inside the hysteresis dead zone.");
        Assert(gate.Update(0.5f, 0.5f), "A panel turned back towards the wearer stayed hidden.");

        // Distance is the other rule, and it is generous - a deliberately
        // placed arm's-length panel must survive it.
        Assert(gate.Update(1f, 1.5f), "A panel 1.5 m away was hidden.");
        Assert(!gate.Update(1f, 2.5f), "A panel 2.5 m away was not hidden.");
        Assert(!gate.Update(1f, 1.9f), "A distant panel came back inside the hysteresis dead zone.");
        Assert(gate.Update(1f, 1.5f), "A panel brought back within reach stayed hidden.");

        // Either rule alone hides; coming back needs both to pass, so a panel
        // that is both turned away and far off does not flicker back the
        // instant one of them recovers.
        Assert(!gate.Update(0.05f, 2.5f), "A panel failing both rules was not hidden.");
        Assert(!gate.Update(1f, 2.5f), "A panel still too far away came back on facing alone.");
        Assert(!gate.Update(0.05f, 0.5f), "A panel still turned away came back on distance alone.");
        Assert(gate.Update(1f, 0.5f), "A panel that recovered on both rules stayed hidden.");

        // Taking hold of the window overrules the gate outright - nothing
        // being handled may be hidden out from under the person handling it.
        gate.Update(0.05f, 2.5f);
        Assert(!gate.IsVisible, "Could not set up the forced-visible case.");
        gate.ForceVisible();
        Assert(gate.IsVisible, "Forcing the panel visible did not.");
    }

    /// <summary>
    /// The rigid-grab arithmetic, with no headset: a panel taken hold of and
    /// carried by a controller keeps exactly its relationship to that
    /// controller, through rotation as well as translation. The first version
    /// of this drag could only translate, which the headset rejected - so
    /// rotation is the property most worth pinning down here.
    /// </summary>
    private static void TestOverlayDragCarriesRotationAsWellAsPosition()
    {
        var anchorPose = SvrBridge.Core.VrOverlayTransform.Translation(0.1f, 1.2f, -0.3f)
                         * SvrBridge.Core.VrOverlayTransform.RotationY(0.4f);
        var pointerPose = SvrBridge.Core.VrOverlayTransform.Translation(0.5f, 1.1f, -0.6f)
                          * SvrBridge.Core.VrOverlayTransform.RotationX(-0.2f);
        var offset = SvrBridge.Core.OverlayPlacement.Default.ControllerOffset;

        var drag = SvrBridge.Core.OverlayDrag.Begin(
            SvrBridge.Core.OverlayAnchorMode.Controller,
            offset,
            anchorPose,
            pointerPose);

        // Nothing has moved yet, so the panel must not have moved either. This
        // is the check that catches an inverted or transposed term: any sign
        // error shows up as a panel that jumps the instant it is grabbed.
        AssertClose(
            drag.OffsetAt(anchorPose, pointerPose),
            offset,
            "Grabbing the panel without moving anything moved it.");

        // Carry the pointing controller 30 cm right and turn the wrist. The
        // panel is rigidly attached, so its world pose must be exactly the
        // grab-time pose carried by the same movement.
        var carry = SvrBridge.Core.VrOverlayTransform.Translation(0.3f, 0f, 0f)
                    * SvrBridge.Core.VrOverlayTransform.RotationZ(0.7f);
        var movedPointer = carry * pointerPose;
        var expectedWorld = carry * (pointerPose * drag.PanelInPointer);
        var actualWorld = anchorPose * drag.OffsetAt(anchorPose, movedPointer);
        AssertClose(
            actualWorld,
            expectedWorld,
            "A rigid grab did not carry the panel with the controller.");

        // The rotation actually arrived. A translation-only drag leaves the
        // panel's rotation block untouched, which is exactly the headset
        // failure this replaced, and it would pass every check above.
        var before = anchorPose * offset;
        Assert(
            !Close(actualWorld.M00, before.M00) || !Close(actualWorld.M01, before.M01),
            "Turning the controller left the panel's orientation unchanged - the drag is translation-only.");

        // Moving the anchor device must leave the panel where it is in the
        // world: it is attached to that device and travels with it already, so
        // a drag that also followed it would move the panel twice over.
        var anchorCarry = SvrBridge.Core.VrOverlayTransform.Translation(0f, -0.2f, 0.4f);
        var movedAnchor = anchorCarry * anchorPose;
        var afterAnchorMove = movedAnchor * drag.OffsetAt(movedAnchor, pointerPose);
        AssertClose(
            afterAnchorMove,
            pointerPose * drag.PanelInPointer,
            "Moving the anchor hand during a drag dragged the panel with it.");
    }

    /// <summary>
    /// <see cref="SvrBridge.Core.VrOverlayTransform.InverseRigid"/> is the one
    /// piece of new arithmetic the grab rests on, and a transposed term in it
    /// would present as a panel flying off on grab rather than as a wrong
    /// number anywhere legible.
    /// </summary>
    private static void TestRigidInverseRoundTrips()
    {
        var transform = SvrBridge.Core.VrOverlayTransform.Translation(0.4f, -1.1f, 2.3f)
                        * SvrBridge.Core.VrOverlayTransform.RotationX(0.6f)
                        * SvrBridge.Core.VrOverlayTransform.RotationY(-1.2f)
                        * SvrBridge.Core.VrOverlayTransform.RotationZ(0.3f);
        AssertClose(
            transform * transform.InverseRigid(),
            SvrBridge.Core.VrOverlayTransform.Identity,
            "A rigid transform composed with its own inverse is not the identity.");
        AssertClose(
            transform.InverseRigid() * transform,
            SvrBridge.Core.VrOverlayTransform.Identity,
            "A rigid inverse is not a left inverse.");
        Assert(
            !SvrBridge.Core.VrOverlayTransform.Translation(float.NaN, 0f, 0f).IsUsable()
            && SvrBridge.Core.VrOverlayTransform.Identity.IsUsable(),
            "A non-finite transform was not rejected.");
    }

    /// <summary>
    /// The placement survives the one hop it makes as text - the OpenVR
    /// worker's command line - and a worker spawned without the argument at
    /// all falls back to the proven default rather than to the origin.
    /// </summary>
    private static void TestOverlayPlacementArgumentRoundTrip()
    {
        // Exact binary fractions, so this proves the round trip rather than
        // the round-trip format's precision - "R" already covers that, and a
        // failure here should mean a lost or reordered element.
        var placement = new SvrBridge.Core.OverlayPlacement(
            SvrBridge.Core.VrOverlayTransform.Translation(0.125f, -0.0625f, -0.375f)
            * SvrBridge.Core.VrOverlayTransform.RotationY(0.5f),
            SvrBridge.Core.VrOverlayTransform.Translation(-0.25f, 0.5f, -1.25f));
        Assert(
            SvrBridge.Core.OverlayPlacement.Parse(placement.ToArgument()).Equals(placement),
            "A chat placement did not survive the worker's command line.");
        Assert(
            SvrBridge.Core.OverlayPlacement.Parse(null).Equals(SvrBridge.Core.OverlayPlacement.Default)
            && SvrBridge.Core.OverlayPlacement.Parse("0.1,0.2").Equals(
                SvrBridge.Core.OverlayPlacement.Default)
            && SvrBridge.Core.OverlayPlacement.Parse(
                    string.Join(",", Enumerable.Repeat("x", 24)))
                .Equals(SvrBridge.Core.OverlayPlacement.Default),
            "A missing or malformed placement argument did not fall back to the proven default.");

        // A placement that arrives non-finite must not be accepted from the
        // command line either - it would be applied before anything else got
        // the chance to reject it.
        var poisoned = string.Join(
            ",",
            SvrBridge.Core.VrOverlayTransform.Translation(float.NaN, 0f, 0f).ToFloats()
                .Concat(SvrBridge.Core.OverlayAnchor.HeadOffset.ToFloats())
                .Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        Assert(
            SvrBridge.Core.OverlayPlacement.Parse(poisoned)
                .Equals(SvrBridge.Core.OverlayPlacement.Default),
            "A non-finite placement argument was accepted.");
    }

    /// <summary>
    /// The move handle hit-tests to the move handle and to nothing else - the
    /// structural rule <see cref="ChatOverlayLayout"/> exists for. Every
    /// corner and the centre resolve to it; every point outside resolves to
    /// nothing, deliberately unlike the dashboard's button rows, where the
    /// gaps belong to the nearest button.
    /// </summary>
    private static void TestChatHandleHitTestMatchesTheDrawnRectangle()
    {
        var buttons = ChatOverlayLayout.Buttons;
        var handle = buttons[ChatOverlayLayout.MoveHandleIndex];

        (float X, float Y)[] inside =
        [
            (handle.Left + (handle.Width / 2f), handle.Top + (handle.Height / 2f)),
            (handle.Left, handle.Top),
            (handle.Right - 1, handle.Top),
            (handle.Left, handle.Bottom - 1),
            (handle.Right - 1, handle.Bottom - 1)
        ];
        foreach (var (x, y) in inside)
        {
            Assert(
                ChatOverlayLayout.IndexAt(buttons, x, y) == ChatOverlayLayout.MoveHandleIndex,
                $"A point inside the move handle ({x}, {y}) did not hit it.");
        }

        (float X, float Y)[] outside =
        [
            (handle.Left - 1, handle.Top + 1),
            (handle.Right, handle.Top + 1),
            (handle.Left + 1, handle.Top - 1),
            (handle.Left + 1, handle.Bottom),
            (0, 0),
            (ChatOverlayLayout.PanelWidth - 1, ChatOverlayLayout.PanelHeight - 1)
        ];
        foreach (var (x, y) in outside)
        {
            Assert(
                ChatOverlayLayout.IndexAt(buttons, x, y) == ChatOverlayLayout.NoButton,
                $"A point outside the move handle ({x}, {y}) hit it anyway.");
        }

        Assert(
            handle.Right <= ChatOverlayLayout.PanelWidth
            && handle.Bottom <= ChatOverlayLayout.PanelHeight
            && handle.Left >= 0
            && handle.Top >= 0,
            "The move handle is partly off the panel, so part of it can never be clicked.");
    }

    /// <summary>
    /// Counts repaints rather than inspecting the highlight, because the bug
    /// this guards against is entirely one of frequency: a per-move repaint
    /// highlights exactly the right control and still drags a 10 Hz panel
    /// through hundreds of WPF renders a second. Asserting the highlight looks
    /// right would pass either way - the same class of mistake as the gaze
    /// animation that eased forever with correct values.
    /// </summary>
    private static void TestChatHoverRepaintsOncePerRectangleCrossed()
    {
        // Two rectangles, so "crossing into a second" is a real crossing
        // rather than a trip through empty space. Production ships one today;
        // the rule has to hold for the table as it grows.
        Rectangle[] buttons = [new(400, 10, 60, 60), new(300, 10, 60, 60)];
        var input = new ChatOverlayInput(buttons);
        input.SetGazing(true);

        Move(input, 410, 20);
        Move(input, 430, 30);
        Move(input, 450, 55);
        Assert(
            input.HoverRepaintCount == 1 && input.HoveredIndex == 0,
            $"Moving within one control asked for {input.HoverRepaintCount} repaints instead of 1.");

        Move(input, 320, 20);
        Move(input, 340, 40);
        Assert(
            input.HoverRepaintCount == 2 && input.HoveredIndex == 1,
            $"Crossing into a second control asked for {input.HoverRepaintCount} repaints instead of 2.");

        Move(input, 100, 400);
        Move(input, 120, 420);
        Assert(
            input.HoverRepaintCount == 3 && input.HoveredIndex == ChatOverlayLayout.NoButton,
            $"Leaving the controls asked for {input.HoverRepaintCount} repaints instead of 3.");

        // The laser leaving the panel is the same transition, reported by
        // SteamVR rather than inferred from a coordinate - and it must not
        // repaint again when hover is already nothing.
        input.Handle(
            new SvrBridge.Core.OverlayMouseEvent(
                SvrBridge.Core.OverlayMouseEventKind.FocusLeave,
                0,
                0,
                0));
        Assert(
            input.HoverRepaintCount == 3,
            "Losing laser focus with nothing hovered asked for a repaint anyway.");

        Assert(
            input.TakeRepaintOwed() && !input.TakeRepaintOwed(),
            "A single hover change was owed either no repaints or more than one.");
    }

    /// <summary>
    /// §B2: input is accepted only while the window is in its gazed-at state,
    /// so an accidental grab needs the wearer to be both looking at the window
    /// and pointing at it. Losing gaze mid-drag abandons the drag rather than
    /// leaving one running on a window that has shrunk away.
    /// </summary>
    private static void TestChatInputIsRejectedOutsideTheGazedState()
    {
        var handle = ChatOverlayLayout.Buttons[ChatOverlayLayout.MoveHandleIndex];
        var centreX = handle.Left + (handle.Width / 2f);
        var centreY = handle.Top + (handle.Height / 2f);
        var input = new ChatOverlayInput();

        Move(input, centreX, centreY);
        Assert(
            input.HoveredIndex == ChatOverlayLayout.NoButton && input.HoverRepaintCount == 0,
            "The window highlighted a control while the wearer was not looking at it.");
        Assert(
            Press(input) == ChatInputOutcome.None && !input.IsHolding,
            "A laser click started a drag while the wearer was not looking at the window.");

        input.SetGazing(true);
        Move(input, centreX, centreY);
        Assert(
            Press(input) == ChatInputOutcome.DragBegan && input.IsHolding,
            "A laser click on the handle did not start a drag while gazing.");

        input.SetGazing(false);
        Assert(
            !input.IsHolding && input.HoveredIndex == ChatOverlayLayout.NoButton,
            "Looking away left a drag running on a window that had shrunk away.");

        // The invariant ChatOverlay's per-tick reconcile depends on: every
        // path that withdraws input also drops the hold, so "a live drag with
        // no live hold" is always a state that can be detected and cancelled.
        // A drag can only end through a release event, and a release event can
        // only arrive while input is on - so a hold that outlived its input
        // would strand the panel on the wearer's hand with no way to let go,
        // which is exactly what shipped and had to be fixed.
        foreach (var withdraw in new (string Name, Action<ChatOverlayInput> Act)[]
                 {
                     ("looking away", i => i.SetGazing(false)),
                     ("the laser leaving the panel", i => i.Handle(
                         new SvrBridge.Core.OverlayMouseEvent(
                             SvrBridge.Core.OverlayMouseEventKind.FocusLeave,
                             0,
                             0,
                             0))),
                     ("an explicit cancel", i => i.CancelDrag())
                 })
        {
            var held = new ChatOverlayInput();
            held.SetGazing(true);
            Move(held, centreX, centreY);
            Assert(
                Press(held) == ChatInputOutcome.DragBegan && held.IsHolding,
                $"Could not set up the {withdraw.Name} case.");

            withdraw.Act(held);
            Assert(
                !held.IsHolding,
                $"After {withdraw.Name} the handle was still held, so the drag could never be released.");
        }
    }

    /// <summary>
    /// The grab and release signalling, which is all the router owns now that
    /// the drag arithmetic lives in <see cref="SvrBridge.Core.OverlayDrag"/>:
    /// only the handle starts a grab, and a release is only reported for a
    /// grab that was actually started.
    /// </summary>
    private static void TestOnlyTheHandleStartsAndEndsAGrab()
    {
        var handle = ChatOverlayLayout.Buttons[ChatOverlayLayout.MoveHandleIndex];
        var centreX = handle.Left + (handle.Width / 2f);
        var centreY = handle.Top + (handle.Height / 2f);
        var input = new ChatOverlayInput();
        input.SetGazing(true);

        // Chat text, not a control: this is where most of the panel is, and
        // grabbing the window every time the wearer points at a message would
        // make it unreadable.
        Move(input, 40, 700);
        Assert(
            Press(input) == ChatInputOutcome.None && !input.IsHolding,
            "A laser click on the chat text started a grab.");
        Assert(
            Release(input) == ChatInputOutcome.None,
            "A release with nothing held was reported as the end of a drag.");

        Move(input, centreX, centreY);
        Assert(
            Press(input) == ChatInputOutcome.DragBegan && input.IsHolding,
            "A laser click on the move handle did not start a grab.");
        Assert(
            Release(input) == ChatInputOutcome.DragEnded && !input.IsHolding,
            "Releasing the trigger did not end the grab.");
        Assert(
            Release(input) == ChatInputOutcome.None,
            "A second release reported a second drag ending.");

        // Losing laser focus mid-grab has to let go: the wearer has pointed
        // away, and a panel that kept following would be being dragged by a
        // laser that is no longer on it.
        Move(input, centreX, centreY);
        Press(input);
        input.Handle(
            new SvrBridge.Core.OverlayMouseEvent(
                SvrBridge.Core.OverlayMouseEventKind.FocusLeave,
                0,
                0,
                0));
        Assert(!input.IsHolding, "The laser leaving the panel left the handle held.");
    }

    /// <summary>
    /// §B5's rule, applied to a hand drag: placing the window by hand is an
    /// explicit user edit, so it wins over an active Streamer.bot anchor
    /// override rather than leaving one in force that a later <c>reset</c>
    /// could use to move a hand-placed window somewhere else.
    /// </summary>
    private static void TestHandPlacedOffsetClearsAStreamerBotOverride()
    {
        var savedDefault = new SvrBridge.Core.OverlayAnchor(
            SvrBridge.Core.OverlayAnchorMode.Controller,
            SvrBridge.Core.OverlayAnchorHand.Left);
        var state = new SvrBridge.Core.SurfaceOverrideState(savedDefault);
        state.Apply(
            new SvrBridge.Core.StreamerBotEventPayload
            {
                Command = "anchor",
                RequestedAnchorMode = SvrBridge.Core.OverlayAnchorMode.Head
            });
        Assert(
            state.AnchorOverride is not null
            && state.EffectiveAnchor.Mode == SvrBridge.Core.OverlayAnchorMode.Head,
            "The anchor control command under test did not take effect.");

        // The wearer drags the window while it is on the headset anchor.
        state.AdoptEffectiveAnchorAsSavedDefault();
        Assert(
            state.AnchorOverride is null
            && state.SavedDefault.Mode == SvrBridge.Core.OverlayAnchorMode.Head,
            "A hand-placed window left the Streamer.bot anchor override in force.");

        state.Apply(new SvrBridge.Core.StreamerBotEventPayload { Command = "reset" });
        Assert(
            state.EffectiveAnchor.Mode == SvrBridge.Core.OverlayAnchorMode.Head,
            "A later reset moved the hand-placed window off the anchor it was placed on.");
    }

    private static void Move(ChatOverlayInput input, float x, float y) =>
        input.Handle(
            new SvrBridge.Core.OverlayMouseEvent(
                SvrBridge.Core.OverlayMouseEventKind.Move,
                x,
                y,
                0));

    private static ChatInputOutcome Press(ChatOverlayInput input) =>
        input.Handle(
            new SvrBridge.Core.OverlayMouseEvent(
                SvrBridge.Core.OverlayMouseEventKind.ButtonDown,
                0,
                0,
                0));

    private static ChatInputOutcome Release(ChatOverlayInput input) =>
        input.Handle(
            new SvrBridge.Core.OverlayMouseEvent(
                SvrBridge.Core.OverlayMouseEventKind.ButtonUp,
                0,
                0,
                0));

    private static bool Close(float actual, float expected) => MathF.Abs(actual - expected) < 1e-4f;

    /// <summary>
    /// Element-wise transform comparison. Composing four rotations and an
    /// inverse accumulates float error well past what exact equality tolerates,
    /// and an exact check here would fail for reasons that say nothing about
    /// whether the grab is right.
    /// </summary>
    private static void AssertClose(
        SvrBridge.Core.VrOverlayTransform actual,
        SvrBridge.Core.VrOverlayTransform expected,
        string message)
    {
        var actualValues = actual.ToFloats();
        var expectedValues = expected.ToFloats();
        for (var index = 0; index < actualValues.Length; index++)
        {
            Assert(
                Close(actualValues[index], expectedValues[index]),
                $"{message} (element {index}: {actualValues[index]} vs {expectedValues[index]})");
        }
    }

    /// <summary>
    /// Covers the control-command semantics from §B3 of the Phase 4 plan
    /// without OpenVR: each command applies the expected override,
    /// <c>reset</c> restores the saved default, and an unrecognised command
    /// or a malformed <c>anchor</c> command changes nothing.
    /// </summary>
    private static void TestSurfaceOverrideStateAppliesAndResetsControlCommands()
    {
        var savedDefault = new SvrBridge.Core.OverlayAnchor(
            SvrBridge.Core.OverlayAnchorMode.Controller,
            SvrBridge.Core.OverlayAnchorHand.Left);
        var state = new SvrBridge.Core.SurfaceOverrideState(savedDefault);

        Assert(
            state.EffectiveAnchor.Equals(savedDefault) && !state.Hidden,
            "A fresh override state did not start at the saved default, visible.");

        state.Apply(ControlPayload("hide"));
        Assert(state.Hidden, "The hide command did not set the hidden override.");

        state.Apply(ControlPayload("show"));
        Assert(!state.Hidden, "The show command did not clear the hidden override.");

        state.Apply(
            ControlPayload("anchor") with
            {
                RequestedAnchorMode = SvrBridge.Core.OverlayAnchorMode.Head,
                RequestedAnchorHand = SvrBridge.Core.OverlayAnchorHand.Right
            });
        Assert(
            state.EffectiveAnchor.Mode == SvrBridge.Core.OverlayAnchorMode.Head,
            "The anchor command did not switch the effective anchor to head mode.");

        state.Apply(ControlPayload("anchor")); // no mode given - malformed
        Assert(
            state.EffectiveAnchor.Mode == SvrBridge.Core.OverlayAnchorMode.Head,
            "A malformed anchor command (no mode) changed the effective anchor.");

        state.Apply(ControlPayload("hide"));
        state.Apply(ControlPayload("reset"));
        Assert(
            state.EffectiveAnchor.Equals(savedDefault) && !state.Hidden,
            "Reset did not restore the saved default anchor and clear the hidden override.");

        state.Apply(ControlPayload("bogus-command"));
        Assert(
            state.EffectiveAnchor.Equals(savedDefault) && !state.Hidden,
            "An unrecognised command changed the override state instead of being ignored.");

        // A VR settings-page edit (§B5 of the Phase 4b plan): while a
        // Streamer.bot anchor override is active, an explicit user edit must
        // win, not appear to do nothing, and must also become the new
        // default a later Streamer.bot "reset" returns to.
        state.Apply(
            ControlPayload("anchor") with
            {
                RequestedAnchorMode = SvrBridge.Core.OverlayAnchorMode.Head,
                RequestedAnchorHand = SvrBridge.Core.OverlayAnchorHand.Right
            });
        var vrChosenAnchor = new SvrBridge.Core.OverlayAnchor(
            SvrBridge.Core.OverlayAnchorMode.Controller,
            SvrBridge.Core.OverlayAnchorHand.Right);
        state.SetSavedDefaultAnchor(vrChosenAnchor);
        Assert(
            state.EffectiveAnchor.Equals(vrChosenAnchor) && state.AnchorOverride is null,
            "A VR settings edit did not take effect immediately and clear the active override.");

        state.Apply(ControlPayload("reset"));
        Assert(
            state.EffectiveAnchor.Equals(vrChosenAnchor),
            "Reset after a VR settings edit did not return to the new default the wearer just chose.");
    }

    private static SvrBridge.Core.StreamerBotEventPayload ControlPayload(string command) =>
        new()
        {
            Target = SvrBridge.Core.StreamerBotEventTarget.Control,
            Command = command
        };

    /// <summary>
    /// Every platform icon this build ships must actually be findable by the
    /// source name it is named for.
    /// <para>
    /// This exists because the first version of the lookup missed all of them
    /// and nothing said so. MSBuild derives a manifest resource name from a
    /// file's path but replaces characters that are not valid in an
    /// identifier, so <c>assets\source-icons\twitch.png</c> embeds as
    /// <c>...assets.source_icons.twitch.png</c> - underscore, not hyphen. The
    /// resolver's prefix had the hyphen, every icon embedded correctly, every
    /// lookup missed, and the chip fallback rendered a perfectly good-looking
    /// picker with no icons in it and no error anywhere. Reading the manifest
    /// rather than hardcoding the expected names is the point: this fails the
    /// moment the two disagree, whatever the reason.
    /// </para>
    /// <para>
    /// Shipping no icons at all is a valid state - the chip fallback is the
    /// design - so an empty icon set passes. What cannot pass is shipping one
    /// the app then fails to find.
    /// </para>
    /// </summary>
    private static void TestSourceIconResourceFindsEveryEmbeddedIcon()
    {
        var embedded = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var name in embedded)
        {
            var source = Path.GetFileNameWithoutExtension(name).Split('.').Last();
            Assert(
                SourceIconResource.TryResolve(source) is not null,
                $"The embedded icon \"{name}\" was not resolvable as source \"{source}\" - "
                + "the resolver's resource prefix and the one MSBuild generated disagree.");
        }

        Assert(
            SourceIconResource.TryResolve("Zorblatt") is null,
            "A source with no icon file resolved to one, so the chip fallback would never be reached.");
        Assert(
            SourceIconResource.TryResolve("") is null && SourceIconResource.TryResolve(null) is null,
            "An empty source name threw or resolved to an icon.");
    }

    /// <summary>
    /// The regression guard for the failure that got two earlier versions of
    /// this picker rejected live. Both listed every available event and built
    /// one control per entry; against the 467 events a real Streamer.bot
    /// instance reports, that was hundreds of live WinForms controls each
    /// triggering its own relayout, and it read as the whole app freezing on
    /// every keystroke.
    /// <para>
    /// What is asserted here is the structural property, not a timing: the
    /// number of controls built has no relationship to the catalog size. A
    /// generous time bound comes with it only to catch a future change that
    /// reintroduces per-entry work somewhere off to the side - it is a
    /// tripwire, not a benchmark, and is loose enough not to fail on a busy
    /// machine.
    /// </para>
    /// </summary>
    private static void TestNotificationEventPickerStaysBoundedAtARealCatalogSize()
    {
        var catalog = BuildRealisticEventCatalog();
        Assert(
            catalog.Count >= 187,
            "The realistic catalog fixture is smaller than the event count this design has to survive.");

        using var picker = new NotificationEventPicker();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        picker.SetCatalog(catalog);

        Assert(
            picker.RenderedResultRowCount
            == SvrBridge.Core.StreamerBotEventSearch.DefaultResultLimit,
            "An unfiltered picker built more rows than the cap, so the control count tracks the catalog.");

        // Typing "follow" one letter at a time, the way the rebuilt-per-
        // keystroke failure was actually reached.
        foreach (var query in new[] { "f", "fo", "fol", "foll", "follo", "follow" })
        {
            picker.ApplySearchNow(query);
            Assert(
                picker.RenderedResultRowCount
                <= SvrBridge.Core.StreamerBotEventSearch.DefaultResultLimit,
                $"Searching \"{query}\" built more rows than the cap.");
        }

        Assert(
            picker.RenderedResultRowCount > 0,
            "Searching a term the catalog definitely contains rendered nothing.");

        picker.ApplySearchNow("nothingmatchesthis");
        Assert(
            picker.RenderedResultRowCount == 0,
            "A query matching nothing still built result rows.");

        clock.Stop();
        Assert(
            clock.ElapsedMilliseconds < 5000,
            $"Filling and searching a {catalog.Count}-event picker took {clock.ElapsedMilliseconds}ms - "
            + "something is doing per-catalog-entry work again.");
    }

    /// <summary>
    /// The enabled list is the setting: it round-trips through
    /// <see cref="UserSettings.EnabledEvents"/>, adding and removing changes
    /// what a <c>Subscribe</c> request would carry, and - the part with a real
    /// failure mode behind it - an enabled event survives a
    /// <c>GetEvents</c> response that no longer mentions it.
    /// <para>
    /// That last case is not hypothetical. Streamer.bot reports what its
    /// currently installed integrations expose, so a key can vanish because a
    /// fetch failed or an integration was reloading. Reconciling the enabled
    /// list against the response would turn the wearer's alerts off with
    /// nothing on screen to explain it.
    /// </para>
    /// </summary>
    private static void TestNotificationEventPickerRoundTripsAndKeepsUnreportedEvents()
    {
        using var picker = new NotificationEventPicker();
        var changes = 0;
        picker.EnabledKeysChanged += () => changes++;

        picker.SetEnabledKeys(["Twitch.Follow", "Kick.Subscription"]);
        Assert(
            changes == 0,
            "Applying saved settings raised a change back at the caller that was applying them.");
        Assert(
            picker.EnabledKeys.SequenceEqual(["Twitch.Follow", "Kick.Subscription"]),
            "The enabled keys did not round-trip through the picker unchanged.");
        Assert(
            picker.RenderedEnabledRowCount == 2,
            "An enabled key with no catalog behind it yet did not get a row.");

        // A catalog that knows Twitch.Follow and has never heard of
        // Kick.Subscription.
        var catalog = BuildRealisticEventCatalog()
            .Where(entry => entry.Source != "Kick")
            .ToArray();
        picker.SetCatalog(catalog);
        Assert(
            picker.EnabledKeys.Contains("Kick.Subscription"),
            "An enabled event this catalog does not report was silently dropped.");
        Assert(
            picker.RenderedEnabledRowCount == 2,
            "An enabled event this catalog does not report lost its row, so it could not be removed.");

        picker.Add("Twitch.GiftSub");
        Assert(
            changes == 1 && picker.EnabledKeys.Contains("Twitch.GiftSub"),
            "Adding an event did not enable it and report the change exactly once.");
        picker.Add("Twitch.GiftSub");
        Assert(
            changes == 1 && picker.EnabledKeys.Count(key => key == "Twitch.GiftSub") == 1,
            "Adding an already-enabled event duplicated it or reported a change.");

        picker.Remove("twitch.follow");
        Assert(
            changes == 2 && !picker.EnabledKeys.Contains("Twitch.Follow"),
            "Removing an event by a differently-cased key did not take it out of the enabled list.");
        picker.Remove("Twitch.NeverEnabled");
        Assert(
            changes == 2,
            "Removing an event that was never enabled reported a change.");

        Assert(
            picker.EnabledKeys.SequenceEqual(["Kick.Subscription", "Twitch.GiftSub"]),
            "The final enabled set was not what adding and removing should have left behind.");

        // Nothing is on by default - an upgrading user must not suddenly
        // start receiving alerts they never chose.
        using var fresh = new NotificationEventPicker();
        fresh.SetCatalog(catalog);
        Assert(
            fresh.EnabledKeys.Count == 0,
            "A picker built from a catalog alone enabled something by itself.");
    }

    /// <summary>
    /// A catalog the shape and size of a real one, from a live
    /// <c>GetEvents</c> capture (Streamer.bot 1.0.4): 467 events across its
    /// real source names. Real platform names appear here because this is a
    /// test fixture - production code contains none, which is what
    /// <see cref="SvrBridge.Core.StreamerBotSourceChip"/> exists to make
    /// possible.
    /// </summary>
    private static IReadOnlyList<SvrBridge.Core.StreamerBotEventDescriptor> BuildRealisticEventCatalog()
    {
        var sources = new (string Source, int Count, string[] Real)[]
        {
            ("Twitch", 137, ["Follow", "Cheer", "Sub", "ReSub", "GiftSub", "Raid", "ChatMessage"]),
            ("Elgato", 90, ["ActionTriggered"]),
            ("YouTube", 29, ["Message", "SuperChat", "NewSponsor"]),
            ("Kick", 21, ["Follow", "Subscription", "ChatMessage"]),
            ("Trovo", 16, ["Follow"]),
            ("Misc", 13, ["TimedAction"]),
            ("Fourthwall", 13, ["OrderPlaced"]),
            ("MeldStudio", 12, ["SceneChanged"]),
            ("VTubeStudio", 11, ["ModelLoaded"]),
            ("Obs", 9, ["SceneChanged"]),
            ("CrowdControl", 9, ["EffectRedeemed"]),
            ("ThrowingSystem", 8, ["ObjectThrown"]),
            ("StreamlabsDesktop", 7, ["SceneChanged"]),
            ("Streamlabs", 6, ["Donation"]),
            ("Application", 6, ["Started"]),
            ("StreamElements", 5, ["Tip"]),
            ("Kofi", 5, ["Donation"]),
            ("Patreon", 5, ["PledgeCreated"]),
            ("HypeRate", 4, ["HeartRatePulse"]),
            ("StreamDeck", 4, ["ButtonPressed"]),
            ("Zorblatt", 4, ["SomethingHappened"]),
            ("Pallygg", 3, ["Tip"]),
            ("DonorDrive", 3, ["Donation"]),
            ("General", 1, ["Custom"])
        };

        var catalog = new List<SvrBridge.Core.StreamerBotEventDescriptor>();
        foreach (var (source, count, real) in sources)
        {
            for (var index = 0; index < count; index++)
            {
                catalog.Add(
                    new SvrBridge.Core.StreamerBotEventDescriptor(
                        source,
                        index < real.Length ? real[index] : $"LongTailEvent{index:D3}"));
            }
        }

        return catalog;
    }

    /// <summary>
    /// Covers §B1's in-app chat test harness: the burst, ring-buffer-fill,
    /// long-message, multi-badge and unknown-emote injectors each produce the
    /// shape the manual headset matrix expects, without needing SteamVR to
    /// run them through.
    /// </summary>
    private static void TestChatDeveloperInjectorProducesExpectedMessages()
    {
        var burst = TrayApplicationContext.BuildChatBurstMessages();
        Assert(
            burst.Count == 12
            && burst.All(message => message.Target == SvrBridge.Core.StreamerBotEventTarget.Chat),
            "The chat burst injector did not produce 12 chat-target messages.");

        var fill = TrayApplicationContext.BuildRingBufferFillMessages();
        Assert(
            fill.Count > SvrBridge.Core.ChatRingBuffer.DefaultCapacity,
            "The ring-buffer fill injector did not produce enough messages to overflow the cap.");

        var buffer = new SvrBridge.Core.ChatRingBuffer();
        foreach (var message in fill)
        {
            buffer.Append(message);
        }

        var evicted = fill.Count - SvrBridge.Core.ChatRingBuffer.DefaultCapacity;
        Assert(
            buffer.Snapshot().Count == SvrBridge.Core.ChatRingBuffer.DefaultCapacity
            && buffer.Snapshot()[0].Text.EndsWith($"#{evicted + 1}.", StringComparison.Ordinal),
            "Filling the ring buffer past its cap did not evict the oldest fill messages first.");

        var longMessage = TrayApplicationContext.BuildLongChatMessage();
        Assert(
            longMessage.Text.Length >= 300 && !longMessage.Text.Contains(' '),
            "The long-message injector did not produce an unbroken 300+ character string.");

        var multiBadge = TrayApplicationContext.BuildMultiBadgeChatMessage();
        Assert(multiBadge.Badges.Count == 3, "The multi-badge injector did not attach three badges.");

        var unknownEmote = TrayApplicationContext.BuildUnknownEmoteChatMessage();
        Assert(
            unknownEmote.EmoteNames.Count == 1,
            "The unknown-emote injector did not tag an emote name for the renderer to miss on.");
    }

    /// <summary>
    /// A "chat" command's <c>StreamerBotEventPayload</c> crosses process
    /// boundaries as JSON, from <c>OpenVrWorkerSession.SendCommand</c> (case
    /// -insensitive, camelCase) to <c>OpenVrWorker.RunAsync</c>'s receiving
    /// side (camelCase only, not case-insensitive). Every other field of the
    /// payload uses plain init-only properties, which System.Text.Json
    /// deserialises via a parameterless constructor and property setters -
    /// but <c>ChatBadge</c> is a positional record, which System.Text.Json
    /// instead deserialises by matching JSON properties to constructor
    /// *parameter* names. That path had never been exercised end to end by
    /// an automated test - only implicitly by live Twitch badges reaching
    /// the headset - so this proves the exact options pair used in
    /// production round-trips a multi-badge chat message without dropping
    /// any of it.
    /// </summary>
    private static void TestChatCommandJsonRoundTripPreservesBadgesAndEmotes()
    {
        var sendOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var receiveOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        var original = TrayApplicationContext.BuildMultiBadgeChatMessage();
        var command = new OpenVrWorkerCommand("chat", Payload: original);

        var json = System.Text.Json.JsonSerializer.Serialize(command, sendOptions);
        var received = System.Text.Json.JsonSerializer.Deserialize<OpenVrWorkerCommand>(json, receiveOptions);

        Assert(
            received?.Payload is not null,
            "The chat command's payload was lost crossing the worker command channel.");
        Assert(
            received!.Payload!.Badges.Count == original.Badges.Count
            && received.Payload.Badges.SequenceEqual(original.Badges),
            "The chat command's badges did not survive the worker command channel's JSON round trip.");

        var unknownEmote = TrayApplicationContext.BuildUnknownEmoteChatMessage();
        var emoteJson = System.Text.Json.JsonSerializer.Serialize(
            new OpenVrWorkerCommand("chat", Payload: unknownEmote),
            sendOptions);
        var receivedEmote = System.Text.Json.JsonSerializer
            .Deserialize<OpenVrWorkerCommand>(emoteJson, receiveOptions)
            ?.Payload;
        Assert(
            receivedEmote is not null
            && receivedEmote.EmoteNames.SequenceEqual(unknownEmote.EmoteNames),
            "The chat command's emote names did not survive the worker command channel's JSON round trip.");
    }

    /// <summary>
    /// A hand-dragged placement crosses the worker message channel as JSON on
    /// its way back to the tray for saving. A record struct with a
    /// parameterless constructor that silently deserialised to zeros would put
    /// the chat window at the controller's own origin, inside the wearer's
    /// hand, on the first VR settings change - a failure that looks nothing
    /// like a serialisation bug from the headset.
    /// </summary>
    private static void TestChatPlacementSurvivesTheWorkerMessageChannel()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var receiveOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        var placement = new SvrBridge.Core.OverlayPlacement(
            SvrBridge.Core.VrOverlayTransform.Translation(0.07f, 0.13f, -0.21f)
            * SvrBridge.Core.VrOverlayTransform.RotationX(0.3f),
            SvrBridge.Core.VrOverlayTransform.Translation(-0.02f, -0.3f, -0.9f));
        var settings = new SvrBridge.Core.VrSettingsSnapshot(
            true,
            new SvrBridge.Core.OverlayAnchor(
                SvrBridge.Core.OverlayAnchorMode.Controller,
                SvrBridge.Core.OverlayAnchorHand.Right),
            placement,
            0.8,
            1.2,
            SvrBridge.Core.GazeSensitivity.Tight,
            // Deliberately true: the default is false, so a field dropped in
            // transit would still round-trip if this matched the default.
            true,
            true,
            SvrBridge.Core.OverlayAnchor.Head,
            0.7,
            0.6,
            placement,
            new SvrBridge.Core.NotificationAppearanceSettings(
                "#112233",
                "#FFEEDD",
                "#00FF00",
                4200,
                SvrBridge.Core.NotificationTransition.Slide,
                SvrBridge.Core.NotificationSlideEdge.Left,
                "C:\\templates\\banner.png",
                0.5,
                12));

        var json = System.Text.Json.JsonSerializer.Serialize(
            new OpenVrWorkerMessage("vrSettingsChanged", VrSettingsChanged: settings),
            options);
        var received = System.Text.Json.JsonSerializer
            .Deserialize<OpenVrWorkerMessage>(json, receiveOptions)
            ?.VrSettingsChanged;

        Assert(
            received is not null && received.ChatPlacement.Equals(placement),
            "A hand-dragged chat placement did not survive the worker message channel.");
        Assert(
            received!.ChatAnchor.Equals(settings.ChatAnchor)
            && received.GazeSensitivity == settings.GazeSensitivity
            && received.ChatGazeScaleEnabled,
            "The rest of the VR settings snapshot did not survive the worker message channel.");
        Assert(
            received.NotificationPlacement.Equals(placement)
            && received.NotificationAppearance == settings.NotificationAppearance,
            "The Phase 7 notification placement/appearance fields did not survive the worker message channel.");
    }

    /// <summary>
    /// Guards the exact regression a live headset session caught:
    /// <c>NotificationPlacement</c> and <c>NotificationAppearance</c> were
    /// both missing from the hand-written <c>with</c> expression that folds
    /// a VR-reported <see cref="SvrBridge.Core.VrSettingsSnapshot"/> back into
    /// saved settings, so every VR-side placement drag or reset applied live
    /// and then silently reverted to default on the very next restart. Every
    /// field of the snapshot is given a value that differs from
    /// <see cref="UserSettings"/>'s own defaults, so a field the merge forgot
    /// would show up as still-default rather than accidentally matching.
    /// </summary>
    private static void TestMergeVrSettingsSnapshotPersistsEveryField()
    {
        var previous = new UserSettings();
        var placement = new SvrBridge.Core.OverlayPlacement(
            SvrBridge.Core.VrOverlayTransform.Translation(0.11f, 0.22f, -0.33f)
            * SvrBridge.Core.VrOverlayTransform.RotationY(0.4f),
            SvrBridge.Core.VrOverlayTransform.Translation(-0.44f, -0.55f, -0.66f)
            * SvrBridge.Core.VrOverlayTransform.RotationX(0.2f));
        var appearance = new SvrBridge.Core.NotificationAppearanceSettings(
            "#ABCDEF",
            "#123456",
            "#654321",
            9999,
            SvrBridge.Core.NotificationTransition.ScalePop,
            SvrBridge.Core.NotificationSlideEdge.Right,
            "C:\\some\\template.png",
            0.33,
            22,
            1234,
            567);
        var snapshot = new SvrBridge.Core.VrSettingsSnapshot(
            true,
            new SvrBridge.Core.OverlayAnchor(SvrBridge.Core.OverlayAnchorMode.Head, SvrBridge.Core.OverlayAnchorHand.Right),
            placement,
            0.81,
            1.44,
            SvrBridge.Core.GazeSensitivity.Tight,
            true,
            true,
            new SvrBridge.Core.OverlayAnchor(SvrBridge.Core.OverlayAnchorMode.Controller, SvrBridge.Core.OverlayAnchorHand.Right),
            0.71,
            1.66,
            placement,
            appearance);

        var updated = TrayApplicationContext.MergeVrSettingsSnapshot(previous, snapshot);

        Assert(updated.ChatEnabled, "ChatEnabled was not merged.");
        Assert(updated.ChatAnchorMode == SvrBridge.Core.OverlayAnchorMode.Head, "ChatAnchorMode was not merged.");
        Assert(updated.ChatAnchorHand == SvrBridge.Core.OverlayAnchorHand.Right, "ChatAnchorHand was not merged.");
        Assert(updated.ChatPlacement.Equals(placement), "ChatPlacement was not merged.");
        Assert(updated.ChatOpacity == 0.81, "ChatOpacity was not merged.");
        Assert(updated.ChatSizeScale == 1.44, "ChatSizeScale was not merged.");
        Assert(updated.GazeSensitivity == SvrBridge.Core.GazeSensitivity.Tight, "GazeSensitivity was not merged.");
        Assert(updated.ChatGazeScaleEnabled, "ChatGazeScaleEnabled was not merged.");
        Assert(updated.NotificationsEnabled, "NotificationsEnabled was not merged.");
        Assert(
            updated.NotificationAnchorMode == SvrBridge.Core.OverlayAnchorMode.Controller,
            "NotificationAnchorMode was not merged.");
        Assert(
            updated.NotificationAnchorHand == SvrBridge.Core.OverlayAnchorHand.Right,
            "NotificationAnchorHand was not merged.");
        Assert(updated.NotificationOpacity == 0.71, "NotificationOpacity was not merged.");
        Assert(updated.NotificationSizeScale == 1.66, "NotificationSizeScale was not merged.");

        // The two fields the live regression was actually about.
        Assert(
            updated.NotificationPlacement.Equals(placement),
            "NotificationPlacement was not merged - this is the exact bug a live headset session caught: "
            + "a VR-side placement drag applied live and then reverted to default on restart.");
        Assert(
            updated.NotificationAppearance == appearance,
            "NotificationAppearance was not merged - every Phase 7 appearance field would revert on restart.");
        Assert(
            updated.NotificationPanelWidth == 1234 && updated.NotificationPanelHeight == 567,
            "The configured panel size was not merged, so a resized panel would revert on restart.");

        TestPanelSizeReadsAnOlderSettingsFileAsTheProvenDefault();
    }

    /// <summary>
    /// A settings file written before the panel size was configurable
    /// deserialises those fields to zero, and zero is not a panel - it is a
    /// texture with no area. System.Text.Json cannot tell "written by an
    /// older shape" from "legitimately zero", so the type has to recognise
    /// its own invalid values, which is what
    /// <see cref="SvrBridge.Core.NotificationAppearanceSettings.SafePanelWidth"/>
    /// is for.
    /// </summary>
    private static void TestPanelSizeReadsAnOlderSettingsFileAsTheProvenDefault()
    {
        var upgraded = SvrBridge.Core.NotificationAppearanceSettings.Default with
        {
            PanelWidthPixels = 0,
            PanelHeightPixels = 0
        };
        Assert(
            upgraded.SafePanelWidth == SvrBridge.Core.NotificationAppearanceSettings.DefaultPanelWidth
            && upgraded.SafePanelHeight == SvrBridge.Core.NotificationAppearanceSettings.DefaultPanelHeight,
            "A settings file predating the panel size did not fall back to the proven default, so an "
            + "upgrading user would get a zero-area notification.");

        var absurd = SvrBridge.Core.NotificationAppearanceSettings.Default with
        {
            PanelWidthPixels = 999999,
            PanelHeightPixels = 1
        };
        Assert(
            absurd.SafePanelWidth == SvrBridge.Core.NotificationAppearanceSettings.MaximumPanelDimension
            && absurd.SafePanelHeight == SvrBridge.Core.NotificationAppearanceSettings.MinimumPanelDimension,
            "An out-of-range panel size was not clamped - this texture is re-uploaded every animation "
            + "frame, so an unbounded value costs real per-frame bandwidth.");

        Assert(
            SvrBridge.Core.NotificationAppearanceSettings.Default.SafePanelWidth == 900
            && SvrBridge.Core.NotificationAppearanceSettings.Default.SafePanelHeight == 260,
            "The default panel size changed from the 900x260 every live headset test to date was run at.");
    }

    /// <summary>
    /// A desktop settings save must restart the runtime for anything
    /// connection- or shortcut-shaped, but apply live for the Phase 4b
    /// appearance/anchor/enable fields alone - otherwise every opacity or
    /// size trackbar drag would restart the worker mid-session, exactly the
    /// disruption the VR settings page's live-apply design exists to avoid.
    /// </summary>
    private static void TestRequiresRuntimeRestartDistinguishesLiveAppliableChanges()
    {
        var baseline = new UserSettings
        {
            StreamerBotAddress = "ws://127.0.0.1:8080/1",
            ChatOpacity = 0.95,
            ChatSizeScale = 1.0,
            GazeSensitivity = SvrBridge.Core.GazeSensitivity.Normal,
            NotificationOpacity = 1.0,
            NotificationSizeScale = 1.0
        };

        Assert(
            !TrayApplicationContext.RequiresRuntimeRestart(baseline, baseline with { ChatEnabled = true }),
            "Toggling chat on required a restart.");
        Assert(
            !TrayApplicationContext.RequiresRuntimeRestart(
                baseline,
                baseline with { ChatOpacity = 0.6, ChatSizeScale = 1.4 }),
            "Changing opacity/size required a restart.");
        Assert(
            !TrayApplicationContext.RequiresRuntimeRestart(
                baseline,
                baseline with
                {
                    ChatAnchorMode = SvrBridge.Core.OverlayAnchorMode.Head,
                    GazeSensitivity = SvrBridge.Core.GazeSensitivity.Tight
                }),
            "Changing anchor mode or gaze sensitivity required a restart.");

        Assert(
            TrayApplicationContext.RequiresRuntimeRestart(
                baseline,
                baseline with { StreamerBotAddress = "ws://192.168.1.50:8080/1" }),
            "Changing the Streamer.bot address did not require a restart.");
        Assert(
            TrayApplicationContext.RequiresRuntimeRestart(baseline, baseline with { Password = "changed" }),
            "Changing the password did not require a restart.");
        Assert(
            TrayApplicationContext.RequiresRuntimeRestart(
                baseline,
                baseline with
                {
                    Shortcuts =
                    [
                        new SvrBridge.Core.ShortcutConfig
                        {
                            Id = "new-shortcut",
                            ActionName = "Some action"
                        }
                    ]
                }),
            "Adding a shortcut did not require a restart.");

        // A freshly-read UserSettings always carries a brand new Shortcuts
        // array instance, even when its contents are identical - this must
        // not be mistaken for a change (a naive record-equality diff would).
        var same = baseline with { Shortcuts = baseline.Shortcuts.ToArray() };
        Assert(
            !TrayApplicationContext.RequiresRuntimeRestart(baseline, same),
            "An unchanged settings object with a new Shortcuts array instance was seen as requiring a restart.");

        // §B2's toggles change the Subscribe request itself, which only a
        // full restart (and the RestartEventStreamLockedAsync it reaches)
        // rebuilds - the live-apply path never touches the event stream.
        Assert(
            TrayApplicationContext.RequiresRuntimeRestart(
                baseline,
                baseline with { EnabledEvents = ["Twitch.Follow"] }),
            "Enabling a notification event did not require a restart.");
        Assert(
            TrayApplicationContext.RequiresRuntimeRestart(
                baseline,
                baseline with
                {
                    EventTemplates = new Dictionary<string, string> { ["Twitch.Follow"] = "{targetUser.name}!" }
                }),
            "Changing a per-event template override did not require a restart.");
        Assert(
            TrayApplicationContext.RequiresRuntimeRestart(
                baseline,
                baseline with { ShowTestEvents = false }),
            "Toggling test-event visibility did not require a restart.");

        // Same hazard as the Shortcuts case above, for the two §B2 collections:
        // a freshly-read EnabledEvents/EventTemplates is a new instance every
        // call even when unchanged, and order must not matter either.
        var sameEvents = baseline with
        {
            EnabledEvents = new List<string> { "Twitch.Follow", "Twitch.Raid" },
            EventTemplates = new Dictionary<string, string> { ["Twitch.Follow"] = "hi" }
        };
        var reorderedEvents = sameEvents with
        {
            EnabledEvents = new List<string> { "Twitch.Raid", "Twitch.Follow" }
        };
        Assert(
            !TrayApplicationContext.RequiresRuntimeRestart(sameEvents, reorderedEvents),
            "Reordering the same enabled-events selection was seen as requiring a restart.");
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
    /// The most important test in Phase 7: proves each of the three named
    /// transitions converges on <see cref="SvrBridge.Core.NotificationPlayer"/>'s
    /// own bounded fade timeline and then issues <b>zero</b> further calls for
    /// the whole Holding phase - the property behind the hard rule that a
    /// non-converging animation looks perfectly still yet issues overlay calls
    /// forever, the exact bug <c>ChatOverlay.AnimateGaze</c> shipped with once.
    /// A visual check cannot catch this; only counting calls does.
    /// </summary>
    private static void TestNotificationTransitionAnimatorConvergesAndStopsIssuingCalls()
    {
        AssertTransitionIsSilentAtSteadyState(
            SvrBridge.Core.NotificationTransition.Fade,
            SvrBridge.Core.NotificationSlideEdge.Bottom);
        AssertTransitionIsSilentAtSteadyState(
            SvrBridge.Core.NotificationTransition.Slide,
            SvrBridge.Core.NotificationSlideEdge.Left);
        AssertTransitionIsSilentAtSteadyState(
            SvrBridge.Core.NotificationTransition.ScalePop,
            SvrBridge.Core.NotificationSlideEdge.Top);

        // Re-asserting the same progress every tick (exactly what a real
        // caller does, since it recomputes progress from the player on every
        // call) must not itself keep re-opening a value already reported.
        var animator = new SvrBridge.Core.NotificationTransitionAnimator();
        Assert(
            animator.Advance(
                SvrBridge.Core.NotificationTransition.Fade,
                SvrBridge.Core.NotificationSlideEdge.Bottom,
                1f,
                0.5f,
                out _),
            "A fresh transition animator reported no change on its first call.");
        for (var index = 0; index < 50; index++)
        {
            Assert(
                !animator.Advance(
                    SvrBridge.Core.NotificationTransition.Fade,
                    SvrBridge.Core.NotificationSlideEdge.Bottom,
                    1f,
                    0.5f,
                    out _),
                "A converged transition animator issued a call even though nothing changed - "
                + "steady state must be zero overlay calls per tick.");
        }

        animator.Reset();
        Assert(
            animator.Advance(
                SvrBridge.Core.NotificationTransition.Fade,
                SvrBridge.Core.NotificationSlideEdge.Bottom,
                1f,
                0.5f,
                out _),
            "Resetting the animator did not force its next call to report a change - a new "
            + "notification's first frame must never be skipped as unchanged against the "
            + "previous one's final value.");
    }

    /// <summary>
    /// Plays one notification's whole timeline through
    /// <see cref="SvrBridge.Core.NotificationPlayer"/> and counts the transition
    /// animator's calls during the Holding phase specifically - the one phase
    /// that lasts long enough (most of a notification's 5 seconds) for a
    /// non-converging animation to matter.
    /// </summary>
    private static void AssertTransitionIsSilentAtSteadyState(
        SvrBridge.Core.NotificationTransition transition,
        SvrBridge.Core.NotificationSlideEdge edge)
    {
        var player = new SvrBridge.Core.NotificationPlayer();
        player.Enqueue(NotificationFor("transition", 5000));
        var animator = new SvrBridge.Core.NotificationTransitionAnimator();

        var holdTicksSeen = 0;
        var callsAfterTheFirstHoldTick = 0;
        for (var nowMs = 0L; nowMs <= 5000; nowMs += 10)
        {
            var frame = player.Tick(nowMs);
            if (frame.Phase == SvrBridge.Core.NotificationPhase.Idle)
            {
                break;
            }

            var changed = animator.Advance(transition, edge, frame.Alpha, 0.5f, out _);
            if (frame.Phase != SvrBridge.Core.NotificationPhase.Holding)
            {
                continue;
            }

            holdTicksSeen++;
            // The very first Holding tick is allowed one call, transitioning
            // in from wherever the fade-in left off - every tick after that
            // is steady state and must issue nothing.
            if (holdTicksSeen > 1 && changed)
            {
                callsAfterTheFirstHoldTick++;
            }
        }

        Assert(
            holdTicksSeen > 5,
            $"Not enough simulated Holding ticks ({holdTicksSeen}) to be a meaningful test of {transition}.");
        Assert(
            callsAfterTheFirstHoldTick == 0,
            $"The {transition} transition issued {callsAfterTheFirstHoldTick} overlay call(s) during "
            + "Holding, where progress never changes - steady state must be zero calls per tick.");
    }

    /// <summary>
    /// §B5: a malformed, truncated or missing PNG template must each degrade
    /// to no background rather than throwing - the same discipline
    /// <see cref="SvrBridge.Core.StreamerBotEventPayload.TryParse(string, out SvrBridge.Core.StreamerBotEventPayload, out string)"/>
    /// applies to hand-authored payloads.
    /// </summary>
    private static void TestNotificationTemplateLoaderDegradesGracefully()
    {
        Assert(
            WpfNotificationRenderer.LoadTemplate(
                Path.Combine(Path.GetTempPath(), $"svr-bridge-missing-{Guid.NewGuid():N}.png")) is null,
            "A missing template path did not degrade to no background.");

        var malformedPath = Path.Combine(Path.GetTempPath(), $"svr-bridge-malformed-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(malformedPath, [0x01, 0x02, 0x03, 0x04, 0x05]);
        try
        {
            Assert(
                WpfNotificationRenderer.LoadTemplate(malformedPath) is null,
                "A malformed (non-PNG) template file did not degrade to no background.");
        }
        finally
        {
            File.Delete(malformedPath);
        }

        var truncatedPath = Path.Combine(Path.GetTempPath(), $"svr-bridge-truncated-{Guid.NewGuid():N}.png");
        using (var bitmap = new System.Drawing.Bitmap(64, 64))
        {
            bitmap.Save(truncatedPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        var wholeFile = File.ReadAllBytes(truncatedPath);
        File.WriteAllBytes(truncatedPath, wholeFile[..(wholeFile.Length / 3)]);
        try
        {
            Assert(
                WpfNotificationRenderer.LoadTemplate(truncatedPath) is null,
                "A truncated PNG template did not degrade to no background.");
        }
        finally
        {
            File.Delete(truncatedPath);
        }
    }

    /// <summary>
    /// §B5: a template larger than this panel could ever usefully show is
    /// downscaled on load, capped memory rather than caching a user's
    /// full-resolution photo for a 900x260 panel; a template already under
    /// the cap must not be upscaled.
    /// </summary>
    /// <summary>
    /// The panel honours its configured pixel size, draws the source's icon
    /// when one ships, and grows short text rather than leaving it small in a
    /// large panel.
    /// <para>
    /// Asserted on the rendered pixels rather than on the WPF tree, because
    /// what matters is what reaches the overlay. Ink coverage - how many
    /// pixels differ from the flat background - is the measurable stand-in
    /// for "the text got bigger": the same two words in a panel of the same
    /// size must cover materially more of it once they are allowed to scale
    /// up, and an icon must add ink on the side of the panel it sits on.
    /// </para>
    /// </summary>
    private static void TestNotificationRendersAtTheConfiguredSizeWithItsIcon()
    {
        using var renderer = new WpfNotificationRenderer();

        var custom = renderer.Render(
            new NotificationContent("Ashling — Follow", "", "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0, "", 640, 400));
        Assert(
            custom.Width == 640 && custom.Height == 400,
            $"The panel ignored its configured pixel size - rendered {custom.Width}x{custom.Height}.");
        Assert(
            custom.Rgba.Length == 640 * 400 * 4,
            "The rendered buffer did not match the configured panel size.");

        var defaulted = renderer.Render(
            new NotificationContent("Ashling — Follow", "", "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0));
        Assert(
            defaulted.Width == SvrBridge.Core.NotificationAppearanceSettings.DefaultPanelWidth
            && defaulted.Height == SvrBridge.Core.NotificationAppearanceSettings.DefaultPanelHeight,
            "A caller that named no panel size did not get the proven default.");

        // Out of range on the way in, clamped rather than trusted - the same
        // texture is re-uploaded every animation frame.
        var absurd = renderer.Render(
            new NotificationContent("x", "", "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0, "", 99999, 10));
        Assert(
            absurd.Width == SvrBridge.Core.NotificationAppearanceSettings.MaximumPanelDimension
            && absurd.Height == SvrBridge.Core.NotificationAppearanceSettings.MinimumPanelDimension,
            "The renderer did not clamp an out-of-range panel size.");

        // Two words in a wide panel: with the Viewbox scaling them up they
        // have to cover far more of it than they would at a fixed 32pt.
        var shortText = renderer.Render(
            new NotificationContent("Hi", "", "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0, "", 900, 260));
        var longText = renderer.Render(
            new NotificationContent(
                "Ashling — Follow",
                "A much longer accompanying message that has to wrap across several lines to fit "
                + "inside a panel of this size at all, which is exactly when scaling down matters.",
                "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0, "", 900, 260));
        Assert(
            InkCoverage(shortText) > 0.02,
            "Two words in a 900x260 panel covered almost none of it - the text is not being scaled up "
            + "to fill the box.");
        Assert(
            InkCoverage(longText) > 0.02,
            "A long message rendered almost no ink, so it was not scaled down to fit either.");

        // An icon has to add ink where there was none. Skipped rather than
        // failed when this build ships no icons at all, since shipping none
        // is a valid state - the coloured chip is the desktop fallback and a
        // notification simply has no icon.
        var anySource = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(name => Path.GetFileNameWithoutExtension(name).Split('.').Last())
            .FirstOrDefault();
        if (anySource is not null)
        {
            var withIcon = renderer.Render(
                new NotificationContent(
                    "Ashling — Follow", "", "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0, anySource, 900, 260));
            var withoutIcon = renderer.Render(
                new NotificationContent(
                    "Ashling — Follow", "", "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0, "", 900, 260));
            Assert(
                InkCoverage(withIcon) > InkCoverage(withoutIcon),
                $"Drawing the \"{anySource}\" icon did not add any ink, so no icon reached the panel.");
            Assert(
                InkCoverage(renderer.Render(
                    new NotificationContent(
                        "Ashling — Follow", "", "#60C8FF", "#182030", "#FFFFFF", "", 1d, 0,
                        "NoSuchPlatform", 900, 260)))
                == InkCoverage(withoutIcon),
                "An unknown source changed the panel, so it did not fall through to drawing no icon.");
        }
    }

    /// <summary>
    /// The fraction of a rendered panel whose pixels differ from its
    /// top-left corner - a proxy for "how much was drawn on it". Compared
    /// between renders rather than against an absolute figure, since the
    /// exact number depends on font rasterisation.
    /// </summary>
    private static double InkCoverage(RenderedPanel panel)
    {
        var backgroundR = panel.Rgba[0];
        var backgroundG = panel.Rgba[1];
        var backgroundB = panel.Rgba[2];
        var differing = 0;
        for (var index = 0; index + 3 < panel.Rgba.Length; index += 4)
        {
            if (Math.Abs(panel.Rgba[index] - backgroundR) > 12
                || Math.Abs(panel.Rgba[index + 1] - backgroundG) > 12
                || Math.Abs(panel.Rgba[index + 2] - backgroundB) > 12)
            {
                differing++;
            }
        }

        return differing / (double)(panel.Width * panel.Height);
    }

    private static void TestNotificationTemplateLoaderDownscalesAnOversizedImage()
    {
        var oversizedPath = Path.Combine(Path.GetTempPath(), $"svr-bridge-oversized-{Guid.NewGuid():N}.png");
        using (var bitmap = new System.Drawing.Bitmap(WpfNotificationRenderer.MaxDecodePixelWidth + 400, 300))
        {
            bitmap.Save(oversizedPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        try
        {
            var loaded = WpfNotificationRenderer.LoadTemplate(oversizedPath);
            Assert(loaded is not null, "A valid oversized PNG template failed to load at all.");
            Assert(
                loaded!.PixelWidth <= WpfNotificationRenderer.MaxDecodePixelWidth,
                $"An oversized template ({loaded.PixelWidth}px wide) was not downscaled to the "
                + $"{WpfNotificationRenderer.MaxDecodePixelWidth}px cap.");
        }
        finally
        {
            File.Delete(oversizedPath);
        }

        var smallPath = Path.Combine(Path.GetTempPath(), $"svr-bridge-small-{Guid.NewGuid():N}.png");
        using (var bitmap = new System.Drawing.Bitmap(64, 32))
        {
            bitmap.Save(smallPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        try
        {
            var loaded = WpfNotificationRenderer.LoadTemplate(smallPath);
            Assert(
                loaded is not null && loaded.PixelWidth == 64,
                "A template already under the decode cap was resized anyway.");
        }
        finally
        {
            File.Delete(smallPath);
        }
    }

    /// <summary>
    /// §B4's precedence rule: a payload's own duration/accent/image win when
    /// present; a settings-level default applies only when the payload is
    /// silent. Pure - proven directly against
    /// <see cref="SvrBridge.Core.StreamerBotEventPayload.WithNotificationDefaults"/>
    /// rather than through a live <c>NotificationOverlay</c>, which needs OpenVR.
    /// </summary>
    private static void TestNotificationPayloadPrecedenceResolvesSettingsDefaults()
    {
        var silent = new SvrBridge.Core.StreamerBotEventPayload
        {
            Target = SvrBridge.Core.StreamerBotEventTarget.Notification,
            Text = "hello"
        };
        var resolvedSilent = silent.WithNotificationDefaults(9000, "#112233", "C:\\default.png");
        Assert(
            resolvedSilent.DurationMs == 9000
            && resolvedSilent.Accent == "#112233"
            && resolvedSilent.Image == "C:\\default.png",
            "A payload silent about duration/accent/image did not take the settings-level defaults.");

        Assert(
            SvrBridge.Core.StreamerBotEventPayload.TryParse(
                """{"target":"notification","duration":1234,"accent":"#ABCDEF","image":"C:\\custom.png","text":"hi"}""",
                out var explicitPayload,
                out _),
            "A well-formed notification payload with duration/accent/image was rejected.");
        var resolvedExplicit = explicitPayload!.WithNotificationDefaults(9000, "#112233", "C:\\default.png");
        Assert(
            resolvedExplicit.DurationMs == 1234
            && resolvedExplicit.Accent == "#ABCDEF"
            && resolvedExplicit.Image == "C:\\custom.png",
            "A payload's own duration/accent/image did not win over the settings-level defaults.");
    }

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
    /// Proves §B3 of the Phase 6 plan's exact requirement for the dashboard
    /// repaint throttle: leading edge, not trailing. The first request in a
    /// burst must paint immediately, a burst within the window must coalesce
    /// into exactly one further paint (not zero, not one per request), and
    /// that one paint must show the final state - not the first, and not a
    /// stale intermediate one. Asserting the render count and the final
    /// value together is deliberate: a naive per-click render would also
    /// leave <c>lastValue</c> at the final state and look correct on that
    /// check alone.
    /// </summary>
    private static void TestDashboardRepaintCoordinatorCoalescesBurst()
    {
        var coordinator = new SvrBridge.Core.DashboardRepaintCoordinator(minimumIntervalMs: 60);
        var renderCount = 0;
        var lastValue = -1;

        void RequestValue(int value, long nowMs) =>
            coordinator.Request(
                () =>
                {
                    renderCount++;
                    lastValue = value;
                },
                nowMs);

        RequestValue(1, 0);
        Assert(
            renderCount == 1 && lastValue == 1,
            "The first update in a burst was not rendered immediately (leading edge).");

        RequestValue(2, 10);
        RequestValue(3, 20);
        RequestValue(4, 30);
        Assert(
            renderCount == 1,
            $"A burst inside the throttle window rendered {renderCount} times before the window elapsed instead of coalescing.");

        coordinator.Flush(59);
        Assert(
            renderCount == 1,
            "The coalesced repaint fired one millisecond before its throttle window elapsed.");

        coordinator.Flush(60);
        Assert(
            renderCount == 2 && lastValue == 4,
            $"The coalesced burst produced {renderCount} render(s) showing value {lastValue} instead of exactly 2 renders, the second showing the final value 4 - this is the per-click-render bug the throttle exists to catch.");

        // Nothing left pending: a flush with no new request must not render
        // again, proving the throttle does not keep re-firing on a stale
        // version once it has caught up.
        coordinator.Flush(1000);
        Assert(renderCount == 2, "A flush with nothing pending rendered anyway.");

        // A single isolated request, well clear of the previous burst, is
        // its own leading edge and must not be held back by the earlier one.
        RequestValue(5, 1000);
        Assert(
            renderCount == 3 && lastValue == 5,
            "A request arriving after the throttle window had long elapsed was not treated as a fresh leading edge.");
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
    /// Proves the gaze-scale ease reaches an explicit converged state and, once
    /// there, stops issuing the calls that would drive
    /// <c>SetOverlayWidthInMeters</c>/<c>SetOverlayAlpha</c> - the bug behind
    /// the dashboard/chat prompt's Part A: exponential easing only asymptotes
    /// towards its target, so without this convergence check those two overlay
    /// calls would fire on every tick, forever, even at rest. Also proves
    /// re-asserting the same target every tick (exactly what
    /// <see cref="ChatOverlay.AnimateGaze"/> does, since it recomputes the
    /// target from the gaze verdict on every call) does not itself re-open a
    /// converged animation, and that a genuine target change does.
    /// </summary>
    private static void TestGazeScaleAnimationConvergesAndStopsIssuingCalls()
    {
        var animation = new SvrBridge.Core.GazeScaleAnimation(0.12f, 0.35f);
        Assert(animation.IsConverged, "A freshly created gaze animation was not already converged at its own initial value.");
        Assert(!animation.Advance(10f, 150f), "A converged gaze animation issued a call with no target change.");

        animation.SetTarget(0.32f, 0.95f);
        Assert(!animation.IsConverged, "Setting a new target did not leave the converged state.");

        var advanceCalls = 0;
        for (var tick = 0; tick < 1000 && !animation.IsConverged; tick++)
        {
            // Re-assert the same target every tick, exactly as AnimateGaze
            // does from the gaze verdict, to prove that alone cannot prevent
            // convergence.
            animation.SetTarget(0.32f, 0.95f);
            if (animation.Advance(10f, 150f))
            {
                advanceCalls++;
            }
        }

        Assert(animation.IsConverged, "The gaze animation never converged within 10 seconds of simulated ticks.");
        Assert(advanceCalls > 0, "The gaze animation converged without ever issuing a call, which would mean it never actually moved.");
        Assert(
            animation.Width == 0.32f && animation.Alpha == 0.95f,
            "The gaze animation did not snap exactly to its target once converged.");

        for (var tick = 0; tick < 50; tick++)
        {
            animation.SetTarget(0.32f, 0.95f);
            Assert(
                !animation.Advance(10f, 150f),
                "A converged gaze animation issued a call even though its target had not changed - "
                + "steady state must be zero overlay calls per tick.");
        }

        animation.SetTarget(0.12f, 0.35f);
        Assert(!animation.IsConverged, "Changing the target after convergence did not re-open the animation.");
        Assert(animation.Advance(10f, 150f), "A freshly re-opened gaze animation issued no call on its first tick.");
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

    /// <summary>
    /// The row-pitch copy, against a destination pitch deliberately wider than
    /// the row - which is the normal case on real hardware, because drivers pad
    /// rows for alignment. A single block copy passes a same-pitch test and
    /// shears the image on every machine where the pitch differs, so the
    /// padding here is the entire point.
    /// </summary>
    private static void TestOverlayTextureCopyRespectsAnOverWideRowPitch()
    {
        const int width = 5;
        const int height = 4;
        const int rowBytes = width * 4;
        // Not a multiple of rowBytes, so an off-by-one in the pitch arithmetic
        // cannot accidentally still line up.
        const int destinationPitch = rowBytes + 12;

        var source = new byte[rowBytes * height];
        for (var index = 0; index < source.Length; index++)
        {
            source[index] = (byte)(index + 1);
        }

        var destination = new byte[destinationPitch * height];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(
            destination,
            System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            D3D11OverlayTexture.CopyRows(
                source,
                pin.AddrOfPinnedObject(),
                destinationPitch,
                rowBytes,
                height);
        }
        finally
        {
            pin.Free();
        }

        for (var row = 0; row < height; row++)
        {
            for (var offset = 0; offset < rowBytes; offset++)
            {
                Assert(
                    destination[(row * destinationPitch) + offset] == source[(row * rowBytes) + offset],
                    $"The row-pitch copy put the wrong byte at row {row}, offset {offset} - "
                    + "the image would be sheared.");
            }

            for (var padding = rowBytes; padding < destinationPitch; padding++)
            {
                Assert(
                    destination[(row * destinationPitch) + padding] == 0,
                    $"The row-pitch copy wrote into row {row}'s padding, which means it "
                    + "treated the destination as tightly packed.");
            }
        }
    }

    /// <summary>
    /// The two source formats, each converted for a known solid colour.
    /// <para>
    /// The mistake this exists to catch is applying the WPF un-premultiply to
    /// GDI+ output: GDI+ <c>Format32bppArgb</c> is straight alpha already, so
    /// dividing by alpha a second time washes the colours out - visibly on the
    /// semi-transparent panel backgrounds both renderers use, and not at all
    /// at full opacity, which is where a casual look would check.
    /// </para>
    /// </summary>
    private static void TestOverlaySourceFormatsConvertToTheSameRgba()
    {
        // One pixel, half transparent, in a colour whose three channels are
        // all different so a swap cannot hide.
        const byte alpha = 128;
        const byte red = 200;
        const byte green = 50;
        const byte blue = 10;

        // GDI+: straight alpha, BGRA in memory.
        var gdi = new byte[] { blue, green, red, alpha };
        SvrBridge.Core.OverlayPixelFormat.ConvertGdiBgra32ToRgba(gdi);
        Assert(
            gdi[0] == red && gdi[1] == green && gdi[2] == blue && gdi[3] == alpha,
            $"The GDI+ conversion produced R={gdi[0]} G={gdi[1]} B={gdi[2]} A={gdi[3]} rather "
            + $"than the straight-alpha RGBA {red}/{green}/{blue}/{alpha} it was given. "
            + "An un-premultiply here would wash the colour out.");

        // WPF: the same colour premultiplied, still BGRA in memory.
        var wpf = new byte[]
        {
            (byte)(blue * alpha / 255),
            (byte)(green * alpha / 255),
            (byte)(red * alpha / 255),
            alpha
        };
        SvrBridge.Core.OverlayPixelFormat.ConvertWpfPbgra32ToRgba(wpf);
        Assert(
            Math.Abs(wpf[0] - red) <= 3
            && Math.Abs(wpf[1] - green) <= 3
            && Math.Abs(wpf[2] - blue) <= 3
            && wpf[3] == alpha,
            $"The WPF conversion produced R={wpf[0]} G={wpf[1]} B={wpf[2]} A={wpf[3]} rather than "
            + $"approximately {red}/{green}/{blue}/{alpha}. Without the un-premultiply the "
            + "channels stay darkened by alpha.");

        // Both paths must land on the same bytes for the same colour, which is
        // what lets one texture format serve every renderer.
        Assert(
            Math.Abs(wpf[0] - gdi[0]) <= 3
            && Math.Abs(wpf[1] - gdi[1]) <= 3
            && Math.Abs(wpf[2] - gdi[2]) <= 3
            && wpf[3] == gdi[3],
            "The GDI+ and WPF conversions disagreed on the same colour, so the dashboard and "
            + "the chat window would not match in the headset.");
    }

    /// <summary>
    /// Device loss must drop to <c>SetOverlayRaw</c> immediately and come back
    /// to the texture path once a device returns - not fall back permanently.
    /// A TDR or driver update mid-session would otherwise mean the blink
    /// returns and never leaves until the app is restarted.
    /// <para>
    /// Driven through a fake source rather than a real GPU: forcing an actual
    /// TDR is not something a start-up self-test can do, and the behaviour
    /// worth pinning down is the routing, not Direct3D.
    /// </para>
    /// </summary>
    private static void TestOverlayUploadFallsBackOnDeviceLossAndRecovers()
    {
        var target = new RecordingUploadTarget();
        var source = new FakeTextureSource();
        using var uploader = new OverlayTextureUploader(target, source, "Test surface", _ => { });
        var frame = new byte[4 * 4 * 4];

        Assert(
            uploader.Upload(frame, 4, 4) == OverlayUploadPath.Texture,
            "A healthy device did not use the texture path.");
        Assert(
            target.NativeUploads == 1 && target.RawUploads == 0,
            "The texture path did not hand SteamVR a native texture.");

        source.FailWrites = true;
        Assert(
            uploader.Upload(frame, 4, 4) == OverlayUploadPath.Raw,
            "A failed write did not fall back to SetOverlayRaw.");
        Assert(
            source.DeviceLossReports == 1,
            "The failed write did not tell the device it was lost, so nothing would start "
            + "recovery.");

        // Still inside the backoff: no device, so still raw, and no repeated
        // loss reports piling up per frame.
        source.Available = false;
        Assert(
            uploader.Upload(frame, 4, 4) == OverlayUploadPath.Raw,
            "An unavailable device did not keep using SetOverlayRaw.");
        Assert(
            source.DeviceLossReports == 1,
            "A frame with no device available reported a device loss it did not cause.");

        source.Available = true;
        source.FailWrites = false;
        Assert(
            uploader.Upload(frame, 4, 4) == OverlayUploadPath.Texture,
            "The uploader did not return to the texture path once a device came back.");
        Assert(
            target.LastNativeTexture != target.FirstNativeTexture,
            "The recovered upload reused the dead texture's pointer.");
        Assert(
            target.NativeUploads == 2,
            "The recovered texture was never handed to SteamVR, so the overlay would freeze "
            + "on its last frame with no error anywhere.");
    }

    /// <summary>
    /// Pins the default-off contract that <see cref="ChatOverlay"/>,
    /// <see cref="NotificationOverlay"/> and <see cref="VrTestOverlay"/> all
    /// rely on: constructing an uploader with <c>TexturePathEnabled = false</c>
    /// (as all three do) must stay on <c>SetOverlayRaw</c> even with a healthy
    /// device available, and only switch once something explicitly flips the
    /// flag - the tray's "overlay texture path" developer toggle, in the real
    /// app. Exercised against the fakes rather than the three overlay
    /// classes themselves, which need a real OpenVR session to construct.
    /// </summary>
    private static void TestOverlayUploadDefaultsOffUntilExplicitlyEnabled()
    {
        var target = new RecordingUploadTarget();
        var source = new FakeTextureSource();
        using var uploader = new OverlayTextureUploader(target, source, "Test surface", _ => { })
        {
            TexturePathEnabled = false
        };
        var frame = new byte[4 * 4 * 4];

        Assert(
            uploader.Upload(frame, 4, 4) == OverlayUploadPath.Raw,
            "An uploader constructed with TexturePathEnabled false used the texture path anyway, "
            + "even though a healthy device was available.");
        Assert(
            target.RawUploads == 1 && target.NativeUploads == 0,
            "A disabled texture path still handed SteamVR a native texture.");

        uploader.TexturePathEnabled = true;
        Assert(
            uploader.Upload(frame, 4, 4) == OverlayUploadPath.Texture,
            "Enabling the texture path did not switch a healthy device onto it.");
        Assert(
            target.NativeUploads == 1,
            "Enabling the texture path did not hand SteamVR a native texture.");
    }

    private sealed class RecordingUploadTarget : IOverlayUploadTarget
    {
        public int RawUploads { get; private set; }

        public int NativeUploads { get; private set; }

        public nint FirstNativeTexture { get; private set; }

        public nint LastNativeTexture { get; private set; }

        public void SetRawTexture(byte[] rgba, int width, int height) => RawUploads++;

        public void SetNativeTexture(nint nativeD3D11Texture)
        {
            if (NativeUploads == 0)
            {
                FirstNativeTexture = nativeD3D11Texture;
            }

            LastNativeTexture = nativeD3D11Texture;
            NativeUploads++;
        }
    }

    private sealed class FakeTextureSource : IOverlayTextureSource
    {
        private nint _nextPointer = 1;

        public bool Available { get; set; } = true;

        public bool FailWrites { get; set; }

        public int DeviceLossReports { get; private set; }

        public IOverlayTexture? TryCreateTexture(int width, int height) =>
            Available ? new FakeTexture(this, _nextPointer++, width, height) : null;

        public void ReportDeviceLost(Exception exception) => DeviceLossReports++;

        private sealed class FakeTexture(FakeTextureSource owner, nint pointer, int width, int height)
            : IOverlayTexture
        {
            public int Width => width;

            public int Height => height;

            public nint NativeTexture => pointer;

            public void Write(byte[] rgba)
            {
                if (owner.FailWrites)
                {
                    throw new InvalidOperationException("Simulated device loss.");
                }
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// The one check that needs a real GPU: writes a known RGBA pattern
    /// through <see cref="D3D11OverlayTexture"/> and reads the texture back,
    /// proving the channel order and the row-pitch copy survive a genuine
    /// driver round trip rather than only the arithmetic above.
    /// <para>
    /// The width is deliberately 37, not a multiple of 256: a driver that pads
    /// rows exposes a naive block copy, and a conveniently aligned width would
    /// hide it.
    /// </para>
    /// <para>
    /// Skipped rather than failed when no device can be created - this suite
    /// also runs on machines with no usable GPU, and falling back to
    /// <c>SetOverlayRaw</c> is exactly what those machines do.
    /// </para>
    /// </summary>
    private static void TestD3D11OverlayTextureRoundTripsRgbaWithoutSwappingChannels()
    {
        const int width = 37;
        const int height = 11;

        using var device = new D3D11OverlayDevice(_ => { });
        using var texture = device.TryCreateTexture(width, height) as D3D11OverlayTexture;
        if (texture is null || device.DeviceForTesting is not { } nativeDevice)
        {
            return;
        }

        var written = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = ((y * width) + x) * 4;
                written[index] = (byte)(x * 5);
                written[index + 1] = (byte)(y * 20);
                written[index + 2] = (byte)(255 - (x * 5));
                written[index + 3] = (byte)(128 + (x % 128));
            }
        }

        texture.Write(written);
        AssertRoundTrip(texture.ReadBackForTesting(nativeDevice), written, "the writing device");

        // Cross-device, the way SteamVR reads it: proves the shared handle
        // opens and the format survives. It deliberately does *not* claim to
        // catch the missing-wait race that made every dashboard click need
        // doing twice - that was measured, and this passes either way. See
        // D3D11OverlayTexture.ReadBackThroughASecondDeviceForTesting.
        var second = new byte[written.Length];
        for (var index = 0; index < second.Length; index++)
        {
            second[index] = (byte)(255 - written[index]);
        }

        texture.Write(second);
        if (texture.ReadBackThroughASecondDeviceForTesting() is { } crossDevice)
        {
            AssertRoundTrip(crossDevice, second, "a second device");
        }
    }

    private static void AssertRoundTrip(byte[] readBack, byte[] written, string via)
    {
        Assert(
            readBack.Length == written.Length,
            $"The D3D11 texture read back via {via} returned a different number of bytes "
            + "than were written.");

        for (var index = 0; index < written.Length; index++)
        {
            Assert(
                readBack[index] == written[index],
                $"The D3D11 texture did not round-trip RGBA unchanged via {via}. Byte "
                + $"{index} (pixel {index / 4}, channel {"RGBA"[index % 4]}) was written as "
                + $"{written[index]} and read back as {readBack[index]} - a channel swap, a "
                + "row-pitch mistake, or a copy that was never waited for.");
        }
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
