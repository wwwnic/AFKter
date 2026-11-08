namespace AFKtor.Core;

/// <summary>One serialized read per host tick; credential replacement resets admission.</summary>
public sealed class DraftSessionMonitor(
    Func<CancellationToken, Task<LcuConnection?>> discover,
    Func<LcuConnection, IDraftClient> createClient,
    DraftObserver observer) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private LcuConnection? connection;
    private IDraftClient? client;
    private long generation, sequence;
    private bool disposed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
        try
        {
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                var discovered = await Task.Run(() => discover(lifetime.Token), lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                if (discovered is null) { Disconnect(); return; }
                if (connection is null || !connection.Matches(discovered))
                {
                    Disconnect();
                    connection = discovered;
                    client = createClient(discovered);
                    generation = observer.BeginSession();
                    sequence = 0;
                }
                var armingEpoch = observer.ArmingEpoch;
                var snapshot = await client!.ReadAsync(lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                observer.Observe(generation, ++sequence, snapshot, armingEpoch);
            }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            { Disconnect(); }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { }
    }

    private void Disconnect()
    {
        connection = null;
        client = null;
        observer.Disconnect();
    }

    public async Task StopAsync()
    {
        stopping.Cancel();
        observer.Disconnect();
        await gate.WaitAsync().ConfigureAwait(false);
        try { Disconnect(); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await StopAsync().ConfigureAwait(false);
        disposed = true;
        stopping.Dispose();
    }
}
