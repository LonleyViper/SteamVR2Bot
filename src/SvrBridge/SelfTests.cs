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
        TestCooldown();
        TestAuthenticationHash();
        await TestStreamerBotRoundTripAsync();
        await TestStreamerBotReconnectAsync();
        await TestUnconfirmedDeliveryIsNotRetriedAsync();
        Console.WriteLine(
            "SELF-TEST PASS: chord detection, authentication, reconnect, " +
            "no-duplicate delivery, and DoAction round trip.");
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
        var server = RunMockStreamerBotAsync(listener, timeout.Token);
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
            await client.TriggerAsync("self-test", timeout.Token);
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

    private static async Task RunMockStreamerBotAsync(
        HttpListener listener,
        CancellationToken cancellationToken,
        bool expectGetActions = true,
        string expectedBinding = "self-test")
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
            action.RootElement.GetProperty("action").GetProperty("name").GetString() == "SVR POC Test",
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
}
