using System.Collections.Immutable;

namespace AFKtor.Core;

public enum DraftRole { Top, Jungle, Middle, Bottom, Support }
public enum PlanList { Picks, Bans }
public sealed record RolePlan(ImmutableArray<int> Picks, ImmutableArray<int> Bans);
public sealed record RolePlanRevision(long Revision, ImmutableDictionary<DraftRole, RolePlan> Roles)
{
    public static RolePlanRevision Empty { get; } = new(0, Enum.GetValues<DraftRole>().ToImmutableDictionary(r => r, _ => new RolePlan([], [])));
    public RolePlan For(DraftRole role) => Roles[role];
}
public enum PlanSaveHealth { Saved, Unsaved, SaveFailed }
public enum PlanRecovery { New, Loaded, Rejected, Unavailable }
