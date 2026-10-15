using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class CdpConnectionTests
{
    [TestMethod]
    public async Task OutOfOrderResponsesEventsCancellationAndPeerFailureAreRouted()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var accept = AcceptWebSocketAsync(server, ct);
        await using var connection = await CdpConnection.ConnectAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}"), ct);
        using var peer = await accept;
        using var events = connection.Subscribe("test.event", "session");
        var first = connection.SendAsync("first", cancellationToken: ct);
        var second = connection.SendAsync("second", cancellationToken: ct);
        var a = await ReadAsync(peer, ct);
        var b = await ReadAsync(peer, ct);
        await SendAsync(peer, new { method = "test.event", sessionId = "session", @params = new { message = "kept" } }, ct);
        await SendAsync(peer, new { id = b.GetProperty("id").GetInt32(), result = new { value = "second" } }, ct);
        await SendAsync(peer, new { id = a.GetProperty("id").GetInt32(), result = new { value = "first" } }, ct);
        Assert.AreEqual("first", (await first).GetProperty("value").GetString());
        Assert.AreEqual("second", (await second).GetProperty("value").GetString());
        Assert.AreEqual("kept", (await events.ReadAsync(ct)).Parameters.GetProperty("message").GetString());

        using var cancelled = new CancellationTokenSource();
        var pending = connection.SendAsync("cancel", cancellationToken: cancelled.Token);
        var cancelledRequest = await ReadAsync(peer, ct);
        cancelled.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await pending);
        await SendAsync(peer, new { id = cancelledRequest.GetProperty("id").GetInt32(), result = new { } }, ct);
        var next = connection.SendAsync("after-cancel", cancellationToken: ct);
        var nextRequest = await ReadAsync(peer, ct);
        await SendAsync(peer, new { id = nextRequest.GetProperty("id").GetInt32(), result = new { alive = true } }, ct);
        Assert.IsTrue((await next).GetProperty("alive").GetBoolean());

        var failed = connection.SendAsync("invalid", cancellationToken: ct);
        var invalid = await ReadAsync(peer, ct);
        await SendAsync(peer, new { id = invalid.GetProperty("id").GetInt32(), error = new { code = -32601, message = "unknown" } }, ct);
        await Assert.ThrowsExceptionAsync<CdpException>(async () => await failed);
        var interrupted = connection.SendAsync("peer-closed", cancellationToken: ct);
        await ReadAsync(peer, ct);
        await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        await Assert.ThrowsExceptionAsync<IOException>(async () => await interrupted);
    }

    [TestMethod]
    public async Task EventOverflowFailsSubscriberWithoutSilentlyDroppingEvents()
    {
        using var subscription = new CdpSubscription(null, null, 1, _ => { });
        subscription.Publish(new CdpEvent("one", default, null));
        subscription.Publish(new CdpEvent("two", default, null));
        Assert.AreEqual("one", (await subscription.ReadAsync()).Method);
        await Assert.ThrowsExceptionAsync<System.Threading.Channels.ChannelClosedException>(async () => await subscription.ReadAsync());
    }

    private static async Task<WebSocket> AcceptWebSocketAsync(TcpListener listener, CancellationToken ct)
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        string? key = null;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line[18..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        return WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
    }

    private static Task SendAsync(WebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonElement> ReadAsync(WebSocket socket, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var bytes = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, ct);
            message.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(message.ToArray());
        return json.RootElement.Clone();
    }
}
