using Arcana.Runtime;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
// --arcana-parent-pid=N: stop when the process that started us is gone (it can't signal us if it crashes).
var parentArg = args.FirstOrDefault(a => a.StartsWith("--arcana-parent-pid=", StringComparison.Ordinal));
if (parentArg is not null)
{
    args = args.Where(a => a != parentArg).ToArray();
    WatchParent(parentArg["--arcana-parent-pid=".Length..], shutdown);
}
try
{
switch (args[0].ToLowerInvariant())
{
    case "engine":
    case "serve":
    case "fetch":
    case "scrape":
    case "mcp":
        var nativeArgs = args[0].Equals("engine", StringComparison.OrdinalIgnoreCase) ? args[1..] : args;
        var executable = GetOption(nativeArgs, "executable");
        Environment.ExitCode = await EngineCommand.RunAsync(nativeArgs.Where(a => !a.StartsWith("--executable=", StringComparison.OrdinalIgnoreCase)), executable, shutdown.Token);
        break;
    case "doctor":
        PrintDoctor();
        break;
    case "run":
        await RunAsync(args[1..], shutdown.Token);
        break;
    case "api":
        await ApiAsync(args[1..], shutdown.Token);
        break;
    case "snapshot" when args.Length >= 2:
        await SnapshotAsync(args[1], args[2..], shutdown.Token);
        break;
    case "fallback" when args.Length == 2:
        ArcanaFallback.OpenDefaultBrowser(args[1]);
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        Environment.ExitCode = 2;
        break;
}
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { Environment.ExitCode = 130; }
catch (Exception error) { Console.Error.WriteLine($"Arcana: {error.Message}"); Environment.ExitCode = 1; }

static void WatchParent(string pid, CancellationTokenSource shutdown)
{
    if (!int.TryParse(pid, out var id)) return;
    try
    {
        var parent = System.Diagnostics.Process.GetProcessById(id);
        _ = parent.WaitForExitAsync().ContinueWith(_ => shutdown.Cancel());
    }
    catch (ArgumentException) { shutdown.Cancel(); } // already gone
}

static async Task SnapshotAsync(string url, string[] args, CancellationToken cancellationToken)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));
    await using var runtime = new BrowserRuntime(new ArcanaOptions
    {
        ExecutablePath = GetOption(args, "executable"),
        AllowPrivateNetwork = args.Contains("--allow-private-network", StringComparer.OrdinalIgnoreCase)
    });
    await runtime.StartAsync(timeout.Token);
    await using var browser = await ArcanaBrowser.ConnectAsync(runtime, timeout.Token);
    var page = await browser.NewPageAsync(url, timeout.Token);
    var snapshot = await page.SnapshotAsync(timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}

static async Task RunAsync(string[] args, CancellationToken cancellationToken)
{
    var options = new ArcanaOptions
    {
        ExecutablePath = GetOption(args, "executable"),
        Port = int.TryParse(GetOption(args, "port"), out var port) ? port : 0,
        AllowPrivateNetwork = args.Contains("--allow-private-network", StringComparer.OrdinalIgnoreCase)
    };

    await using var runtime = new BrowserRuntime(options);
    await runtime.StartAsync(cancellationToken);
    Console.WriteLine($"Arcana engine running on {runtime.Endpoint} (PID {runtime.ProcessId}).");
    Console.WriteLine("Press Ctrl+C or Enter to stop and clean up the process tree.");
    await Console.In.ReadLineAsync(cancellationToken);
}

static async Task ApiAsync(string[] args, CancellationToken cancellationToken)
{
    var token = GetOption(args, "token") ?? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var options = new ArcanaOptions
    {
        ExecutablePath = GetOption(args, "executable"),
        AllowPrivateNetwork = args.Contains("--allow-private-network", StringComparer.OrdinalIgnoreCase)
    };
    await using var runtime = new BrowserRuntime(options);
    await runtime.StartAsync(cancellationToken);
    await using var browser = await ArcanaBrowser.ConnectAsync(runtime, cancellationToken);
    await using var api = new ArcanaApiServer(browser, token, int.TryParse(GetOption(args, "port"), out var port) ? port : 0);
    api.Start();
    var ready = new { apiVersion = ArcanaApiServer.Version, endpoint = api.Endpoint, token };
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ready));
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}

static void PrintDoctor()
{
    Console.WriteLine($"Arcana engine: {EngineLocator.Resolve()}");
    Console.WriteLine($"Parity baseline: Obscura {EngineLocator.Version}");
    Console.WriteLine("Default-browser fallback: Windows default handler, opened only by explicit caller request");
    Console.WriteLine("Default policy: loopback only, one worker, and private-network access disabled.");
}

static string? GetOption(string[] args, string name)
{
    var prefix = "--" + name + "=";
    var value = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return value?[prefix.Length..];
}

static void PrintHelp()
{
    Console.WriteLine("Arcana Browser runtime");
    Console.WriteLine("  fetch|scrape|serve|mcp [engine options] [--executable=PATH]");
    Console.WriteLine("  engine [all native engine arguments, including global flags]");
    Console.WriteLine("  engine --help     (complete engine CLI reference)");
    Console.WriteLine("  doctor");
    Console.WriteLine("  run [--executable=PATH] [--port=PORT] [--allow-private-network]");
    Console.WriteLine("  api [--executable=PATH] [--port=PORT] [--token=TOKEN] [--allow-private-network]");
    Console.WriteLine("  snapshot URL [--executable=PATH] [--allow-private-network]");
    Console.WriteLine("  fallback https://example.com");
}
