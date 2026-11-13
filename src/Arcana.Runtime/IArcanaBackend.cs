using System.Text.Json;

namespace Arcana.Runtime;

/// <summary>Browser operations required by the versioned API transport.</summary>
public interface IArcanaBrowserBackend
{
    Task<JsonElement> GetVersionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BrowserTab>> GetTabsAsync(CancellationToken cancellationToken = default);
    Task<IArcanaPageBackend> NewPageAsync(string? url = null, CancellationToken cancellationToken = default);
}

public interface IArcanaEventSubscription : IDisposable
{
    IAsyncEnumerable<CdpEvent> ReadAllAsync(CancellationToken cancellationToken = default);
    ValueTask<CdpEvent> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Page operations exposed by the versioned API transport.</summary>
public interface IArcanaPageBackend
{
    Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken = default);
    Task<PageSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetCookiesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> SetCookiesAsync(object[] cookies, CancellationToken cancellationToken = default);
    Task<PageStorageState> ExportStorageAsync(CancellationToken cancellationToken = default);
    Task RestoreStorageAsync(PageStorageState state, CancellationToken cancellationToken = default);
    Task<byte[]> CaptureScreenshotAsync(string format, int? quality = null, CancellationToken cancellationToken = default);
    Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default);
    Task<JsonElement> SendCdpAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    IArcanaEventSubscription Subscribe(string? method = null, int capacity = 256);
    Task<JsonElement> CloseAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ReloadAsync(CancellationToken cancellationToken = default);
    Task<bool> GoBackAsync(CancellationToken cancellationToken = default);
    Task<bool> GoForwardAsync(CancellationToken cancellationToken = default);
    Task WaitForNetworkIdleAsync(int timeoutMs, CancellationToken cancellationToken = default);
    Task<JsonElement> SetViewportAsync(int width, int height, CancellationToken cancellationToken = default);
    Task ClickAtAsync(double x, double y, CancellationToken cancellationToken = default);
    Task PressKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<JsonElement> TypeTextAsync(string text, CancellationToken cancellationToken = default);
    Task<JsonElement> ScrollAsync(double x, double y, CancellationToken cancellationToken = default);
    Task UploadFileAsync(string reference, string path, CancellationToken cancellationToken = default);
    Task ClickAsync(string reference, CancellationToken cancellationToken = default);
    Task FillAsync(string reference, string value, CancellationToken cancellationToken = default);
    Task SelectOptionAsync(string reference, string value, CancellationToken cancellationToken = default);
}
