namespace Arcana.Runtime;

public sealed record ArcanaOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ProfileDirectory { get; init; }
    public bool AllowPrivateNetwork { get; init; }
    public int RendererProcessLimit { get; init; } = 1;
    public int WorkerCount { get; init; } = 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host is not ("127.0.0.1" or "localhost"))
            throw new ArgumentException("Arcana must bind to loopback unless a future explicit host policy is added.", nameof(Host));
        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port));
        if (RendererProcessLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(RendererProcessLimit));
        if (WorkerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(WorkerCount));
    }
}
