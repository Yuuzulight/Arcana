using System.Diagnostics;

namespace Arcana.Runtime;

/// <summary>Runs the bundled engine's complete CLI with byte-for-byte stdio relay.</summary>
public static class EngineCommand
{
    public static async Task<int> RunAsync(IEnumerable<string> arguments, string? executable = null, CancellationToken cancellationToken = default)
    {
        var args = arguments.ToArray();
        var profile = args.Any(a => a == "--storage-dir" || a.StartsWith("--storage-dir=", StringComparison.Ordinal))
            ? null : Path.Combine(Path.GetTempPath(), "mana-arcana", Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(EngineLocator.Resolve(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (profile is not null)
        {
            Directory.CreateDirectory(profile);
            start.ArgumentList.Add("--storage-dir");
            start.ArgumentList.Add(profile);
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process? process = null;
        using var ioLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            process = Process.Start(start) ?? throw new IOException("Arcana engine could not start.");
            ProcessJob.Add(process);
            var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput(), cancellationToken);
            var errors = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError(), cancellationToken);
            // Input can remain open after a finite command exits. Do not wait on the host's stdin in that case.
            _ = RelayInputAsync(process, ioLifetime.Token);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            ioLifetime.Cancel();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally { process.Dispose(); }
            }
            if (profile is not null)
            {
                try { Directory.Delete(profile, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task RelayInputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }
}
