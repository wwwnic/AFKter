using AFKtor.Core.Diagnostics;

namespace AFKtor.Diagnostics;

/// <summary>Composition lifetime: startup never waits on hosting or disk; all diagnostic shutdown shares two seconds.</summary>
public sealed class DiagnosticRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private int disposed;
    public DiagnosticCollector Collector { get; }
    public Task<DiagnosticHost> Started { get; }

    private DiagnosticRuntime(string discoveryDirectory, IDiagnosticSink? sink, IDiagnosticHistory? history)
    {
        Collector = new(sink);
        Started = Task.Run(() => DiagnosticHost.StartAsync(Collector, discoveryDirectory, history, stopping.Token));
    }

    public static DiagnosticRuntime Start(string discoveryDirectory, IDiagnosticSink? sink = null, IDiagnosticHistory? history = null) =>
        new(discoveryDirectory, sink, history);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        var stopCollector = Collector.DisposeAsync().AsTask();
        var stopHost = Task.Run(async () =>
        {
            try { await (await Started.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { Collector.SetApiHealth(false); }
        });
        try
        {
            await Task.WhenAll(stopCollector, stopHost)
                .WaitAsync(TimeSpan.FromMilliseconds(DiagnosticLimits.ShutdownMilliseconds)).ConfigureAwait(false);
        }
        catch (Exception) { /* Abandon diagnostic work after the shared shutdown deadline. */ }
    }
}
