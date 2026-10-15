using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Arcana.Runtime;

internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _nextId;
    private bool _disposed;

    private CdpConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new CdpConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextId);
        var request = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };
        if (sessionId is not null)
            request["sessionId"] = sessionId;

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                using var message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!message.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                    continue;
                if (message.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"CDP {method} failed: {error}");
                return message.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Arcana shutdown", CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // The browser process may already be gone.
            }
        }
        _socket.Dispose();
        _sendGate.Dispose();
    }

    private async Task<JsonDocument> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("The CDP socket closed before a response was received.");
            await buffer.WriteAsync(chunk.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
        }
        while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.ToArray());
    }
}
