using System.Text.Json;

namespace Arcana.Runtime;

public sealed partial class ArcanaPage
{
    private readonly CdpConnection _connection;
    private readonly string _targetId;
    private readonly string _sessionId;
    private readonly string _elementStore = "__arcana_" + Guid.NewGuid().ToString("N");

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
        var result = await _connection.SendAsync("Page.navigate", new { url }, _sessionId, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("errorText", out var error))
            throw new InvalidOperationException($"Navigation failed: {error.GetString()}");
    }

    public async Task<string?> EvaluateStringAsync(string expression, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var value = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var result = await _connection.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, _sessionId, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var exception))
            throw new InvalidOperationException($"Page JavaScript failed: {exception}");
        if (!result.TryGetProperty("result", out var remoteObject) || !remoteObject.TryGetProperty("value", out var value))
            return JsonSerializer.SerializeToElement<object?>(null);
        return value.Clone();
    }

    public Task<string?> GetHtmlAsync(CancellationToken cancellationToken = default) =>
        EvaluateStringAsync("document.documentElement?.outerHTML ?? ''", cancellationToken);

    /// <summary>Captures visible top-document controls. References survive snapshots but expire with the document.</summary>
    public async Task<PageSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var json = await EvaluateStringAsync(PageScripts.Snapshot(_elementStore), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PageSnapshot>(json ?? throw new InvalidOperationException("Snapshot was empty."),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Snapshot was invalid.");
    }

    /// <summary>Dispatches a DOM click after checking that the referenced control is still available.</summary>
    public async Task ClickAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        await EvaluateStringAsync(PageScripts.Action(_elementStore, reference, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a text control's value and dispatches input/change events.</summary>
    public async Task FillAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(value);
        await EvaluateStringAsync(PageScripts.Action(_elementStore, reference, value), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendAsync("Page.captureScreenshot", new { format = "png" }, _sessionId, cancellationToken).ConfigureAwait(false);
        var base64 = result.GetProperty("data").GetString()
            ?? throw new InvalidOperationException("CDP did not return screenshot data.");
        return Convert.FromBase64String(base64);
    }
}
