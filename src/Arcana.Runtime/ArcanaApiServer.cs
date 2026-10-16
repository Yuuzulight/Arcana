using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arcana.Runtime;

/// <summary>Versioned loopback transport for the engine-neutral page contract.</summary>
public sealed class ArcanaApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ArcanaBrowser _browser;
    private readonly string _token;
    private readonly string? _profileDirectory;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, ArcanaPage> _pages = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private bool _disposed;

    public ArcanaApiServer(ArcanaBrowser browser, string token, int port = 0, string? profileDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _browser = browser;
        _token = token;
        _profileDirectory = profileDirectory;
        Port = port == 0 ? GetFreePort() : port;
        Endpoint = new Uri($"http://127.0.0.1:{Port}/");
        _listener.Prefixes.Add(Endpoint.ToString());
    }

    public const string Version = ArcanaOptions.ApiVersion;
    public Uri Endpoint { get; }
    public int Port { get; }
    public bool IsRunning => _listener.IsListening;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener.IsListening) return;
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (HttpListenerException) { }
        }
        foreach (var page in _pages.Values) await page.CloseAsync().ConfigureAwait(false);
        _pages.Clear();
        _listener.Close();
        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { break; }
            _ = Task.Run(() => HandleAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!Authorize(context.Request)) { await ErrorAsync(context.Response, 401, "unauthorized", "A valid bearer token is required."); return; }
            if (!AllowedOrigin(context.Request)) { await ErrorAsync(context.Response, 403, "origin_forbidden", "The request origin is not loopback."); return; }
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (context.Request.IsWebSocketRequest && path == "/v1/events") { await EventsAsync(context, cancellationToken); return; }
            if (context.Request.HttpMethod == "GET" && path == "/v1/health")
            {
                await JsonAsync(context.Response, 200, new { apiVersion = Version, engine = await _browser.SendCdpAsync("Browser.getVersion", cancellationToken: cancellationToken) });
                return;
            }
            if (context.Request.HttpMethod == "GET" && path == "/v1/tabs") { await JsonAsync(context.Response, 200, await _browser.GetTabsAsync(cancellationToken)); return; }
            if (path == "/v1/pages" && context.Request.HttpMethod == "POST") { await CreatePageAsync(context, cancellationToken); return; }
            if (!path.StartsWith("/v1/pages/", StringComparison.Ordinal)) { await ErrorAsync(context.Response, 404, "not_found", "The API route was not found."); return; }
            var pieces = path[10..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length == 0 || !_pages.TryGetValue(pieces[0], out var page)) { await ErrorAsync(context.Response, 404, "page_not_found", "The page does not exist."); return; }
            var operation = pieces.Length == 1 ? "" : pieces[1];
            await DispatchPageAsync(context, pieces[0], page, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException error) { await ErrorAsync(context.Response, 400, "invalid_request", error.Message); }
        catch (TimeoutException error) { await ErrorAsync(context.Response, 408, "timeout", error.Message); }
        catch (KeyNotFoundException error) { await ErrorAsync(context.Response, 404, "not_found", error.Message); }
        catch (Exception error) when (error is InvalidOperationException or CdpException)
        { await ErrorAsync(context.Response, 409, "operation_failed", error.Message); }
        catch (Exception error) { await ErrorAsync(context.Response, 500, "engine_error", error.Message); }
    }

    private async Task CreatePageAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadJsonAsync<CreateRequest>(context.Request, cancellationToken);
        var page = await _browser.NewPageAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await RestoreCookiesAsync(page, cancellationToken).ConfigureAwait(false);
        if (body?.Url is not null) await page.NavigateAndWaitAsync(body.Url, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        _pages[id] = page;
        await JsonAsync(context.Response, 201, new { pageId = id, url = body?.Url ?? "about:blank" });
    }

    private async Task RestoreCookiesAsync(ArcanaPage page, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_profileDirectory)) return;
        var stateFile = Path.Combine(_profileDirectory, "credentials.json");
        if (!File.Exists(stateFile)) return;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile, cancellationToken).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
                await page.SetCookiesAsync(cookies.EnumerateArray().Select(cookie => (object)cookie.Clone()).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { }
    }

    private async Task DispatchPageAsync(HttpListenerContext context, string id, ArcanaPage page, string operation, CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod == "DELETE" && operation.Length == 0) { _pages.TryRemove(id, out _); await page.CloseAsync(cancellationToken); await JsonAsync(context.Response, 204, null); return; }
        if (context.Request.HttpMethod == "GET" && operation == "snapshot") { await JsonAsync(context.Response, 200, await page.SnapshotAsync(cancellationToken)); return; }
        if (context.Request.HttpMethod == "GET" && operation == "cookies") { await JsonAsync(context.Response, 200, await page.GetCookiesAsync(cancellationToken)); return; }
        if (context.Request.HttpMethod == "GET" && operation == "storage") { await JsonAsync(context.Response, 200, await page.ExportStorageAsync(cancellationToken)); return; }
        if (context.Request.HttpMethod == "PUT" && operation is "cookies" or "storage")
        {
            using var stateDocument = await ReadDocumentAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (stateDocument is null) throw new ArgumentException("request body is required.");
            if (operation == "cookies")
            {
                if (stateDocument.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("cookies must be an array.");
                await page.SetCookiesAsync(stateDocument.RootElement.EnumerateArray().Select(cookie => (object)cookie.Clone()).ToArray(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var state = stateDocument.RootElement.Deserialize<PageStorageState>(JsonOptions) ?? throw new ArgumentException("storage state is invalid.");
                await page.RestoreStorageAsync(state, cancellationToken).ConfigureAwait(false);
            }
            await JsonAsync(context.Response, 200, new { ok = true }); return;
        }
        if (context.Request.HttpMethod == "GET" && operation == "screenshot")
        {
            var format = context.Request.QueryString["format"] is "jpeg" ? "jpeg" : "png";
            var quality = int.TryParse(context.Request.QueryString["quality"], out var q) ? q : (int?)null;
            await BytesAsync(context.Response, format == "jpeg" ? "image/jpeg" : "image/png", await page.CaptureScreenshotAsync(format, quality, cancellationToken)); return;
        }
        if (context.Request.HttpMethod != "POST") { await ErrorAsync(context.Response, 405, "method_not_allowed", "The method is not supported for this page route."); return; }
        using var requestJson = await ReadDocumentAsync(context.Request, cancellationToken).ConfigureAwait(false);
        var root = requestJson?.RootElement;
        var body = root is { } bodyRoot ? bodyRoot.Deserialize<ActionRequest>(JsonOptions) ?? new ActionRequest() : new ActionRequest();
        switch (operation)
        {
            case "storage":
                var state = root is { } storageRoot ? storageRoot.Deserialize<PageStorageState>(JsonOptions) : null;
                if (state is null) throw new ArgumentException("storage state is required.");
                await page.RestoreStorageAsync(state, cancellationToken); break;
            case "find":
                var query = body.Value ?? throw new ArgumentException("value is required.");
                await JsonAsync(context.Response, 200, await page.EvaluateAsync($"Array.from(document.querySelectorAll('a,button,input,textarea,select,[role]')).filter(el => (el.innerText || el.getAttribute('aria-label') || el.value || '').toLowerCase().includes({JsonSerializer.Serialize(query.ToLowerInvariant())})).map(el => ({{tag:el.tagName.toLowerCase(),text:(el.innerText||el.getAttribute('aria-label')||el.value||'').trim()}}))", cancellationToken)); return;
            case "cdp":
                var cdp = root is { } cdpRoot ? cdpRoot.Deserialize<CdpRequest>(JsonOptions) : null;
                if (cdp is null) throw new ArgumentException("CDP request is required.");
                await JsonAsync(context.Response, 200, await page.SendCdpAsync(cdp.Method ?? throw new ArgumentException("method is required."), cdp.Parameters, cancellationToken)); return;
            case "evaluate":
                var evaluated = await page.EvaluateAsync(body.Expression ?? throw new ArgumentException("expression is required."), cancellationToken);
                await JsonAsync(context.Response, 200, evaluated); return;
            case "viewport": await page.SetViewportAsync(body.Width, body.Height, cancellationToken); break;
            case "click-at": await page.ClickAtAsync(body.X, body.Y, cancellationToken); break;
            case "key": await page.PressKeyAsync(body.Key ?? throw new ArgumentException("key is required."), cancellationToken); break;
            case "type": await page.TypeTextAsync(body.Value ?? "", cancellationToken); break;
            case "mouse-wheel": await page.ScrollAsync(body.DeltaX, body.DeltaY, cancellationToken); break;
            case "navigate": await page.NavigateAndWaitAsync(body.Url ?? throw new ArgumentException("url is required."), cancellationToken); break;
            case "back": await page.GoBackAsync(cancellationToken); break;
            case "forward": await page.GoForwardAsync(cancellationToken); break;
            case "reload": await page.ReloadAsync(cancellationToken); break;
            case "click": await page.ClickAsync(body.Ref ?? throw new ArgumentException("ref is required."), cancellationToken); break;
            case "fill": await page.FillAsync(body.Ref ?? throw new ArgumentException("ref is required."), body.Value ?? throw new ArgumentException("value is required."), cancellationToken); break;
            case "select": await page.SelectOptionAsync(body.Ref ?? throw new ArgumentException("ref is required."), body.Value ?? throw new ArgumentException("value is required."), cancellationToken); break;
            case "press": await page.PressKeyAsync(body.Key ?? throw new ArgumentException("key is required."), cancellationToken); break;
            case "scroll": await page.ScrollAsync(body.DeltaX, body.DeltaY, cancellationToken); break;
            default: await ErrorAsync(context.Response, 404, "not_found", "The page operation was not found."); return;
        }
        await JsonAsync(context.Response, 200, new { ok = true });
    }

    private async Task EventsAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var id = context.Request.QueryString["pageId"];
        if (id is null || !_pages.TryGetValue(id, out var page)) { await ErrorAsync(context.Response, 404, "page_not_found", "A pageId query parameter is required."); return; }
        var socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
        using var events = page.Subscribe();
        try
        {
            await foreach (var evt in events.ReadAllAsync(cancellationToken))
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(new { type = "event", pageId = id, name = evt.Method, data = evt.Parameters }, JsonOptions);
                await socket.SendAsync(data, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally { events.Dispose(); try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { } socket.Dispose(); }
    }

    private bool Authorize(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var supplied = Encoding.UTF8.GetBytes(header[7..]);
        var expected = Encoding.UTF8.GetBytes(_token);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static bool AllowedOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host is "127.0.0.1" or "localhost" or "[::1]";
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 == 0) return default;
        return await JsonSerializer.DeserializeAsync<T>(request.InputStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
    }

    private static async Task<JsonDocument?> ReadDocumentAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 == 0) return null;
        return await JsonDocument.ParseAsync(request.InputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task JsonAsync(HttpListenerResponse response, int status, object? value)
    {
        response.StatusCode = status;
        if (status == 204) { response.Close(); return; }
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task BytesAsync(HttpListenerResponse response, string contentType, byte[] bytes)
    {
        response.StatusCode = 200; response.ContentType = contentType; response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes); response.Close();
    }

    private static async Task ErrorAsync(HttpListenerResponse response, int status, string code, string message) => await JsonAsync(response, status, new { error = new { code, message } });

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record CreateRequest(string? Url);
    private sealed record ActionRequest(string? Url = null, string? Ref = null, string? Value = null, string? Key = null, string? Expression = null,
        int Width = 1280, int Height = 720, double X = 0, double Y = 0, double DeltaX = 0, double DeltaY = 0);
    private sealed record CdpRequest(string? Method, JsonElement Parameters);
}
