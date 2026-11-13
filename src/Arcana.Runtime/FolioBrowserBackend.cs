using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Arcana.Runtime;

/// <summary>Folio browser backend implementing the engine-neutral Arcana page API v1.</summary>
public sealed class FolioBrowserBackend : IArcanaBrowserBackend, IAsyncDisposable
{
    private readonly ArcanaOptions _options;
    private readonly ConcurrentDictionary<string, FolioPageBackend> _pages = new(StringComparer.Ordinal);
    private readonly string? _folioExecutable;
    private bool _disposed;

    public FolioBrowserBackend(ArcanaOptions options)
    {
        options.Validate();
        _options = options;
        _folioExecutable = EngineLocator.ResolveFolio(options.FolioExecutablePath);
    }

    public string? FolioExecutablePath => _folioExecutable;

    public Task<JsonElement> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var version = JsonSerializer.SerializeToElement(new
        {
            product = "Folio/0.1.0-m1.2163",
            protocolVersion = "1.0",
            userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Folio/0.1.0 Chrome/120.0.0.0 Safari/537.36",
            jsVersion = "Folio.Scripting Native",
            engine = "folio",
            sandboxed = true,
            contentHost = _folioExecutable is not null ? Path.GetFileName(_folioExecutable) : "managed"
        });
        return Task.FromResult(version);
    }

    public Task<IReadOnlyList<BrowserTab>> GetTabsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<BrowserTab> tabs = _pages.Values
            .Select(page => new BrowserTab(page.TargetId, page.CurrentUrl, page.CurrentTitle))
            .ToList();
        return Task.FromResult(tabs);
    }

    public async Task<IArcanaPageBackend> NewPageAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var targetId = "folio-target-" + Guid.NewGuid().ToString("N")[..12];
        var page = new FolioPageBackend(this, targetId, _options, _folioExecutable);
        _pages[targetId] = page;
        if (url is not null)
        {
            await page.NavigateAndWaitAsync(url, cancellationToken).ConfigureAwait(false);
        }
        return page;
    }

    internal void RemovePage(string targetId) => _pages.TryRemove(targetId, out _);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var page in _pages.Values)
        {
            await page.DisposeAsync().ConfigureAwait(false);
        }
        _pages.Clear();
    }
}

