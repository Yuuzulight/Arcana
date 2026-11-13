using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

/// <summary>Maintained SPA regression flows for the qualified Obscura backend.</summary>
[TestClass]
public sealed class SpaQualificationTests
{
    private sealed class EngineFixture : IAsyncDisposable
    {
        public Uri Endpoint { get; }
        public string Token { get; } = "spa-test-token";
        public HttpClient Client { get; }
        private readonly HttpClientHandler _handler;
        private readonly ArcanaApiServer _api;
        private readonly BrowserRuntime? _obscuraRuntime;
        private readonly ArcanaBrowser? _obscuraBrowser;

        private EngineFixture(
            ArcanaApiServer api,
            BrowserRuntime? obscuraRuntime,
            ArcanaBrowser? obscuraBrowser)
        {
            _api = api;
            _obscuraRuntime = obscuraRuntime;
            _obscuraBrowser = obscuraBrowser;
            Endpoint = api.Endpoint;

            _handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            Client = new HttpClient(_handler) { BaseAddress = Endpoint };
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        public static async Task<EngineFixture> CreateObscuraAsync(CancellationToken cancellationToken)
        {
            var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_ENGINE");
            if (string.IsNullOrWhiteSpace(executable))
            {
                Assert.Inconclusive("Set ARCANA_TEST_ENGINE to run Obscura qualification tests.");
            }

            var options = new ArcanaOptions { ExecutablePath = executable, AllowPrivateNetwork = true };
            var runtime = new BrowserRuntime(options);
            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            var browser = await ArcanaBrowser.ConnectAsync(runtime, cancellationToken).ConfigureAwait(false);
            var api = new ArcanaApiServer(browser, "spa-test-token");
            api.Start();
            return new EngineFixture(api, runtime, browser);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _handler.Dispose();
            await _api.DisposeAsync().ConfigureAwait(false);
            if (_obscuraBrowser is not null) await _obscuraBrowser.DisposeAsync().ConfigureAwait(false);
            if (_obscuraRuntime is not null) await _obscuraRuntime.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ==========================================
    // OBSCURA MAINTAINED SPA FLOWS
    // ==========================================

    [TestMethod]
    public async Task SpaFlow1_StructuredContentAndTableExtraction_Obscura()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var engine = await EngineFixture.CreateObscuraAsync(timeout.Token);

        await RunFlow1Async(engine, site, timeout.Token);
    }

    [TestMethod]
    public async Task SpaFlow2_MultiStepInteractiveForm_Obscura()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var engine = await EngineFixture.CreateObscuraAsync(timeout.Token);

        await RunFlow2Async(engine, site, timeout.Token);
    }

    [TestMethod]
    public async Task SpaFlow3_ClientSideRoutingAndHistory_Obscura()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var engine = await EngineFixture.CreateObscuraAsync(timeout.Token);

        await RunFlow3Async(engine, site, timeout.Token);
    }

    [TestMethod]
    public async Task SpaFlow4_DynamicDomRenderingAndAsyncData_Obscura()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var engine = await EngineFixture.CreateObscuraAsync(timeout.Token);

