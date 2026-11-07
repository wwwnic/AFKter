namespace AFKtor.Core;

/// <summary>The only setting persisted between application launches.</summary>
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
}
