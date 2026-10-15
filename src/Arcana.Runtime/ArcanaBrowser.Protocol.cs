using System.Text.Json;

namespace Arcana.Runtime;

public sealed partial class ArcanaBrowser
{
    /// <summary>Full native CDP access, including domains without a convenience wrapper.</summary>
    public Task<JsonElement> SendCdpAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        _connection.SendAsync(method, parameters, cancellationToken: cancellationToken);

    public CdpSubscription Subscribe(string? method = null, int capacity = 256) => _connection.Subscribe(method, capacity: capacity);

    public async Task<IReadOnlyList<BrowserTab>> GetTabsAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendCdpAsync("Target.getTargets", cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.GetProperty("targetInfos").EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page")
            .Select(t => new BrowserTab(t.GetProperty("targetId").GetString()!, t.GetProperty("url").GetString() ?? "", t.GetProperty("title").GetString() ?? "")).ToArray();
    }

    public async Task<ArcanaPage> AttachPageAsync(string targetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var result = await SendCdpAsync("Target.attachToTarget", new { targetId, flatten = true }, cancellationToken).ConfigureAwait(false);
        var page = new ArcanaPage(_connection, targetId, result.GetProperty("sessionId").GetString()!);
        await page.EnableAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    public Task<JsonElement> CloseTabAsync(string targetId, CancellationToken cancellationToken = default) =>
        SendCdpAsync("Target.closeTarget", new { targetId }, cancellationToken);

    public async Task<string> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendCdpAsync("Target.createBrowserContext", cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.GetProperty("browserContextId").GetString()!;
    }

    public Task<JsonElement> DisposeContextAsync(string browserContextId, CancellationToken cancellationToken = default) =>
        SendCdpAsync("Target.disposeBrowserContext", new { browserContextId }, cancellationToken);
}

public sealed record BrowserTab(string TargetId, string Url, string Title);
