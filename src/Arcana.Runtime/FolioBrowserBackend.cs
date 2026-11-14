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
    private readonly ConcurrentDictionary<string, FolioPageBackend> _pages = new(StringComparer.Ordinal);
    private bool _disposed;

    public FolioBrowserBackend(string brokerExecutable, IEnumerable<string>? allowedOrigins = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerExecutable);
        _brokerExecutable = Path.GetFullPath(brokerExecutable);
        _allowedOrigins = allowedOrigins?.ToArray() ?? [];
        if (!File.Exists(_brokerExecutable)) throw new FileNotFoundException("The Folio content broker executable was not found.", _brokerExecutable);
    }

    public FolioBrowserBackend(ArcanaOptions options) : this(ResolveBrokerPath(options), options.FolioAllowedOrigins) { }

    private static string ResolveBrokerPath(ArcanaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options.FolioBrokerPath
            ?? throw new ArgumentException("The Folio backend requires FolioBrokerPath to point to the broker-enabled Folio.ContentHost build.", nameof(options));
    }

    /// <summary>Confirms the configured broker can start its required sandbox before the API starts listening.</summary>
    public async Task VerifyStartupAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var broker = await BrokerConnection.StartAsync(_brokerExecutable, _allowedOrigins, cancellationToken).ConfigureAwait(false);
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

    public async Task<IArcanaPageBackend> NewPageAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Guid.NewGuid().ToString("N");
        var page = await FolioPageBackend.CreateAsync(_brokerExecutable, _allowedOrigins, url ?? "about:blank", cancellationToken).ConfigureAwait(false);
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
        private readonly List<NavigationEntry> _history = [];
        private int _historyIndex = -1;

        private sealed record NavigationEntry(string Url, string? Html = null);
        private bool _closed;

        private FolioPageBackend(BrokerConnection broker, bool navigationEnabled)
        {
            _broker = broker;
            _navigationEnabled = navigationEnabled;
            Url = "about:blank";
        }

        public event Action? Closed;
        public string Url { get; private set; }
        public string CurrentUrl => Url;
        public string Title { get; private set; } = "";

        public static async Task<FolioPageBackend> CreateAsync(string executable, string[] allowedOrigins, string url, CancellationToken cancellationToken)
        {
            var connection = await BrokerConnection.StartAsync(executable, allowedOrigins, cancellationToken).ConfigureAwait(false);
            var page = new FolioPageBackend(connection, allowedOrigins.Length > 0);
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
            Url = "about:blank";
            await UpdateSnapshotMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<PageSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            using var response = await _broker.RequestAsync("snapshot", new { }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            if (!response.RootElement.TryGetProperty("snapshot", out var snapshot))
                throw new InvalidOperationException("Folio broker did not return a page snapshot.");
            var pageSnapshot = snapshot.Deserialize<PageSnapshot>(JsonOptions)
                ?? throw new InvalidOperationException("Folio broker returned an invalid page snapshot.");
            Url = pageSnapshot.Url;
            Title = pageSnapshot.Title;
            return pageSnapshot;
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
            Url = response.RootElement.GetProperty("url").GetString() ?? url;
            await UpdateSnapshotMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task WaitForNetworkIdleAsync(int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("Folio network-idle waiting is not supported by the Folio broker."));

        public Task<JsonElement> GetCookiesAsync(CancellationToken cancellationToken = default) => Unsupported<JsonElement>("cookies");
        public Task<JsonElement> SetCookiesAsync(object[] cookies, CancellationToken cancellationToken = default) => Unsupported<JsonElement>("cookies");
        public Task<PageStorageState> ExportStorageAsync(CancellationToken cancellationToken = default) => Unsupported<PageStorageState>("storage");
        public Task RestoreStorageAsync(PageStorageState state, CancellationToken cancellationToken = default) => Unsupported("storage");
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
        public IArcanaEventSubscription Subscribe(string? method = null, int capacity = 256) => throw new NotSupportedException("Folio event subscriptions are not supported in this slice.");
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

        public Task<JsonElement> SetViewportAsync(int width, int height, CancellationToken cancellationToken = default) => Unsupported<JsonElement>("viewport changes");
        public async Task ClickAtAsync(double x, double y, CancellationToken cancellationToken = default)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
            await SendInputAsync("click-at", new { x, y }, cancellationToken).ConfigureAwait(false);
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

        public Task UploadFileAsync(string reference, string path, CancellationToken cancellationToken = default) => Unsupported("file upload");
        public async Task ClickAsync(string reference, CancellationToken cancellationToken = default)
        {
            var bounds = await RunReferenceActionAsync(reference, """
                const rect = element.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0 ? [rect.left + rect.width / 2, rect.top + rect.height / 2] : [];
                """, cancellationToken).ConfigureAwait(false);
            if (bounds.GetArrayLength() != 2)
                throw new InvalidOperationException("The referenced element has no clickable bounds.");
            await ClickAtAsync(bounds[0].GetDouble(), bounds[1].GetDouble(), cancellationToken).ConfigureAwait(false);
        }

        public async Task FillAsync(string reference, string value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(value);
            await RunReferenceActionAsync(reference, $$"""
                if (element.readOnly || element.hasAttribute('readonly')) throw new Error('Element is read-only.');
                const value = {{JsonSerializer.Serialize(value)}};
                if (element.tagName === 'TEXTAREA' || (element.tagName === 'INPUT' &&
                    ['text', 'search', 'email', 'url', 'tel', 'password', 'number'].includes(element.type))) {
                    element.focus();
                    element.value = value;
                } else if (element.getAttribute('contenteditable') === 'true') {
                    element.focus();
                    element.textContent = value;
                } else {
                    throw new Error('Element is not a supported text control.');
                }
                element.dispatchEvent(new Event('input', { bubbles: true }));
                element.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
                """, cancellationToken).ConfigureAwait(false);
        }

        public async Task SelectOptionAsync(string reference, string value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(value);
            await RunReferenceActionAsync(reference, $$"""
                if (element.tagName !== 'SELECT') throw new Error('Element is not a select.');
                const value = {{JsonSerializer.Serialize(value)}};
                const option = Array.from(element.options).find(o => o.value === value || o.textContent.trim() === value);
                if (!option || option.disabled || option.parentElement?.disabled) throw new Error('Option unavailable.');
                element.value = option.value;
                element.dispatchEvent(new Event('input', { bubbles: true }));
                element.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
                """, cancellationToken).ConfigureAwait(false);
        }

        private Task<JsonElement> RunReferenceActionAsync(string reference, string action, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference);
            return EvaluateAsync($$"""
                (() => {
                    const reference = {{JsonSerializer.Serialize(reference)}};
                    const element = Array.from(document.querySelectorAll('[data-folio-ref]'))
                        .find(candidate => candidate.getAttribute('data-folio-ref') === reference);
                    if (!element) throw new Error('Stale element reference; capture a new snapshot.');
                    if (element.disabled || element.matches(':disabled') || element.getAttribute('aria-disabled') === 'true')
                        throw new Error('Element is disabled.');
                    for (let node = element; node; node = node.parentElement) {
                        const style = getComputedStyle(node);
                        if (node.hasAttribute('hidden') || node.hasAttribute('inert') ||
                            node.getAttribute('aria-hidden') === 'true' || style.display === 'none' ||
                            style.visibility === 'hidden' || style.visibility === 'collapse') throw new Error('Element is hidden.');
                    }
                    {{action}}
                })()
                """, cancellationToken);
        }

        private static Task Unsupported(string feature) => Task.FromException(new NotSupportedException($"Folio {feature} is not supported in this offline fixture slice."));
        private static Task<T> Unsupported<T>(string feature) => Task.FromException<T>(new NotSupportedException($"Folio {feature} is not supported in this offline fixture slice."));

        private async Task<JsonElement> CloseCoreAsync(CancellationToken cancellationToken)
        {
            if (_closed) return JsonSerializer.SerializeToElement(new { ok = true });
            _closed = true;
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

    private sealed class BrokerConnection : IAsyncDisposable
    {
        private const int MaxResponseChars = 2 * 1024 * 1024;
        // The 1280 Ã— 720 RGBA viewport can exceed 4 MiB after PNG and base64 encoding.
        private const int MaxScreenshotResponseChars = 8 * 1024 * 1024;
        private readonly Process _process;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly BoundedLineReader _reader;
        private long _nextId;
        private int _disposed;
        private int _terminated;

        private BrokerConnection(Process process)
        {
            _process = process;
            _reader = new BoundedLineReader(process.StandardOutput);
            _ = process.StandardError.ReadToEndAsync();
        }

        public static async Task<BrokerConnection> StartAsync(string executable, string[] allowedOrigins, CancellationToken cancellationToken)
        {
            var startInfo = CreateStartInfo(executable, allowedOrigins);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the Folio broker.");
            var connection = new BrokerConnection(process);
            try
            {
                using var hello = await connection.RequestAsync("hello", new { }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(hello);
                if (!hello.RootElement.TryGetProperty("sandboxed", out var sandboxed) || sandboxed.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Folio broker did not confirm its sandboxed content process.");
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<JsonDocument> RequestAsync(string operation, object payload, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var gateHeld = false;
            try
            {
                await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                gateHeld = true;
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _terminated) != 0, this);
                var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                var responseLimit = operation == "screenshot" ? MaxScreenshotResponseChars : MaxResponseChars;
                var line = await _reader.ReadLineAsync(responseLimit, timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("Folio broker closed its response stream.");
                var response = JsonDocument.Parse(line);
                if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetString() != id)
                {
                    response.Dispose();
                    throw new InvalidDataException("Folio broker returned a response for a different request.");
                }
                if (operation == "close") Interlocked.Exchange(ref _terminated, 1);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _terminated, 1);
                await KillAsync().ConfigureAwait(false);
                throw new TimeoutException("Folio broker request timed out.");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _terminated, 1);
                await KillAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ObjectDisposedException)
            {
                Interlocked.Exchange(ref _terminated, 1);
                await KillAsync().ConfigureAwait(false);
                throw;
            }
            finally { if (gateHeld) _gate.Release(); }
        }

        public async Task<JsonElement> CloseAsync(CancellationToken cancellationToken)
        {
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
            if (!_process.HasExited)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { await KillAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
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
            _process.Dispose();
        }

        private static ProcessStartInfo CreateStartInfo(string executable, string[] allowedOrigins)
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
            foreach (var origin in allowedOrigins) startInfo.ArgumentList.Add($"--allow-origin={origin}");
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
