using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class ConnectionMonitorTests
{
    [Test]
    public async Task TracksClientStartingExitingAndRestarting()
    {
        var probe = new Probe();
        var monitor = new ConnectionMonitor(probe);
        Assert.That(monitor.IsConnected, Is.False);
        await monitor.RefreshAsync();
        Assert.That(monitor.IsConnected, Is.False);
        probe.Connected = true;
        await monitor.RefreshAsync();
        Assert.That(monitor.IsConnected, Is.True);
        probe.Connected = false;
        await monitor.RefreshAsync();
        Assert.That(monitor.IsConnected, Is.False);
        probe.Connected = true;
        await monitor.RefreshAsync();
        Assert.That(monitor.IsConnected, Is.True);
    }

    private sealed class Probe : ILeagueConnectionProbe
    {
        public bool Connected { get; set; }
        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
            => Task.FromResult(Connected);
    }
}
