using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class LcuLoopbackIntegrationTests
{
    [Test]
    public async Task DefaultSocketAndHttpShareAuthenticatedLoopbackTlsTransport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var key = RSA.Create(2048);
        var certRequest = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var subscription = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeAsync();
        using var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri!.Host == "127.0.0.1"
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var api = new LcuReadyCheckClient(LcuConnection.Parse($"LeagueClient:1:{port}:secret:https")!, http);
        var running = api.RunAsync(update => { if (update.Snapshot.IsPending) { pending.TrySetResult(); } }, stop.Token);
        try
        {
            await pending.Task.WaitAsync(timeout.Token);
            Assert.That(await subscription.Task, Is.EqualTo("[5,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\"]"));
            Assert.That((await api.ReadAsync(timeout.Token)).IsPending, Is.True);
            await api.AcceptAsync(timeout.Token);
            await accepted.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            await server;
        }

        async Task ServeAsync()
        {
            var requests = new List<Task>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                    requests.Add(HandleAsync(tcp));
                }
            }
            catch (OperationCanceledException) { }
            finally { await Task.WhenAll(requests); }
        }

        async Task HandleAsync(TcpClient tcp)
        {
            using (tcp)
            using (var tls = new SslStream(tcp.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
                    var requestBytes = new List<byte>();
                    var one = new byte[1];
                    while (true)
                    {
                        if (await tls.ReadAsync(one, stop.Token) == 0) { return; }
                        requestBytes.Add(one[0]);
                        if (requestBytes.Count >= 4 && Encoding.ASCII.GetString(requestBytes.TakeLast(4).ToArray()) == "\r\n\r\n") { break; }
                    }
                    string request = Encoding.ASCII.GetString(requestBytes.ToArray());
                    Assert.That(request, Does.Contain("Authorization: Basic cmlvdDpzZWNyZXQ="));
                    if (request.StartsWith("GET / HTTP/1.1", StringComparison.Ordinal))
                    {
                        string wsKey = request.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                        string acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n"), stop.Token);
                        using var ws = WebSocket.CreateFromStream(tls, true, null, TimeSpan.FromSeconds(1));
                        var buffer = new byte[4096];
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                        subscription.SetResult(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        var message = Encoding.UTF8.GetBytes("[8,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\",{\"uri\":\"/lol-matchmaking/v1/ready-check\",\"eventType\":\"Create\",\"data\":{\"state\":\"InProgress\",\"playerResponse\":\"None\",\"timer\":0}}]");
                        await ws.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, stop.Token);
                        await Task.Delay(Timeout.Infinite, stop.Token);
                    }
                    else
                    {
                        bool isAccept = request.StartsWith("POST /lol-matchmaking/v1/ready-check/accept HTTP/1.1", StringComparison.Ordinal);
                        Assert.That(isAccept || request.StartsWith("GET /lol-matchmaking/v1/ready-check HTTP/1.1", StringComparison.Ordinal), Is.True);
                        string body = isAccept ? "{}" : "{\"state\":\"InProgress\",\"playerResponse\":\"None\",\"timer\":3}";
                        await tls.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"), stop.Token);
                        if (isAccept) { accepted.TrySetResult(); }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }
}
