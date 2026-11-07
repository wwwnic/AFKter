using System.Text.Json;

namespace AFKtor.Core;

public sealed record ReadyCheckSnapshot(string State, string PlayerResponse, double ElapsedSeconds)
{
    public bool IsPending => State == "InProgress" && PlayerResponse == "None";
    public static ReadyCheckSnapshot Inactive { get; } = new("Invalid", "None", 0);

    public static ReadyCheckSnapshot Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("playerResponse", out var response) || response.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("timer", out var timer) || timer.ValueKind != JsonValueKind.Number || !timer.TryGetDouble(out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 ||
                state.GetString() is not ("Invalid" or "InProgress" or "EveryoneReady" or "StrangerNotReady" or "PartyNotReady" or "Error") ||
                response.GetString() is not ("None" or "Accepted" or "Declined"))
            {
                throw new InvalidDataException("Unverifiable ready-check state.");
            }
            return new(state.GetString()!, response.GetString()!, seconds);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Malformed ready-check state.");
        }
    }
}

public sealed record ReadyCheckUpdate(ReadyCheckSnapshot Snapshot, bool IsCreate = false, bool IsInitial = false);

public interface IReadyCheckClient
{
    Task<ReadyCheckSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task AcceptAsync(CancellationToken cancellationToken);
    // Existing simulated clients may not expose HTTP metadata. Null explicitly means unavailable.
    async Task<int?> AcceptWithStatusAsync(CancellationToken cancellationToken)
    {
        await AcceptAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }
    Task RunAsync(Action<ReadyCheckUpdate> receive, CancellationToken cancellationToken);
}
