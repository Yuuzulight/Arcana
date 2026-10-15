using System.Text.Json;

namespace Arcana.Runtime;

public sealed partial class ArcanaBrowser : IAsyncDisposable
{
    private readonly CdpConnection _connection;
    private bool _disposed;

    private ArcanaBrowser(CdpConnection connection)
    {
        _connection = connection;
    }

    public static async Task<ArcanaBrowser> ConnectAsync(BrowserRuntime runtime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var versionJson = await runtime.GetVersionJsonAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Arcana backend did not provide a CDP version response.");
        using var version = JsonDocument.Parse(versionJson);
        var websocketUrl = version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
            ?? throw new InvalidOperationException("The CDP version response did not contain a WebSocket endpoint.");
        return new ArcanaBrowser(await CdpConnection.ConnectAsync(new Uri(websocketUrl), cancellationToken).ConfigureAwait(false));
    }

    public async Task<ArcanaPage> NewPageAsync(string? url = null, CancellationToken cancellationToken = default)
        => await NewPageInContextAsync(null, url, cancellationToken).ConfigureAwait(false);

    public async Task<ArcanaPage> NewPageInContextAsync(string? browserContextId, string? url = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "about")))
            throw new ArgumentException("Arcana navigation only accepts http, https, or about URLs.", nameof(url));
        var result = await _connection.SendAsync("Target.createTarget", new { url = "about:blank", browserContextId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var targetId = result.GetProperty("targetId").GetString()
            ?? throw new InvalidOperationException("CDP did not return a target id.");
        var attached = await _connection.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var sessionId = attached.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("CDP did not return a session id.");
        var page = new ArcanaPage(_connection, targetId, sessionId);
        await page.EnableAsync(cancellationToken).ConfigureAwait(false);
        if (url is not null)
            await page.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
