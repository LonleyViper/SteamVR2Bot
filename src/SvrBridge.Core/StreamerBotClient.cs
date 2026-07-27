using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SvrBridge.Core;

public sealed record StreamerBotAction(string Id, string Name, string Group)
{
    public string FriendlyName =>
        string.IsNullOrWhiteSpace(Group) || Group.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{Group} — {Name}";
}

public sealed class StreamerBotDeliveryException : Exception
{
    public StreamerBotDeliveryException(
        string message,
        bool deliveryMayHaveOccurred,
        Exception innerException)
        : base(message, innerException)
    {
        DeliveryMayHaveOccurred = deliveryMayHaveOccurred;
    }

    public bool DeliveryMayHaveOccurred { get; }
}

public sealed class StreamerBotClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ConnectionRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    private readonly StreamerBotConfig _config;
    private readonly Action<string> _log;
    private ClientWebSocket? _socket;

    public StreamerBotClient(StreamerBotConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log ?? Console.WriteLine;
    }

    public async Task TriggerAsync(string bindingName, CancellationToken cancellationToken)
        => await TriggerAsync(
            bindingName,
            _config.ActionId,
            _config.ActionName,
            cancellationToken);

    public async Task TriggerAsync(
        string bindingName,
        string? actionId,
        string? actionName,
        CancellationToken cancellationToken)
    {
        if (_config.DryRun)
        {
            _log(
                $"DRY RUN: would execute Streamer.bot action " +
                $"'{actionName}' ({actionId ?? "no id"}).");
            return;
        }

        await EnsureConnectedWithBackoffAsync(cancellationToken);
        try
        {
            await SendActionAsync(bindingName, actionId, actionName, cancellationToken);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await ResetSocketAsync();
            throw new StreamerBotDeliveryException(
                "The Streamer.bot connection was lost while delivering the action.",
                deliveryMayHaveOccurred: true,
                exception);
        }
    }

    public async Task<IReadOnlyList<StreamerBotAction>> GetActionsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureConnectedWithBackoffAsync(cancellationToken);
        var socket = _socket ?? throw new InvalidOperationException("WebSocket is not connected.");
        var id = $"svr-actions-{Guid.NewGuid():N}";

        await SendJsonAsync(
            socket,
            new
            {
                request = "GetActions",
                id
            },
            cancellationToken);

        using var response = await ReceiveJsonAsync(socket, cancellationToken);
        EnsureSuccessfulResponse(response.RootElement, id, "GetActions");

        if (!response.RootElement.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Streamer.bot returned no action list.");
        }

        return actions
            .EnumerateArray()
            .Where(action =>
                !action.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind != JsonValueKind.False)
            .Select(action => new StreamerBotAction(
                action.GetProperty("id").GetString() ?? "",
                action.GetProperty("name").GetString() ?? "Unnamed action",
                action.TryGetProperty("group", out var group)
                    ? group.GetString() ?? ""
                    : ""))
            .Where(action => !string.IsNullOrWhiteSpace(action.Id))
            .OrderBy(action => action.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await ResetSocketAsync();
    }

    public static string BuildAuthentication(string password, string salt, string challenge)
    {
        var secretBytes = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var secret = Convert.ToBase64String(secretBytes);
        var authenticationBytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge));
        return Convert.ToBase64String(authenticationBytes);
    }

    private async Task EnsureConnectedWithBackoffAsync(CancellationToken cancellationToken)
    {
        if (_socket?.State == WebSocketState.Open)
        {
            return;
        }

        Exception? lastFailure = null;
        for (var attempt = 0; attempt < ConnectionRetryDelays.Length; attempt++)
        {
            var delay = ConnectionRetryDelays[attempt];
            if (delay > TimeSpan.Zero)
            {
                _log(
                    $"Streamer.bot is unavailable; reconnecting in {delay.TotalSeconds:0} second(s).");
                await Task.Delay(delay, cancellationToken);
            }

            try
            {
                await ConnectAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                lastFailure = exception;
                await ResetSocketAsync();
            }
        }

        throw new StreamerBotDeliveryException(
            "Could not connect to Streamer.bot after three attempts.",
            deliveryMayHaveOccurred: false,
            lastFailure ?? new WebSocketException("Connection failed."));
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await ResetSocketAsync();
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        await _socket.ConnectAsync(new Uri(_config.WebSocketUrl), cancellationToken);

        using var hello = await ReceiveJsonAsync(_socket, cancellationToken);
        if (!hello.RootElement.TryGetProperty("request", out var request)
            || request.GetString() != "Hello")
        {
            throw new InvalidDataException("Streamer.bot did not send the expected Hello message.");
        }

        if (hello.RootElement.TryGetProperty("authentication", out var authentication))
        {
            if (string.IsNullOrEmpty(_config.Password))
            {
                throw new InvalidOperationException(
                    "Streamer.bot requires authentication but streamerBot.password is empty.");
            }

            var salt = authentication.GetProperty("salt").GetString()
                       ?? throw new InvalidDataException("Hello authentication salt is missing.");
            var challenge = authentication.GetProperty("challenge").GetString()
                            ?? throw new InvalidDataException("Hello authentication challenge is missing.");
            var id = $"svr-auth-{Guid.NewGuid():N}";
            var payload = new
            {
                request = "Authenticate",
                id,
                authentication = BuildAuthentication(_config.Password, salt, challenge)
            };

            await SendJsonAsync(_socket, payload, cancellationToken);
            using var response = await ReceiveJsonAsync(_socket, cancellationToken);
            EnsureSuccessfulResponse(response.RootElement, id, "Authenticate");
        }

        _log($"Connected to Streamer.bot at {_config.WebSocketUrl}");
    }

    private async Task SendActionAsync(
        string bindingName,
        string? actionId,
        string? actionName,
        CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("WebSocket is not connected.");
        var id = $"svr-action-{Guid.NewGuid():N}";
        var payload = new
        {
            request = "DoAction",
            id,
            action = new
            {
                id = string.IsNullOrWhiteSpace(actionId) ? null : actionId,
                name = string.IsNullOrWhiteSpace(actionName) ? null : actionName
            },
            args = new
            {
                source = "SVRBridge",
                binding = bindingName,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        await SendJsonAsync(socket, payload, cancellationToken);
        using var response = await ReceiveJsonAsync(socket, cancellationToken);
        EnsureSuccessfulResponse(response.RootElement, id, "DoAction");
        _log($"Streamer.bot acknowledged '{actionName}'.");
    }

    private static void EnsureSuccessfulResponse(JsonElement response, string expectedId, string operation)
    {
        if (!response.TryGetProperty("id", out var id) || id.GetString() != expectedId)
        {
            throw new InvalidDataException($"{operation} returned an unexpected response id.");
        }

        if (response.TryGetProperty("status", out var status)
            && !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            var error = response.TryGetProperty("error", out var errorValue)
                ? errorValue.ToString()
                : response.ToString();
            throw new InvalidOperationException($"{operation} failed: {error}");
        }
    }

    private static async Task SendJsonAsync(
        ClientWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Streamer.bot closed the WebSocket connection.");
            }

            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task ResetSocketAsync()
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "SteamVR2Bot stopping",
                    timeout.Token);
            }
        }
        catch (Exception exception) when (IsConnectionFailure(exception)
                                          || exception is OperationCanceledException)
        {
            // The connection is already unusable; disposal below is sufficient.
        }

        _socket.Dispose();
        _socket = null;
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is WebSocketException or IOException
        || exception.InnerException is WebSocketException or IOException;
}
