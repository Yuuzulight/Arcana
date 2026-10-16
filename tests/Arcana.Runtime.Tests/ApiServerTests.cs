using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class ApiServerTests
{
    [TestMethod]
    public async Task V1RequiresTokenOriginAndServesPageActions()
    {
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_ENGINE");
        if (string.IsNullOrWhiteSpace(executable)) Assert.Inconclusive("Set ARCANA_TEST_ENGINE to run the API transport test.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var site = new TestSite();
        await using var runtime = new BrowserRuntime(new ArcanaOptions { ExecutablePath = executable, AllowPrivateNetwork = true });
        await runtime.StartAsync(timeout.Token);
        await using var browser = await ArcanaBrowser.ConnectAsync(runtime, timeout.Token);
        await using var api = new ArcanaApiServer(browser, "test-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };

        using (var unauthorized = await client.GetAsync("v1/health", timeout.Token))
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        using (var foreign = await client.GetAsync("v1/health", timeout.Token))
            Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");

        using var health = await client.GetAsync("v1/health", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync(timeout.Token));
        Assert.AreEqual("v1", healthJson.RootElement.GetProperty("apiVersion").GetString());

        using var create = await client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
        var pageId = created.RootElement.GetProperty("pageId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(pageId));

        using var snapshot = await client.GetAsync($"v1/pages/{pageId}/snapshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, snapshot.StatusCode);
        StringAssert.Contains(await snapshot.Content.ReadAsStringAsync(timeout.Token), "Arcana parity fixture");

        using var action = await client.PostAsJsonAsync($"v1/pages/{pageId}/navigate", new { url = site.Origin + "/two" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, action.StatusCode);
        using var deleted = await client.DeleteAsync($"v1/pages/{pageId}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }
}
