namespace AFKtor.Core;

public enum TimingField { AcceptDelay, ActionPacing, HoverLock, Urgent, MinimumDispatch }

/// <summary>Milliseconds for draft timing; ready-check delay remains whole seconds.</summary>
public sealed record TimingSettings(int AcceptDelaySeconds, int ActionPacingMilliseconds,
    int HoverLockMilliseconds, int UrgentMilliseconds, int MinimumDispatchMilliseconds)
{
    public static TimingSettings Default { get; } = new(3, 1000, 1000, 3000, 250);
    // Keep at least the original request safety margin; urgency must leave a usable request window.
    public bool IsValid => AcceptDelaySeconds is >= 0 and <= 30 &&
        ActionPacingMilliseconds is >= 0 and <= 5000 && HoverLockMilliseconds is >= 0 and <= 5000 &&
        UrgentMilliseconds is >= 1000 and <= 10000 && MinimumDispatchMilliseconds is >= 250 and <= 1000 &&
        UrgentMilliseconds > MinimumDispatchMilliseconds;
}
