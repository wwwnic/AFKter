using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class LcuHttpTests
{
    [TestCase("https://127.0.0.1:2999/lol-matchmaking/v1/ready-check", true)]
    // The event socket's upgrade request reaches the certificate callback with its wss scheme.
    [TestCase("wss://127.0.0.1:2999/", true)]
    [TestCase("https://localhost:2999/", false)]
    [TestCase("https://example.com/", false)]
    [TestCase("http://127.0.0.1:2999/", false)]
    [TestCase("ws://127.0.0.1:2999/", false)]
    public void TrustsOnlyEncryptedLoopbackEndpoints(string uri, bool trusted)
        => Assert.That(LcuHttp.IsTrustedEndpoint(new Uri(uri)), Is.EqualTo(trusted));

    [Test]
    public void TrustsNothingWithoutAUri() => Assert.That(LcuHttp.IsTrustedEndpoint(null), Is.False);
}
