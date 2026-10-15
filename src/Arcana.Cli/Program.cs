using Arcana.Runtime;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return;
}

switch (args[0].ToLowerInvariant())
{
    case "doctor":
        PrintDoctor();
        break;
    case "run":
        await RunAsync(args[1..]);
        break;
    case "snapshot" when args.Length >= 2:
        await SnapshotAsync(args[1], args[2..]);
        break;
    case "fallback" when args.Length == 2:
        ArcanaFallback.OpenDefaultBrowser(args[1]);
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        Environment.ExitCode = 2;
        break;
}

static async Task SnapshotAsync(string url, string[] args)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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

static async Task RunAsync(string[] args)
{
    var options = new ArcanaOptions
    {
        ExecutablePath = GetOption(args, "executable"),
        Port = int.TryParse(GetOption(args, "port"), out var port) ? port : 0,
        AllowPrivateNetwork = args.Contains("--allow-private-network", StringComparer.OrdinalIgnoreCase)
    };

    await using var runtime = new BrowserRuntime(options);
    await runtime.StartAsync();
    Console.WriteLine($"Arcana engine running on {runtime.Endpoint} (PID {runtime.ProcessId}).");
    Console.WriteLine("Press Ctrl+C or Enter to stop and clean up the process tree.");
    Console.ReadLine();
}

static void PrintDoctor()
{
    Console.WriteLine("Arcana engine: arcana.exe, with the Obscura-compatible engine used during development");
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
    Console.WriteLine("  doctor");
    Console.WriteLine("  run [--executable=PATH] [--port=PORT] [--allow-private-network]");
    Console.WriteLine("  snapshot URL [--executable=PATH] [--allow-private-network]");
    Console.WriteLine("  fallback https://example.com");
}
