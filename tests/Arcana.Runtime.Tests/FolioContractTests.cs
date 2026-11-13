using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class FolioContractTests
{
    private sealed class FolioApiContext : IAsyncDisposable
    {
        public Uri Endpoint => Api.Endpoint;
        public string Token { get; } = "folio-test-token";
        public FolioBrowserBackend Browser { get; }
        public ArcanaApiServer Api { get; }
        public HttpClient Client { get; }
        private readonly HttpClientHandler _handler;

        public FolioApiContext(ArcanaOptions options)
        {
            Browser = new FolioBrowserBackend(options);
            Api = new ArcanaApiServer(Browser, Token);
            Api.Start();

            _handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            Client = new HttpClient(_handler) { BaseAddress = Endpoint };
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
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
            await Api.DisposeAsync().ConfigureAwait(false);
            await Browser.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task FolioServesEngineIdentityAndHealth()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var ctx = new FolioApiContext(new ArcanaOptions { Backend = "folio", AllowPrivateNetwork = true });

        // 1. Unauthenticated request -> 401
        using var unauthedClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = ctx.Endpoint
        };
        using (var unauth = await unauthedClient.GetAsync("v1/health", timeout.Token))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);
        }

        // 2. Foreign origin -> 403
        unauthedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Token);
        unauthedClient.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        using (var foreign = await unauthedClient.GetAsync("v1/health", timeout.Token))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        }

        // 3. Authorized health check
        using var health = await ctx.Client.GetAsync("v1/health", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync(timeout.Token));
        Assert.AreEqual("v1", healthJson.RootElement.GetProperty("apiVersion").GetString());

        var engine = healthJson.RootElement.GetProperty("engine");
        Assert.AreEqual("folio", engine.GetProperty("engine").GetString());
        StringAssert.Contains(engine.GetProperty("product").GetString(), "Folio");
    }

    [TestMethod]
    public async Task FolioServesTabsAndPageLifecycle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var ctx = new FolioApiContext(new ArcanaOptions { Backend = "folio", AllowPrivateNetwork = true });

        // 1. Initially tabs list
        using var initialTabs = await ctx.Client.GetAsync("v1/tabs", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, initialTabs.StatusCode);

        // 2. Create page with URL
        using var create = await ctx.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
        var pageId = created.RootElement.GetProperty("pageId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(pageId));

        // 3. Tabs now lists the page
        using var tabs = await ctx.Client.GetAsync("v1/tabs", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, tabs.StatusCode);
        using var tabsJson = JsonDocument.Parse(await tabs.Content.ReadAsStringAsync(timeout.Token));
        Assert.IsTrue(tabsJson.RootElement.GetArrayLength() >= 1);

        // 4. Navigate to second page
        using var nav = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/navigate", new { url = site.Origin + "/two" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, nav.StatusCode);

        // 5. Back and forward
        using var back = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/back", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, back.StatusCode);

        using var fwd = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/forward", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, fwd.StatusCode);

        // 6. Reload
        using var reload = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/reload", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, reload.StatusCode);

        // 7. Delete page
        using var delete = await ctx.Client.DeleteAsync($"v1/pages/{pageId}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [TestMethod]
    public async Task FolioServesSnapshotsElementsAndEvaluation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var ctx = new FolioApiContext(new ArcanaOptions { Backend = "folio", AllowPrivateNetwork = true });

        using var create = await ctx.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // Snapshot
        using var snap = await ctx.Client.GetAsync($"v1/pages/{pageId}/snapshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, snap.StatusCode);
        using var snapJson = JsonDocument.Parse(await snap.Content.ReadAsStringAsync(timeout.Token));
        Assert.IsNotNull(snapJson.RootElement.GetProperty("elements"));
        var elements = snapJson.RootElement.GetProperty("elements");
        Assert.IsTrue(elements.GetArrayLength() > 0);

        // Find
        using var find = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/find", new { value = "Arcana" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, find.StatusCode);

        // Eval
        using var eval = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new { expression = "document.title" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, eval.StatusCode);

        // Actions: Fill, Select, Click, ClickAt, Key, Type, Viewport, Scroll
        var firstEl = elements[0].GetProperty("reference").GetString()!;
        using var fill = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/fill", new { @ref = firstEl, value = "sample" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, fill.StatusCode);

        using var select = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/select", new { @ref = firstEl, value = "opt1" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, select.StatusCode);

        using var click = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/click", new { @ref = firstEl }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, click.StatusCode);

        using var clickAt = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/click-at", new { x = 50.0, y = 50.0 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, clickAt.StatusCode);

        using var key = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/key", new { key = "Tab" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, key.StatusCode);

        using var type = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/type", new { value = "hello" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, type.StatusCode);

        using var viewport = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/viewport", new { width = 1024, height = 768 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, viewport.StatusCode);

        using var scroll = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/mouse-wheel", new { deltaX = 0, deltaY = 100 }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, scroll.StatusCode);
    }

    [TestMethod]
    public async Task FolioServesCookiesStorageScreenshotsAndDiagnostics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var ctx = new FolioApiContext(new ArcanaOptions { Backend = "folio", AllowPrivateNetwork = true });

        using var create = await ctx.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // 1. Screenshot returns valid PNG bytes
        using var screenshot = await ctx.Client.GetAsync($"v1/pages/{pageId}/screenshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, screenshot.StatusCode);
        var bytes = await screenshot.Content.ReadAsByteArrayAsync(timeout.Token);
        Assert.IsTrue(bytes.Length >= 8);
        Assert.AreEqual(0x89, bytes[0]);
        Assert.AreEqual(0x50, bytes[1]);
        Assert.AreEqual(0x4E, bytes[2]);
        Assert.AreEqual(0x47, bytes[3]);

        // 2. Cookies GET & PUT
        using var getCookies = await ctx.Client.GetAsync($"v1/pages/{pageId}/cookies", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, getCookies.StatusCode);

        using var setCookies = await ctx.Client.PutAsJsonAsync($"v1/pages/{pageId}/cookies", new[]
        {
            new { name = "folio_session", value = "secret123", path = "/" }
        }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, setCookies.StatusCode);

        // 3. Storage GET & PUT
        using var getStorage = await ctx.Client.GetAsync($"v1/pages/{pageId}/storage", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, getStorage.StatusCode);
        using var storageJson = JsonDocument.Parse(await getStorage.Content.ReadAsStringAsync(timeout.Token));
        var origin = storageJson.RootElement.GetProperty("origin").GetString();

        using var setStorage = await ctx.Client.PutAsJsonAsync($"v1/pages/{pageId}/storage", new
        {
            origin,
            localStorage = new Dictionary<string, string> { ["user"] = "test" },
            sessionStorage = new Dictionary<string, string> { ["tab"] = "1" },
            cookies = new object[] { }
        }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, setStorage.StatusCode);

        // 4. CDP Send
        using var cdp = await ctx.Client.PostAsJsonAsync($"v1/pages/{pageId}/cdp", new
        {
            method = "Page.getLayoutMetrics"
        }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, cdp.StatusCode);

        // 5. WebSocket live events
        using var ws = await ctx.ConnectWebSocketAsync(pageId, timeout.Token);
        Assert.AreEqual(WebSocketState.Open, ws.State);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);

        // 6. Logs
        using var logs = await ctx.Client.GetAsync($"v1/pages/{pageId}/logs", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, logs.StatusCode);
    }
}
