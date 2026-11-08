using System.Net;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class LcuDraftClientTests
{
    internal const string Flow = """{"phase":"ChampSelect","gameData":{"gameId":101,"queue":{"id":400}}}""";
    internal static string Ban => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/draft/ban.json"));

    // Catches confusing ban selections with teammate intent or losing action/role/timer units.
    [Test]
    public void ParsesSanitizedReplayWithoutPlayerIdentity()
    {
        var snapshot = LcuDraftClient.Parse(Flow, Ban);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.QueueId, Is.EqualTo(400));
            Assert.That(snapshot.Players[0].Role, Is.EqualTo(DraftRole.Middle));
            Assert.That(snapshot.Players[1].PickIntent, Is.EqualTo(86));
            Assert.That(snapshot.Actions[1].ChampionId, Is.EqualTo(86));
            Assert.That(snapshot.Actions[0].Id, Is.EqualTo(11));
            Assert.That(snapshot.TimeLeftMilliseconds, Is.EqualTo(28000));
        });
    }

    [TestCase("{}")]
    [TestCase("{\"gameId\":101}")]
    [TestCase("not json")]
    public void RejectsInsufficientOrMalformedState(string championSelect)
        => Assert.Throws<InvalidDataException>(() => LcuDraftClient.Parse(Flow, championSelect));

    // A changed gameflow after reading champ select cannot establish a coherent admission baseline.
    [Test]
    public async Task ChangedLifecycleDuringReadIsRejectedAndTransportOnlyUsesGets()
    {
        using var handler = new ReplayHandler([Flow, Ban, Flow.Replace("101", "102")]);
        using var http = new HttpClient(handler);
        var client = new LcuDraftClient(LcuConnection.Parse("LeagueClient:123:12345:secret:https")!, http);
        Assert.ThrowsAsync<InvalidDataException>(async () => await client.ReadAsync(default));
        Assert.That(handler.Paths, Is.EqualTo(new[] { "/lol-gameflow/v1/session", "/lol-champ-select/v1/session", "/lol-gameflow/v1/session" }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ReadOnlyProgressionUsesFreshCoherentState()
    {
        using var handler = new ReplayHandler([Flow, Ban, Flow]);
        using var http = new HttpClient(handler);
        var client = new LcuDraftClient(LcuConnection.Parse("LeagueClient:123:12345:secret:https")!, http);
        var snapshot = await client.ReadAsync(default);
        Assert.That(snapshot?.GameId, Is.EqualTo(101));
        Assert.That(handler.Paths.Count, Is.EqualTo(3));
    }

    private sealed class ReplayHandler(string[] responses) : HttpMessageHandler
    {
        private int index;
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get), "Observer must never write draft state");
            Assert.That(request.RequestUri?.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[index++]) });
        }
    }
}
