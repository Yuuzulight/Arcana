using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Arcana.Runtime;

public sealed class BrowserRuntime : IAsyncDisposable
{
    private readonly ArcanaOptions _options;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _process;
    private string? _ownedProfile;
    private bool _disposed;

    public BrowserRuntime(ArcanaOptions options)
    {
        options.Validate();
        _options = options;
    }

    public Uri? Endpoint { get; private set; }
    public int Port { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        Port = _options.Port == 0 ? GetFreePort() : _options.Port;
        var profile = PrepareProfile();
        var executable = ResolveExecutable(_options);
        var startInfo = BuildStartInfo(executable, profile, Port);
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("The browser process could not be started.");
        Endpoint = new Uri($"http://{_options.Host}:{Port}/");

        try
        {
            await WaitForEndpointAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string?> GetVersionJsonAsync(CancellationToken cancellationToken = default)
    {
        if (Endpoint is null)
            return null;
        try
        {
            return await _httpClient.GetStringAsync(new Uri(Endpoint, "json/version"), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the state check and Kill.
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        if (_ownedProfile is not null)
        {
            try
            {
                Directory.Delete(_ownedProfile, recursive: true);
            }
            catch (IOException)
            {
                // A browser child may still be releasing a file handle.
            }
            catch (UnauthorizedAccessException)
            {
                // Shutdown must not fail because a child-owned file is read-only.
            }
        }

        _httpClient.Dispose();
        await ValueTask.CompletedTask;
    }

    private async Task WaitForEndpointAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                if (_process is { HasExited: true } process)
                    throw new InvalidOperationException($"Browser process exited during startup with code {process.ExitCode}.");
                if (await CanConnectAsync(timeout.Token).ConfigureAwait(false))
                    return;
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Arcana backend did not open CDP endpoint {Endpoint}.");
        }
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        if (Endpoint is null)
            return false;
        try
        {
            using var response = await _httpClient.GetAsync(new Uri(Endpoint, "json/version"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private ProcessStartInfo BuildStartInfo(string executable, string profile, int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--workers");
        startInfo.ArgumentList.Add(_options.WorkerCount.ToString());
        if (!string.IsNullOrWhiteSpace(profile))
        {
            startInfo.ArgumentList.Add("--storage-dir");
            startInfo.ArgumentList.Add(profile);
        }
        if (_options.AllowPrivateNetwork)
            startInfo.ArgumentList.Add("--allow-private-network");
        return startInfo;
    }

    private string PrepareProfile()
    {
        if (_options.ProfileDirectory is not null)
        {
            Directory.CreateDirectory(_options.ProfileDirectory);
            return _options.ProfileDirectory;
        }

        _ownedProfile = Path.Combine(Path.GetTempPath(), "mana-arcana", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_ownedProfile);
        return _ownedProfile;
    }

    private static string ResolveExecutable(ArcanaOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            if (!File.Exists(options.ExecutablePath))
                throw new FileNotFoundException("Arcana backend executable was not found.", options.ExecutablePath);
            return options.ExecutablePath;
        }

        var candidates = new[] { "arcana.exe", "arcana", "obscura.exe", "obscura" };

        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                return candidate;
            if (!Path.IsPathFullyQualified(candidate))
                return candidate;
        }
        throw new FileNotFoundException("No Arcana engine executable was found. Supply --executable or install Arcana.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
