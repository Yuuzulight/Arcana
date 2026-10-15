using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class PageInteractionTests
{
    [TestMethod]
    public async Task SnapshotAndActionsUseLiveDocumentReferences()
    {
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_ENGINE");
        if (string.IsNullOrWhiteSpace(executable))
            Assert.Inconclusive("Set ARCANA_TEST_ENGINE to run the real engine interaction test.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        await using var runtime = new BrowserRuntime(new ArcanaOptions { ExecutablePath = executable });
        await runtime.StartAsync(ct);
        await using var browser = await ArcanaBrowser.ConnectAsync(runtime, ct);
        var page = await browser.NewPageAsync(cancellationToken: ct);
        await page.EvaluateStringAsync("""
            document.body.innerHTML = `<label for="query">Search</label><input id="query">
              <input type="password" value="do-not-expose" aria-label="Password">
              <button id="go" onclick="document.title=document.getElementById('query').value">Go</button>
              <button disabled>Disabled</button><button hidden>Hidden</button>
              <a href="https://example.com/next">Next</a>`;
            """, ct);

        var snapshot = await page.SnapshotAsync(ct);
        Assert.AreEqual(5, snapshot.Elements.Count, System.Text.Json.JsonSerializer.Serialize(snapshot));
        var query = snapshot.Elements.Single(e => e.Name == "Search");
        var go = snapshot.Elements.Single(e => e.Name == "Go");
        Assert.AreEqual("https://example.com/next", snapshot.Elements.Single(e => e.Name == "Next").Href);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(snapshot).Contains("do-not-expose"));
        Assert.AreEqual(query.Reference, (await page.SnapshotAsync(ct)).Elements.Single(e => e.Name == "Search").Reference);

        const string value = "hello 'quoted' \\ world\nnew line";
        await page.EvaluateStringAsync("window.inputEvents=0; document.getElementById('query').addEventListener('input',()=>window.inputEvents++)", ct);
        await page.FillAsync(query.Reference, value, ct);
        Assert.AreEqual(value, await page.EvaluateStringAsync("document.getElementById('query').value", ct));
        Assert.AreEqual("1", await page.EvaluateStringAsync("String(window.inputEvents)", ct));
        await page.EvaluateStringAsync("document.getElementById('query').readOnly=true", ct);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => page.FillAsync(query.Reference, "blocked", ct));
        await page.EvaluateStringAsync("document.getElementById('query').readOnly=false", ct);
        await page.ClickAsync(go.Reference, ct);
        Assert.AreEqual(value.Replace('\n', ' '), await page.EvaluateStringAsync("document.title", ct));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => page.ClickAsync(snapshot.Elements.Single(e => e.Name == "Disabled").Reference, ct));
        await page.EvaluateStringAsync("document.getElementById('go').style.display='none'", ct);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => page.ClickAsync(go.Reference, ct));
        await page.EvaluateStringAsync("document.getElementById('go').remove()", ct);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => page.ClickAsync(go.Reference, ct));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => page.EvaluateStringAsync("throw new Error('expected failure')", ct));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => browser.NewPageAsync("file:///sensitive.txt", ct));

        var otherPage = await browser.NewPageAsync(cancellationToken: ct);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => otherPage.FillAsync(query.Reference, "wrong page", ct));

        await page.NavigateAsync("about:blank", ct);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => page.FillAsync(query.Reference, "stale", ct));
    }
}
