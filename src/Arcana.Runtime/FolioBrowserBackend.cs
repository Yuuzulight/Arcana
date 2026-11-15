using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Arcana.Runtime;

internal sealed class FolioNavigationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Opt-in Arcana adapter for Folio's AppContainer-backed headless broker.</summary>
public sealed class FolioBrowserBackend : IArcanaBrowserBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _brokerExecutable;
    private readonly string[] _allowedOrigins;
    private readonly string? _profileDirectory;
    private readonly bool _allowPrivateNetwork;
    private readonly ConcurrentDictionary<string, FolioPageBackend> _pages = new(StringComparer.Ordinal);
    private bool _disposed;

    public FolioBrowserBackend(string brokerExecutable, IEnumerable<string>? allowedOrigins = null, string? profileDirectory = null, bool allowPrivateNetwork = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerExecutable);
        _brokerExecutable = Path.GetFullPath(brokerExecutable);
        _allowedOrigins = allowedOrigins?.ToArray() ?? [];
        _profileDirectory = profileDirectory;
        _allowPrivateNetwork = allowPrivateNetwork;
        foreach (var origin in _allowedOrigins)
        {
            if (origin.Equals("https://*", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivateNetwork && uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host))))
                throw new ArgumentException("Folio allowed origins must be HTTPS origins or path prefixes without credentials, query, or fragment.", nameof(allowedOrigins));
        }
        if (!File.Exists(_brokerExecutable)) throw new FileNotFoundException("The Folio content broker executable was not found.", _brokerExecutable);
    }

    public FolioBrowserBackend(ArcanaOptions options) : this(ResolveBrokerPath(options), options.FolioAllowedOrigins, options.ProfileDirectory, options.AllowPrivateNetwork) { }

    private static bool IsLoopbackHost(string host)
        => host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

    private static string ResolveBrokerPath(ArcanaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options.FolioBrokerPath
            ?? EngineLocator.ResolveFolio(options.FolioExecutablePath)
            ?? throw new ArgumentException("The Folio backend requires FolioBrokerPath or a discoverable Folio.ContentHost build.", nameof(options));
    }

    /// <summary>Confirms the configured broker can start its required sandbox before the API starts listening.</summary>
    public async Task VerifyStartupAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var broker = await BrokerConnection.StartAsync(_brokerExecutable, _allowedOrigins, _profileDirectory, _allowPrivateNetwork, cancellationToken).ConfigureAwait(false);
        await broker.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            product = "Folio.ContentHost/native",
            protocolVersion = "v1",
            backend = "folio"
        }, JsonOptions));
    }

    public Task<IReadOnlyList<BrowserTab>> GetTabsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<BrowserTab> tabs = _pages.Select(pair => new BrowserTab(pair.Key, pair.Value.Url, pair.Value.Title)).ToArray();
        return Task.FromResult(tabs);
    }

    internal int GetBrokerProcessId(string tabId) => _pages[tabId].BrokerProcessId;

    public async Task<IArcanaPageBackend> NewPageAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Guid.NewGuid().ToString("N");
        var page = await FolioPageBackend.CreateAsync(_brokerExecutable, _allowedOrigins, _profileDirectory, _allowPrivateNetwork, url ?? "about:blank", cancellationToken).ConfigureAwait(false);
        if (!_pages.TryAdd(id, page))
        {
            await page.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A duplicate Folio page identifier was generated.");
        }
        page.Closed += () => _pages.TryRemove(id, out _);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var page in _pages.Values)
        {
            try { await page.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        _pages.Clear();
    }

    private static void EnsureSuccess(JsonDocument response)
    {
        if (response.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return;
        var code = response.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
        var message = response.RootElement.TryGetProperty("error", out error) && error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
        if (code == "timeout") throw new TimeoutException(message ?? "Folio navigation timed out.");
        if (code is "policy_refused" or "navigation_failed")
            throw new FolioNavigationException(code, message ?? "Folio navigation failed.");
        if (code is "unsupported" or "sandbox_unavailable" or "sandbox_bypassed")
            throw new NotSupportedException(message ?? "The Folio sandbox does not support this operation in the current environment.");
        throw new InvalidOperationException(message ?? $"Folio broker operation failed ({code ?? "unknown"}).");
    }

    private sealed class FolioPageBackend : IArcanaPageBackend
    {
        private const string BlankHtml = "<!doctype html><html><head><title></title></head><body></body></html>";
        private readonly BrokerConnection _broker;
        private readonly bool _navigationEnabled;
        private readonly SemaphoreSlim _navigationGate = new(1, 1);
        private readonly ConcurrentDictionary<CdpSubscription, byte> _subscriptions = new();
        private readonly List<NavigationEntry> _history = [];
        private int _historyIndex = -1;
        private string _referencePrefix = Guid.NewGuid().ToString("N") + ":";

        private sealed record NavigationEntry(string Url, string? Html = null);
        private int _closed;

        internal int BrokerProcessId => _broker.ProcessId;

        private FolioPageBackend(BrokerConnection broker, bool navigationEnabled)
        {
            _broker = broker;
            _navigationEnabled = navigationEnabled;
            _broker.EventReceived += PublishEvent;
            Url = "about:blank";
        }

        public event Action? Closed;
        public string Url { get; private set; }
        public string CurrentUrl => Url;
        public string Title { get; private set; } = "";

        public static async Task<FolioPageBackend> CreateAsync(string executable, string[] allowedOrigins, string? profileDirectory, bool allowPrivateNetwork, string url, CancellationToken cancellationToken)
        {
            var connection = await BrokerConnection.StartAsync(executable, allowedOrigins, profileDirectory, allowPrivateNetwork, cancellationToken).ConfigureAwait(false);
            var page = new FolioPageBackend(connection, allowedOrigins.Length > 0 || allowPrivateNetwork);
            try
            {
                await page.NavigateAndWaitAsync(url, cancellationToken).ConfigureAwait(false);
                return page;
            }
            catch
            {
                await page.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        public Task SetContentAsync(string html, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(html);
            return CommitNavigationAsync(new NavigationEntry("about:blank", html), replace: true, cancellationToken);
        }

        private async Task LoadAsync(string html, CancellationToken cancellationToken)
        {
            using var response = await _broker.RequestAsync("load", new { html, urlLabel = "about:blank" }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            _referencePrefix = Guid.NewGuid().ToString("N") + ":";
            Url = "about:blank";
            await UpdateSnapshotMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<PageSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var response = await _broker.RequestAsync("snapshot", new { }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
                if (!response.RootElement.TryGetProperty("snapshot", out var snapshot))
                    throw new InvalidOperationException("Folio broker did not return a page snapshot.");
                var pageSnapshot = snapshot.Deserialize<PageSnapshot>(JsonOptions)
                    ?? throw new InvalidOperationException("Folio broker returned an invalid page snapshot.");
                Url = pageSnapshot.Url;
                Title = pageSnapshot.Title;
                return pageSnapshot with
                {
                    Elements = pageSnapshot.Elements.Select(element => element with { Reference = FormatClientReference(element.Reference) }).ToArray()
                };
            }
            finally { _navigationGate.Release(); }
        }

        private async Task UpdateSnapshotMetadataAsync(CancellationToken cancellationToken)
        {
            using var response = await _broker.RequestAsync("snapshot", new { }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            if (!response.RootElement.TryGetProperty("snapshot", out var snapshot))
                throw new InvalidOperationException("Folio broker did not return a page snapshot.");
            Title = snapshot.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
            Url = snapshot.TryGetProperty("url", out var url) ? url.GetString() ?? Url : Url;
        }

        public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expression);
            var wrapped = $"JSON.stringify(({{ value: ({expression}) }}))";
            using var response = await _broker.RequestAsync("evaluate", new { expression = wrapped }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            if (!response.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                return JsonSerializer.SerializeToElement<object?>(null);
            using var parsed = JsonDocument.Parse(value.GetString() ?? "null");
            return parsed.RootElement.TryGetProperty("value", out var result) ? result.Clone() : JsonSerializer.SerializeToElement<object?>(null);
        }

        public Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            return CommitNavigationAsync(new NavigationEntry(url), replace: false, cancellationToken);
        }

        private async Task CommitNavigationAsync(NavigationEntry entry, bool replace, CancellationToken cancellationToken)
        {
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await LoadEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                if (replace && _historyIndex >= 0)
                    _history[_historyIndex] = entry;
                else
                {
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                    _history.Add(entry);
                    _historyIndex = _history.Count - 1;
                }
            }
            finally { _navigationGate.Release(); }
        }

        private async Task LoadEntryAsync(NavigationEntry entry, CancellationToken cancellationToken)
        {
            var url = entry.Url;
            if (url == "about:blank")
            {
                await LoadAsync(entry.Html ?? BlankHtml, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_navigationEnabled) throw new NotSupportedException("Folio navigation requires an explicit HTTPS origin policy.");

            using var response = await _broker.RequestAsync("navigate", new { url }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            _referencePrefix = Guid.NewGuid().ToString("N") + ":";
            Url = response.RootElement.GetProperty("url").GetString() ?? url;
            await UpdateSnapshotMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task WaitForNetworkIdleAsync(int timeoutMs, CancellationToken cancellationToken = default)
        {
            using var response = await _broker.RequestAsync("wait-network-idle", new { timeoutMs }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
        }

        public async Task<JsonElement> GetCookiesAsync(CancellationToken cancellationToken = default)
        {
            using var response = await _broker.RequestAsync("get-cookies", new { url = Url == "about:blank" ? null : Url }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            var cookies = response.RootElement.TryGetProperty("cookies", out var found)
                ? found.Clone()
                : JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonOptions);
            return JsonSerializer.SerializeToElement(new { cookies }, JsonOptions);
        }

        public async Task<JsonElement> SetCookiesAsync(object[] cookies, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cookies);
            using var response = await _broker.RequestAsync("set-cookies", new { cookies, url = Url == "about:blank" ? null : Url }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            return JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
        }

        public async Task SetInterceptionAsync(JsonElement rules, CancellationToken cancellationToken = default)
        {
            var payloadRules = rules.ValueKind == JsonValueKind.Array
                ? rules.Clone()
                : JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonOptions);
            using var response = await _broker.RequestAsync("set-interception", new { rules = payloadRules }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
        }
        public async Task<PageStorageState> ExportStorageAsync(CancellationToken cancellationToken = default)
        {
            var local = await EvaluateAsync("""
                (() => {
                    const entries = function(storage) {
                        const pairs = [];
                        for (let i = 0; i < storage.length; i++) {
                            const key = storage.key(i);
                            if (key !== null) pairs.push([key, storage.getItem(key)]);
                        }
                        return Object.fromEntries(pairs);
                    };
                    return { origin: location.origin, local: entries(localStorage), session: entries(sessionStorage) };
                })()
                """, cancellationToken).ConfigureAwait(false);
            var origin = local.TryGetProperty("origin", out var originProp) ? originProp.GetString() ?? "" : "";
            var localDict = local.TryGetProperty("local", out var localProp) && localProp.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(localProp) ?? new()
                : new Dictionary<string, string>();
            var sessionDict = local.TryGetProperty("session", out var sessionProp) && sessionProp.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(sessionProp) ?? new()
                : new Dictionary<string, string>();
            using var emptyCookies = JsonDocument.Parse("[]");
            return new PageStorageState(origin, localDict, sessionDict, emptyCookies.RootElement.Clone());
        }

        public async Task RestoreStorageAsync(PageStorageState state, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Cookies.ValueKind == JsonValueKind.Array && state.Cookies.GetArrayLength() > 0)
                throw new NotSupportedException("Folio storage restore does not support cookies.");

            var payload = JsonSerializer.Serialize(new { state.Origin, state.LocalStorage, state.SessionStorage });
            var restored = await EvaluateAsync($$"""
                (()=>{const state=JSON.parse({{JsonSerializer.Serialize(payload)}});
                  if (location.origin !== state.Origin) return false;
                  localStorage.clear(); sessionStorage.clear();
                  for(const [k,v] of Object.entries(state.LocalStorage || {})) localStorage.setItem(k,v);
                  for(const [k,v] of Object.entries(state.SessionStorage || {})) sessionStorage.setItem(k,v); return true;})()
                """, cancellationToken).ConfigureAwait(false);
            if (restored.ValueKind == JsonValueKind.False)
                throw new ArgumentException("Navigate to the storage state's origin before restoring it.", nameof(state));
            if (restored.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Folio did not confirm storage restoration.");
        }
        public async Task<byte[]> CaptureScreenshotAsync(string format, int? quality = null, CancellationToken cancellationToken = default)
        {
            if (format != "png") throw new NotSupportedException("The Folio broker supports PNG screenshots.");

            using var response = await _broker.RequestAsync("screenshot", new { }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            var payload = response.RootElement;
            if (!payload.TryGetProperty("mimeType", out var mimeType) || mimeType.ValueKind != JsonValueKind.String || mimeType.GetString() != "image/png" ||
                !payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String || !data.TryGetBytesFromBase64(out var bytes) ||
                !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidOperationException("Folio broker returned an invalid PNG screenshot.");

            return bytes;
        }
        public Task<JsonElement> SendCdpAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Unsupported<JsonElement>("CDP");

        public IArcanaEventSubscription Subscribe(string? method = null, int capacity = 256)
        {
            var subscription = new CdpSubscription(method, null, capacity, sub => _subscriptions.TryRemove(sub, out _));
            if (Volatile.Read(ref _closed) != 0)
            {
                subscription.Complete();
                return subscription;
            }
            _subscriptions[subscription] = 0;
            return subscription;
        }

        private void PublishEvent(CdpEvent evt)
        {
            foreach (var sub in _subscriptions.Keys)
                sub.Publish(evt);
        }
        public Task<JsonElement> CloseAsync(CancellationToken cancellationToken = default) => CloseCoreAsync(cancellationToken);
        public async Task<JsonElement> ReloadAsync(CancellationToken cancellationToken = default)
        {
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = _history[_historyIndex];
                await LoadEntryAsync(entry.Html is null ? new NavigationEntry(Url) : entry, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { });
            }
            finally { _navigationGate.Release(); }
        }

        public Task<bool> GoBackAsync(CancellationToken cancellationToken = default) => MoveHistoryAsync(-1, cancellationToken);
        public Task<bool> GoForwardAsync(CancellationToken cancellationToken = default) => MoveHistoryAsync(1, cancellationToken);

        private async Task<bool> MoveHistoryAsync(int delta, CancellationToken cancellationToken)
        {
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = _historyIndex + delta;
                if (index < 0 || index >= _history.Count) return false;
                await LoadEntryAsync(_history[index], cancellationToken).ConfigureAwait(false);
                _historyIndex = index;
                return true;
            }
            finally { _navigationGate.Release(); }
        }

        public async Task<JsonElement> SetViewportAsync(int width, int height, CancellationToken cancellationToken = default)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            using var response = await _broker.RequestAsync("viewport", new { width, height }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            return JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
        }

        public async Task ClickAtAsync(double x, double y, string button = "left", int clickCount = 1, CancellationToken cancellationToken = default)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
            if (button is not ("left" or "right" or "middle")) throw new ArgumentException("Mouse button must be left, right, or middle.", nameof(button));
            if (clickCount is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(clickCount));
            await SendInputAsync("click-at", new { x, y, button, clickCount }, cancellationToken).ConfigureAwait(false);
        }

        public async Task HoverAsync(string reference, CancellationToken cancellationToken = default)
        {
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var brokerRef = ResolveBrokerReference(reference);
                using var response = await _broker.RequestAsync("hover", new { reference = brokerRef }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
            }
            finally { _navigationGate.Release(); }
        }

        public async Task PressKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            await SendInputAsync("press", new { key }, cancellationToken).ConfigureAwait(false);
        }

        public Task<JsonElement> TypeTextAsync(string text, CancellationToken cancellationToken = default) =>
            SendInputAsync("type", new { text }, cancellationToken);

        public Task<JsonElement> ScrollAsync(double x, double y, CancellationToken cancellationToken = default)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
            return SendInputAsync("scroll", new { deltaX = x, deltaY = y }, cancellationToken);
        }

        private async Task<JsonElement> SendInputAsync(string operation, object payload, CancellationToken cancellationToken)
        {
            using var response = await _broker.RequestAsync(operation, payload, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            return JsonSerializer.SerializeToElement(new { });
        }

        public async Task UploadFileAsync(string reference, string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Upload file was not found.", path);
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var brokerRef = ResolveBrokerReference(reference);
                using var response = await _broker.RequestAsync("upload", new { reference = brokerRef, paths = new[] { fullPath } }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
            }
            finally { _navigationGate.Release(); }
        }

        public async Task ClickAsync(string reference, CancellationToken cancellationToken = default)
        {
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var brokerRef = ResolveBrokerReference(reference);
                var previousUrl = Url;
                using var response = await _broker.RequestAsync("click", new { reference = brokerRef }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
                await UpdateSnapshotMetadataAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(previousUrl, Url, StringComparison.Ordinal))
                {
                    _referencePrefix = Guid.NewGuid().ToString("N") + ":";
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                    _history.Add(new NavigationEntry(Url));
                    _historyIndex = _history.Count - 1;
                }
            }
            finally { _navigationGate.Release(); }
        }

        public async Task FillAsync(string reference, string value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(value);
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var brokerRef = ResolveBrokerReference(reference);
                using var response = await _broker.RequestAsync("fill", new { reference = brokerRef, value }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
            }
            finally { _navigationGate.Release(); }
        }

        public async Task SelectOptionAsync(string reference, string value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(value);
            await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var brokerRef = ResolveBrokerReference(reference);
                using var response = await _broker.RequestAsync("select", new { reference = brokerRef, value }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
            }
            finally { _navigationGate.Release(); }
        }

        private string FormatClientReference(string brokerRef)
        {
            if (brokerRef.Length > 3 && brokerRef[0] == 'f')
            {
                var marker = brokerRef.IndexOf("e-", StringComparison.Ordinal);
                if (marker > 1 && int.TryParse(brokerRef.AsSpan(1, marker - 1), out _))
                    return string.Concat(brokerRef.AsSpan(0, marker), "e_", _referencePrefix, brokerRef.AsSpan(marker + 2));
            }
            return _referencePrefix + brokerRef;
        }

        private string ResolveBrokerReference(string reference)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference);
            if (reference.StartsWith("f", StringComparison.Ordinal))
            {
                var marker = reference.IndexOf("e_", StringComparison.Ordinal);
                if (marker > 1 && int.TryParse(reference.AsSpan(1, marker - 1), out _))
                {
                    var rest = reference.AsSpan(marker + 2);
                    if (!rest.StartsWith(_referencePrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("Stale element reference; capture a new snapshot.");
                    return string.Concat(reference.AsSpan(0, marker), "e-", rest[_referencePrefix.Length..]);
                }
            }
            if (!reference.StartsWith(_referencePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Stale element reference; capture a new snapshot.");
            return reference[_referencePrefix.Length..];
        }

        private static Task<T> Unsupported<T>(string feature) => Task.FromException<T>(new NotSupportedException($"Folio {feature} is not supported in this offline fixture slice."));

        private async Task<JsonElement> CloseCoreAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return JsonSerializer.SerializeToElement(new { ok = true });
            _broker.EventReceived -= PublishEvent;
            foreach (var sub in _subscriptions.Keys.ToArray())
                sub.Complete();
            try
            {
                return await _broker.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _broker.DisposeAsync().ConfigureAwait(false);
                Closed?.Invoke();
            }
        }

    }

    internal sealed class BrokerConnection : IAsyncDisposable
    {
        private const int MaxResponseChars = 2 * 1024 * 1024;
        // The 1280 Ã— 720 RGBA viewport can exceed 4 MiB after PNG and base64 encoding.
        private const int MaxScreenshotResponseChars = 8 * 1024 * 1024;
        private readonly Process _process;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly BoundedLineReader _reader;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _readCts = new();
        private readonly Task _readLoopTask;
        private long _nextId;
        private int _disposed;
        private int _terminated;

        private BrokerConnection(Process process)
        {
            _process = process;
            _reader = new BoundedLineReader(process.StandardOutput);
            _ = process.StandardError.ReadToEndAsync();
            _readLoopTask = Task.Run(ReadLoopAsync);
        }

        internal int ProcessId => _process.Id;
        public event Action<CdpEvent>? EventReceived;

        public static Task<BrokerConnection> StartAsync(string executable, string[] allowedOrigins, CancellationToken cancellationToken) =>
            StartAsync(executable, allowedOrigins, null, false, cancellationToken);

        public static Task<BrokerConnection> StartAsync(string executable, string[] allowedOrigins, string? profileDirectory, CancellationToken cancellationToken = default) =>
            StartAsync(executable, allowedOrigins, profileDirectory, false, cancellationToken);

        public static async Task<BrokerConnection> StartAsync(string executable, string[] allowedOrigins, string? profileDirectory, bool allowPrivateNetwork, CancellationToken cancellationToken = default)
        {
            var startInfo = CreateStartInfo(executable, allowedOrigins, profileDirectory, allowPrivateNetwork);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the Folio broker.");
            var connection = new BrokerConnection(process);
            try
            {
                using var hello = await connection.RequestAsync("hello", new { }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(hello);
                if (!hello.RootElement.TryGetProperty("sandboxed", out var sandboxed) || sandboxed.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Folio broker did not confirm its sandboxed content process.");
                var networkEnabled = hello.RootElement.TryGetProperty("networkEnabled", out var network) && network.ValueKind == JsonValueKind.True;
                if (networkEnabled != (allowedOrigins.Length > 0 || allowPrivateNetwork))
                    throw new InvalidOperationException("Folio broker did not confirm the requested network policy.");
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task ReadLoopAsync()
        {
            Exception streamError = new IOException("Folio broker closed its response stream.");
            try
            {
                while (!_readCts.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(MaxScreenshotResponseChars, _readCts.Token).ConfigureAwait(false);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var doc = JsonDocument.Parse(line);
                    var transferred = false;
                    try
                    {
                        if (doc.RootElement.TryGetProperty("id", out var responseIdProp) &&
                            responseIdProp.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        {
                            var responseId = responseIdProp.ValueKind == JsonValueKind.String
                                ? responseIdProp.GetString()
                                : responseIdProp.GetRawText();
                            if (responseId is not null && _pending.TryRemove(responseId, out var tcs))
                                transferred = tcs.TrySetResult(doc);
                        }
                        else if (doc.RootElement.TryGetProperty("event", out var eventProp) &&
                            eventProp.ValueKind == JsonValueKind.String)
                        {
                            var eventName = eventProp.GetString();
                            if (!string.IsNullOrWhiteSpace(eventName))
                            {
                                try { EventReceived?.Invoke(new CdpEvent(eventName, doc.RootElement.Clone(), null)); }
                                catch { }
                            }
                        }
                    }
                    finally
                    {
                        if (!transferred) doc.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (_readCts.IsCancellationRequested)
            {
                streamError = new IOException("Folio broker connection was closed.");
            }
            catch (Exception error)
            {
                streamError = error;
            }
            finally
            {
                Interlocked.Exchange(ref _terminated, 1);
                foreach (var key in _pending.Keys.ToArray())
                {
                    if (_pending.TryRemove(key, out var tcs))
                        tcs.TrySetException(streamError);
                }
            }
        }

        public async Task<JsonDocument> RequestAsync(string operation, object payload, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registered = false;
            try
            {
                await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _terminated) != 0, this);
                    _pending[id] = tcs;
                    registered = true;
                    using var payloadDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
                    var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["id"] = JsonSerializer.SerializeToElement(id),
                        ["op"] = JsonSerializer.SerializeToElement(operation)
                    };
                    foreach (var property in payloadDocument.RootElement.EnumerateObject()) fields[property.Name] = property.Value.Clone();
                    var request = JsonSerializer.Serialize(fields, JsonOptions);
                    await _process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                finally { _gate.Release(); }

                var response = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (operation == "close") Interlocked.Exchange(ref _terminated, 1);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (registered)
                {
                    Interlocked.Exchange(ref _terminated, 1);
                    await KillAsync().ConfigureAwait(false);
                }
                throw new TimeoutException("Folio broker request timed out.");
            }
            catch (OperationCanceledException)
            {
                if (registered)
                {
                    Interlocked.Exchange(ref _terminated, 1);
                    await KillAsync().ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ObjectDisposedException)
            {
                if (registered)
                {
                    Interlocked.Exchange(ref _terminated, 1);
                    await KillAsync().ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                if (registered && _pending.TryRemove(id, out var removed) && removed.Task.IsCompletedSuccessfully)
                    removed.Task.Result.Dispose();
            }
        }

        public async Task<JsonElement> CloseAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _terminated) != 0 || _process.HasExited)
            {
                await DisposeAsync().ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { ok = true });
            }

            using var response = await RequestAsync("close", new { }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            var result = response.RootElement.Clone();
            await DisposeAsync().ConfigureAwait(false);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _terminated, 1);
            try { _readCts.Cancel(); } catch (ObjectDisposedException) { }
            if (!_process.HasExited)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { await KillAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
            try { await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
            _readCts.Dispose();
            _process.Dispose();
        }

        private async Task KillAsync()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
        }

        private static ProcessStartInfo CreateStartInfo(string executable, string[] allowedOrigins, string? profileDirectory, bool allowPrivateNetwork)
        {
            var path = Path.GetFullPath(executable);
            var isAssembly = Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);
            var startInfo = new ProcessStartInfo
            {
                FileName = isAssembly ? "dotnet" : path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (isAssembly) startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("--broker");
            startInfo.ArgumentList.Add($"--arcana-parent-pid={Environment.ProcessId}");
            startInfo.ArgumentList.Add("--stream-events");
            if (allowPrivateNetwork) startInfo.ArgumentList.Add("--allow-private-network");
            foreach (var origin in allowedOrigins) startInfo.ArgumentList.Add($"--allow-origin={origin}");
            if (!string.IsNullOrWhiteSpace(profileDirectory))
            {
                Directory.CreateDirectory(profileDirectory);
                startInfo.Environment["FOLIO_STORAGE_PATH"] = Path.Combine(Path.GetFullPath(profileDirectory), "local-storage.json");
                startInfo.Environment["FOLIO_DOWNLOADS_DIR"] = Path.Combine(Path.GetFullPath(profileDirectory), "downloads");
            }
            else
            {
                startInfo.Environment["FOLIO_DOWNLOADS_DIR"] = Path.Combine(Path.GetTempPath(), "arcana-folio-downloads", Guid.NewGuid().ToString("N"));
            }
            return startInfo;
        }

        private sealed class BoundedLineReader(TextReader reader)
        {
            private readonly char[] _buffer = new char[4096];
            private int _offset;
            private int _length;

            public async Task<string?> ReadLineAsync(int maxChars, CancellationToken cancellationToken)
            {
                var line = new System.Text.StringBuilder(Math.Min(maxChars, 4096));
                while (true)
                {
                    if (_offset == _length)
                    {
                        _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        _offset = 0;
                        if (_length == 0)
                        {
                            if (line.Length == 0) return null;
                            throw new InvalidDataException("Folio broker ended a response before the JSON line was complete.");
                        }
                    }

                    var newline = Array.IndexOf(_buffer, '\n', _offset, _length - _offset);
                    var count = (newline >= 0 ? newline : _length) - _offset;
                    if (line.Length + count > maxChars)
                        throw new InvalidDataException("Folio broker response exceeded the configured size limit.");
                    line.Append(_buffer, _offset, count);
                    _offset += count;
                    if (newline >= 0)
                    {
                        _offset++;
                        if (line.Length > 0 && line[^1] == '\r') line.Length--;
                        return line.ToString();
                    }
                }
            }
        }
    }
}
