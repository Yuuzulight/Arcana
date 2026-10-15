using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Arcana.Runtime;

internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<CdpSubscription, byte> _subscriptions = new();
    private readonly Task _receiver;
    private int _nextId;
    private volatile bool _disposed;
    private volatile Exception? _failure;

    private CdpConnection(ClientWebSocket socket) { _socket = socket; _receiver = ReceiveLoopAsync(); }

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new CdpConnection(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    public CdpSubscription Subscribe(string? method = null, string? sessionId = null, int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        var subscription = new CdpSubscription(method, sessionId, capacity, s => _subscriptions.TryRemove(s, out _));
        _subscriptions.TryAdd(subscription, 0);
        if (_failure is { } failure) subscription.Complete(failure);
        return subscription;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            if (_failure is { } failure) throw new IOException("CDP connection failed.", failure);
            var request = new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { } };
            if (sessionId is not null) request["sessionId"] = sessionId;
            await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException($"CDP {method} timed out after 30 seconds."); }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            var chunk = new byte[16 * 1024];
            while (!_lifetime.IsCancellationRequested)
            {
                using var buffer = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await _socket.ReceiveAsync(chunk, _lifetime.Token).ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("CDP socket closed.");
                    if (buffer.Length + frame.Count > 128 * 1024 * 1024) throw new IOException("CDP message exceeds 128 MiB.");
                    buffer.Write(chunk, 0, frame.Count);
                } while (!frame.EndOfMessage);
                using var message = JsonDocument.Parse(buffer.ToArray());
                var root = message.RootElement;
                if (root.TryGetProperty("id", out var id))
                {
                    if (!_pending.TryRemove(id.GetInt32(), out var pending)) continue;
                    if (root.TryGetProperty("error", out var error)) pending.TrySetException(new CdpException(error.Clone()));
                    else pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement(new { }));
                }
                else if (root.TryGetProperty("method", out var method))
                {
                    var evt = new CdpEvent(method.GetString()!, root.TryGetProperty("params", out var p) ? p.Clone() : default,
                        root.TryGetProperty("sessionId", out var session) ? session.GetString() : null);
                    foreach (var subscriber in _subscriptions.Keys) subscriber.Publish(evt);
                }
            }
        }
        catch (Exception error)
        {
            _failure = error;
            foreach (var pending in _pending.Values) pending.TrySetException(error);
            foreach (var subscriber in _subscriptions.Keys) subscriber.Complete(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _socket.Abort();
        await _receiver.ConfigureAwait(false);
        foreach (var subscriber in _subscriptions.Keys) subscriber.Dispose();
        _socket.Dispose();
        // Senders may still be unwinding cancellation; do not dispose their gate here.
    }
}
