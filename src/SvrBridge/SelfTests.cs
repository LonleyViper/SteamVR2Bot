using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
        TestAuthenticationHash();
        await TestStreamerBotRoundTripAsync();
        await TestStreamerBotReconnectAsync();
        await TestStreamerBotRestartRecoveryAsync();
        await TestUnconfirmedDeliveryIsNotRetriedAsync();
        await TestSteamVrSessionRestartAsync();
        Console.WriteLine(
            "SELF-TEST PASS: chord detection, physical controller mapping, authentication, SteamVR worker " +
            "recovery, Streamer.bot restart recovery, no-duplicate delivery, " +
            "and DoAction round trip.");
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
        probe.SetEnabled(false, 3000);
        probe.BeginPoll();
        probe.ObserveAction(pressed);
        probe.EndPoll(9000);
        Assert(
            lines.Count == 1 && !probe.Enabled,
            "Disabling the input probe did not stop it logging.");
    }

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
