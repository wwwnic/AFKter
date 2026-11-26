using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class MatchStatusTests
{
    [Test]
    public void ReadyCheckFollowedByChampSelectConfirmsTheMatchStarted()
    {
        var tracker = Track(GameflowPhase.Matchmaking, GameflowPhase.ReadyCheck, GameflowPhase.ChampSelect);
        Assert.That(tracker.Current, Is.EqualTo(new MatchStatus(GameflowPhase.ChampSelect, MatchOutcome.Started)));
        Assert.That(tracker.Current.IsInMatch, Is.True);
        Assert.That(tracker.Current.NeedsAttention, Is.False);
        Assert.That(tracker.Current.Describe(), Is.EqualTo("Match accepted — in champ select"));
        tracker.Update(GameflowPhase.InProgress);
        Assert.That(tracker.Current.Outcome, Is.EqualTo(MatchOutcome.None), "The confirmation belongs to champ select only.");
        Assert.That(tracker.Current.Describe(), Is.EqualTo("In game"));
    }

    [Test]
    public void ReadyCheckBackToMatchmakingMeansSomeoneElseDidNotAccept()
    {
        var tracker = Track(GameflowPhase.Matchmaking, GameflowPhase.ReadyCheck, GameflowPhase.Matchmaking);
        Assert.That(tracker.Current.Outcome, Is.EqualTo(MatchOutcome.Requeued));
        Assert.That(tracker.Current.NeedsAttention, Is.False, "League keeps queueing; AFKtor keeps working.");
        Assert.That(tracker.Current.Describe(), Is.EqualTo("Match didn't start — back in queue"));
        tracker.Update(GameflowPhase.ReadyCheck);
        Assert.That(tracker.Current.Describe(), Is.EqualTo("Match found"));
    }

    [TestCase(GameflowPhase.Lobby)]
    [TestCase(GameflowPhase.None)]
    public void ReadyCheckThatRemovesThePlayerFromQueueNeedsAttention(GameflowPhase next)
    {
        var tracker = Track(GameflowPhase.ReadyCheck, next);
        Assert.That(tracker.Current.Outcome, Is.EqualTo(MatchOutcome.LeftQueue));
        Assert.That(tracker.Current.NeedsAttention, Is.True);
        Assert.That(tracker.Current.Describe(), Is.EqualTo("Match missed — out of queue"));
    }

    [TestCase(GameflowPhase.Matchmaking, "Champ select ended — back in queue")]
    [TestCase(GameflowPhase.Lobby, "Champ select ended — out of queue")]
    public void ChampSelectEndingWithoutAGameIsReported(GameflowPhase next, string text)
    {
        var tracker = Track(GameflowPhase.ReadyCheck, GameflowPhase.ChampSelect, next);
        Assert.That(tracker.Current.Outcome, Is.EqualTo(MatchOutcome.ChampSelectEnded));
        Assert.That(tracker.Current.Describe(), Is.EqualTo(text));
    }

    [Test]
    public void UnknownPhasesAndRepeatsDoNotEraseTheOutcome()
    {
        var tracker = Track(GameflowPhase.ReadyCheck, GameflowPhase.Lobby, GameflowPhase.Unknown, GameflowPhase.Lobby);
        Assert.That(tracker.Current, Is.EqualTo(new MatchStatus(GameflowPhase.Lobby, MatchOutcome.LeftQueue)));
    }

    [Test]
    public void ResetForgetsThePhase()
    {
        var tracker = Track(GameflowPhase.ReadyCheck, GameflowPhase.ChampSelect);
        tracker.Reset();
        Assert.That(tracker.Current, Is.EqualTo(MatchStatus.Unknown));
        Assert.That(tracker.Current.Describe(), Is.Null);
        tracker.Update(GameflowPhase.ChampSelect);
        Assert.That(tracker.Current.Outcome, Is.EqualTo(MatchOutcome.None), "A reconnect cannot prove a ready check was accepted.");
    }

    [Test]
    public void EveryKnownPhaseHasTrayText()
    {
        foreach (var phase in Enum.GetValues<GameflowPhase>().Where(p => p != GameflowPhase.Unknown))
        {
            Assert.That(new MatchStatus(phase, MatchOutcome.None).Describe(), Is.Not.Null.And.Not.Empty, phase.ToString());
        }
    }

    [TestCase("\"ChampSelect\"", GameflowPhase.ChampSelect)]
    [TestCase("\"None\"", GameflowPhase.None)]
    [TestCase("\"champselect\"", GameflowPhase.Unknown)]
    [TestCase("\"5\"", GameflowPhase.Unknown)]
    [TestCase("\"Lobby, ChampSelect\"", GameflowPhase.Unknown)]
    [TestCase("\"SomethingNew\"", GameflowPhase.Unknown)]
    [TestCase("5", GameflowPhase.Unknown)]
    [TestCase("null", GameflowPhase.Unknown)]
    [TestCase("not json", GameflowPhase.Unknown)]
    public void PhaseParsingTrustsOnlyExactNames(string json, GameflowPhase expected)
        => Assert.That(GameflowPhases.Parse(json), Is.EqualTo(expected));

    [Test]
    public void PhaseEventsDecodeWithoutThrowing()
    {
        Assert.That(LcuReadyCheckClient.TryDecodePhaseEvent(PhaseEvent("\"InProgress\""), out var phase), Is.True);
        Assert.That(phase, Is.EqualTo(GameflowPhase.InProgress));
        Assert.That(LcuReadyCheckClient.TryDecodePhaseEvent(PhaseEvent("{\"bad\":1}"), out phase), Is.True);
        Assert.That(phase, Is.EqualTo(GameflowPhase.Unknown));
        Assert.That(LcuReadyCheckClient.TryDecodePhaseEvent(
            "[8,\"OnJsonApiEvent_lol-gameflow_v1_gameflow-phase\",{\"uri\":\"/lol-gameflow/v1/gameflow-phase\",\"eventType\":3,\"data\":\"Lobby\"}]", out phase), Is.True);
        Assert.That(phase, Is.EqualTo(GameflowPhase.Unknown));
        Assert.That(LcuReadyCheckClient.TryDecodePhaseEvent(ReadyCheckEvent, out _), Is.False);
        Assert.That(LcuReadyCheckClient.TryDecodePhaseEvent("not json", out _), Is.False);
    }

    [Test]
    public async Task StreamSubscribesToPhasesAndSeedsThemOnlyWhenAsked()
    {
        using var socket = new ScriptedSocket();
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(path => path == "/lol-gameflow/v1/gameflow-phase"
            ? Json("\"Matchmaking\"") : Json("{\"state\":\"Invalid\",\"playerResponse\":\"None\",\"timer\":0}")));
        var phases = Channel.CreateUnbounded<GameflowPhase>();
        var running = Api(http, socket).RunAsync(_ => { }, phase => phases.Writer.TryWrite(phase), stopping.Token);
        Assert.That(await phases.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(GameflowPhase.Matchmaking));
        socket.Emit(PhaseEvent("\"ReadyCheck\""));
        Assert.That(await phases.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(GameflowPhase.ReadyCheck));
        Assert.That(socket.Sent, Is.EqualTo(new[]
        {
            "[5,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\"]",
            "[5,\"OnJsonApiEvent_lol-gameflow_v1_gameflow-phase\"]"
        }));
        stopping.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task PhaseFailuresNeverEndTheReadyCheckSession()
    {
        using var socket = new ScriptedSocket();
        using var stopping = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(path => path == "/lol-gameflow/v1/gameflow-phase"
            ? throw new HttpRequestException("phase unavailable")
            : Json("{\"state\":\"Invalid\",\"playerResponse\":\"None\",\"timer\":0}")));
        var updates = Channel.CreateUnbounded<ReadyCheckUpdate>();
        var phases = new List<GameflowPhase>();
        var running = Api(http, socket).RunAsync(update => updates.Writer.TryWrite(update), phases.Add, stopping.Token);
        Assert.That((await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).IsInitial, Is.True);
        socket.Emit(PhaseEvent("{\"unexpected\":true}"));
        socket.Emit(PhaseEvent("\"ANewPhase\""));
        socket.Emit(ReadyCheckEvent);
        var update = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(update.Snapshot.IsPending, Is.True);
        Assert.That(running.IsCompleted, Is.False);
        Assert.That(phases, Is.Empty, "Unreadable phases are dropped, not guessed.");
        stopping.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task MonitorFeedsTheTrackerAndResetsItOnDisconnect()
    {
        var clock = new ManualClock();
        await using var controller = new ReadyCheckController(clock.DelayAsync, () => clock.Now);
        var tracker = new MatchStatusTracker();
        var first = new PhaseClient(); var second = new PhaseClient(); var created = 0;
        var connection = LcuConnection.Parse("LeagueClient:1:1001:one:https");
        await using var monitor = new ReadyCheckSessionMonitor(_ => Task.FromResult(connection),
            _ => ++created == 1 ? first : second, controller, match: tracker);
        await monitor.RefreshAsync(); await first.Started.Task;
        first.Phase(GameflowPhase.ReadyCheck);
        first.Phase(GameflowPhase.ChampSelect);
        Assert.That(tracker.Current.Outcome, Is.EqualTo(MatchOutcome.Started));

        first.Failure.SetException(new IOException("stream closed"));
        await monitor.SessionCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(tracker.Current, Is.EqualTo(MatchStatus.Unknown));

        connection = LcuConnection.Parse("LeagueClient:2:1002:two:https");
        await monitor.RefreshAsync(); await second.Started.Task;
        first.Phase(GameflowPhase.InProgress);
        Assert.That(tracker.Current, Is.EqualTo(MatchStatus.Unknown), "An old session's phases are ignored.");
        second.Phase(GameflowPhase.Lobby);
        Assert.That(tracker.Current.Phase, Is.EqualTo(GameflowPhase.Lobby));
    }

    private static MatchStatusTracker Track(params GameflowPhase[] phases)
    {
        var tracker = new MatchStatusTracker();
        foreach (var phase in phases) { tracker.Update(phase); }
        return tracker;
    }

    private const string ReadyCheckEvent = "[8,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\",{\"uri\":\"/lol-matchmaking/v1/ready-check\",\"eventType\":\"Create\",\"data\":{\"state\":\"InProgress\",\"playerResponse\":\"None\",\"timer\":0}}]";
    private static string PhaseEvent(string data)
        => $"[8,\"OnJsonApiEvent_lol-gameflow_v1_gameflow-phase\",{{\"uri\":\"/lol-gameflow/v1/gameflow-phase\",\"eventType\":\"Update\",\"data\":{data}}}]";
    private static LcuReadyCheckClient Api(HttpClient http, WebSocket socket)
        => new(LcuConnection.Parse("LeagueClient:123:54321:secret:https")!, http, _ => Task.FromResult(socket));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<string, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(send(request.RequestUri!.AbsolutePath));
    }

    private sealed class PhaseClient : IReadyCheckClient
    {
        private Action<GameflowPhase>? receivePhase;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Phase(GameflowPhase phase) => receivePhase!(phase);
        public Task<ReadyCheckSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(ReadyCheckSnapshot.Inactive);
        public Task AcceptAsync(CancellationToken token) => Task.CompletedTask;
        public Task RunAsync(Action<ReadyCheckUpdate> receive, CancellationToken token)
            => throw new AssertionException("The monitor must ask for phases when it has a tracker.");
        public async Task RunAsync(Action<ReadyCheckUpdate> receive, Action<GameflowPhase> onPhase, CancellationToken token)
        {
            receivePhase = onPhase;
            receive(new(ReadyCheckSnapshot.Inactive, IsInitial: true));
            Started.TrySetResult();
            await Failure.Task.WaitAsync(token);
        }
    }

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<string?> messages = Channel.CreateUnbounded<string?>();
        public List<string> Sent { get; } = [];
        public void Emit(string json) => messages.Writer.TryWrite(json);
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
            lock (Sent) { Sent.Add(Encoding.UTF8.GetString(buffer)); }
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
