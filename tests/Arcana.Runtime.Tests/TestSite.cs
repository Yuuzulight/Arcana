using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Arcana.Runtime.Tests;

internal sealed class TestSite : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    public string Origin { get; }
    public string? LastPost { get; private set; }

    public TestSite()
    {
        _listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var request = await reader.ReadLineAsync(_lifetime.Token);
                if (request is null) return;
                var path = request.Split(' ')[1];
                var length = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_lifetime.Token)))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                if (length > 0)
                {
                    var body = new char[length];
                    await reader.ReadBlockAsync(body.AsMemory(), _lifetime.Token);
                    LastPost = new string(body);
                }
                var bodyText = path switch
                {
                    "/api" => "{\"message\":\"network works\"}",
                    "/robots.txt" => "User-agent: *\nAllow: /\n",
                    "/two" => "<html><head><title>Second page</title></head><body><h1>Second page</h1></body></html>",
                    "/submit" => "<html><head><title>Submitted</title></head><body>Submitted</body></html>",
                    _ => """
                        <!doctype html><html><head><title>Arcana fixture</title></head><body>
                        <h1>Arcana parity fixture</h1><p>Readable test content</p>
                        <a id="next" href="/two">Next page</a>
                        <form action="/submit" method="post"><label for="name">Name</label>
                        <input id="name" name="name"><input id="check" type="checkbox" name="check">
                        <select id="choice" name="choice"><option value="a">Alpha</option><option value="b">Beta</option></select>
                        <button id="submit">Submit</button></form>
                        <button id="click" type="button" onclick="document.getElementById('result').textContent='Clicked'">Click me</button>
                        <p id="result">Waiting</p><div style="height:1200px">Long content</div>
                        <script>console.log('fixture console');</script></body></html>
                        """
                };
                var bytes = path == "/binary" ? new byte[] { 0, 255, 128, 10, 13, 65, 0, 254 } : Encoding.UTF8.GetBytes(bodyText);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: {(path == "/api" ? "application/json" : "text/html")}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _lifetime.Token);
                await stream.WriteAsync(bytes, _lifetime.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        await _loop;
    }
}
