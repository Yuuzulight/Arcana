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
        if (string.IsNullOrWhiteSpace(executable))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                Assert.Fail("CI must set ARCANA_TEST_FOLIO_BROKER to the pinned standalone Folio.ContentHost executable.");
            Assert.Inconclusive("Set ARCANA_TEST_FOLIO_BROKER to a built Folio.ContentHost executable to run the sandboxed Folio API test.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var browser = new FolioBrowserBackend(executable!);
        await using var api = new ArcanaApiServer(browser, "folio-test-token");
        api.Start();
        using var client = new HttpClient { BaseAddress = api.Endpoint };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "folio-test-token");

        using var createdResponse = await client.PostAsJsonAsync("v1/pages", new
        {
            html = "<!doctype html><html><head><title>Folio Fixture</title></head><body><button>Folio sandbox fixture</button><p id='mutation'>before</p><script>window.fixtureCount=7</script></body></html>"
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

        using var evaluateResponse = await client.PostAsJsonAsync($"v1/pages/{id}/evaluate", new { expression = "(()=>{window.fixtureCount++;document.querySelector('#mutation').textContent='DOM mutated '+window.fixtureCount;return window.fixtureCount})()" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, evaluateResponse.StatusCode);
        Assert.AreEqual(8, await evaluateResponse.Content.ReadFromJsonAsync<int>(cancellationToken: timeout.Token));
        using var mutatedSnapshotResponse = await client.GetAsync($"v1/pages/{id}/snapshot", timeout.Token);
        using var mutatedSnapshot = JsonDocument.Parse(await mutatedSnapshotResponse.Content.ReadAsStringAsync(timeout.Token));
        StringAssert.Contains(mutatedSnapshot.RootElement.GetProperty("text").GetString(), "DOM mutated 8", "The JS side effect must be visible in the real page snapshot.");
        using var sideEffectResponse = await client.PostAsJsonAsync($"v1/pages/{id}/evaluate", new { expression = "window.fixtureCount" }, timeout.Token);
        Assert.AreEqual(8, await sideEffectResponse.Content.ReadFromJsonAsync<int>(cancellationToken: timeout.Token));

        using var unsupportedNavigation = await client.PostAsJsonAsync($"v1/pages/{id}/navigate", new { url = "https://example.com" }, timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, unsupportedNavigation.StatusCode);
        using var unsupportedScreenshot = await client.GetAsync($"v1/pages/{id}/screenshot", timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotImplemented, unsupportedScreenshot.StatusCode);
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
    public async Task FolioBrokerRunsDomMutationAndProducesARealRenderedFrame()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Folio AppContainer rendering is verified on Windows.");
        var executable = Environment.GetEnvironmentVariable("ARCANA_TEST_FOLIO_BROKER");
        if (string.IsNullOrWhiteSpace(executable))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                Assert.Fail("CI must set ARCANA_TEST_FOLIO_BROKER to the pinned standalone Folio.ContentHost executable.");
            Assert.Inconclusive("Set ARCANA_TEST_FOLIO_BROKER to a built Folio.ContentHost executable to verify rendered-frame evidence.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(Path.GetFullPath(executable!))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--broker");
        start.ArgumentList.Add($"--arcana-parent-pid={Environment.ProcessId}");
        using var broker = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the standalone Folio broker.");
        var stderr = broker.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            using var hello = await BrokerRequestAsync(broker, "hello", new { }, timeout.Token);
            Assert.IsTrue(hello.RootElement.GetProperty("sandboxed").GetBoolean(), "Folio must confirm that its content worker is sandboxed.");
            Assert.AreEqual("Folio.ContentHost/native", hello.RootElement.GetProperty("engine").GetProperty("product").GetString());
            Assert.IsFalse(hello.RootElement.GetProperty("networkEnabled").GetBoolean(), "The qualification fixture must run without network access.");

            using var load = await BrokerRequestAsync(broker, "load", new
            {
                urlLabel = "about:blank",
                html = "<!doctype html><html><body><canvas id='surface' width='80' height='80'></canvas><p id='mutation'>before</p><script>window.count=1;const c=document.querySelector('#surface').getContext('2d');c.fillStyle='#e02030';c.fillRect(0,0,80,80)</script></body></html>"
            }, timeout.Token);
            Assert.IsTrue(load.RootElement.GetProperty("rendered").GetBoolean(), "The engine must report a rendered frame.");
            Assert.IsTrue(load.RootElement.GetProperty("displayItems").GetInt32() > 0, "The frame must contain rendered display items.");
            Assert.IsTrue(load.RootElement.GetProperty("canvasCommands").GetInt32() > 0, "The fixture must execute native canvas drawing commands.");
            Assert.IsTrue(load.RootElement.TryGetProperty("canvasFrame", out var canvasFrame), "The broker must return a real content-frame descriptor.");
            Assert.IsTrue(canvasFrame.GetProperty("sequence").GetInt64() > 0);
            Assert.IsTrue(canvasFrame.GetProperty("width").GetInt32() > 0 && canvasFrame.GetProperty("height").GetInt32() > 0);
            var pixelProbe = canvasFrame.GetProperty("pixelProbe");
            Assert.IsTrue(pixelProbe.GetProperty("changedPixels").GetInt32() > 0, "The read-back frame must contain non-background pixels.");

            using var evaluate = await BrokerRequestAsync(broker, "evaluate", new
            {
                expression = "(()=>{window.count++;document.querySelector('#mutation').textContent='DOM mutated '+window.count;return window.count})()"
            }, timeout.Token);
            Assert.AreEqual("2", evaluate.RootElement.GetProperty("value").GetString(), "The broker must return the observable JavaScript side effect.");
            using var snapshot = await BrokerRequestAsync(broker, "snapshot", new { }, timeout.Token);
            StringAssert.Contains(snapshot.RootElement.GetProperty("snapshot").GetProperty("text").GetString(), "DOM mutated 2");
        }
        finally
        {
            if (!broker.HasExited)
            {
                try { _ = await BrokerRequestAsync(broker, "close", new { }, timeout.Token); }
                catch { broker.Kill(entireProcessTree: true); }
                if (!broker.HasExited) await broker.WaitForExitAsync(CancellationToken.None);
            }
        }
        Assert.AreEqual(0, broker.ExitCode, "Folio broker exited with an error: " + await stderr);
    }

    private static async Task<JsonDocument> BrokerRequestAsync(Process broker, string operation, object payload, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var fields = new Dictionary<string, object?>
        {
            ["id"] = requestId,
            ["op"] = operation
        };
        foreach (var property in payload.GetType().GetProperties()) fields[property.Name] = property.GetValue(payload);
        var request = JsonSerializer.Serialize(fields);
        await broker.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
        await broker.StandardInput.FlushAsync(cancellationToken);
        var line = await broker.StandardOutput.ReadLineAsync(cancellationToken)
            ?? throw new InvalidOperationException("Folio broker closed stdout without a response: " + await broker.StandardError.ReadToEndAsync(cancellationToken));
        var response = JsonDocument.Parse(line);
        if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetString() != requestId)
        {
            response.Dispose();
            throw new InvalidDataException("Folio broker returned a response for a different qualification request.");
        }
        if (response.RootElement.GetProperty("ok").ValueKind != JsonValueKind.True)
        {
            var details = response.RootElement.GetProperty("error").GetProperty("message").GetString();
            response.Dispose();
            throw new InvalidOperationException($"Folio broker {operation} failed: {details}");
        }
        return response;
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
        var cliPath = Path.GetFullPath(cli!);
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
