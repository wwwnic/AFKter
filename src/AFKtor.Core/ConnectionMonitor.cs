namespace AFKtor.Core;

public interface ILeagueConnectionProbe
{
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken);
}

public sealed class ConnectionMonitor(ILeagueConnectionProbe probe)
{
    public bool IsConnected { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = await probe.IsConnectedAsync(cancellationToken);
    }
}