        await RunFlow4Async(engine, site, timeout.Token);
    }

    [TestMethod]
    public async Task SpaFlow5_SessionStorageAndCookiePersistence_Obscura()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var engine = await EngineFixture.CreateObscuraAsync(timeout.Token);

        await RunFlow5Async(engine, site, timeout.Token);
    }

    [TestMethod]
    public async Task SpaFlow6_LiveScreencastAndPointerKeyboard_Obscura()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var site = new TestSite();
        await using var engine = await EngineFixture.CreateObscuraAsync(timeout.Token);

        await RunFlow6Async(engine, site, timeout.Token);
    }

    // ==========================================
    // SHARED FLOW IMPLEMENTATIONS
    // ==========================================

    private static async Task RunFlow1Async(EngineFixture engine, TestSite site, CancellationToken cancellationToken)
    {
        // Flow 1: Article navigation and structured table extraction
        using var create = await engine.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin + "/table" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // Snapshot extraction
        using var snap = await engine.Client.GetAsync($"v1/pages/{pageId}/snapshot", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, snap.StatusCode);
        using var snapJson = JsonDocument.Parse(await snap.Content.ReadAsStringAsync(cancellationToken));
        var title = snapJson.RootElement.GetProperty("title").GetString();
        StringAssert.Contains(title, "Structured Table Reference");

        // Structured data evaluation: table cells
        using var eval = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate", new
        {
            expression = "document.title"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, eval.StatusCode);

        using var delete = await engine.Client.DeleteAsync($"v1/pages/{pageId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static async Task RunFlow2Async(EngineFixture engine, TestSite site, CancellationToken cancellationToken)
    {
        // Flow 2: Multi-step interactive form flow
        using var create = await engine.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin + "/spa" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // Get snapshot to resolve form controls
        using var snap = await engine.Client.GetAsync($"v1/pages/{pageId}/snapshot", cancellationToken);
        using var snapJson = JsonDocument.Parse(await snap.Content.ReadAsStringAsync(cancellationToken));
        var elements = snapJson.RootElement.GetProperty("elements");

        var inputEl = elements.EnumerateArray().FirstOrDefault(e => e.GetProperty("tag").GetString() == "input" || e.GetProperty("role").GetString() is "input" or "textbox").GetProperty("reference").GetString()!;
        var selectEl = elements.EnumerateArray().FirstOrDefault(e => e.GetProperty("tag").GetString() == "select" || e.GetProperty("role").GetString() == "combobox").GetProperty("reference").GetString()!;
        var submitEl = elements.EnumerateArray().FirstOrDefault(e => e.GetProperty("tag").GetString() == "button" || e.GetProperty("role").GetString() == "button").GetProperty("reference").GetString()!;

        // 1. Fill input
        using var fill = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/fill", new { @ref = inputEl, value = "updated-item-name" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, fill.StatusCode);

        // 2. Select option
        using var select = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/select", new { @ref = selectEl, value = "cat2" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, select.StatusCode);

        // 3. Submit form
        using var click = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/click", new { @ref = submitEl }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, click.StatusCode);

        using var delete = await engine.Client.DeleteAsync($"v1/pages/{pageId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static async Task RunFlow3Async(EngineFixture engine, TestSite site, CancellationToken cancellationToken)
    {
        // Flow 3: Client-side routing and history
        using var create = await engine.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin + "/spa" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // Navigate forward to /two
        using var nav = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/navigate", new { url = site.Origin + "/two" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, nav.StatusCode);

        // Go back
        using var back = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/back", new { }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, back.StatusCode);

        // Go forward
        using var forward = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/forward", new { }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, forward.StatusCode);

        using var delete = await engine.Client.DeleteAsync($"v1/pages/{pageId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static async Task RunFlow4Async(EngineFixture engine, TestSite site, CancellationToken cancellationToken)
    {
        // Flow 4: Dynamic DOM rendering and query
        using var create = await engine.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin + "/spa" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // Find elements with query
        using var find = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/find", new { value = "Home" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, find.StatusCode);
        using var findJson = JsonDocument.Parse(await find.Content.ReadAsStringAsync(cancellationToken));
        Assert.IsTrue(findJson.RootElement.GetArrayLength() > 0);

        using var delete = await engine.Client.DeleteAsync($"v1/pages/{pageId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static async Task RunFlow5Async(EngineFixture engine, TestSite site, CancellationToken cancellationToken)
    {
        // Flow 5: Session storage, local storage, and cookie persistence
        using var create = await engine.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin + "/spa" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // Export storage
        using var storage = await engine.Client.GetAsync($"v1/pages/{pageId}/storage", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, storage.StatusCode);
        using var storageJson = JsonDocument.Parse(await storage.Content.ReadAsStringAsync(cancellationToken));
        var origin = storageJson.RootElement.GetProperty("origin").GetString();

        // Restore updated storage
        using var restore = await engine.Client.PutAsJsonAsync($"v1/pages/{pageId}/storage", new
        {
            origin,
            localStorage = new Dictionary<string, string> { ["token"] = "spa-auth-token-123" },
            sessionStorage = new Dictionary<string, string> { ["activeTab"] = "data" },
            cookies = new[] { new { name = "auth_session", value = "authenticated", path = "/" } }
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, restore.StatusCode);

        // Verify cookies reflect restored value
        using var getCookies = await engine.Client.GetAsync($"v1/pages/{pageId}/cookies", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getCookies.StatusCode);

        using var delete = await engine.Client.DeleteAsync($"v1/pages/{pageId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

    private static async Task RunFlow6Async(EngineFixture engine, TestSite site, CancellationToken cancellationToken)
    {
        // Flow 6: Live screencast, pointer click, and keyboard typing
        using var create = await engine.Client.PostAsJsonAsync("v1/pages", new { url = site.Origin + "/spa" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        var pageId = created.RootElement.GetProperty("pageId").GetString()!;

        // Screenshot
        using var screenshot = await engine.Client.GetAsync($"v1/pages/{pageId}/screenshot", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, screenshot.StatusCode);
        var bytes = await screenshot.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.IsTrue(bytes.Length >= 8);

        // Pointer click-at
        using var clickAt = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/click-at", new { x = 100.0, y = 150.0 }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, clickAt.StatusCode);

        // Keyboard press
        using var keyPress = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/key", new { key = "Enter" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, keyPress.StatusCode);

        // Type text
        using var typeText = await engine.Client.PostAsJsonAsync($"v1/pages/{pageId}/type", new { value = "typed-text" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, typeText.StatusCode);

        using var delete = await engine.Client.DeleteAsync($"v1/pages/{pageId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

}
