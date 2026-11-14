using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Arcana.Runtime;

/// <summary>Opt-in Arcana adapter for Folio's AppContainer-backed headless broker.</summary>
public sealed class FolioBrowserBackend : IArcanaBrowserBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _brokerExecutable;
    private readonly ConcurrentDictionary<string, FolioPageBackend> _pages = new(StringComparer.Ordinal);
    private bool _disposed;

    public FolioBrowserBackend(string brokerExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerExecutable);
        _brokerExecutable = Path.GetFullPath(brokerExecutable);
        if (!File.Exists(_brokerExecutable)) throw new FileNotFoundException("The Folio content broker executable was not found.", _brokerExecutable);
    }

    public FolioBrowserBackend(ArcanaOptions options) : this(ResolveBrokerPath(options)) { }

    private static string ResolveBrokerPath(ArcanaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options.FolioBrokerPath
            ?? throw new ArgumentException("The Folio backend requires FolioBrokerPath to point to the broker-enabled Folio.ContentHost build.", nameof(options));
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
        if (url is not null && url != "about:blank")
            throw new NotSupportedException("Folio pages currently accept only about:blank and inline HTML fixtures.");

        var id = Guid.NewGuid().ToString("N");
        var page = await FolioPageBackend.CreateAsync(_brokerExecutable, url ?? "about:blank", cancellationToken).ConfigureAwait(false);
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
        if (code is "unsupported" or "sandbox_unavailable" or "sandbox_bypassed")
            throw new NotSupportedException(message ?? "The Folio sandbox does not support this operation in the current environment.");
        throw new InvalidOperationException(message ?? $"Folio broker operation failed ({code ?? "unknown"}).");
    }

    private sealed class FolioPageBackend : IArcanaPageBackend
    {
        private const string BlankHtml = "<!doctype html><html><head><title></title></head><body></body></html>";
        private readonly BrokerConnection _broker;
        private bool _closed;

        private FolioPageBackend(BrokerConnection broker, string url)
        {
            _broker = broker;
            Url = url;
        }

        public event Action? Closed;
        public string Url { get; private set; }
        public string Title { get; private set; } = "";

        public static async Task<FolioPageBackend> CreateAsync(string executable, string url, CancellationToken cancellationToken)
        {
            var connection = await BrokerConnection.StartAsync(executable, cancellationToken).ConfigureAwait(false);
            var page = new FolioPageBackend(connection, url);
            try
            {
                await page.LoadAsync(BlankHtml, cancellationToken).ConfigureAwait(false);
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
            return LoadAsync(html, cancellationToken);
        }

        private async Task LoadAsync(string html, CancellationToken cancellationToken)
        {
            using var response = await _broker.RequestAsync("load", new { html, urlLabel = Url }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            await UpdateSnapshotMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<PageSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            var response = await _broker.RequestAsync("snapshot", new { }, cancellationToken).ConfigureAwait(false);
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
            var response = await _broker.RequestAsync("evaluate", new { expression = wrapped }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            if (!response.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                return JsonSerializer.SerializeToElement<object?>(null);
            using var parsed = JsonDocument.Parse(value.GetString() ?? "null");
            return parsed.RootElement.TryGetProperty("value", out var result) ? result.Clone() : JsonSerializer.SerializeToElement<object?>(null);
        }

        public Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("Folio navigation is not supported in this offline fixture slice."));

        public Task WaitForNetworkIdleAsync(int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("Folio network-idle waiting is not supported because this backend does not enable network access."));

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
        public Task<JsonElement> ReloadAsync(CancellationToken cancellationToken = default) => Unsupported<JsonElement>("reload");
        public Task<bool> GoBackAsync(CancellationToken cancellationToken = default) => Unsupported<bool>("history navigation");
        public Task<bool> GoForwardAsync(CancellationToken cancellationToken = default) => Unsupported<bool>("history navigation");
        public Task<JsonElement> SetViewportAsync(int width, int height, CancellationToken cancellationToken = default) => Unsupported<JsonElement>("viewport changes");
        public Task ClickAtAsync(double x, double y, CancellationToken cancellationToken = default) => Unsupported("click-at");
        public Task PressKeyAsync(string key, CancellationToken cancellationToken = default) => Unsupported("keyboard input");
        public Task<JsonElement> TypeTextAsync(string text, CancellationToken cancellationToken = default) => Unsupported<JsonElement>("typing");
        public Task<JsonElement> ScrollAsync(double x, double y, CancellationToken cancellationToken = default) => Unsupported<JsonElement>("scrolling");
        public Task UploadFileAsync(string reference, string path, CancellationToken cancellationToken = default) => Unsupported("file upload");
        public Task ClickAsync(string reference, CancellationToken cancellationToken = default) => Unsupported("click");
        public Task FillAsync(string reference, string value, CancellationToken cancellationToken = default) => Unsupported("fill");
        public Task SelectOptionAsync(string reference, string value, CancellationToken cancellationToken = default) => Unsupported("select");

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

        public static async Task<BrokerConnection> StartAsync(string executable, CancellationToken cancellationToken)
        {
            var startInfo = CreateStartInfo(executable);
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
                var line = await _reader.ReadLineAsync(MaxResponseChars, timeout.Token).ConfigureAwait(false)
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

        private static ProcessStartInfo CreateStartInfo(string executable)
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
