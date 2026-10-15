using System.Text.Json;
using System.Threading.Channels;

namespace Arcana.Runtime;

public sealed record CdpEvent(string Method, JsonElement Parameters, string? SessionId);

public sealed class CdpException(JsonElement error) : InvalidOperationException($"CDP command failed: {error}")
{
    public JsonElement Error { get; } = error;
}

/// <summary>A filtered stream. Subscribe before sending the command that produces an event.</summary>
public sealed class CdpSubscription : IDisposable
{
    private readonly Channel<CdpEvent> _events;
    private readonly string? _method;
    private readonly string? _session;
    private readonly Action<CdpSubscription> _remove;

    internal CdpSubscription(string? method, string? session, int capacity, Action<CdpSubscription> remove)
    {
        _method = method; _session = session; _remove = remove;
        _events = Channel.CreateBounded<CdpEvent>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
    }

    public IAsyncEnumerable<CdpEvent> ReadAllAsync(CancellationToken cancellationToken = default) => _events.Reader.ReadAllAsync(cancellationToken);
    public ValueTask<CdpEvent> ReadAsync(CancellationToken cancellationToken = default) => _events.Reader.ReadAsync(cancellationToken);
    internal void Publish(CdpEvent evt)
    {
        if ((_method is null || _method == evt.Method) && (_session is null || _session == evt.SessionId) && !_events.Writer.TryWrite(evt))
            Complete(new IOException("CDP event subscriber overflowed; consume events faster or increase capacity."));
    }
    internal void Complete(Exception? error = null) { _events.Writer.TryComplete(error); _remove(this); }
    public void Dispose() => Complete();
}
