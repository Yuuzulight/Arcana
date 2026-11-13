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
                    "/spa-api" => "{\"items\":[\"Item Alpha\",\"Item Beta\",\"Item Gamma\"],\"total\":3}",
                    "/robots.txt" => "User-agent: *\nAllow: /\n",
                    "/two" => "<html><head><title>Second page</title></head><body><h1>Second page</h1></body></html>",
                    "/submit" => "<html><head><title>Submitted</title></head><body>Submitted</body></html>",
                    "/table" => """
                        <!doctype html><html><head><title>Structured Table Reference</title></head><body>
                        <h1>Article with Structured Data</h1>
                        <table class="infobox"><tr><th>Key</th><td>Value</td></tr></table>
                        <table class="wikitable">
                        <thead><tr><th>Header 1</th><th>Header 2</th><th>Header 3</th></tr></thead>
                        <tbody>
                        <tr><td>Row 1 Col 1</td><td>Row 1 Col 2</td><td>Row 1 Col 3</td></tr>
                        <tr><td>Row 2 Col 1</td><td>Row 2 Col 2</td><td>Row 2 Col 3</td></tr>
                        </tbody>
                        </table>
                        </body></html>
                        """,
                    "/spa" => """
                        <!doctype html><html><head><title>Single Page Application</title></head><body>
                        <h1>SPA Interactive Matrix</h1>
                        <nav><a id="tab-home" href="#home">Home</a> | <a id="tab-data" href="#data">Data</a> | <a id="tab-settings" href="#settings">Settings</a></nav>
                        <div id="content">Welcome to SPA</div>
                        <table id="spa-table"><thead><tr><th>Metric</th><th>Score</th></tr></thead><tbody><tr><td>Speed</td><td>99</td></tr><tr><td>Reliability</td><td>100</td></tr></tbody></table>
                        <form id="spa-form" action="/submit" method="post">
                          <input id="spa-input" name="item" value="initial-value">
                          <select id="spa-select" name="category"><option value="cat1">Category 1</option><option value="cat2">Category 2</option></select>
                          <button id="spa-btn" type="submit">Submit Item</button>
                        </form>
                        <iframe id="spa-frame" src="/two" title="Nested Frame"></iframe>
                        <div id="dynamic-container">Initial items</div>
                        <script>
                          window.addEventListener('hashchange', () => {
                            document.getElementById('content').textContent = 'Viewing: ' + location.hash;
                          });
                          sessionStorage.setItem('spa_session', 'active');
                          localStorage.setItem('spa_local', 'persisted');
                        </script>
                        </body></html>
                        """,
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
