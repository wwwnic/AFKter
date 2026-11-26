namespace AFKtor.Core;

/// <summary>The preferences persisted between application launches. Arming itself is never saved.</summary>
public sealed record AcceptDelaySettings
{
    public const int MinimumSeconds = 0;
    public const int MaximumSeconds = 30;
    public static AcceptDelaySettings Default { get; } = new(3);

    public AcceptDelaySettings(int delaySeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delaySeconds, MinimumSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delaySeconds, MaximumSeconds);
        DelaySeconds = delaySeconds;
    }

    public int DelaySeconds { get; }

    /// <summary>Opt-in: arm auto-accept as AFKtor starts instead of starting disarmed.</summary>
    public bool ArmOnLaunch { get; init; }
}
