using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class LcuReadyCheckStreamTests
{
    [Test]
    public async Task StreamClosureInterruptsInitialHttpRead()
    {
        using var socket = new ScriptedSocket();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async token =>
        {
            reading.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Response("Invalid", 0);
        }));
        var api = Api(http, socket);
        var running = api.RunAsync(_ => Assert.Fail("No update expected"), default);
        await reading.Task;
        socket.CloseStream();
        Assert.ThrowsAsync<IOException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(socket.Subscription, Is.EqualTo("[5,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\"]"));
    }

    [Test]
    public async Task EventsWinOverAnOlderSeedResponse()
    {
        using var socket = new ScriptedSocket();
        using var stopping = new CancellationTokenSource();
        var seed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(_ => seed.Task));
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<ReadyCheckUpdate>();
        var running = Api(http, socket).RunAsync(update => { updates.Add(update); seen.TrySetResult(); }, stopping.Token);
        socket.Emit("[8,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\",{\"uri\":\"/lol-matchmaking/v1/ready-check\",\"eventType\":\"Update\",\"data\":{\"state\":\"InProgress\",\"playerResponse\":\"Declined\",\"timer\":2}}]");
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        seed.SetResult(Response("InProgress", 1));
        socket.CloseStream();
        Assert.ThrowsAsync<IOException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(updates.Select(u => u.Snapshot.PlayerResponse), Is.EqualTo(new[] { "Declined" }));
    }

    [Test]
    public async Task CancellationStopsAnIdleStream()
    {
        using var socket = new ScriptedSocket();
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response("Invalid", 0))));
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Api(http, socket).RunAsync(_ => seen.SetResult(), stopping.Token);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stopping.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static LcuReadyCheckClient Api(HttpClient http, WebSocket socket)
        => new(LcuConnection.Parse("LeagueClient:123:54321:secret:https")!, http, _ => Task.FromResult(socket));
    private static HttpResponseMessage Response(string state, double timer) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { state, playerResponse = "None", timer }))
    };
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(token);
    }
    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<string?> messages = Channel.CreateUnbounded<string?>();
        public string? Subscription { get; private set; }
        public void Emit(string json) => messages.Writer.TryWrite(json);
        public void CloseStream() => messages.Writer.TryWrite(null);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            Subscription = Encoding.UTF8.GetString(buffer);
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var json = await messages.Reader.ReadAsync(token);
            if (json is null) { return new(0, WebSocketMessageType.Close, true); }
            var bytes = Encoding.UTF8.GetBytes(json);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
    }
}
