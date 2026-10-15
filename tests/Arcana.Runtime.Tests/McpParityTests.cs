using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class McpParityTests
{
    [TestMethod]
    public async Task ArcanaExposesTheExactNativeMcpSchemasAndToolsWork()
    {
        var engine = ProtocolParityTests.Engine();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        await using var site = new TestSite();
        await using var native = RpcProcess.Native(engine);
        await using var arcana = RpcProcess.Arcana(engine);
        await native.CallAsync("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "parity-test", version = "1" } }, ct);
        await arcana.CallAsync("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "parity-test", version = "1" } }, ct);
        var nativeTools = await native.CallAsync("tools/list", new { }, ct);
        var arcanaTools = await arcana.CallAsync("tools/list", new { }, ct);
        Assert.AreEqual(nativeTools.GetRawText(), arcanaTools.GetRawText(), "Every MCP tool schema must match the pinned engine.");
        var expected = arcanaTools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        Assert.AreEqual(38, expected.Count, "Pinned Obscura main rendering build tool inventory.");
        var exercised = new HashSet<string>();

        async Task<JsonElement> Tool(string name, object? args = null)
        {
            var result = await arcana.CallAsync("tools/call", new { name, arguments = args ?? new { } }, ct);
            Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), $"{name}: {result}");
            exercised.Add(name);
            return result;
        }

        await Tool("browser_navigate", new { url = site.Origin });
        foreach (var name in new[] { "browser_snapshot", "browser_markdown", "browser_links", "browser_interactive_elements", "browser_detect_forms", "browser_network_requests", "browser_console_messages" })
            Assert.IsTrue((await Tool(name)).GetProperty("content").GetArrayLength() > 0);
        await Tool("browser_fill", new { selector = "#name", value = "Ada" });
        await Tool("browser_type", new { selector = "#name", text = " Lovelace" });
        await Tool("browser_press_key", new { selector = "#name", key = "End" });
        await Tool("browser_select_option", new { selector = "#choice", value = "b" });
        await Tool("browser_fill_form", new { fields = new[] { new { selector = "#name", value = "Grace", type = "text" }, new { selector = "#check", value = "", type = "check" } } });
        await Tool("browser_click", new { selector = "#click" });
        StringAssert.Contains((await Tool("browser_evaluate", new { expression = "document.getElementById('result').textContent" })).ToString(), "Clicked");
        await Tool("browser_wait_for_function", new { function = "document.getElementById('result').textContent === 'Clicked'", timeout = 1000 });
        await Tool("browser_wait_for", new { selector = "#result", timeout = 1 });
        await Tool("browser_wait_for_text", new { text = "Clicked", timeout = 1 });
        await Tool("browser_get_attribute", new { selector = "#name", attribute = "name" });
        await Tool("browser_count", new { selector = "form" });
        await Tool("browser_extract", new { schema = new Dictionary<string, string> { ["heading"] = "h1", ["links[]"] = "a@href" } });
        await Tool("browser_search", new { query = "Readable" });
        await Tool("browser_scroll", new { direction = "down", amount = 100 });
        await Tool("browser_set_cookie", new { name = "test", value = "cookie", domain = "127.0.0.1" });
        StringAssert.Contains((await Tool("browser_get_cookies")).ToString(), "cookie");
        await Tool("browser_evaluate", new { expression = "localStorage.setItem('parity','stored'); true" });
        var storage = await Tool("browser_storage_state");
        var text = storage.GetProperty("content")[0].GetProperty("text").GetString()!;
        using (var state = JsonDocument.Parse(text)) await Tool("browser_set_storage_state", new { state = state.RootElement });
        await Tool("browser_clear_cookies");
        var image = await Tool("browser_screenshot", new { width = 320, height = 240 });
        var imageBytes = Convert.FromBase64String(image.GetProperty("content")[0].GetProperty("data").GetString()!);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, imageBytes[..8]);
        var pdf = await Tool("browser_pdf", new { scale = 0.5 });
        Assert.AreEqual("application/pdf", pdf.GetProperty("content")[0].GetProperty("resource").GetProperty("mimeType").GetString());
        await Tool("browser_navigate", new { url = site.Origin + "/two" });
        await Tool("browser_back");
        await Tool("browser_forward");
        await Tool("browser_reload");
        await Tool("browser_tab_new", new { url = site.Origin });
        var tabs = await Tool("browser_tab_list");
        StringAssert.Contains(tabs.ToString(), "Arcana fixture");
        // The pinned engine assigns tab-1 to the initial MCP tab.
        await Tool("browser_tab_switch", new { tab_id = "tab-1" });
        await Tool("browser_tab_close");
        await Tool("browser_close");
        CollectionAssert.AreEquivalent(expected.ToArray(), exercised.ToArray(), "Exercise every advertised MCP tool.");
    }
}

internal sealed class RpcProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    private int _id;
    private RpcProcess(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        _process = Process.Start(start)!;
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public static RpcProcess Native(string engine)
    {
        var start = new ProcessStartInfo(engine);
        start.ArgumentList.Add("mcp"); start.ArgumentList.Add("--allow-private-network");
        return new RpcProcess(start);
    }

    public static string CliPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Arcana.slnx"))) root = root.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root!.FullName, "src", "Arcana.Cli", "bin", configuration, "net10.0", "Arcana.Cli.dll");
    }

    public static RpcProcess Arcana(string engine)
    {
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(CliPath()); start.ArgumentList.Add("mcp");
        start.ArgumentList.Add("--executable=" + engine); start.ArgumentList.Add("--allow-private-network");
        return new RpcProcess(start);
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = ++_id;
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null) throw new IOException("MCP exited: " + await _errors);
            using var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (response.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException($"MCP {method}: {error}");
            return response.RootElement.GetProperty("result").Clone();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
        _process.Dispose();
    }
}
