using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SvrBridge;

internal sealed class StreamerBotClient : IAsyncDisposable
{
    private readonly StreamerBotConfig _config;
    private ClientWebSocket? _socket;

    public StreamerBotClient(StreamerBotConfig config)
    {
        _config = config;
    }

    public async Task TriggerAsync(string bindingName, CancellationToken cancellationToken)
    {
        if (_config.DryRun)
        {
            Console.WriteLine(
                $"DRY RUN: would execute Streamer.bot action " +
                $"'{_config.ActionName}' ({_config.ActionId ?? "no id"}).");
            return;
        }

        try
        {
            await EnsureConnectedAsync(cancellationToken);
            await SendActionAsync(bindingName, cancellationToken);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            Console.WriteLine($"Streamer.bot connection failed; retrying once: {exception.Message}");
            await ResetSocketAsync();
            await EnsureConnectedAsync(cancellationToken);
            await SendActionAsync(bindingName, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetSocketAsync();
    }

    internal static string BuildAuthentication(string password, string salt, string challenge)
    {
        var secretBytes = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var secret = Convert.ToBase64String(secretBytes);
        var authenticationBytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge));
        return Convert.ToBase64String(authenticationBytes);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_socket?.State == WebSocketState.Open)
        {
            return;
        }

        await ResetSocketAsync();
        _socket = new ClientWebSocket();
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

        Console.WriteLine($"Connected to Streamer.bot at {_config.WebSocketUrl}");
    }

    private async Task SendActionAsync(string bindingName, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("WebSocket is not connected.");
        var id = $"svr-action-{Guid.NewGuid():N}";
        var payload = new
        {
            request = "DoAction",
            id,
            action = new
            {
                id = string.IsNullOrWhiteSpace(_config.ActionId) ? null : _config.ActionId,
                name = string.IsNullOrWhiteSpace(_config.ActionName) ? null : _config.ActionName
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
        Console.WriteLine($"Streamer.bot acknowledged '{_config.ActionName}'.");
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
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "SVR Bridge stopping",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // The connection is already unusable; disposal below is sufficient.
        }

        _socket.Dispose();
        _socket = null;
    }
}

