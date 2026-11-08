using System.Collections.Immutable;

namespace AFKtor.Core;

public enum DraftRole { Top, Jungle, Middle, Bottom, Support }
public enum ManualPickPolicy { LockCurrentHover, UseRolePlan, DoNothing }
public static class ManualPickPolicies
{
    public static string Explanation(ManualPickPolicy policy) => policy switch
    {
        ManualPickPolicy.LockCurrentHover => "At the deadline, lock only your current eligible hover, including outside your plan. An invalid hover is never replaced.",
        ManualPickPolicy.UseRolePlan => "At the deadline, select and lock the first eligible champion in your latest role plan, replacing your manual hover. An exhausted plan does nothing.",
        _ => "After a manual pick change, make no further automatic pick selections or locks in this draft."
    };
}
public enum PlanList { Picks, Bans }
public sealed record RolePlan(ImmutableArray<int> Picks, ImmutableArray<int> Bans);
public sealed record RolePlanRevision(long Revision, ImmutableDictionary<DraftRole, RolePlan> Roles, ManualPickPolicy ManualPickPolicy = ManualPickPolicy.LockCurrentHover)
{
    public TimingSettings Timing { get; init; } = TimingSettings.Default;
    public static RolePlanRevision Empty { get; } = new(0, Enum.GetValues<DraftRole>().ToImmutableDictionary(r => r, _ => new RolePlan([], [])));
    public RolePlan For(DraftRole role) => Roles[role];
}
public enum PlanSaveHealth { Saved, Unsaved, SaveFailed }
public enum PlanRecovery { New, Loaded, Rejected, Unavailable }
