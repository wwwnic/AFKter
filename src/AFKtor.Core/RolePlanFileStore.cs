using System.Collections.Immutable;
using System.Text.Json;

namespace AFKtor.Core;

public interface IRolePlanStore
{
    (RolePlanRevision Plan, PlanRecovery Recovery) Load(ChampionCatalog catalog);
    void Save(RolePlanRevision revision);
}

public sealed class RolePlanFileStore(string path) : IRolePlanStore
{
    private readonly string file = Path.GetFullPath(path);
    private sealed record Stored(int SchemaVersion, long Revision, StoredRole[] Roles);
    private sealed record StoredRole(DraftRole Role, int[] Picks, int[] Bans);

    public (RolePlanRevision Plan, PlanRecovery Recovery) Load(ChampionCatalog catalog)
    {
        try
        {
            if (!File.Exists(file)) return (RolePlanRevision.Empty, PlanRecovery.New);
            if (!catalog.Available) return (RolePlanRevision.Empty, PlanRecovery.Unavailable);
            if (new FileInfo(file).Length > 131072) return (RolePlanRevision.Empty, PlanRecovery.Rejected);
            var stored = JsonSerializer.Deserialize<Stored>(System.IO.File.ReadAllText(file));
            if (stored is null || stored.SchemaVersion != 1 || stored.Revision < 0 || stored.Revision == long.MaxValue ||
                stored.Roles is null || stored.Roles.Length != 5 || stored.Roles.Any(r => r is null) ||
                stored.Roles.Select(r => r.Role).Distinct().Count() != 5 ||
                stored.Roles.Any(r => !Enum.IsDefined(r.Role) || !Valid(r.Picks, catalog) || !Valid(r.Bans, catalog)))
                return (RolePlanRevision.Empty, PlanRecovery.Rejected);
            return (new(stored.Revision, stored.Roles.ToImmutableDictionary(r => r.Role,
                r => new RolePlan([.. r.Picks], [.. r.Bans]))), PlanRecovery.Loaded);
        }
        catch (JsonException) { return (RolePlanRevision.Empty, PlanRecovery.Rejected); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return (RolePlanRevision.Empty, PlanRecovery.Unavailable); }
    }

    internal static bool Valid(int[]? ids, ChampionCatalog catalog) => ids is not null && ids.Length <= 1000 &&
        ids.Distinct().Count() == ids.Length && ids.All(id => catalog.Find(id) is not null);

    public void Save(RolePlanRevision revision)
    {
        var directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".plans-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Stored(1, revision.Revision,
                    revision.Roles.OrderBy(r => r.Key).Select(r => new StoredRole(r.Key, r.Value.Picks.ToArray(), r.Value.Bans.ToArray())).ToArray()));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
