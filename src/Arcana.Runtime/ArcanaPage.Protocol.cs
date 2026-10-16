using System.Text.Json;

namespace Arcana.Runtime;

public sealed partial class ArcanaPage
{
    public Task<JsonElement> SendCdpAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        _connection.SendAsync(method, parameters, _sessionId, cancellationToken);

    public CdpSubscription Subscribe(string? method = null, int capacity = 256) => _connection.Subscribe(method, _sessionId, capacity);

    public Task<JsonElement> CloseAsync(CancellationToken cancellationToken = default) =>
        _connection.SendAsync("Target.closeTarget", new { targetId = _targetId }, cancellationToken: cancellationToken);

    public Task<JsonElement> ReloadAsync(CancellationToken cancellationToken = default) => SendCdpAsync("Page.reload", cancellationToken: cancellationToken);

    public async Task<bool> GoBackAsync(CancellationToken cancellationToken = default) => await MoveHistoryAsync(-1, cancellationToken).ConfigureAwait(false);
    public async Task<bool> GoForwardAsync(CancellationToken cancellationToken = default) => await MoveHistoryAsync(1, cancellationToken).ConfigureAwait(false);

    private async Task<bool> MoveHistoryAsync(int offset, CancellationToken cancellationToken)
    {
        var history = await SendCdpAsync("Page.getNavigationHistory", cancellationToken: cancellationToken).ConfigureAwait(false);
        var index = history.GetProperty("currentIndex").GetInt32() + offset;
        var entries = history.GetProperty("entries");
        if (index < 0 || index >= entries.GetArrayLength()) return false;
        await SendCdpAsync("Page.navigateToHistoryEntry", new { entryId = entries[index].GetProperty("id").GetInt32() }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<string> GetMarkdownAsync(CancellationToken cancellationToken = default) =>
        (await SendCdpAsync("LP.getMarkdown", cancellationToken: cancellationToken).ConfigureAwait(false)).GetProperty("markdown").GetString() ?? "";

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default) => EvaluateStringAsync("document.body?.innerText ?? ''", cancellationToken);

    public async Task WaitForAsync(string expression, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var result = await EvaluateAsync($"Boolean({expression})", deadline.Token).ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.True) return;
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Page condition was not satisfied before the deadline."); }
    }

    public Task WaitForSelectorAsync(string selector, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitForAsync($"document.querySelector({JsonSerializer.Serialize(selector)}) !== null", timeout, cancellationToken);

    public Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitForAsync($"(document.body?.textContent ?? '').includes({JsonSerializer.Serialize(text)})", timeout, cancellationToken);

    public async Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken = default)
    {
        await NavigateAsync(url, cancellationToken).ConfigureAwait(false);
        await WaitForAsync("document.readyState === 'complete'", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> SetViewportAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(width), "Viewport dimensions must be 1–8192 CSS pixels.");
        return SendCdpAsync("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile = false }, cancellationToken);
    }

    public async Task<byte[]> CaptureScreenshotAsync(bool fullPage, CancellationToken cancellationToken = default)
    {
        object parameters = new { format = "png", captureBeyondViewport = false };
        if (fullPage)
        {
            var metrics = await SendCdpAsync("Page.getLayoutMetrics", cancellationToken: cancellationToken).ConfigureAwait(false);
            var size = metrics.GetProperty("contentSize");
            parameters = new { format = "png", captureBeyondViewport = true,
                clip = new { x = 0, y = 0, width = size.GetProperty("width").GetDouble(), height = size.GetProperty("height").GetDouble(), scale = 1 } };
        }
        var result = await SendCdpAsync("Page.captureScreenshot", parameters, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(result.GetProperty("data").GetString()!);
    }

    public async Task<byte[]> CaptureScreenshotAsync(string format, int? quality = null, CancellationToken cancellationToken = default)
    {
        if (format is not ("png" or "jpeg")) throw new ArgumentException("Screenshot format must be png or jpeg.", nameof(format));
        var parameters = new Dictionary<string, object?> { ["format"] = format };
        if (quality is not null) parameters["quality"] = quality;
        var result = await SendCdpAsync("Page.captureScreenshot", parameters, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(result.GetProperty("data").GetString()!);
    }

    public async Task<byte[]> PrintPdfAsync(bool landscape = false, CancellationToken cancellationToken = default)
    {
        var result = await SendCdpAsync("Page.printToPDF", new { landscape, printBackground = true }, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(result.GetProperty("data").GetString()!);
    }

    public Task<JsonElement> GetCookiesAsync(CancellationToken cancellationToken = default) => SendCdpAsync("Network.getCookies", cancellationToken: cancellationToken);
    public Task<JsonElement> SetCookiesAsync(object[] cookies, CancellationToken cancellationToken = default) => SendCdpAsync("Network.setCookies", new { cookies }, cancellationToken);
    public Task<JsonElement> ClearCookiesAsync(CancellationToken cancellationToken = default) => SendCdpAsync("Network.clearBrowserCookies", cancellationToken: cancellationToken);
    public Task<JsonElement> SetHeadersAsync(IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default) =>
        SendCdpAsync("Network.setExtraHTTPHeaders", new { headers }, cancellationToken);

    public async Task ClickAtAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        await SendCdpAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, cancellationToken).ConfigureAwait(false);
        await SendCdpAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PressKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await SendCdpAsync("Input.dispatchKeyEvent", new { type = "keyDown", key }, cancellationToken).ConfigureAwait(false);
        await SendCdpAsync("Input.dispatchKeyEvent", new { type = "keyUp", key }, cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> TypeTextAsync(string text, CancellationToken cancellationToken = default) =>
        SendCdpAsync("Input.dispatchKeyEvent", new { type = "char", text }, cancellationToken);

    public Task<JsonElement> ScrollAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        return SendCdpAsync("Input.dispatchMouseEvent", new { type = "mouseWheel", x = 0, y = 0, deltaX = x, deltaY = y }, cancellationToken);
    }
}
