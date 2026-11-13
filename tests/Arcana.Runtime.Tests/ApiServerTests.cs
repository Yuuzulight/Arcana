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
            Assert.Fail("ARCANA_TEST_ENDPOINT, ARCANA_TEST_TOKEN, and ARCANA_TEST_ENGINE_ID must be configured together.");

        Uri endpoint;
        string token;
        BrowserRuntime? runtime = null;
        ArcanaBrowser? browser = null;
        ArcanaApiServer? api = null;
        if (useExternalEndpoint)
        {
            if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out endpoint!) ||
                endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
                endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                endpoint.AbsolutePath != "/")
                Assert.Fail("ARCANA_TEST_ENDPOINT must be an absolute HTTP loopback origin without a path, query, or fragment.");
            token = configuredToken!;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(executable))
                Assert.Inconclusive("Set ARCANA_TEST_ENGINE to run the API transport test, or set ARCANA_TEST_ENDPOINT, ARCANA_TEST_TOKEN, and ARCANA_TEST_ENGINE_ID for an external loopback API.");
            token = "test-token";
            endpoint = null!;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var site = new TestSite();
        try
        {
            if (!useExternalEndpoint)
            {
                runtime = new BrowserRuntime(new ArcanaOptions { ExecutablePath = executable, AllowPrivateNetwork = true });
                await runtime.StartAsync(timeout.Token);
                browser = await ArcanaBrowser.ConnectAsync(runtime, timeout.Token);
                api = new ArcanaApiServer(browser, token);
                api.Start();
                endpoint = api.Endpoint;
            }

            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = endpoint };

            using (var unauthorized = await client.GetAsync("v1/health", timeout.Token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
            using (var foreign = await client.GetAsync("v1/health", timeout.Token))
                Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");

            using var health = await client.GetAsync("v1/health", timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
            using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync(timeout.Token));
            Assert.AreEqual("v1", healthJson.RootElement.GetProperty("apiVersion").GetString());
            if (useExternalEndpoint)
            {
                var engine = healthJson.RootElement.GetProperty("engine");
                var actualEngineId = engine.GetProperty("product").GetString();
                Assert.AreEqual(expectedEngineId, actualEngineId,
                    "Health engine.product did not match ARCANA_TEST_ENGINE_ID.");
            }

            using var create = await client.PostAsJsonAsync("v1/pages", new { url = site.Origin }, timeout.Token);
            Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
            var pageId = created.RootElement.GetProperty("pageId").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(pageId));

            using var snapshot = await client.GetAsync($"v1/pages/{pageId}/snapshot", timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, snapshot.StatusCode);
            StringAssert.Contains(await snapshot.Content.ReadAsStringAsync(timeout.Token), "Arcana parity fixture");

            using var wait = await client.PostAsJsonAsync($"v1/pages/{pageId}/wait", new { condition = "selector", value = "body", timeoutMs = 1000 }, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, wait.StatusCode);
            using var idle = await client.PostAsJsonAsync($"v1/pages/{pageId}/wait", new { condition = "networkidle", value = "", timeoutMs = 3000 }, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, idle.StatusCode);
            using var logs = await client.GetAsync($"v1/pages/{pageId}/logs", timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, logs.StatusCode);

            using var action = await client.PostAsJsonAsync($"v1/pages/{pageId}/navigate", new { url = site.Origin + "/two" }, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, action.StatusCode);
            using var deleted = await client.DeleteAsync($"v1/pages/{pageId}", timeout.Token);
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        finally
        {
            if (api is not null) await api.DisposeAsync();
            if (browser is not null) await browser.DisposeAsync();
            if (runtime is not null) await runtime.DisposeAsync();
        }
    }
}