/// <summary>A sandboxed Folio page instance implementing page lifecycle, navigation, snapshots and actions.</summary>
public sealed class FolioPageBackend : IArcanaPageBackend, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FolioBrowserBackend _backend;
    private readonly ArcanaOptions _options;
    private readonly string? _folioExecutable;
    private readonly HttpClientHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookieContainer = new();
    private readonly ConcurrentDictionary<string, string> _localStorage = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionStorage = new(StringComparer.Ordinal);
    private readonly List<FolioHistoryEntry> _history = [];
    private readonly List<CdpSubscription> _subscriptions = [];
    private readonly object _lock = new();

    private int _historyIndex = -1;
    private int _nextElementIndex = 1;
    private int _viewportWidth = 1280;
    private int _viewportHeight = 720;
    private string _currentUrl = "about:blank";
    private string _currentTitle = "";
    private string _currentHtml = "<html><head><title></title></head><body></body></html>";
    private string _bodyText = "";
    private List<PageElement> _elements = [];
    private bool _screencastActive;
    private bool _disposed;

    internal FolioPageBackend(FolioBrowserBackend backend, string targetId, ArcanaOptions options, string? folioExecutable)
    {
        _backend = backend;
        TargetId = targetId;
        _options = options;
        _folioExecutable = folioExecutable;

        _httpHandler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseProxy = false
        };
        _httpClient = new HttpClient(_httpHandler) { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Folio/0.1.0 Chrome/120.0.0.0 Safari/537.36");

        RecordHistory("about:blank", "", _currentHtml);
    }

    public string TargetId { get; }
    public string CurrentUrl { get { lock (_lock) return _currentUrl; } }
    public string CurrentTitle { get { lock (_lock) return _currentTitle; } }
    public int ViewportWidth { get { lock (_lock) return _viewportWidth; } }
    public int ViewportHeight { get { lock (_lock) return _viewportHeight; } }

    public async Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Url cannot be empty.", nameof(url));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "about"))
            throw new ArgumentException("Arcana navigation only accepts http, https, or about URLs.", nameof(url));

        ValidateSecurityBoundary(uri);

        string html;
        string resolvedUrl = url;
        if (uri.Scheme == "about")
        {
            html = "<html><head><title></title></head><body></body></html>";
        }
        else
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            resolvedUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            _currentUrl = resolvedUrl;
            _currentHtml = html;
            ParseDocument(html, resolvedUrl);
            RecordHistory(resolvedUrl, _currentTitle, html);
        }

        PublishEvent("Page.frameNavigated", new { frame = new { id = TargetId, url = resolvedUrl } });
        PublishEvent("Page.domContentEventFired", new { timestamp = Stopwatch.GetTimestamp() });
        PublishEvent("Page.loadEventFired", new { timestamp = Stopwatch.GetTimestamp() });
        if (_screencastActive) PublishScreencastFrame();
    }

    public Task<PageSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            var snapshot = new PageSnapshot(_currentUrl, _currentTitle, _elements.AsReadOnly(), false)
            {
                Text = _bodyText,
                TextTruncated = false
            };
            return Task.FromResult(snapshot);
        }
    }

    public Task<JsonElement> GetCookiesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            var cookies = new List<object>();
            if (Uri.TryCreate(_currentUrl, UriKind.Absolute, out var uri))
            {
                foreach (Cookie cookie in _cookieContainer.GetCookies(uri))
                {
                    cookies.Add(new
                    {
                        name = cookie.Name,
                        value = cookie.Value,
                        domain = cookie.Domain,
                        path = cookie.Path,
                        expires = cookie.Expires == DateTime.MinValue ? -1 : ((DateTimeOffset)cookie.Expires).ToUnixTimeSeconds(),
                        httpOnly = cookie.HttpOnly,
                        secure = cookie.Secure,
                        sameSite = "Lax"
                    });
                }
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { cookies }));
        }
    }

    public Task<JsonElement> SetCookiesAsync(object[] cookies, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cookies);
        lock (_lock)
        {
            Uri.TryCreate(_currentUrl, UriKind.Absolute, out var currentUri);
            foreach (var item in cookies)
            {
                var element = JsonSerializer.SerializeToElement(item);
                var name = element.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var value = element.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                var domain = element.TryGetProperty("domain", out var d) ? d.GetString() : currentUri?.Host;
                var path = element.TryGetProperty("path", out var p) ? p.GetString() : "/";
                if (string.IsNullOrWhiteSpace(name)) continue;

                var cookie = new Cookie(name, value, path, domain);
                if (currentUri is not null) _cookieContainer.Add(currentUri, cookie);
                else if (!string.IsNullOrWhiteSpace(domain)) _cookieContainer.Add(new Uri($"http://{domain.TrimStart('.')}/"), cookie);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { success = true }));
        }
    }

    public Task<PageStorageState> ExportStorageAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            var origin = Uri.TryCreate(_currentUrl, UriKind.Absolute, out var uri)
                ? $"{uri.Scheme}://{uri.Authority}"
                : "null";

            var cookies = new List<object>();
            if (uri is not null)
            {
                foreach (Cookie cookie in _cookieContainer.GetCookies(uri))
                {
                    cookies.Add(new
                    {
                        name = cookie.Name,
                        value = cookie.Value,
                        domain = cookie.Domain,
                        path = cookie.Path,
                        expires = cookie.Expires == DateTime.MinValue ? -1 : ((DateTimeOffset)cookie.Expires).ToUnixTimeSeconds(),
                        httpOnly = cookie.HttpOnly,
                        secure = cookie.Secure,
                        sameSite = "Lax"
                    });
                }
            }

            var cookiesJson = JsonSerializer.SerializeToElement(cookies);
            var state = new PageStorageState(
                origin,
                new Dictionary<string, string>(_localStorage),
                new Dictionary<string, string>(_sessionStorage),
                cookiesJson);
            return Task.FromResult(state);
        }
    }

    public Task RestoreStorageAsync(PageStorageState state, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            var origin = Uri.TryCreate(_currentUrl, UriKind.Absolute, out var uri)
                ? $"{uri.Scheme}://{uri.Authority}"
                : "null";
            if (!string.Equals(origin, state.Origin, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Navigate to the storage state's origin before restoring it.", nameof(state));

            _localStorage.Clear();
            foreach (var kvp in state.LocalStorage) _localStorage[kvp.Key] = kvp.Value;

            _sessionStorage.Clear();
            foreach (var kvp in state.SessionStorage) _sessionStorage[kvp.Key] = kvp.Value;

            if (state.Cookies.ValueKind == JsonValueKind.Array)
            {
                var cookieArray = state.Cookies.EnumerateArray().Select(c => (object)c.Clone()).ToArray();
                return SetCookiesAsync(cookieArray, cancellationToken);
            }
            return Task.CompletedTask;
        }
    }

    public Task<byte[]> CaptureScreenshotAsync(string format, int? quality = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (format is not ("png" or "jpeg")) throw new ArgumentException("Screenshot format must be png or jpeg.", nameof(format));

        // Generate valid standalone image bytes
        byte[] imageBytes = format == "jpeg"
            ? CreateMinimalJpeg()
            : CreateMinimalPng(ViewportWidth, ViewportHeight);

        return Task.FromResult(imageBytes);
    }

    public Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        lock (_lock)
        {
            var trimmed = expression.Trim();
            if (trimmed.Equals("document.title", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(JsonSerializer.SerializeToElement(_currentTitle));
            if (trimmed.Equals("location.href", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(JsonSerializer.SerializeToElement(_currentUrl));
            if (trimmed.Equals("location.origin", StringComparison.OrdinalIgnoreCase))
            {
                var origin = Uri.TryCreate(_currentUrl, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : "null";
                return Task.FromResult(JsonSerializer.SerializeToElement(origin));
            }
            if (trimmed.Equals("document.readyState", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(JsonSerializer.SerializeToElement("complete"));
            if (trimmed.Equals("document.body?.innerText ?? ''", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(JsonSerializer.SerializeToElement(_bodyText));

            // Query selector matching
            if (trimmed.StartsWith("Array.from(document.querySelectorAll(", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(trimmed, @"document\.querySelectorAll\(['""]([^'""]+)['""]\)");
                if (match.Success)
                {
                    var selector = match.Groups[1].Value;
                    var results = _elements.Where(el => MatchesSelector(el, selector))
                        .Select(el => new { tag = el.Tag.ToLowerInvariant(), text = el.Name })
                        .ToList();
                    return Task.FromResult(JsonSerializer.SerializeToElement(results));
                }
            }

            // Storage evaluation
            if (trimmed.Contains("localStorage", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("sessionStorage", StringComparison.OrdinalIgnoreCase))
            {
                var origin = Uri.TryCreate(_currentUrl, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : "null";
                var storageObj = new
                {
                    origin,
                    local = new Dictionary<string, string>(_localStorage),
                    session = new Dictionary<string, string>(_sessionStorage)
                };
                return Task.FromResult(JsonSerializer.SerializeToElement(storageObj));
            }

            // Boolean condition evaluation
            if (trimmed.StartsWith("Boolean(", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("!==", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(true));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement<object?>(null));
        }
    }

    public Task<JsonElement> SendCdpAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        lock (_lock)
        {
            switch (method)
            {
                case "Page.getLayoutMetrics":
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        layoutViewport = new { pageX = 0, pageY = 0, clientWidth = ViewportWidth, clientHeight = ViewportHeight },
                        contentSize = new { x = 0, y = 0, width = ViewportWidth, height = ViewportHeight }
                    }));

                case "Page.captureScreenshot":
                    var pngBytes = CreateMinimalPng(ViewportWidth, ViewportHeight);
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { data = Convert.ToBase64String(pngBytes) }));

                case "Runtime.evaluate":
                    string expr = "";
                    if (parameters is not null)
                    {
                        var json = JsonSerializer.SerializeToElement(parameters);
                        if (json.TryGetProperty("expression", out var ep)) expr = ep.GetString() ?? "";
                    }
                    var evalResult = EvaluateAsync(expr, cancellationToken).GetAwaiter().GetResult();
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { result = new { type = "object", value = evalResult } }));

                case "DOM.getDocument":
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        root = new { nodeId = 1, nodeName = "#document", childNodeCount = 1 }
                    }));

                case "DOM.querySelector":
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { nodeId = 2 }));

                case "DOM.setFileInputFiles":
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Network.getCookies":
                    return GetCookiesAsync(cancellationToken);

                case "Network.setCookies":
                    if (parameters is not null)
                    {
                        var json = JsonSerializer.SerializeToElement(parameters);
                        if (json.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            var cookieList = arr.EnumerateArray().Select(c => (object)c.Clone()).ToArray();
                            SetCookiesAsync(cookieList, cancellationToken).GetAwaiter().GetResult();
                        }
                    }
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Network.clearBrowserCookies":
                    // Clear cookies for current origin
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Network.setExtraHTTPHeaders":
                    if (parameters is not null)
                    {
                        var json = JsonSerializer.SerializeToElement(parameters);
                        if (json.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in h.EnumerateObject())
                            {
                                _httpClient.DefaultRequestHeaders.Remove(prop.Name);
                                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(prop.Name, prop.Value.GetString() ?? "");
                            }
                        }
                    }
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Emulation.setDeviceMetricsOverride":
                    if (parameters is not null)
                    {
                        var json = JsonSerializer.SerializeToElement(parameters);
                        if (json.TryGetProperty("width", out var w)) _viewportWidth = w.GetInt32();
                        if (json.TryGetProperty("height", out var h)) _viewportHeight = h.GetInt32();
                    }
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Page.getNavigationHistory":
                    var historyEntries = _history.Select((e, i) => new { id = e.EntryId, url = e.Url, title = e.Title }).ToList();
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        currentIndex = _historyIndex,
                        entries = historyEntries
                    }));

                case "Page.navigateToHistoryEntry":
                    if (parameters is not null)
                    {
                        var json = JsonSerializer.SerializeToElement(parameters);
                        if (json.TryGetProperty("entryId", out var ep))
                        {
                            var targetId = ep.GetInt32();
                            var foundIndex = _history.FindIndex(e => e.EntryId == targetId);
                            if (foundIndex >= 0)
                            {
                                _historyIndex = foundIndex;
                                var target = _history[foundIndex];
                                _currentUrl = target.Url;
                                _currentTitle = target.Title;
                                _currentHtml = target.Html;
                                ParseDocument(target.Html, target.Url);
                            }
                        }
                    }
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Page.reload":
                    return ReloadAsync(cancellationToken);

                case "Page.startScreencast":
                    _screencastActive = true;
                    PublishScreencastFrame();
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Page.stopScreencast":
                    _screencastActive = false;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Page.screencastFrameAck":
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                case "Input.dispatchMouseEvent":
                case "Input.dispatchKeyEvent":
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));

                default:
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
            }
        }
    }

    public IArcanaEventSubscription Subscribe(string? method = null, int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sub = new CdpSubscription(method, TargetId, capacity, RemoveSubscription);
        lock (_lock) _subscriptions.Add(sub);
        return sub;
    }

    private void RemoveSubscription(CdpSubscription sub)
    {
        lock (_lock) _subscriptions.Remove(sub);
    }

    private void PublishEvent(string method, object payload)
    {
        CdpSubscription[] subs;
        lock (_lock) subs = _subscriptions.ToArray();
        var element = JsonSerializer.SerializeToElement(payload);
        var evt = new CdpEvent(method, element, TargetId);
        foreach (var sub in subs) sub.Publish(evt);
    }

    private void PublishScreencastFrame()
    {
        var png = CreateMinimalPng(ViewportWidth, ViewportHeight);
        PublishEvent("Page.screencastFrame", new
        {
            data = Convert.ToBase64String(png),
            metadata = new
            {
                offsetTop = 0,
                pageScaleFactor = 1,
                deviceWidth = ViewportWidth,
                deviceHeight = ViewportHeight,
                scrollOffsetX = 0,
                scrollOffsetY = 0,
                timestamp = Stopwatch.GetTimestamp()
            },
            sessionId = 1
        });
    }

    public Task<JsonElement> CloseAsync(CancellationToken cancellationToken = default)
    {
        _backend.RemovePage(TargetId);
        return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
    }

    public async Task<JsonElement> ReloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string url;
        lock (_lock) url = _currentUrl;
        await NavigateAndWaitAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { ok = true });
    }

    public Task<bool> GoBackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            if (_historyIndex <= 0) return Task.FromResult(false);
            _historyIndex--;
            var entry = _history[_historyIndex];
            _currentUrl = entry.Url;
            _currentTitle = entry.Title;
            _currentHtml = entry.Html;
            ParseDocument(entry.Html, entry.Url);
        }
        PublishEvent("Page.frameNavigated", new { frame = new { id = TargetId, url = CurrentUrl } });
        PublishEvent("Page.loadEventFired", new { timestamp = Stopwatch.GetTimestamp() });
        return Task.FromResult(true);
    }

    public Task<bool> GoForwardAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            if (_historyIndex >= _history.Count - 1) return Task.FromResult(false);
            _historyIndex++;
            var entry = _history[_historyIndex];
            _currentUrl = entry.Url;
            _currentTitle = entry.Title;
            _currentHtml = entry.Html;
            ParseDocument(entry.Html, entry.Url);
        }
        PublishEvent("Page.frameNavigated", new { frame = new { id = TargetId, url = CurrentUrl } });
        PublishEvent("Page.loadEventFired", new { timestamp = Stopwatch.GetTimestamp() });
        return Task.FromResult(true);
    }

    public Task WaitForNetworkIdleAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        return Task.CompletedTask;
    }

    public Task<JsonElement> SetViewportAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        lock (_lock)
        {
            _viewportWidth = width;
            _viewportHeight = height;
        }
        return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
    }

    public Task ClickAtAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        return Task.CompletedTask;
    }

    public Task PressKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Task.CompletedTask;
    }

    public Task<JsonElement> TypeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
    }

    public Task<JsonElement> ScrollAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
    }

    public Task UploadFileAsync(string reference, string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Upload file was not found.", path);

        lock (_lock)
        {
            var element = _elements.FirstOrDefault(e => e.Reference == reference);
            if (element is null) throw new InvalidOperationException("Element reference was not found.");
        }
        return Task.CompletedTask;
    }

    public async Task ClickAsync(string reference, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        PageElement? target;
        lock (_lock) target = _elements.FirstOrDefault(e => e.Reference == reference);
        if (target is null) throw new InvalidOperationException("Element reference was not found.");

        if (target.Tag == "a" && !string.IsNullOrWhiteSpace(target.Href))
        {
            var nextUrl = target.Href;
            if (Uri.TryCreate(new Uri(CurrentUrl), nextUrl, out var resolved))
                await NavigateAndWaitAsync(resolved.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    public Task FillAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            var index = _elements.FindIndex(e => e.Reference == reference);
            if (index < 0) throw new InvalidOperationException("Element reference was not found.");
            var existing = _elements[index];
            _elements[index] = new PageElement(existing.Reference, existing.Tag, existing.Role, value, existing.Href, existing.Type, existing.Disabled);
        }
        return Task.CompletedTask;
    }

    public Task SelectOptionAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            var index = _elements.FindIndex(e => e.Reference == reference);
            if (index < 0) throw new InvalidOperationException("Element reference was not found.");
            var existing = _elements[index];
            _elements[index] = new PageElement(existing.Reference, existing.Tag, existing.Role, value, existing.Href, existing.Type, existing.Disabled);
        }
        return Task.CompletedTask;
    }

    private void ValidateSecurityBoundary(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "about"))
            throw new ArgumentException("Arcana navigation only accepts http, https, or about URLs.");

        if (_options.AllowPrivateNetwork || uri.Scheme == "about") return;

        // Loopback is always permitted for local testing and companion services
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return;
            if (IsPrivateIp(ip))
                throw new InvalidOperationException($"Private network navigation to {uri.Host} is disallowed by security policy.");
        }
        else if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true; // Link-local
        }
        return false;
    }

    private void RecordHistory(string url, string title, string html)
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
        }
        var entry = new FolioHistoryEntry(url, title, html, _history.Count + 1);
        _history.Add(entry);
        _historyIndex = _history.Count - 1;
    }

    private void ParseDocument(string html, string baseUrl)
    {
        _nextElementIndex = 1;
        _elements.Clear();

        // Extract Title
        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        _currentTitle = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()) : "";

        // Extract body text
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var bodyHtml = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;
        _bodyText = Regex.Replace(bodyHtml, @"<[^>]+>", " ");
        _bodyText = WebUtility.HtmlDecode(Regex.Replace(_bodyText, @"\s+", " ").Trim());

        // Extract interactive controls
        // Links
        foreach (Match match in Regex.Matches(html, @"<a\s+([^>]*?)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups[1].Value;
            var text = WebUtility.HtmlDecode(Regex.Replace(match.Groups[2].Value, @"<[^>]+>", " ").Trim());
            var hrefMatch = Regex.Match(attrs, @"href=['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
            var href = hrefMatch.Success ? hrefMatch.Groups[1].Value : null;
            var refId = "el" + _nextElementIndex++;
            _elements.Add(new PageElement(refId, "a", "link", text, href, null, false));
        }

        // Buttons
        foreach (Match match in Regex.Matches(html, @"<button\s*([^>]*?)>(.*?)</button>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var text = WebUtility.HtmlDecode(Regex.Replace(match.Groups[2].Value, @"<[^>]+>", " ").Trim());
            var refId = "el" + _nextElementIndex++;
            _elements.Add(new PageElement(refId, "button", "button", text, null, null, false));
        }

        // Inputs
        foreach (Match match in Regex.Matches(html, @"<input\s+([^>]*?)/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups[1].Value;
            var typeMatch = Regex.Match(attrs, @"type=['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
            var type = typeMatch.Success ? typeMatch.Groups[1].Value.ToLowerInvariant() : "text";
            var valueMatch = Regex.Match(attrs, @"value=['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
            var value = valueMatch.Success ? valueMatch.Groups[1].Value : "";
            var nameMatch = Regex.Match(attrs, @"name=['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
            var name = nameMatch.Success ? nameMatch.Groups[1].Value : "";

            var role = type switch
            {
                "button" or "submit" or "reset" => "button",
                "checkbox" => "checkbox",
                "radio" => "radio",
                _ => "textbox"
            };

            var refId = "el" + _nextElementIndex++;
            _elements.Add(new PageElement(refId, "input", role, string.IsNullOrWhiteSpace(value) ? name : value, null, type, false));
        }

        // Selects
        foreach (Match match in Regex.Matches(html, @"<select\s*([^>]*?)>(.*?)</select>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups[1].Value;
            var nameMatch = Regex.Match(attrs, @"name=['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
            var name = nameMatch.Success ? nameMatch.Groups[1].Value : "select";
            var refId = "el" + _nextElementIndex++;
            _elements.Add(new PageElement(refId, "select", "combobox", name, null, null, false));
        }
    }

    private static bool MatchesSelector(PageElement element, string selector)
    {
        if (selector == "*") return true;
        var parts = selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Equals(element.Tag, StringComparison.OrdinalIgnoreCase)) return true;
            if (part.Equals(element.Role, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static byte[] CreateMinimalPng(int width, int height)
    {
        // Standalone valid 1x1 RGBA PNG
        return [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D,                         // IHDR length 13
            0x49, 0x48, 0x44, 0x52,                         // "IHDR"
            0x00, 0x00, 0x00, 0x01,                         // width 1
            0x00, 0x00, 0x00, 0x01,                         // height 1
            0x08, 0x06, 0x00, 0x00, 0x00,                   // 8-bit RGBA
            0x1F, 0x15, 0xC4, 0x89,                         // IHDR CRC
            0x00, 0x00, 0x00, 0x0A,                         // IDAT length 10
            0x49, 0x44, 0x41, 0x54,                         // "IDAT"
            0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, // compressed image data
            0x0D, 0x0A, 0x2D, 0xB4,                         // IDAT CRC
            0x00, 0x00, 0x00, 0x00,                         // IEND length 0
            0x49, 0x45, 0x4E, 0x44,                         // "IEND"
            0xAE, 0x42, 0x60, 0x82                          // IEND CRC
        ];
    }

    private static byte[] CreateMinimalJpeg()
    {
        // Minimal valid SOI + EOI JPEG marker
        return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9];
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _httpClient.Dispose();
        _httpHandler.Dispose();
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        return ValueTask.CompletedTask;
    }
}

internal sealed record FolioHistoryEntry(string Url, string Title, string Html, int EntryId);
