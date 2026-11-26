using System.Text.Json;

namespace AFKtor.Core;

/// <summary>League's gameflow phase, as published at <c>/lol-gameflow/v1/gameflow-phase</c>.</summary>
public enum GameflowPhase
{
    Unknown,
    None,
    Lobby,
    Matchmaking,
    CheckedIntoTournament,
    ReadyCheck,
    ChampSelect,
    GameStart,
    FailedToLaunch,
    InProgress,
    Reconnect,
    WaitingForStats,
    PreEndOfGame,
    EndOfGame,
    TerminatedInError
}

public static class GameflowPhases
{
    /// <summary>Reads the JSON string body or event data. Anything unrecognized is <see cref="GameflowPhase.Unknown"/>.</summary>
    public static GameflowPhase Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) { return GameflowPhase.Unknown; }
        var name = value.GetString();
        // Enum.TryParse also accepts numbers and lists; only exact LCU names are trusted.
        return name is not null && Enum.GetNames<GameflowPhase>().Contains(name, StringComparer.Ordinal) &&
            Enum.TryParse<GameflowPhase>(name, out var phase) ? phase : GameflowPhase.Unknown;
    }

    public static GameflowPhase Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return GameflowPhase.Unknown;
        }
    }
}

/// <summary>What happened to the most recent ready check or champ select, judged from the phase that followed it.</summary>
public enum MatchOutcome
{
    None,
    /// <summary>The ready check was followed by champ select or a game: the match really started.</summary>
    Started,
    /// <summary>The ready check ended and League put the player back in the queue (another player did not accept).</summary>
    Requeued,
    /// <summary>The ready check ended and League removed the player from the queue (the check was not accepted).</summary>
    LeftQueue,
    /// <summary>Champ select ended without a game, for example because someone dodged.</summary>
    ChampSelectEnded
}

public sealed record MatchStatus(GameflowPhase Phase, MatchOutcome Outcome)
{
    public static MatchStatus Unknown { get; } = new(GameflowPhase.Unknown, MatchOutcome.None);

    public bool IsInMatch => Phase is GameflowPhase.ChampSelect or GameflowPhase.GameStart or
        GameflowPhase.InProgress or GameflowPhase.Reconnect;

    /// <summary>The player has to act in League to get back into a match.</summary>
    public bool NeedsAttention => Outcome == MatchOutcome.LeftQueue || Phase == GameflowPhase.Reconnect;

    /// <summary>Short tray text, or null when the phase is not known.</summary>
    public string? Describe() => (Phase, Outcome) switch
    {
        (GameflowPhase.Unknown, _) => null,
        (GameflowPhase.ChampSelect, MatchOutcome.Started) => "Match accepted — in champ select",
        (GameflowPhase.Matchmaking, MatchOutcome.Requeued) => "Match didn't start — back in queue",
        (GameflowPhase.Matchmaking, MatchOutcome.ChampSelectEnded) => "Champ select ended — back in queue",
        (GameflowPhase.Lobby or GameflowPhase.None, MatchOutcome.LeftQueue) => "Match missed — out of queue",
        (GameflowPhase.Lobby or GameflowPhase.None, MatchOutcome.ChampSelectEnded) => "Champ select ended — out of queue",
        (GameflowPhase.None, _) => "Not in a lobby",
        (GameflowPhase.Lobby, _) => "In lobby",
        (GameflowPhase.Matchmaking or GameflowPhase.CheckedIntoTournament, _) => "In queue",
        (GameflowPhase.ReadyCheck, _) => "Match found",
        (GameflowPhase.ChampSelect, _) => "In champ select",
        (GameflowPhase.GameStart, _) => "Game starting",
        (GameflowPhase.InProgress, _) => "In game",
        (GameflowPhase.Reconnect, _) => "Game running — reconnect in League",
        (GameflowPhase.WaitingForStats or GameflowPhase.PreEndOfGame or GameflowPhase.EndOfGame, _) => "Post-game",
        (GameflowPhase.FailedToLaunch or GameflowPhase.TerminatedInError, _) => "Game failed to start",
        _ => null
    };
}

/// <summary>
/// Follows gameflow phase changes for one League connection and remembers how the last
/// ready check or champ select ended. It only reports; it never acts in League.
/// </summary>
public sealed class MatchStatusTracker
{
    private readonly object gate = new();
    private MatchStatus current = MatchStatus.Unknown;

    public MatchStatus Current { get { lock (gate) { return current; } } }

    public void Update(GameflowPhase next)
    {
        lock (gate)
        {
            var previous = current.Phase;
            // An unrecognized phase must not erase what the player needs to see.
            if (next == previous || next == GameflowPhase.Unknown) { return; }
            var outcome = (previous, next) switch
            {
                (GameflowPhase.ReadyCheck, GameflowPhase.ChampSelect or GameflowPhase.GameStart or GameflowPhase.InProgress)
                    => MatchOutcome.Started,
                (GameflowPhase.ReadyCheck, GameflowPhase.Matchmaking) => MatchOutcome.Requeued,
                (GameflowPhase.ReadyCheck, GameflowPhase.Lobby or GameflowPhase.None) => MatchOutcome.LeftQueue,
                (GameflowPhase.ChampSelect, GameflowPhase.Matchmaking or GameflowPhase.Lobby or GameflowPhase.None)
                    => MatchOutcome.ChampSelectEnded,
                _ => MatchOutcome.None
            };
            current = new(next, outcome);
        }
    }

    /// <summary>Forgets everything, for example when League disconnects.</summary>
    public void Reset()
    {
        lock (gate) { current = MatchStatus.Unknown; }
    }
}
