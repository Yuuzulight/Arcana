namespace Arcana.Runtime;

public sealed record ArcanaOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ProfileDirectory { get; init; }
    public bool AllowPrivateNetwork { get; init; }
    public int WorkerCount { get; init; } = 1;
    public int MaxConnections { get; init; } = 16;
    public string? Proxy { get; init; }
    public string? UserAgent { get; init; }
    public bool Stealth { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host is not ("127.0.0.1" or "localhost"))
            throw new ArgumentException("Arcana must bind to loopback unless a future explicit host policy is added.", nameof(Host));
        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port));
        if (WorkerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(WorkerCount));
        if (MaxConnections < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (Proxy is not null && (!Uri.TryCreate(Proxy, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5" or "socks5h")))
            throw new ArgumentException("Proxy must be an HTTP(S) or SOCKS5 URL.", nameof(Proxy));
    }
}
