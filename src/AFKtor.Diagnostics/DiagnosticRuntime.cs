using AFKtor.Core.Diagnostics;

namespace AFKtor.Diagnostics;

/// <summary>Composition lifetime: startup never waits on hosting or disk; all diagnostic shutdown shares two seconds.</summary>
public sealed class DiagnosticRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private int disposed;
    private readonly DiagnosticHistoryStore? store;
    public DiagnosticCollector Collector { get; }
    public Task<DiagnosticHost> Started { get; }

    private DiagnosticRuntime(string discoveryDirectory, IDiagnosticSink? sink, IDiagnosticHistory? history, string? historyDirectory)
    {
        // Construction is memory-only: ownership, recovery and cleanup happen on workers.
        if (sink is null) sink = store = new DiagnosticHistoryStore(historyDirectory ?? Path.Combine(discoveryDirectory, "history"));
        Collector = new(sink);
        if (store is not null) history ??= new RetainedDiagnosticHistory(Collector, store);
        Started = Task.Run(() => DiagnosticHost.StartAsync(Collector, discoveryDirectory, history, stopping.Token));
    }

    public static DiagnosticRuntime Start(string discoveryDirectory, IDiagnosticSink? sink = null, IDiagnosticHistory? history = null, string? historyDirectory = null) =>
        new(discoveryDirectory, sink, history, historyDirectory);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        var stopCollector = Task.Run(async () =>
        {
            await Collector.DisposeAsync().ConfigureAwait(false);
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
        });
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
