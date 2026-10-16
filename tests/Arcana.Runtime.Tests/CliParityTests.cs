using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class CliParityTests
{
    [TestMethod]
    public async Task ServeAndAuthenticatedHttpMcpAreAvailableThroughArcana()
    {
        var engine = ProtocolParityTests.Engine();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        foreach (var mode in new[] { "serve", "mcp" })
        {
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { RpcProcess.CliPath(), mode, "--port", port.ToString(), "--executable=" + engine }) start.ArgumentList.Add(arg);
            if (mode == "mcp") start.ArgumentList.Add("--http");
            const string token = "arcana-parity-test-authentication-token";
            start.Environment["OBSCURA_MCP_TOKEN"] = token;
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
                while (true)
                {
                    try { using var ready = await http.GetAsync(mode == "serve" ? "/json/version" : "/mcp", ct); break; }
                    catch (HttpRequestException) { await Task.Delay(50, ct); }
                }
                if (mode == "serve")
                {
                    using var version = JsonDocument.Parse(await http.GetStringAsync("/json/version", ct));
                    StringAssert.StartsWith(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!, "ws://127.0.0.1:");
                }
                else
                {
                    var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
                    using var denied = await http.PostAsync("/mcp", new StringContent(request, Encoding.UTF8, "application/json"), ct);
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var accepted = await http.PostAsync("/mcp", new StringContent(request, Encoding.UTF8, "application/json"), ct);
                    accepted.EnsureSuccessStatusCode();
                    using var reply = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(ct));
                    StringAssert.Contains(reply.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString()!, PinnedEngineCommit()[..7]);
                }
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(output, errors);
            }
        }
    }

    [TestMethod]
    public async Task FetchFormatsBinaryOutputScrapeAndExitCodesMatchNativeEngine()
    {
        var engine = ProtocolParityTests.Engine();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        await using var site = new TestSite();
        foreach (var format in new[] { "html", "text", "links", "markdown", "assets", "cookies" })
        {
            var args = new[] { "fetch", site.Origin, "--dump", format, "--wait", "0", "--quiet", "--allow-private-network" };
            var native = await RunAsync(engine, args, ct);
            var arcana = await RunArcanaAsync(engine, args, ct);
            Assert.AreEqual(0, arcana.ExitCode, arcana.Errors);
            CollectionAssert.AreEqual(native.Output, arcana.Output, format);
        }
        var binaryArgs = new[] { "fetch", site.Origin + "/binary", "--dump", "original", "--quiet", "--allow-private-network" };
        var binary = await RunArcanaAsync(engine, binaryArgs, ct);
        Assert.AreEqual(0, binary.ExitCode, binary.Errors);
        CollectionAssert.AreEqual(new byte[] { 0, 255, 128, 10, 13, 65, 0, 254 }, binary.Output);
        var scrape = await RunArcanaAsync(engine, ["scrape", site.Origin, site.Origin + "/two", "--eval", "document.title", "--concurrency", "2", "--quiet", "--allow-private-network"], ct);
        Assert.AreEqual(0, scrape.ExitCode, scrape.Errors);
        var scraped = Encoding.UTF8.GetString(scrape.Output);
        StringAssert.Contains(scraped, "Arcana fixture"); StringAssert.Contains(scraped, "Second page");
        var badArgs = new[] { "fetch", "--this-is-not-a-flag" };
        Assert.AreEqual((await RunAsync(engine, badArgs, ct)).ExitCode, (await RunArcanaAsync(engine, badArgs, ct)).ExitCode);
        var blocked = await RunArcanaAsync(engine, ["fetch", site.Origin, "--dump", "text", "--quiet"], ct);
        Assert.AreNotEqual(0, blocked.ExitCode, "Private networks must require opt-in.");
    }

    private static Task<CommandResult> RunArcanaAsync(string engine, string[] args, CancellationToken ct) =>
        RunAsync("dotnet", [RpcProcess.CliPath(), .. args, "--executable=" + engine], ct);

    internal static async Task<CommandResult> RunAsync(string executable, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["OBSCURA_ALLOW_PRIVATE_NETWORK"] = "0";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var bytes = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(bytes, ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            await output;
            return new CommandResult(process.ExitCode, bytes.ToArray(), await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    // The engine commit pinned in engine.lock.json, so a pin bump does not mean editing this test.
    private static string PinnedEngineCommit()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var lockFile = Path.Combine(directory.FullName, "engine.lock.json");
            if (!File.Exists(lockFile)) continue;
            using var pin = JsonDocument.Parse(File.ReadAllText(lockFile));
            return pin.RootElement.GetProperty("sourceCommit").GetString()!;
        }
        throw new FileNotFoundException("engine.lock.json not found above the test binaries.");
    }
}

internal sealed record CommandResult(int ExitCode, byte[] Output, string Errors);
