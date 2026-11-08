namespace AFKtor.Core;

/// <summary>Discrete editor commands; selection and unfinished text are never revisions.</summary>
public sealed class RolePlanEditor(RolePlanService plans)
{
    private int[] Read(DraftRole role, PlanList list)
    {
        var plan = plans.Current.For(role);
        return (list == PlanList.Picks ? plan.Picks : plan.Bans).ToArray();
    }
    public bool Add(DraftRole role, PlanList list, int champion) => plans.Replace(role, list, [.. Read(role, list), champion]);
    public bool Remove(DraftRole role, PlanList list, int index)
    {
        var values = Read(role, list).ToList();
        if (index < 0 || index >= values.Count) return false;
        values.RemoveAt(index);
        return plans.Replace(role, list, values);
    }
    public bool Move(DraftRole role, PlanList list, int from, int to)
    {
        var values = Read(role, list).ToList();
        if (from < 0 || from >= values.Count || to < 0 || to >= values.Count || from == to) return false;
        var champion = values[from];
        values.RemoveAt(from);
        values.Insert(to, champion);
        return plans.Replace(role, list, values);
    }
}
