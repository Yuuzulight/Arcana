using System.Text;
using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class ProtocolParityTests
{
    internal static string Engine()
    {
        var engine = Environment.GetEnvironmentVariable("ARCANA_TEST_ENGINE");
        if (string.IsNullOrEmpty(engine)) Assert.Inconclusive("Set ARCANA_TEST_ENGINE for engine parity tests.");
        return engine;
    }

    [TestMethod]
    public async Task PagesInputCaptureCookiesHistoryAndTabs()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        await using var site = new TestSite();
        await using var runtime = new BrowserRuntime(new ArcanaOptions { ExecutablePath = Engine(), AllowPrivateNetwork = true });
        await runtime.StartAsync(ct);
        await using var browser = await ArcanaBrowser.ConnectAsync(runtime, ct);
        var page = await browser.NewPageAsync(site.Origin, ct);
        await page.WaitForSelectorAsync("#name", cancellationToken: ct);
        StringAssert.Contains(await page.GetTextAsync(ct), "Readable test content");
        StringAssert.Contains(await page.GetMarkdownAsync(ct), "Arcana parity fixture");
        var values = await Task.WhenAll(Enumerable.Range(0, 12).Select(n => page.EvaluateStringAsync($"String({n})", ct)));
        CollectionAssert.AreEqual(Enumerable.Range(0, 12).Select(n => n.ToString()).ToArray(), values);

        using var console = page.Subscribe("Runtime.consoleAPICalled");
        await page.EvaluateStringAsync("console.log('event survived')", ct);
        StringAssert.Contains((await console.ReadAsync(ct)).Parameters.ToString(), "event survived");
        await page.EvaluateStringAsync("document.getElementById('name').focus()", ct);
        await page.TypeTextAsync("typed", ct);
        Assert.AreEqual("typed", await page.EvaluateStringAsync("document.getElementById('name').value", ct));
        await page.PressKeyAsync("Backspace", ct);
        Assert.AreEqual("type", await page.EvaluateStringAsync("document.getElementById('name').value", ct));
        var rect = await page.EvaluateAsync("(() => {const r=document.getElementById('click').getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2};})()", ct);
        await page.ClickAtAsync(rect.GetProperty("x").GetDouble(), rect.GetProperty("y").GetDouble(), ct);
        await page.WaitForTextAsync("Clicked", cancellationToken: ct);

        await page.SetCookiesAsync([new { name = "parity", value = "cookie-value", url = site.Origin, path = "/" }], ct);
        StringAssert.Contains((await page.GetCookiesAsync(ct)).ToString(), "cookie-value");
        await page.ClearCookiesAsync(ct);
        Assert.AreEqual(0, (await page.GetCookiesAsync(ct)).GetProperty("cookies").GetArrayLength());
        await page.SetViewportAsync(640, 480, ct);
        var png = await page.CaptureScreenshotAsync(ct);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var full = await page.CaptureScreenshotAsync(true, ct);
        Assert.IsTrue(full.Length > 100);
        var pdf = await page.PrintPdfAsync(cancellationToken: ct);
        Assert.AreEqual("%PDF-", Encoding.ASCII.GetString(pdf[..5]));

        using var frames = page.Subscribe("Page.screencastFrame");
        await page.SendCdpAsync("Page.startScreencast", new { format = "png", maxWidth = 320, maxHeight = 240 }, ct);
        var frame = await frames.ReadAsync(ct);
        Assert.IsTrue(Convert.FromBase64String(frame.Parameters.GetProperty("data").GetString()!).Length > 100);
        await page.SendCdpAsync("Page.screencastFrameAck", new { sessionId = frame.Parameters.GetProperty("sessionId").GetInt32() }, ct);
        await page.SendCdpAsync("Page.stopScreencast", cancellationToken: ct);

        await page.NavigateAndWaitAsync(site.Origin + "/two", ct);
        Assert.IsTrue(await page.GoBackAsync(ct));
        Assert.AreEqual("Arcana fixture", await page.EvaluateStringAsync("document.title", ct));
        Assert.IsTrue(await page.GoForwardAsync(ct));
        await page.ReloadAsync(ct);
        Assert.AreEqual("Second page", await page.EvaluateStringAsync("document.title", ct));
        var second = await browser.NewPageAsync(cancellationToken: ct);
        Assert.IsTrue((await browser.GetTabsAsync(ct)).Any(t => t.TargetId == second.TargetId));
        var attached = await browser.AttachPageAsync(second.TargetId, ct);
        await attached.EvaluateStringAsync("document.title='attached'", ct);
        Assert.AreEqual("attached", await second.EvaluateStringAsync("document.title", ct));
        await second.CloseAsync(ct);
        Assert.IsFalse((await browser.GetTabsAsync(ct)).Any(t => t.TargetId == second.TargetId));
        var context = await browser.CreateContextAsync(ct);
        var isolated = await browser.NewPageInContextAsync(context, site.Origin, ct);
        await page.SetCookiesAsync([new { name = "private", value = "default-context", url = site.Origin }], ct);
        Assert.IsFalse((await isolated.GetCookiesAsync(ct)).ToString().Contains("default-context"));
        await browser.DisposeContextAsync(context, ct);
        await Assert.ThrowsExceptionAsync<CdpException>(() => page.SendCdpAsync("Arcana.nonexistent", cancellationToken: ct));
        await Assert.ThrowsExceptionAsync<TimeoutException>(() => page.WaitForSelectorAsync("#missing", TimeSpan.FromMilliseconds(150), ct));
    }

    [TestMethod]
    public async Task FormsStorageAndNetworkBodiesRoundTrip()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var site = new TestSite();
        await using var runtime = new BrowserRuntime(new ArcanaOptions { ExecutablePath = Engine(), AllowPrivateNetwork = true });
        await runtime.StartAsync(ct);
        await using var browser = await ArcanaBrowser.ConnectAsync(runtime, ct);
        var page = await browser.NewPageAsync(site.Origin, ct);
        var snapshot = await page.SnapshotAsync(ct);
        Assert.AreEqual(1, (await page.DetectFormsAsync(ct)).GetArrayLength());
        Assert.AreEqual(1, (await page.GetLinksAsync(ct)).GetArrayLength());
        Assert.AreEqual("Arcana parity fixture", (await page.QueryAsync("h1", ct))[0].GetString());
        string Ref(string name) => snapshot.Elements.Single(e => e.Name == name).Reference;
        var nameRef = Ref("Name");
        var selectRef = snapshot.Elements.Single(e => e.Tag == "select").Reference;
        var checkRef = snapshot.Elements.Single(e => e.Type == "checkbox").Reference;
        await page.FillFormAsync([new(nameRef, "Ada"), new(selectRef, "b", FormFieldKind.Select), new(checkRef, Kind: FormFieldKind.Check)], cancellationToken: ct);
        Assert.AreEqual("b", await page.EvaluateStringAsync("document.getElementById('choice').value", ct));
        Assert.AreEqual("true", await page.EvaluateStringAsync("document.getElementById('check').checked", ct));
        Assert.AreEqual("name", await page.GetAttributeAsync(nameRef, "name", ct));
        await page.SetCheckedAsync(checkRef, false, ct);
        await page.EvaluateStringAsync("localStorage.setItem('local','saved');sessionStorage.setItem('session','saved')", ct);
        var storage = await page.ExportStorageAsync(ct);
        await page.EvaluateStringAsync("localStorage.clear();sessionStorage.clear()", ct);
        await page.RestoreStorageAsync(storage, ct);
        Assert.AreEqual("saved", await page.EvaluateStringAsync("localStorage.getItem('local')", ct));
        Assert.AreEqual("saved", await page.EvaluateStringAsync("sessionStorage.getItem('session')", ct));
        await page.NavigateAndWaitAsync(site.Origin + "/two", ct);
        Assert.AreEqual("saved", await page.EvaluateStringAsync("localStorage.getItem('local')", ct));
        Assert.AreEqual("saved", await page.EvaluateStringAsync("sessionStorage.getItem('session')", ct));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => page.RestoreStorageAsync(storage with { Origin = "https://different.example" }, ct));

        using var network = page.Subscribe("Network.responseReceived");
        await page.SendCdpAsync("Network.enable", cancellationToken: ct);
        StringAssert.Contains(await page.EvaluateStringAsync("fetch('/api').then(r=>r.text())", ct), "network works");
        var response = await network.ReadAsync(ct);
        var id = response.Parameters.GetProperty("requestId").GetString();
        var stream = await page.SendCdpAsync("Fetch.takeResponseBodyAsStream", new { requestId = id }, ct);
        var handle = stream.GetProperty("stream").GetString();
        var body = await page.SendCdpAsync("IO.read", new { handle, size = 4096 }, ct);
        var text = body.GetProperty("data").GetString()!;
        if (body.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean()) text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
        StringAssert.Contains(text, "network works");
        await page.SendCdpAsync("IO.close", new { handle }, ct);
        await page.ClickAsync(Ref("Submit"), ct);
        await page.WaitForTextAsync("Submitted", cancellationToken: ct);
        StringAssert.Contains(site.LastPost, "name=Ada");
    }

    [TestMethod]
    public async Task InterceptionCanReplyWhileEvaluationIsPending()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var site = new TestSite();
        await using var runtime = new BrowserRuntime(new ArcanaOptions { ExecutablePath = Engine(), AllowPrivateNetwork = true });
        await runtime.StartAsync(ct);
        await using var browser = await ArcanaBrowser.ConnectAsync(runtime, ct);
        var page = await browser.NewPageAsync(site.Origin, ct);
        using var requests = page.Subscribe("Fetch.requestPaused");
        await page.SendCdpAsync("Fetch.enable", new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } }, ct);
        var evaluation = page.EvaluateStringAsync("fetch('/api').then(r=>r.text())", ct);
        var paused = await requests.ReadAsync(ct);
        await page.SendCdpAsync("Fetch.fulfillRequest", new
        {
            requestId = paused.Parameters.GetProperty("requestId").GetString(), responseCode = 200,
            responseHeaders = new[] { new { name = "Content-Type", value = "text/html" } },
            body = Convert.ToBase64String(Encoding.UTF8.GetBytes("Intercepted response"))
        }, ct);
        Assert.AreEqual("Intercepted response", await evaluation);
        await page.SendCdpAsync("Fetch.disable", cancellationToken: ct);
    }
}
