using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class ApiServerTests
{
    private sealed class PageCreationGateBackend : IArcanaBrowserBackend
    {
        public TaskCompletionSource CreationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCreation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GatedPage Page { get; } = new();

        public Task<JsonElement> GetVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { product = "test" }));

        public Task<IReadOnlyList<BrowserTab>> GetTabsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BrowserTab>>([]);

        public async Task<IArcanaPageBackend> NewPageAsync(string? url = null, CancellationToken cancellationToken = default)
        {
            CreationStarted.TrySetResult();
            await ReleaseCreation.Task.ConfigureAwait(false);
            return Page;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedPage : IArcanaPageBackend
    {
        public int CloseCount;

        public Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken = default) => Unsupported();
        public Task SetContentAsync(string html, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PageSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Unsupported<PageSnapshot>();
        public Task<JsonElement> GetCookiesAsync(CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public Task<JsonElement> SetCookiesAsync(object[] cookies, CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public Task<PageStorageState> ExportStorageAsync(CancellationToken cancellationToken = default) => Unsupported<PageStorageState>();
        public Task RestoreStorageAsync(PageStorageState state, CancellationToken cancellationToken = default) => Unsupported();
        public Task<byte[]> CaptureScreenshotAsync(string format, int? quality = null, CancellationToken cancellationToken = default) => Unsupported<byte[]>();
        public Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public Task<JsonElement> SendCdpAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public IArcanaEventSubscription Subscribe(string? method = null, int capacity = 256) => throw new NotSupportedException();
        public Task<JsonElement> CloseAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CloseCount);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
        }
        public Task<JsonElement> ReloadAsync(CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public Task<bool> GoBackAsync(CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<bool> GoForwardAsync(CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task WaitForNetworkIdleAsync(int timeoutMs, CancellationToken cancellationToken = default) => Unsupported();
        public Task<JsonElement> SetViewportAsync(int width, int height, CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public Task ClickAtAsync(double x, double y, CancellationToken cancellationToken = default) => Unsupported();
        public Task PressKeyAsync(string key, CancellationToken cancellationToken = default) => Unsupported();
        public Task<JsonElement> TypeTextAsync(string text, CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public Task<JsonElement> ScrollAsync(double x, double y, CancellationToken cancellationToken = default) => Unsupported<JsonElement>();
        public Task UploadFileAsync(string reference, string path, CancellationToken cancellationToken = default) => Unsupported();
        public Task ClickAsync(string reference, CancellationToken cancellationToken = default) => Unsupported();
        public Task FillAsync(string reference, string value, CancellationToken cancellationToken = default) => Unsupported();
        public Task SelectOptionAsync(string reference, string value, CancellationToken cancellationToken = default) => Unsupported();

        private static Task Unsupported() => Task.FromException(new NotSupportedException());
        private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
    }

    private sealed class ApiTestContext : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly BrowserRuntime? _runtime;
        private readonly ArcanaBrowser? _browser;
        private readonly ArcanaApiServer? _api;
        private readonly HttpClientHandler _handler;

        public Uri Endpoint { get; }
        public string Token { get; }
        public HttpClient Client { get; }
        public bool UseExternalEndpoint { get; }
        public string? ExpectedEngineId { get; }

        private ApiTestContext(
            Uri endpoint,
            string token,
            bool useExternalEndpoint,
            string? expectedEngineId,
            BrowserRuntime? runtime,
            ArcanaBrowser? browser,
            ArcanaApiServer? api)
        {
            Endpoint = endpoint;
            Token = token;
            UseExternalEndpoint = useExternalEndpoint;
            ExpectedEngineId = expectedEngineId;
            _runtime = runtime;
            _browser = browser;
            _api = api;

            _handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            Client = new HttpClient(_handler) { BaseAddress = Endpoint };
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        public static async Task<ApiTestContext> CreateAsync(CancellationToken cancellationToken)
        {
            var configuredEndpoint = Environment.GetEnvironmentVariable("ARCANA_TEST_ENDPOINT");
            var configuredToken = Environment.GetEnvironmentVariable("ARCANA_TEST_TOKEN");
            var expectedEngineId = Environment.GetEnvironmentVariable("ARCANA_TEST_ENGINE_ID");
            var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_ENGINE");

            var useExternalEndpoint = !string.IsNullOrWhiteSpace(configuredEndpoint);
            var hasExternalConfiguration = useExternalEndpoint || !string.IsNullOrWhiteSpace(configuredToken) ||
                !string.IsNullOrWhiteSpace(expectedEngineId);
            if (hasExternalConfiguration &&
                (string.IsNullOrWhiteSpace(configuredEndpoint) || string.IsNullOrWhiteSpace(configuredToken) ||
                 string.IsNullOrWhiteSpace(expectedEngineId)))
            {
                Assert.Fail("ARCANA_TEST_ENDPOINT, ARCANA_TEST_TOKEN, and ARCANA_TEST_ENGINE_ID must be configured together.");
            }

            if (useExternalEndpoint)
            {
                if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpoint) ||
                    endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
                    endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                    endpoint.AbsolutePath != "/")
                {
                    Assert.Fail("ARCANA_TEST_ENDPOINT must be an absolute HTTP loopback origin without a path, query, or fragment.");
                }
                return new ApiTestContext(endpoint, configuredToken!, true, expectedEngineId, null, null, null);
            }

            if (string.IsNullOrWhiteSpace(executable))
            {
                Assert.Inconclusive("Set ARCANA_TEST_ENGINE to run the API transport test, or set ARCANA_TEST_ENDPOINT, ARCANA_TEST_TOKEN, and ARCANA_TEST_ENGINE_ID for an external loopback API.");
            }

            const string token = "test-token";
            var runtime = new BrowserRuntime(new ArcanaOptions { ExecutablePath = executable, AllowPrivateNetwork = true });
            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            var browser = await ArcanaBrowser.ConnectAsync(runtime, cancellationToken).ConfigureAwait(false);
            var api = new ArcanaApiServer(browser, token);
            api.Start();
            return new ApiTestContext(api.Endpoint, token, false, null, runtime, browser, api);
        }

        public async Task<ClientWebSocket> ConnectWebSocketAsync(string pageId, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
            var wsUri = new UriBuilder(Endpoint) { Scheme = "ws", Path = "v1/events", Query = $"pageId={pageId}" }.Uri;
            await socket.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(false);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _handler.Dispose();
            if (_api is not null) await _api.DisposeAsync().ConfigureAwait(false);
            if (_browser is not null) await _browser.DisposeAsync().ConfigureAwait(false);
            if (_runtime is not null) await _runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ShutdownWaitsForInflightPageCreationAndClosesTheCreatedPage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var backend = new PageCreationGateBackend();
        var api = new ArcanaApiServer(backend, "shutdown-race-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "shutdown-race-token");

        var creation = client.PostAsJsonAsync("v1/pages", new { html = "<html><body>gated</body></html>" });
        var shutdown = Task.CompletedTask;
        try
        {
            await backend.CreationStarted.Task.WaitAsync(timeout.Token);
            shutdown = api.DisposeAsync().AsTask();
            await Task.Delay(100, timeout.Token);
            Assert.IsFalse(shutdown.IsCompleted, "Shutdown must wait for a request handler that is creating a page.");

            backend.ReleaseCreation.TrySetResult();
            await shutdown.WaitAsync(timeout.Token);
        }
        finally
        {
            backend.ReleaseCreation.TrySetResult();
            await shutdown.WaitAsync(timeout.Token);
            await backend.DisposeAsync();
        }

        Assert.AreEqual(1, backend.Page.CloseCount, "The page registered by the in-flight request must be closed during graceful shutdown.");
        try { using var response = await creation.WaitAsync(timeout.Token); }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    [TestMethod]
    public async Task V1RequiresTokenOriginAndServesPageActions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var site = new TestSite();
        await using var ctx = await ApiTestContext.CreateAsync(timeout.Token);

        using var unauthedClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = ctx.Endpoint
        };

        using (var unauthorized = await unauthedClient.GetAsync("v1/health", timeout.Token))
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        unauthedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Token);
        unauthedClient.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        using (var foreign = await unauthedClient.GetAsync("v1/health", timeout.Token))
            Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);

        using var health = await ctx.Client.GetAsync("v1/health", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync(timeout.Token));
        Assert.AreEqual("v1", healthJson.RootElement.GetProperty("apiVersion").GetString());
        if (ctx.UseExternalEndpoint)
        {
            var engine = healthJson.RootElement.GetProperty("engine");
            var actualEngineId = engine.GetProperty("product").GetString();
            Assert.AreEqual(ctx.ExpectedEngineId, actualEngineId,
                "Health engine.product did not match ARCANA_TEST_ENGINE_ID.");
        }

        using var create = await ctx.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
        var pageId = created.RootElement.GetProperty("pageId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(pageId));

        using var snapshot = await ctx.Client.GetAsync($"v1/pages/{pageId}/snapshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, snapshot.StatusCode);
        StringAssert.Contains(await snapshot.Content.ReadAsStringAsync(timeout.Token), "Arcana parity fixture");

        using var wait = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/wait", new { condition = "selector", value = "body", timeoutMs = 1000 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, wait.StatusCode);
        using var idleTimeout = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/wait", new { condition = "networkidle", value = "", timeoutMs = 100 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.RequestTimeout, idleTimeout.StatusCode);
        using var idle = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/wait", new { condition = "networkidle", value = "", timeoutMs = 3000 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, idle.StatusCode);
        using var logs = await ctx.Client.GetAsync($"v1/pages/{pageId}/logs", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, logs.StatusCode);

        using var action = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/navigate", new { url = site.Origin + "/two" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, action.StatusCode);
        using var deleted = await ctx.Client.DeleteAsync($"v1/pages/{pageId}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [TestMethod]
    public async Task V1ServesFullLifecycleTabsAndHistory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var site = new TestSite();
        await using var ctx = await ApiTestContext.CreateAsync(timeout.Token);

        // 1. Create page
        using var create = await ctx.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // 2. Query tabs
        using var tabs = await ctx.Client.GetAsync("v1/tabs", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, tabs.StatusCode);
        using var tabsJson = JsonDocument.Parse(await tabs.Content.ReadAsStringAsync(timeout.Token));
        Assert.IsTrue(tabsJson.RootElement.GetArrayLength() > 0, "Tabs list must not be empty.");

        // 3. Navigate to /two
        using var nav = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/navigate", new { url = site.Origin + "/two" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, nav.StatusCode);
        using var titleTwo = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "document.title" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, titleTwo.StatusCode);
        Assert.AreEqual("Second page", (await titleTwo.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetString());

        // 4. Back
        using var back = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/back", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, back.StatusCode);
        using var titleBack = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "document.title" }, timeout.Token);
        Assert.AreEqual("Arcana fixture", (await titleBack.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetString());

        // 5. Forward
        using var fwd = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/forward", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, fwd.StatusCode);
        using var titleFwd = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "document.title" }, timeout.Token);
        Assert.AreEqual("Second page", (await titleFwd.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetString());

        // 6. Reload
        using var reload = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/reload", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, reload.StatusCode);

        // 7. Delete page
        using var deleted = await ctx.Client.DeleteAsync($"v1/pages/{pageId}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [TestMethod]
    public async Task V1ServesFullActionsSnapshotAndEvaluation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var site = new TestSite();
        await using var ctx = await ApiTestContext.CreateAsync(timeout.Token);

        using var create = await ctx.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        var pageId = (await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetProperty("pageId").GetString()!;

        // Snapshot
        using var snapshotRes = await ctx.Client.GetAsync($"v1/pages/{pageId}/snapshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, snapshotRes.StatusCode);
        var snapshot = await snapshotRes.Content.ReadFromJsonAsync<PageSnapshot>(cancellationToken: timeout.Token);
        Assert.IsNotNull(snapshot);
        var nameInput = snapshot.Elements.First(e => e.Name == "Name");
        var choiceSelect = snapshot.Elements.First(e => e.Name == "choice" || e.Role == "combobox");
        var clickBtn = snapshot.Elements.First(e => e.Name == "Click me");

        // Find
        using var findRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/find", new { value = "Click me" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, findRes.StatusCode);
        using var findJson = JsonDocument.Parse(await findRes.Content.ReadAsStringAsync(timeout.Token));
        Assert.IsTrue(findJson.RootElement.GetArrayLength() > 0);

        // Fill
        using var fillRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/fill", new { @ref = nameInput.Reference, value = "Agent Wire Input" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, fillRes.StatusCode);
        using var evalFill = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "document.getElementById('name').value" }, timeout.Token);
        Assert.AreEqual("Agent Wire Input", (await evalFill.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetString());

        // Select
        using var selectRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/select", new { @ref = choiceSelect.Reference, value = "b" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, selectRes.StatusCode);
        using var evalSelect = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "document.getElementById('choice').value" }, timeout.Token);
        Assert.AreEqual("b", (await evalSelect.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetString());

        // Click
        using var clickRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/click", new { @ref = clickBtn.Reference }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, clickRes.StatusCode);
        using var evalClick = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "document.getElementById('result').textContent" }, timeout.Token);
        Assert.AreEqual("Clicked", (await evalClick.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetString());

        // Wait conditions: text and expression
        using var waitText = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/wait", new { condition = "text", value = "Clicked", timeoutMs = 2000 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, waitText.StatusCode);
        using var waitExpr = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/wait", new { condition = "expression", value = "document.getElementById('result').textContent === 'Clicked'", timeoutMs = 2000 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, waitExpr.StatusCode);

        // Viewport and Scroll
        using var viewportRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/viewport", new { width = 800, height = 600 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, viewportRes.StatusCode);
        using var scrollRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/scroll", new { deltaX = 0, deltaY = 150 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, scrollRes.StatusCode);

        // Click-at, type, key
        var clickCoords = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "(() => { const r = document.getElementById('name').getBoundingClientRect(); return { x: r.x + 5, y: r.y + 5 }; })()" }, timeout.Token);
        var coords = await clickCoords.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
        var x = coords.GetProperty("x").GetDouble();
        var y = coords.GetProperty("y").GetDouble();

        using var clickAtRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/click-at", new { x, y }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, clickAtRes.StatusCode);

        using var keyRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/key", new { key = "Backspace" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, keyRes.StatusCode);

        using var typeRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/type", new { value = "!" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, typeRes.StatusCode);

        using var deleted = await ctx.Client.DeleteAsync($"v1/pages/{pageId}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [TestMethod]
    public async Task V1ServesStorageCookiesScreenshotAndDiagnostics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var site = new TestSite();
        await using var ctx = await ApiTestContext.CreateAsync(timeout.Token);

        using var create = await ctx.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        var pageId = (await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token)).GetProperty("pageId").GetString()!;

        // 1. Screenshot
        using var screenshotRes = await ctx.Client.GetAsync($"v1/pages/{pageId}/screenshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, screenshotRes.StatusCode);
        Assert.AreEqual("image/png", screenshotRes.Content.Headers.ContentType?.MediaType);
        var bytes = await screenshotRes.Content.ReadAsByteArrayAsync(timeout.Token);
        Assert.IsTrue(bytes.Length > 8);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        // 2. Cookies (GET / PUT)
        using var putCookies = await ctx.Client.PutAsJsonAsync($"v1/pages/{pageId}/cookies", new object[]
        {
            new { name = "wire_cookie", value = "wire_value_123", url = site.Origin, path = "/" }
        }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, putCookies.StatusCode);

        using var getCookies = await ctx.Client.GetAsync($"v1/pages/{pageId}/cookies", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, getCookies.StatusCode);
        StringAssert.Contains(await getCookies.Content.ReadAsStringAsync(timeout.Token), "wire_value_123");

        // 3. Storage (GET / PUT)
        using var getStorage = await ctx.Client.GetAsync($"v1/pages/{pageId}/storage", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, getStorage.StatusCode);
        var storage = await getStorage.Content.ReadFromJsonAsync<PageStorageState>(cancellationToken: timeout.Token);
        Assert.IsNotNull(storage);

        using var putStorage = await ctx.Client.PutAsJsonAsync($"v1/pages/{pageId}/storage", storage, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, putStorage.StatusCode);

        // 4. CDP extension
        using var cdpRes = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/cdp", new
        {
            method = "Page.getLayoutMetrics",
            parameters = new { }
        }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, cdpRes.StatusCode);
        using var cdpJson = JsonDocument.Parse(await cdpRes.Content.ReadAsStringAsync(timeout.Token));
        Assert.IsTrue(cdpJson.RootElement.TryGetProperty("contentSize", out var contentSize));
        Assert.IsTrue(contentSize.TryGetProperty("width", out _));

        // 5. WebSocket events & Logs
        using var ws = await ctx.ConnectWebSocketAsync(pageId, timeout.Token);
        Assert.AreEqual(WebSocketState.Open, ws.State);

        // Trigger console log
        await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "console.log('ws-live-console-event');" }, timeout.Token);

        var wsBuffer = new byte[4096];
        var wsResult = await ws.ReceiveAsync(wsBuffer, timeout.Token);
        var wsText = Encoding.UTF8.GetString(wsBuffer, 0, wsResult.Count);
        StringAssert.Contains(wsText, "ws-live-console-event");

        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);

        // 6. Logs endpoint
        using var logsRes = await ctx.Client.GetAsync($"v1/pages/{pageId}/logs", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, logsRes.StatusCode);
        StringAssert.Contains(await logsRes.Content.ReadAsStringAsync(timeout.Token), "ws-live-console-event");

        using var deleted = await ctx.Client.DeleteAsync($"v1/pages/{pageId}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }
}
