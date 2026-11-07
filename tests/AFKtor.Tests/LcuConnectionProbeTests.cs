using System.Net;
using System.Text;
using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class LcuConnectionProbeTests
{
    [TestCase("partial")]
    [TestCase("LeagueClient:123:0:secret:https")]
    [TestCase("LeagueClient:123:65536:secret:https")]
    [TestCase("LeagueClient:123:1234::https")]
    [TestCase("LeagueClient:123:1234:secret:http")]
    [TestCase("RiotClient:123:1234:secret:https")]
    public async Task InvalidLockfileDoesNotSendRequest(string contents)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new AssertionException("Unexpected request")));
        var probe = new LcuConnectionProbe(_ => Task.FromResult<string?>(contents), client);
        Assert.That(await probe.IsConnectedAsync(default), Is.False);
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.Redirect)]
    public async Task FailedResponseMeansDisconnected(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var probe = new LcuConnectionProbe(_ => Task.FromResult<string?>("LeagueClient:123:1234:secret:https"), client);
        Assert.That(await probe.IsConnectedAsync(default), Is.False);
    }

    [Test]
    public async Task NetworkFailureAndTimeoutDisconnectThenRecover()
    {
        Exception? failure = new HttpRequestException("Client exited");
        using var client = new HttpClient(new Handler((_, _) => failure is null
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
            : Task.FromException<HttpResponseMessage>(failure)));
        var probe = new LcuConnectionProbe(_ => Task.FromResult<string?>("LeagueClient:123:1234:secret:https"), client);
        Assert.That(await probe.IsConnectedAsync(default), Is.False);
        failure = new TaskCanceledException("Request timeout");
        Assert.That(await probe.IsConnectedAsync(default), Is.False);
        failure = null;
        Assert.That(await probe.IsConnectedAsync(default), Is.True);
    }

    [Test]
    public async Task UnreadableLockfileMeansDisconnected()
    {
        using var client = new HttpClient();
        var probe = new LcuConnectionProbe(_ => throw new IOException("Client exited"), client);
        Assert.That(await probe.IsConnectedAsync(default), Is.False);
    }

    [Test]
    public void ShutdownCancellationPropagates()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var client = new HttpClient();
        var probe = new LcuConnectionProbe(token => Task.FromCanceled<string?>(token), client);
        Assert.ThrowsAsync<TaskCanceledException>(() => probe.IsConnectedAsync(stop.Token));
    }

    [Test]
    public async Task AuthenticatesWithFreshCredentialsAfterRestart()
    {
        string? lockfile = null;
        var requests = new List<(Uri?, string?)>();
        using var handler = new Handler((request, _) =>
        {
            requests.Add((request.RequestUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler);
        var probe = new LcuConnectionProbe(_ => Task.FromResult(lockfile), client);
        Assert.That(await probe.IsConnectedAsync(default), Is.False);
        lockfile = "LeagueClient:123:54321:first:https";
        Assert.That(await probe.IsConnectedAsync(default), Is.True);
        lockfile = "LeagueClient:456:54322:second:https";
        Assert.That(await probe.IsConnectedAsync(default), Is.True);
        Assert.That(requests.Select(r => r.Item1!.ToString()), Is.EqualTo(new[]
        {
            "https://127.0.0.1:54321/lol-gameflow/v1/gameflow-phase",
            "https://127.0.0.1:54322/lol-gameflow/v1/gameflow-phase"
        }));
        Assert.That(requests.Select(r => r.Item2), Is.EqualTo(new[]
        {
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:first")),
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:second"))
        }));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => send(request, token);
    }
}
