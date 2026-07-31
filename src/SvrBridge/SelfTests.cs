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
        TestDashboardPointerTracking();
        TestInputProbe();
        TestBodyFrame();
        TestAuthenticationHash();
        TestStreamerBotEventPayload();
        TestTwitchChatMessageMapper();
        TestTwitchEmoteCatalog();
        TestStreamerBotEventTemplateResolvesDottedPaths();
        TestStreamerBotEventCatalogParsesGetEventsResponse();
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
            ["Twitch.Follow", "twitch.follow"],
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

        await SendEventAsync(
            socket,
            "Twitch",
            "Follow",
            new { targetUser = new { name = "Ashling" }, isTest = false },
            timeout.Token);
        var received = await stream.Events.ReadAsync(timeout.Token);
        Assert(
            received.Payload.Target == StreamerBotEventTarget.Notification
            && received.Payload.Text.Contains("Twitch.Follow"),
            "A directly-subscribed enabled event did not produce a notification payload via the generic template.");

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
