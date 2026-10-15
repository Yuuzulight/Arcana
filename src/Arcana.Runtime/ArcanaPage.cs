using System.Text.Json;

namespace Arcana.Runtime;

public sealed class ArcanaPage
{
    private readonly CdpConnection _connection;
    private readonly string _targetId;
    private readonly string _sessionId;

    internal ArcanaPage(CdpConnection connection, string targetId, string sessionId)
    {
        _connection = connection;
        _targetId = targetId;
        _sessionId = sessionId;
    }

    public string TargetId => _targetId;

    internal async Task EnableAsync(CancellationToken cancellationToken)
    {
        await _connection.SendAsync("Page.enable", sessionId: _sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _connection.SendAsync("Runtime.enable", sessionId: _sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "about"))
            throw new ArgumentException("Arcana navigation only accepts http, https, or about URLs.", nameof(url));
        await _connection.SendAsync("Page.navigate", new { url }, _sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> EvaluateStringAsync(string expression, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var result = await _connection.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, _sessionId, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("result", out var remoteObject) || !remoteObject.TryGetProperty("value", out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public Task<string?> GetHtmlAsync(CancellationToken cancellationToken = default) =>
        EvaluateStringAsync("document.documentElement?.outerHTML ?? ''", cancellationToken);

    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendAsync("Page.captureScreenshot", new { format = "png" }, _sessionId, cancellationToken).ConfigureAwait(false);
        var base64 = result.GetProperty("data").GetString()
            ?? throw new InvalidOperationException("CDP did not return screenshot data.");
        return Convert.FromBase64String(base64);
    }
}
