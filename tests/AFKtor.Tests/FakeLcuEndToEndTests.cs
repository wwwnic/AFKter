using AFKtor.Core;
using AFKtor.FakeLcu;
using NUnit.Framework;

namespace AFKtor.Tests;

/// <summary>
/// End-to-end runs of the real Core pipeline (session monitor, controller, LCU client and match
/// tracker) against <see cref="FakeLcuServer"/> over loopback TLS and WebSockets. Only League and
/// the tray UI are replaced; discovery reads the fake's lockfile contents.
/// </summary>
[NonParallelizable]
public sealed class FakeLcuEndToEndTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    /// <summary>How long to watch for an accept that must not happen.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    [Test]
    public async Task AcceptsAFreshReadyCheckAndReportsTheMatchStarted()
    {
        await using var run = await Run.StartAsync(armed: true);
        await run.Server.SetPhaseAsync("Matchmaking");
        await run.WaitForPhaseAsync(GameflowPhase.Matchmaking);

        await run.Server.StartReadyCheckAsync();
        await run.Server.WaitForAcceptsAsync(1, Timeout);
        Assert.That(run.Server.ReadyCheckResource!["playerResponse"]!.GetValue<string>(), Is.EqualTo("Accepted"));

        await run.Server.EveryoneReadyAsync();
        await run.WaitForMatchAsync(new MatchStatus(GameflowPhase.ChampSelect, MatchOutcome.Started));
        await Task.Delay(Quiet);
        Assert.That(run.Server.AcceptCount, Is.EqualTo(1));
        Assert.That(run.Server.UnauthorizedCount, Is.Zero);
    }

    [Test]
    public async Task WaitsForTheConfiguredDelayBeforeAccepting()
    {
        await using var run = await Run.StartAsync(armed: true, delaySeconds: 1);
        await run.Server.SetPhaseAsync("Matchmaking");

        // With no head start, League's whole-second timer can still read 0 when Task.Delay
        // wakes a few milliseconds early, and the controller then refuses the check. Real
        // checks reach AFKtor after they started, so give this one a realistic 200 ms lead.
        await run.Server.StartReadyCheckAsync(startedEarlier: TimeSpan.FromMilliseconds(200));
        await FakeLcuServer.WaitUntilAsync(() => run.Controller.Remaining is not null, Timeout);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.That(run.Server.AcceptCount, Is.Zero, "accepted before the countdown ended");

        await run.Server.WaitForAcceptsAsync(1, Timeout);
        Assert.That(run.Server.Requests, Does.Contain("GET " + FakeLcuServer.ReadyCheckRoute),
            "the check is re-read before accepting");
    }

    [Test]
    public async Task RequeuesAfterAnotherPlayerDeclinesAndAcceptsTheNextCheck()
    {
        await using var run = await Run.StartAsync(armed: true);
        await run.Server.SetPhaseAsync("Matchmaking");

        await run.Server.StartReadyCheckAsync();
        await run.Server.WaitForAcceptsAsync(1, Timeout);
        await run.Server.DeclinedByOtherPlayerAsync();
        await run.WaitForMatchAsync(new MatchStatus(GameflowPhase.Matchmaking, MatchOutcome.Requeued));

        await run.Server.StartReadyCheckAsync();
        await run.Server.WaitForAcceptsAsync(2, Timeout);
        await run.Server.EveryoneReadyAsync();
        await run.WaitForMatchAsync(new MatchStatus(GameflowPhase.ChampSelect, MatchOutcome.Started));
        Assert.That(run.Server.AcceptCount, Is.EqualTo(2));
    }

    [Test]
    public async Task LeavesACheckAloneWhenDisarmedAndReportsItMissed()
    {
        await using var run = await Run.StartAsync(armed: false);
        await run.Server.SetPhaseAsync("Matchmaking");

        await run.Server.StartReadyCheckAsync();
        await run.WaitForPhaseAsync(GameflowPhase.ReadyCheck);
        await Task.Delay(Quiet);
        await run.Server.ExpireAsync();

        await run.WaitForMatchAsync(new MatchStatus(GameflowPhase.Lobby, MatchOutcome.LeftQueue));
        Assert.That(run.Match.Current.NeedsAttention, Is.True);
        Assert.That(run.Server.AcceptCount, Is.Zero);
    }

    [Test]
    public async Task DisarmingDuringTheCountdownSendsNothing()
    {
        await using var run = await Run.StartAsync(armed: true, delaySeconds: 1);
        await run.Server.SetPhaseAsync("Matchmaking");

        await run.Server.StartReadyCheckAsync();
        await FakeLcuServer.WaitUntilAsync(() => run.Controller.Remaining is not null, Timeout);
        run.Controller.SetArmed(false);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.That(run.Server.AcceptCount, Is.Zero);
    }

    [Test]
    public async Task AcceptingInLeagueDuringTheCountdownSendsNothing()
    {
        await using var run = await Run.StartAsync(armed: true, delaySeconds: 1);
        await run.Server.SetPhaseAsync("Matchmaking");

        await run.Server.StartReadyCheckAsync();
        await FakeLcuServer.WaitUntilAsync(() => run.Controller.Remaining is not null, Timeout);
        await run.Server.AnswerInClientAsync(accept: true);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.That(run.Server.AcceptCount, Is.Zero);
    }

    [Test]
    public async Task DoesNotAcceptACheckThatWasAlreadyShowingWhenItConnects()
    {
        await using var run = await Run.StartAsync(armed: true, connect: false);
        await run.Server.SetPhaseAsync("Matchmaking");
        await run.Server.StartReadyCheckAsync();

        await run.ConnectAsync();
        await Task.Delay(Quiet);
        Assert.That(run.Server.AcceptCount, Is.Zero);

        // The next check in the same session is handled normally.
        await run.Server.DeclinedByOtherPlayerAsync();
        await run.Server.StartReadyCheckAsync();
        await run.Server.WaitForAcceptsAsync(1, Timeout);
    }

    [Test]
    public async Task ReconnectsAfterLeagueRestartsWithANewPortAndPassword()
    {
        await using var run = await Run.StartAsync(armed: true);
        var oldPassword = run.Server.Password;

        await run.Server.RestartAsync();
        await run.Monitor.RefreshAsync();
        Assert.That(run.Server.Password, Is.Not.EqualTo(oldPassword));
        await run.WaitForConnectedAsync();

        await run.Server.SetPhaseAsync("Matchmaking");
        await run.Server.StartReadyCheckAsync();
        await run.Server.WaitForAcceptsAsync(1, Timeout);
        Assert.That(run.Server.UnauthorizedCount, Is.Zero);
    }

    [Test]
    public async Task ShowsDisconnectedWhileLeagueIsClosed()
    {
        await using var run = await Run.StartAsync(armed: true);

        await run.Server.StopAsync();
        await FakeLcuServer.WaitUntilAsync(() => !run.Monitor.IsConnected, Timeout);
        await run.Monitor.RefreshAsync();
        Assert.That(run.Monitor.IsConnected, Is.False);
        Assert.That(run.Match.Current, Is.EqualTo(MatchStatus.Unknown));
    }

    [Test]
    public async Task RecoversWhenTheEventStreamDrops()
    {
        await using var run = await Run.StartAsync(armed: true);

        run.Server.DropEventStreams();
        await FakeLcuServer.WaitUntilAsync(() => !run.Monitor.IsConnected, Timeout);
        await run.ConnectAsync();

        await run.Server.SetPhaseAsync("Matchmaking");
        await run.Server.StartReadyCheckAsync();
        await run.Server.WaitForAcceptsAsync(1, Timeout);
    }

    [Test]
    public async Task NeverRetriesAFailedAccept()
    {
        await using var run = await Run.StartAsync(armed: true);
        run.Server.FailAccepts = true;
        await run.Server.SetPhaseAsync("Matchmaking");

        await run.Server.StartReadyCheckAsync();
        await run.Server.WaitForAcceptsAsync(1, Timeout);
        await Task.Delay(Quiet);

        Assert.That(run.Server.AcceptCount, Is.EqualTo(1));
        Assert.That(run.Server.ReadyCheckResource!["playerResponse"]!.GetValue<string>(), Is.EqualTo("None"));
    }

    [Test]
    public async Task AStaleOrWrongPasswordNeverConnects()
    {
        await using var server = await FakeLcuServer.StartAsync();
        var stale = server.LockfileContents.Replace(server.Password, "not-the-password", StringComparison.Ordinal);
        await using var run = await Run.StartAsync(armed: true, connect: false, server: server, lockfile: () => stale);

        await run.Monitor.RefreshAsync();
        await FakeLcuServer.WaitUntilAsync(() => server.UnauthorizedCount > 0, Timeout);
        await run.Monitor.SessionCompletion.WaitAsync(Timeout);
        await server.StartReadyCheckAsync();
        await Task.Delay(Quiet);

        Assert.That(run.Monitor.IsConnected, Is.False);
        Assert.That(server.AcceptCount, Is.Zero);
    }

    [Test]
    public async Task FakeResourcesParseAsRealLeaguePayloads()
    {
        await using var server = await FakeLcuServer.StartAsync();
        await server.StartReadyCheckAsync();
        var snapshot = ReadyCheckSnapshot.Parse(server.ReadyCheckResource!.ToJsonString());
        Assert.That(snapshot.IsPending, Is.True);
        Assert.That(server.ReadyCheckResource!.Select(property => property.Key),
            Is.EquivalentTo(new[] { "declinerIds", "dodgeWarning", "playerResponse", "state", "suppressUx", "timer" }));
        Assert.That(LcuConnection.Parse(server.LockfileContents)?.Port, Is.EqualTo(server.Port));
    }

    /// <summary>The Core pipeline wired as <c>TrayController</c> wires it, pointed at a fake League client.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private readonly bool ownsServer;

        private Run(FakeLcuServer server, bool ownsServer, Func<string?> lockfile, bool armed, int delaySeconds)
        {
            Server = server;
            this.ownsServer = ownsServer;
            // The tray app's own client, so its certificate policy is part of what these tests cover.
            Http = LcuHttp.CreateClient();
            Controller = new ReadyCheckController { AcceptDelay = TimeSpan.FromSeconds(delaySeconds) };
            Controller.SetArmed(armed);
            Monitor = new ReadyCheckSessionMonitor(
                _ => Task.FromResult(Server.IsRunning ? LcuConnection.Parse(lockfile()) : null),
                connection => new LcuReadyCheckClient(connection, Http), Controller, match: Match);
        }

        public FakeLcuServer Server { get; }
        public HttpClient Http { get; }
        public ReadyCheckController Controller { get; }
        public MatchStatusTracker Match { get; } = new();
        public ReadyCheckSessionMonitor Monitor { get; }

        public static async Task<Run> StartAsync(bool armed, int delaySeconds = 0, bool connect = true,
            FakeLcuServer? server = null, Func<string?>? lockfile = null)
        {
            var owns = server is null;
            server ??= await FakeLcuServer.StartAsync(log: message => TestContext.Progress.WriteLine(message));
            var current = server;
            var run = new Run(server, owns, lockfile ?? (() => current.LockfileContents), armed, delaySeconds);
            if (connect) { await run.ConnectAsync(); }
            return run;
        }

        /// <summary>One discovery tick, then waits until both event subscriptions and the seed reads are in.</summary>
        public async Task ConnectAsync()
        {
            await Monitor.RefreshAsync();
            await WaitForConnectedAsync();
        }

        public async Task WaitForConnectedAsync()
        {
            await Server.WaitForSubscribersAsync(Timeout,
                FakeLcuServer.TopicFor(FakeLcuServer.ReadyCheckRoute), FakeLcuServer.TopicFor(FakeLcuServer.GameflowPhaseRoute));
            await FakeLcuServer.WaitUntilAsync(() => Monitor.IsConnected && Match.Current.Phase != GameflowPhase.Unknown, Timeout);
        }

        public Task WaitForPhaseAsync(GameflowPhase phase)
            => FakeLcuServer.WaitUntilAsync(() => Match.Current.Phase == phase, Timeout);

        public async Task WaitForMatchAsync(MatchStatus expected)
        {
            try { await FakeLcuServer.WaitUntilAsync(() => Match.Current == expected, Timeout); }
            catch (TimeoutException) { Assert.That(Match.Current, Is.EqualTo(expected)); }
        }

        public async ValueTask DisposeAsync()
        {
            await Monitor.DisposeAsync();
            await Controller.DisposeAsync();
            Http.Dispose();
            if (ownsServer) { await Server.DisposeAsync(); }
        }
    }
}
