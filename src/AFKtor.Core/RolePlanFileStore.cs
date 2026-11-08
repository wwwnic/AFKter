using System.Collections.Immutable;
using System.Text.Json;

namespace AFKtor.Core;

public interface IRolePlanStore
{
    (RolePlanRevision Plan, PlanRecovery Recovery) Load(ChampionCatalog catalog);
    void Save(RolePlanRevision revision);
}

public sealed class RolePlanFileStore(string path, string? legacySettingsPath = null) : IRolePlanStore
{
    private readonly string file = Path.GetFullPath(path);
    private sealed record Stored(int SchemaVersion, long Revision, StoredRole[] Roles, JsonElement ManualPickPolicy = default, JsonElement Timing = default);
    private sealed record StoredRole(DraftRole Role, int[] Picks, int[] Bans);

    public (RolePlanRevision Plan, PlanRecovery Recovery) Load(ChampionCatalog catalog)
    {
        try
        {
            if (!File.Exists(file)) return (RolePlanRevision.Empty with { Timing = LegacyTiming() }, PlanRecovery.New);
            if (!catalog.Available) return (RolePlanRevision.Empty with { ManualPickPolicy = ManualPickPolicy.DoNothing }, PlanRecovery.Unavailable);
            if (new FileInfo(file).Length > 131072) return (RolePlanRevision.Empty with { ManualPickPolicy = ManualPickPolicy.DoNothing }, PlanRecovery.Rejected);
            var stored = JsonSerializer.Deserialize<Stored>(System.IO.File.ReadAllText(file));
            if (stored is null || stored.SchemaVersion != 1 || stored.Revision < 0 || stored.Revision == long.MaxValue ||
                stored.ManualPickPolicy.ValueKind != JsonValueKind.Undefined &&
                    (stored.ManualPickPolicy.ValueKind != JsonValueKind.Number || !stored.ManualPickPolicy.TryGetInt32(out var policy) || !Enum.IsDefined((ManualPickPolicy)policy)) ||
                stored.Timing.ValueKind != JsonValueKind.Undefined && !ValidTiming(stored.Timing) ||
                stored.Roles is null || stored.Roles.Length != 5 || stored.Roles.Any(r => r is null) ||
                stored.Roles.Select(r => r.Role).Distinct().Count() != 5 ||
                stored.Roles.Any(r => !Enum.IsDefined(r.Role) || !Valid(r.Picks, catalog) || !Valid(r.Bans, catalog)))
                return (RolePlanRevision.Empty with { ManualPickPolicy = ManualPickPolicy.DoNothing }, PlanRecovery.Rejected);
            return (new(stored.Revision, stored.Roles.ToImmutableDictionary(r => r.Role,
                r => new RolePlan([.. r.Picks], [.. r.Bans])), stored.ManualPickPolicy.ValueKind == JsonValueKind.Undefined ? ManualPickPolicy.LockCurrentHover : (ManualPickPolicy)stored.ManualPickPolicy.GetInt32()) { Timing = stored.Timing.ValueKind == JsonValueKind.Undefined ? LegacyTiming() : stored.Timing.Deserialize<TimingSettings>()! }, PlanRecovery.Loaded);
        }
        catch (JsonException) { return (RolePlanRevision.Empty with { ManualPickPolicy = ManualPickPolicy.DoNothing }, PlanRecovery.Rejected); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return (RolePlanRevision.Empty with { ManualPickPolicy = ManualPickPolicy.DoNothing }, PlanRecovery.Unavailable); }
    }

    private TimingSettings LegacyTiming() => TimingSettings.Default with
    { AcceptDelaySeconds = legacySettingsPath is null ? 3 : new AcceptDelaySettingsStore(legacySettingsPath).Load().DelaySeconds };

    private static bool ValidTiming(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        // Missing members are corruption, not zero-valued preferences.
        foreach (var name in new[] { "AcceptDelaySeconds", "ActionPacingMilliseconds", "HoverLockMilliseconds", "UrgentMilliseconds", "MinimumDispatchMilliseconds" })
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) return false;
        return element.Deserialize<TimingSettings>() is { IsValid: true };
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
                    revision.Roles.OrderBy(r => r.Key).Select(r => new StoredRole(r.Key, r.Value.Picks.ToArray(), r.Value.Bans.ToArray())).ToArray(), JsonSerializer.SerializeToElement(revision.ManualPickPolicy), JsonSerializer.SerializeToElement(revision.Timing)));
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
