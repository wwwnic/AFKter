using System.Net;
using System.Text.Json;
using AFKtor.Core;
using AFKtor.Core.Diagnostics;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class PlannedPickPipelineTests
{
    // Catches missing monitor/transport/diagnostic wiring, wrong PATCH payload, and bypassing shipped readiness.
    [TestCase(true)]
    [TestCase(false)]
    public async Task MonitorExecutesOnlyWithNativeReadinessAndConfirmsObservedOutcome(bool verified)
    {
        await using var diagnostics = new DiagnosticCollector();
        var observer = new DraftObserver(diagnostics); observer.SetEnabled(true);
        var bans = new PlannedPickExecutor(observer, () => PlannedPickTests.Plan(1), verified, diagnostics: diagnostics);
        var plannedBans = new PlannedBanExecutor(observer, () => PlannedBanTests.Plan(1), verified, diagnostics: diagnostics);
        using var handler = new League(); using var http = new HttpClient(handler);
        LcuConnection? connection = LcuConnection.Parse("LeagueClient:123:12345:secret:https")!;
        await using var monitor = new DraftSessionMonitor(_ => Task.FromResult<LcuConnection?>(connection),
            c => new LcuDraftClient(c, http), observer, plannedBans, bans);
        await monitor.RefreshAsync();
        handler.InDraft = true;
        await monitor.RefreshAsync();
        Assert.That(handler.Writes, Is.EqualTo(verified ? 1 : 0));
        Assert.That(diagnostics.GetState().Pick?.Status, Is.EqualTo(verified ? PickStatus.AwaitingOutcome : PickStatus.NativeVerificationRequired));
        if (verified)
        {
            await monitor.RefreshAsync();
            Assert.That(bans.State.Status, Is.EqualTo(PickStatus.Completed));
            Assert.That(handler.Writes, Is.EqualTo(1));
            var evidence = diagnostics.GetMemoryHistory().Events.Where(e => e.Code == "draft.pick").ToArray();
            Assert.That(evidence.Select(e => e.Details.Pick?.Status), Does.Contain(PickStatus.Dispatching));
            Assert.That(evidence[^1].Details.Pick?.Status, Is.EqualTo(PickStatus.Completed));
            Assert.That(evidence.All(e => JsonSerializer.SerializeToUtf8Bytes(e).Length <= DiagnosticLimits.EventBytes), Is.True);
        }
        connection = null;
        await monitor.RefreshAsync();
        Assert.That(bans.State.Status, Is.EqualTo(PickStatus.Cancelled));
        Assert.That(diagnostics.GetState().Pick?.Status, Is.EqualTo(PickStatus.Cancelled));
    }

    // Catches interpreting server failure as a confirmed rejection and unauthenticated/wrong-route writes.
    [TestCase(200, BanRequestOutcome.Accepted)]
    [TestCase(400, BanRequestOutcome.Rejected)]
    [TestCase(500, BanRequestOutcome.Ambiguous)]
    public async Task TransportClassifiesCompletionWithoutAssumingAppliedState(int status, BanRequestOutcome outcome)
    {
        using var handler = new League { Status = status }; using var http = new HttpClient(handler);
        var client = new LcuDraftClient(LcuConnection.Parse("LeagueClient:123:12345:secret:https")!, http);
        Assert.That(await client.PickAsync(11, 1, true, default), Is.EqualTo(outcome));
    }

    private sealed class League : HttpMessageHandler
    {
        public bool InDraft { get; set; }
        public int Writes { get; private set; }
        public int Status { get; set; } = 200;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(request.RequestUri?.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Patch)
            {
                Assert.That(path, Is.EqualTo("/lol-champ-select/v1/session/actions/11"));
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.That(body.RootElement.GetProperty("championId").GetInt32(), Is.EqualTo(1));
                Assert.That(body.RootElement.GetProperty("completed").GetBoolean(), Is.True);
                Writes++;
                return new((HttpStatusCode)Status) { Content = new StringContent("{}") };
            }
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            var json = path switch {
                "/lol-gameflow/v1/session" => InDraft ? LcuDraftClientTests.Flow : "{\"phase\":\"Lobby\"}",
                "/lol-champ-select/v1/pickable-champion-ids" => "[1,2,86]",
                "/lol-champ-select/v1/session" => LcuDraftClientTests.Ban.Replace("28000", "1500").Replace("\"type\":\"ban\"", "\"type\":\"pick\""),
                _ => throw new AssertionException("Unexpected LCU endpoint")
            };
            if (path.EndsWith("/session") && path.Contains("champ-select") && Writes > 0)
            {
                json = json.Replace("\"championId\":0", "\"championId\":1")
                    .Replace("\"isInProgress\":true", "\"isInProgress\":false").Replace("\"completed\":false", "\"completed\":true");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
