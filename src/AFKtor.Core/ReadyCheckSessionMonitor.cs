using System.Net.WebSockets;

namespace AFKtor.Core;

/// <summary>Replaces whole event/HTTP sessions when League credentials change.</summary>
public sealed class ReadyCheckSessionMonitor(
    Func<CancellationToken, Task<LcuConnection?>> discover,
    Func<LcuConnection, IReadyCheckClient> createClient,
    ReadyCheckController controller) : IAsyncDisposable
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private Session? active;
    private volatile bool connected;
    private bool disposed;
    public bool IsConnected => connected;
    public Task SessionCompletion => active?.Completion ?? Task.CompletedTask;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
        await refreshGate.WaitAsync(refresh.Token).ConfigureAwait(false);
        try
        {
            LcuConnection? connection;
            try
            {
                // Discovery is supplied by the UI. Invoke it off that context so
                // synchronous disposal cannot wait on a queued UI continuation.
                connection = await Task.Run(() => discover(refresh.Token), refresh.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                connection = null;
            }
            if (connection is not null && active is not null && active.Connection.Matches(connection) && !active.Completion.IsCompleted)
            {
                return;
            }
            await StopSessionAsync().ConfigureAwait(false);
            refresh.Token.ThrowIfCancellationRequested();
            if (connection is null) { return; }
            var client = createClient(connection);
            var session = new Session(connection, client, controller.BeginSession(client),
                CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
            active = session;
            session.Completion = RunSessionAsync(session);
        }
        finally { refreshGate.Release(); }
    }

    private async Task RunSessionAsync(Session session)
    {
        try
        {
            await session.Client.RunAsync(update =>
            {
                if (!session.Cancellation.IsCancellationRequested && ReferenceEquals(active, session))
                {
                    controller.Update(session.Generation, update);
                    connected = true;
                }
            }, session.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or WebSocketException)
        {
            // Credentials and raw response bodies never reach UI/logs. The next
            // discovery tick retries with a completely new, unambiguous lifetime.
        }
        finally
        {
            if (ReferenceEquals(active, session))
            {
                connected = false;
                await controller.DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task StopSessionAsync()
    {
        connected = false;
        if (active is { } session)
        {
            session.Cancellation.Cancel();
            var pending = controller.DisconnectAsync();
            await session.Completion.ConfigureAwait(false);
            await pending.ConfigureAwait(false);
            session.Cancellation.Dispose();
            active = null;
        }
    }

    public async Task StopAsync()
    {
        stopping.Cancel();
        var pending = controller.DisconnectAsync();
        await refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopSessionAsync().ConfigureAwait(false);
            await pending.ConfigureAwait(false);
        }
        finally { refreshGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        await StopAsync().ConfigureAwait(false);
        disposed = true;
        stopping.Dispose();
    }

    private sealed record Session(LcuConnection Connection, IReadyCheckClient Client, long Generation, CancellationTokenSource Cancellation)
    {
        public Task Completion { get; set; } = Task.CompletedTask;
    }
}
