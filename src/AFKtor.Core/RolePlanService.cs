using AFKtor.Core.Diagnostics;

namespace AFKtor.Core;

/// <summary>Publishes whole immutable edits immediately; one background owner persists coherent snapshots.</summary>
public sealed class RolePlanService : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IRolePlanStore store;
    private readonly ChampionCatalog catalog;
    private readonly DiagnosticCollector? diagnostics;
    private readonly SemaphoreSlim writer = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private RolePlanRevision current;
    private long savedRevision;
    private PlanSaveHealth health = PlanSaveHealth.Saved;
    private bool disposed;
    public RolePlanRevision Current { get { lock (gate) return current; } }
    public PlanSaveHealth Health { get { lock (gate) return health; } }
    public long SavedRevision { get { lock (gate) return savedRevision; } }
    public PlanRecovery Recovery { get; }

    public RolePlanService(IRolePlanStore store, ChampionCatalog catalog, DiagnosticCollector? diagnostics = null, bool startWorker = true)
    {
        this.store = store; this.catalog = catalog; this.diagnostics = diagnostics;
        (current, Recovery) = store.Load(catalog);
        savedRevision = current.Revision;
        Observe();
        worker = startWorker ? Task.Run(AutosaveAsync) : Task.CompletedTask;
    }

    public bool Replace(DraftRole role, PlanList list, IEnumerable<int> champions)
    {
        var ids = champions.Take(1001).ToArray();
        if (!catalog.Available || !Enum.IsDefined(role) || !Enum.IsDefined(list) || !RolePlanFileStore.Valid(ids, catalog)) return false;
        lock (gate)
        {
            if (disposed || current.Revision == long.MaxValue - 1) return false;
            var plan = current.For(role);
            if ((list == PlanList.Picks ? plan.Picks : plan.Bans).SequenceEqual(ids)) return false;
            var replacement = list == PlanList.Picks ? plan with { Picks = [.. ids] } : plan with { Bans = [.. ids] };
            current = new(current.Revision + 1, current.Roles.SetItem(role, replacement), current.ManualPickPolicy);
            health = PlanSaveHealth.Unsaved;
            Observe();
            return true;
        }
    }

    public bool SetManualPickPolicy(ManualPickPolicy policy)
    {
        if (!Enum.IsDefined(policy)) return false;
        lock (gate)
        {
            if (disposed || current.Revision == long.MaxValue - 1 || current.ManualPickPolicy == policy) return false;
            current = current with { Revision = current.Revision + 1, ManualPickPolicy = policy };
            health = PlanSaveHealth.Unsaved;
            Observe();
            return true;
        }
    }

    /// <summary>Orders an asynchronous request's start against publication of a newer plan; never awaits under the revision gate.</summary>
    public Task<T>? StartIfCurrent<T>(long revision, Func<Task<T>> start)
    {
        lock (gate) return !disposed && current.Revision == revision ? start() : null;
    }

    public async Task SaveLatestAsync()
    {
        await writer.WaitAsync().ConfigureAwait(false);
        try
        {
            RolePlanRevision snapshot;
            lock (gate)
            {
                if (current.Revision == savedRevision) return;
                snapshot = current;
            }
            try
            {
                // File I/O never holds the revision gate or runs on the UI thread.
                await Task.Run(() => store.Save(snapshot)).ConfigureAwait(false);
                lock (gate)
                {
                    savedRevision = snapshot.Revision;
                    health = current.Revision == savedRevision ? PlanSaveHealth.Saved : PlanSaveHealth.Unsaved;
                    Observe();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { lock (gate) { health = PlanSaveHealth.SaveFailed; Observe(); } }
        }
        finally { writer.Release(); }
    }

    private void Observe() => diagnostics?.ObserveRolePlans(new(current.Revision, savedRevision, health, Recovery, current.ManualPickPolicy));

    private async Task AutosaveAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                await SaveLatestAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        stopping.Cancel();
        await worker.ConfigureAwait(false);
        await SaveLatestAsync().ConfigureAwait(false);
        // A previously requested UI retry may still hold writer; retain the tiny semaphore.
        stopping.Dispose();
    }
}
