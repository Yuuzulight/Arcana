using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class FolioApiBackendTests
{
    [TestMethod]
    public async Task FolioApiUsesSandboxedInlineDocumentAndRejectsUnsupportedOperations()
    {
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_BROKER");
        if (string.IsNullOrWhiteSpace(executable)) Assert.Inconclusive("Set ARCANA_TEST_FOLIO_BROKER to a built Folio.ContentHost executable to run the sandboxed Folio API test.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var browser = new FolioBrowserBackend(executable!);
        await using var api = new ArcanaApiServer(browser, "folio-test-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "folio-test-token");

        using var createdResponse = await client.PostAsJsonAsync("v1/pages", new
        {
            html = "<!doctype html><html><head><title>Folio Fixture</title></head><body><button>Folio sandbox fixture</button><script>window.fixtureCount=7</script></body></html>"
        }, timeout.Token);
        var createdBody = await createdResponse.Content.ReadAsStringAsync(timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode, createdBody);
        using var created = JsonDocument.Parse(createdBody);
        var id = created.RootElement.GetProperty("pageId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(id));

        using var snapshotResponse = await client.GetAsync($"v1/pages/{id}/snapshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, snapshotResponse.StatusCode);
        using var snapshot = JsonDocument.Parse(await snapshotResponse.Content.ReadAsStringAsync(timeout.Token));
        Assert.AreEqual("Folio Fixture", snapshot.RootElement.GetProperty("title").GetString());
        StringAssert.Contains(snapshot.RootElement.GetProperty("text").GetString(), "Folio sandbox fixture");
        Assert.AreEqual("button", snapshot.RootElement.GetProperty("elements")[0].GetProperty("tag").GetString());

        using var evaluateResponse = await client.PostAsJsonAsync($"v1/pages/{id}/evaluate", new { expression = "window.fixtureCount++" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, evaluateResponse.StatusCode);
        Assert.AreEqual(7, await evaluateResponse.Content.ReadFromJsonAsync<int>(cancellationToken: timeout.Token));
        using var sideEffectResponse = await client.PostAsJsonAsync($"v1/pages/{id}/evaluate", new { expression = "window.fixtureCount" }, timeout.Token);
        Assert.AreEqual(8, await sideEffectResponse.Content.ReadFromJsonAsync<int>(cancellationToken: timeout.Token));

        using var reloaded = await client.PostAsJsonAsync($"v1/pages/{id}/reload", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, reloaded.StatusCode, await reloaded.Content.ReadAsStringAsync(timeout.Token));
        using var resetState = await client.PostAsJsonAsync($"v1/pages/{id}/evaluate", new { expression = "window.fixtureCount" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, resetState.StatusCode);
        Assert.AreEqual(7, await resetState.Content.ReadFromJsonAsync<int>(cancellationToken: timeout.Token));

        using var unsupportedNavigation = await client.PostAsJsonAsync($"v1/pages/{id}/navigate", new { url = "https://example.com" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, unsupportedNavigation.StatusCode);
        using var screenshot = await client.GetAsync($"v1/pages/{id}/screenshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, screenshot.StatusCode);
        Assert.AreEqual("image/png", screenshot.Content.Headers.ContentType?.MediaType);
        var png = await screenshot.Content.ReadAsByteArrayAsync(timeout.Token);
        Assert.IsTrue(png.Length > 33);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.IsTrue(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) > 0);
        Assert.IsTrue(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) > 0);
        using var unsupportedJpeg = await client.GetAsync($"v1/pages/{id}/screenshot?format=jpeg", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, unsupportedJpeg.StatusCode);
        using var unsupportedCookies = await client.GetAsync($"v1/pages/{id}/cookies", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, unsupportedCookies.StatusCode);
        using var unsupportedLogs = await client.GetAsync($"v1/pages/{id}/logs", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, unsupportedLogs.StatusCode);
        using var unsupportedClick = await client.PostAsJsonAsync($"v1/pages/{id}/click", new { @ref = "e1" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, unsupportedClick.StatusCode);

        using var failedCreate = await client.PostAsJsonAsync("v1/pages", new { url = "https://example.com" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, failedCreate.StatusCode);
        using var tabs = JsonDocument.Parse(await client.GetStringAsync("v1/tabs", timeout.Token));
        Assert.AreEqual(1, tabs.RootElement.GetArrayLength(), "A rejected external navigation must retire its just-created sandboxed page.");

        using var deleted = await client.DeleteAsync($"v1/pages/{id}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        using var emptyTabs = JsonDocument.Parse(await client.GetStringAsync("v1/tabs", timeout.Token));
        Assert.AreEqual(0, emptyTabs.RootElement.GetArrayLength());
    }

    [TestMethod]
    public async Task FolioNavigationRefusesAnUnlistedOriginAndRetiresThePage()
    {
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_NAVIGATION_BROKER");
        if (string.IsNullOrWhiteSpace(executable)) Assert.Inconclusive("Set ARCANA_TEST_FOLIO_NAVIGATION_BROKER to the navigation-enabled Folio host.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var browser = new FolioBrowserBackend(executable!, ["https://allowed.invalid/fixtures/"]);
        await using var api = new ArcanaApiServer(browser, "folio-navigation-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "folio-navigation-token");

        using var response = await client.PostAsJsonAsync("v1/pages", new { url = "https://denied.invalid/" }, timeout.Token);
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, json);
        using var error = JsonDocument.Parse(json);
        Assert.AreEqual("policy_refused", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var tabs = JsonDocument.Parse(await client.GetStringAsync("v1/tabs", timeout.Token));
        Assert.AreEqual(0, tabs.RootElement.GetArrayLength());
    }

    [TestMethod]
    public async Task FolioNavigationLoadsTheAllowedHttpsDocument()
    {
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_NAVIGATION_BROKER");
        var fixture = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_HTTPS_FIXTURE");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(fixture))
            Assert.Inconclusive("Set the navigation broker and HTTPS fixture URL to run the live navigation test.");
        var url = new Uri(fixture!);
        var expectedUrl = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_HTTPS_FINAL_URL") ?? url.AbsoluteUri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var browser = new FolioBrowserBackend(executable!, [url.GetLeftPart(UriPartial.Authority)]);
        await using var api = new ArcanaApiServer(browser, "folio-navigation-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "folio-navigation-token");

        using var response = await client.PostAsJsonAsync("v1/pages", new { url = url.AbsoluteUri }, timeout.Token);
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, json);
        using var page = JsonDocument.Parse(json);
        Assert.AreEqual(expectedUrl, page.RootElement.GetProperty("url").GetString());
        using var snapshot = JsonDocument.Parse(await client.GetStringAsync($"v1/pages/{page.RootElement.GetProperty("pageId").GetString()}/snapshot", timeout.Token));
        var root = snapshot.RootElement;
        Assert.AreEqual(expectedUrl, root.GetProperty("url").GetString());
        using var tabs = JsonDocument.Parse(await client.GetStringAsync("v1/tabs", timeout.Token));
        Assert.AreEqual(expectedUrl, tabs.RootElement[0].GetProperty("url").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(root.GetProperty("text").GetString()));

        var pagePath = $"v1/pages/{page.RootElement.GetProperty("pageId").GetString()}";
        using var blank = await client.PostAsJsonAsync($"{pagePath}/navigate", new { url = "about:blank" }, timeout.Token);
        blank.EnsureSuccessStatusCode();
        await AssertHistory("back", expectedUrl);
        await AssertHistory("forward", "about:blank");

        async Task AssertHistory(string action, string expected)
        {
            using var move = await client.PostAsJsonAsync($"{pagePath}/{action}", new { }, timeout.Token);
            Assert.IsTrue(move.IsSuccessStatusCode, await move.Content.ReadAsStringAsync(timeout.Token));
            using var current = JsonDocument.Parse(await client.GetStringAsync($"{pagePath}/snapshot", timeout.Token));
            Assert.AreEqual(expected, current.RootElement.GetProperty("url").GetString());
        }
    }

    [TestMethod]
    public async Task FolioNavigationRefusesARedirectOutsideTheAllowedPath()
    {
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_NAVIGATION_BROKER");
        if (string.IsNullOrWhiteSpace(executable) || Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_LIVE_WEB") != "1")
            Assert.Inconclusive("Set the navigation broker and ARCANA_TEST_FOLIO_LIVE_WEB=1 to run the public HTTPS fixture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var browser = new FolioBrowserBackend(executable!, ["https://httpbingo.org/redirect-to"]);
        await using var api = new ArcanaApiServer(browser, "folio-redirect-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "folio-redirect-token");

        using var response = await client.PostAsJsonAsync("v1/pages",
            new { url = "https://httpbingo.org/redirect-to?url=%2Fhtml" }, timeout.Token);
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, json);
        using var error = JsonDocument.Parse(json);
        Assert.AreEqual("policy_refused", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var tabs = JsonDocument.Parse(await client.GetStringAsync("v1/tabs", timeout.Token));
        Assert.AreEqual(0, tabs.RootElement.GetArrayLength());
    }

    [TestMethod]
    public async Task FolioNavigationRunsARelativeExternalScript()
    {
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_NAVIGATION_BROKER");
        if (string.IsNullOrWhiteSpace(executable) || Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_LIVE_WEB") != "1")
            Assert.Inconclusive("Set the navigation broker and ARCANA_TEST_FOLIO_LIVE_WEB=1 to run the public HTTPS fixture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var browser = new FolioBrowserBackend(executable!, ["https://wpt.live"]);
        await using var api = new ArcanaApiServer(browser, "folio-script-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "folio-script-token");

        const string fixture = "https://wpt.live/html/semantics/scripting-1/the-script-element/script-charset-01.html";
        using var response = await client.PostAsJsonAsync("v1/pages", new { url = fixture }, timeout.Token);
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, json);
        using var page = JsonDocument.Parse(json);
        var pageId = page.RootElement.GetProperty("pageId").GetString();
        using var evaluated = await client.PostAsJsonAsync($"v1/pages/{pageId}/evaluate",
            new { expression = "document.body.textContent = window.getSomeString()" }, timeout.Token);
        var result = await evaluated.Content.ReadAsStringAsync(timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, evaluated.StatusCode, result);
        var scriptText = JsonSerializer.Deserialize<string>(result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(scriptText), "The external script should return text.");
        using var snapshot = JsonDocument.Parse(await client.GetStringAsync($"v1/pages/{pageId}/snapshot", timeout.Token));
        Assert.AreEqual(scriptText, snapshot.RootElement.GetProperty("text").GetString()?.Trim());

        using var reload = await client.PostAsJsonAsync($"v1/pages/{pageId}/reload", new { }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, reload.StatusCode, await reload.Content.ReadAsStringAsync(timeout.Token));
        using var reloaded = JsonDocument.Parse(await client.GetStringAsync($"v1/pages/{pageId}/snapshot", timeout.Token));
        Assert.AreEqual(fixture, reloaded.RootElement.GetProperty("url").GetString());
        Assert.AreNotEqual(scriptText, reloaded.RootElement.GetProperty("text").GetString()?.Trim());
    }

    [TestMethod]
    public async Task ArcanaCliDoesNotReportReadyWhenFolioDisablesTheSandbox()
    {
        var broker = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_BROKER");
        var cli = Environment.GetEnvironmentVariable("ARCANA_TEST_ARCANA_CLI");
        if (string.IsNullOrWhiteSpace(broker) || string.IsNullOrWhiteSpace(cli))
            Assert.Inconclusive("Set the Folio broker and Arcana CLI paths to verify startup.");

        var start = CreateFolioApiStartInfo(cli!, broker!);
        start.Environment["FOLIO_UNSANDBOXED"] = "1";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var host = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Arcana.");
        try
        {
            var output = host.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = host.StandardError.ReadToEndAsync(timeout.Token);
            await host.WaitForExitAsync(timeout.Token);
            Assert.AreNotEqual(0, host.ExitCode);
            Assert.AreEqual("", await output);
            StringAssert.Contains(await error, "FOLIO_UNSANDBOXED disables the required content-process sandbox");
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync();
            }
        }
    }

    [TestMethod]
    public async Task ArcanaHostExitReleasesBrokerAndSandboxWorker()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Folio AppContainer process cleanup is verified on Windows.");
        var broker = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_BROKER");
        var cli = Environment.GetEnvironmentVariable("ARCANA_TEST_ARCANA_CLI");
        if (string.IsNullOrWhiteSpace(broker) || string.IsNullOrWhiteSpace(cli))
            Assert.Inconclusive("Set ARCANA_TEST_FOLIO_BROKER and ARCANA_TEST_ARCANA_CLI to verify process cleanup after an Arcana host exit.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var before = GetProcessIds(broker!);
        var start = CreateFolioApiStartInfo(cli!, broker!);

        using var host = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the Arcana API test host.");
        var stderr = host.StandardError.ReadToEndAsync();
        try
        {
            var readyLine = await host.StandardOutput.ReadLineAsync(timeout.Token);
            if (readyLine is null)
            {
                await host.WaitForExitAsync(timeout.Token);
                Assert.Fail("Arcana API host exited before reporting readiness: " + await stderr);
            }
            using var ready = JsonDocument.Parse(readyLine);
            var endpoint = new Uri(ready.RootElement.GetProperty("endpoint").GetString()!);
            using var client = new HttpClient { BaseAddress = endpoint };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "crash-cleanup-test");
            using var page = await client.PostAsJsonAsync("v1/pages", new { html = "<!doctype html><title>Crash cleanup</title><body>live</body>" }, timeout.Token);
            Assert.AreEqual(HttpStatusCode.Created, page.StatusCode, await page.Content.ReadAsStringAsync(timeout.Token));

            var spawned = Array.Empty<int>();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                spawned = GetProcessIds(broker!).Except(before).ToArray();
                if (spawned.Length >= 2) break;
                await Task.Delay(100, timeout.Token);
            }
            Assert.IsTrue(spawned.Length >= 2, "The API should own a broker and a separately sandboxed Folio worker.");

            host.Kill();
            await host.WaitForExitAsync(timeout.Token);
            var remaining = spawned;
            for (var attempt = 0; attempt < 150; attempt++)
            {
                remaining = spawned.Intersect(GetProcessIds(broker!)).ToArray();
                if (remaining.Length == 0) break;
                await Task.Delay(100, timeout.Token);
            }
            Assert.AreEqual(0, remaining.Length, "Folio broker and sandbox worker must exit after their Arcana parent process dies.");
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill();
                await host.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static ProcessStartInfo CreateFolioApiStartInfo(string cli, string broker)
    {
        var cliPath = Path.GetFullPath(cli);
        var cliIsAssembly = Path.GetExtension(cliPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(cliIsAssembly ? "dotnet" : cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (cliIsAssembly) start.ArgumentList.Add(cliPath);
        start.ArgumentList.Add("api");
        start.ArgumentList.Add("--backend=folio");
        start.ArgumentList.Add($"--folio-broker={broker}");
        start.ArgumentList.Add("--port=0");
        start.ArgumentList.Add("--token=crash-cleanup-test");

        return start;
    }

    private static int[] GetProcessIds(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        return Process.GetProcessesByName(processName)
            .Where(process =>
            {
                try { return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
            })
            .Select(process =>
            {
                try { return process.Id; }
                finally { process.Dispose(); }
            })
            .ToArray();
    }
}
