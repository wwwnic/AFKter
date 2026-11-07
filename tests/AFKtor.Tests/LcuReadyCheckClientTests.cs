using System.Net;
using System.Text;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class LcuReadyCheckClientTests
{
    [TestCase("InProgress", "None", true)]
    [TestCase("InProgress", "Accepted", false)]
    [TestCase("InProgress", "Declined", false)]
    [TestCase("StrangerNotReady", "None", false)]
    [TestCase("Invalid", "None", false)]
    public async Task ReadsPendingOnlyForAnUnansweredInProgressCheck(string state, string response, bool pending)
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.That(request.RequestUri!.ToString(), Is.EqualTo("https://127.0.0.1:54321/lol-matchmaking/v1/ready-check"));
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.Headers.Authorization!.ToString(), Is.EqualTo("Basic cmlvdDpzZWNyZXQ="));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { state, playerResponse = response, timer = 2.5, declinerIds = Array.Empty<int>(), suppressUx = false, dodgeWarning = new { } }))
            });
        }));
        var api = new LcuReadyCheckClient(Connection(), http);
        var snapshot = await api.ReadAsync(default);
        Assert.That(snapshot.IsPending, Is.EqualTo(pending));
        Assert.That(snapshot.ElapsedSeconds, Is.EqualTo(2.5));
    }

    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("not json")]
    [TestCase("{\"state\":\"InProgress\",\"playerResponse\":\"None\",\"timer\":\"bad\"}")]
    [TestCase("{\"state\":\"InProgress\",\"playerResponse\":\"None\",\"timer\":-1}")]
    [TestCase("{\"state\":7,\"playerResponse\":\"None\",\"timer\":0}")]
    public void MalformedSnapshotFailsClosed(string json)
    {
        Assert.Throws<InvalidDataException>(() => ReadyCheckSnapshot.Parse(json));
    }

    [Test]
    public async Task AcceptanceUsesOneAuthenticatedBodylessPost()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.RequestUri!.ToString(), Is.EqualTo("https://127.0.0.1:54321/lol-matchmaking/v1/ready-check/accept"));
            Assert.That(request.Headers.Authorization!.ToString(), Is.EqualTo("Basic cmlvdDpzZWNyZXQ="));
            Assert.That(request.Content, Is.Null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        await new LcuReadyCheckClient(Connection(), http).AcceptAsync(default);
        Assert.That(requests, Is.EqualTo(1));
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Conflict)]
    [TestCase(HttpStatusCode.Redirect)]
    public void FailedHttpResponsesAreNotPendingOrRetried(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var api = new LcuReadyCheckClient(Connection(), http);
        Assert.ThrowsAsync<HttpRequestException>(() => api.ReadAsync(default));
        Assert.ThrowsAsync<HttpRequestException>(() => api.AcceptAsync(default));
    }

    [Test]
    public void ParsesDeleteAndIgnoresUnrelatedEvents()
    {
        Assert.That(LcuReadyCheckClient.DecodeEvent("[8,\"other\",{}]"), Is.Null);
        var update = LcuReadyCheckClient.DecodeEvent("[8,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\",{\"uri\":\"/lol-matchmaking/v1/ready-check\",\"eventType\":\"Delete\",\"data\":null}]");
        Assert.That(update!.Snapshot.IsPending, Is.False);
        Assert.That(update.IsCreate, Is.False);
    }

    [Test]
    public void MalformedRelevantEventInvalidatesSession()
    {
        Assert.Throws<InvalidDataException>(() => LcuReadyCheckClient.DecodeEvent("[8,\"OnJsonApiEvent_lol-matchmaking_v1_ready-check\",{\"uri\":\"/lol-matchmaking/v1/ready-check\",\"eventType\":\"Update\",\"data\":{}}]"));
    }

    private static LcuConnection Connection() => LcuConnection.Parse("LeagueClient:123:54321:secret:https")!;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
