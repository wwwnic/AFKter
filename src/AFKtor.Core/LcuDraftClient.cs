using System.Collections.Immutable;
using System.Text.Json;

namespace AFKtor.Core;

public interface IDraftClient
{
    Task<DraftSnapshot?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Read-only, coherent HTTP snapshots; event payloads never establish admission.</summary>
public sealed class LcuDraftClient(LcuConnection connection, HttpClient client) : IDraftClient
{
    public async Task<DraftSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        var before = await ReadJsonAsync("/lol-gameflow/v1/session", cancellationToken).ConfigureAwait(false);
        var flow = ReadFlow(before);
        if (!flow.Draft) return null;
        var select = await ReadJsonAsync("/lol-champ-select/v1/session", cancellationToken).ConfigureAwait(false);
        var after = await ReadJsonAsync("/lol-gameflow/v1/session", cancellationToken).ConfigureAwait(false);
        if (flow != ReadFlow(after)) throw new InvalidDataException("Draft lifecycle changed during read.");
        return Parse(after, select);
    }

    private async Task<string> ReadJsonAsync(string route, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, connection.Endpoint(route));
        request.Headers.Authorization = connection.Authorization;
        using var response = await client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (body.Length > 1048576) throw new InvalidDataException("Draft response too large.");
        return body;
    }

    private static (bool Draft, long GameId, int QueueId) ReadFlow(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var phase = root.GetProperty("phase").GetString();
            if (string.IsNullOrEmpty(phase)) throw new InvalidDataException("Missing gameflow phase.");
            if (phase != "ChampSelect") return (false, 0, 0);
            var game = root.GetProperty("gameData");
            return (true, game.GetProperty("gameId").GetInt64(), game.GetProperty("queue").GetProperty("id").GetInt32());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Malformed gameflow state."); }
    }

    public static DraftSnapshot Parse(string gameflow, string championSelect)
    {
        try
        {
            var flow = ReadFlow(gameflow);
            using var document = JsonDocument.Parse(championSelect);
            var root = document.RootElement;
            var id = root.GetProperty("gameId").GetInt64();
            if (!flow.Draft || id <= 0 || id != flow.GameId) throw new InvalidDataException("Incoherent draft state.");
            var players = root.GetProperty("myTeam").EnumerateArray().Select(p => new DraftPlayer(
                p.GetProperty("cellId").GetInt64(), ParseRole(p.GetProperty("assignedPosition").GetString()),
                p.GetProperty("championId").GetInt32(), p.GetProperty("championPickIntent").GetInt32())).ToImmutableArray();
            var actions = root.GetProperty("actions").EnumerateArray().SelectMany(group => group.EnumerateArray())
                .Select(a => new DraftAction(a.GetProperty("id").GetInt64(), a.GetProperty("actorCellId").GetInt64(),
                    a.GetProperty("type").GetString() switch { "pick" => DraftActionKind.Pick, "ban" => DraftActionKind.Ban, _ => DraftActionKind.None },
                    a.GetProperty("championId").GetInt32(), a.GetProperty("isInProgress").GetBoolean(), a.GetProperty("completed").GetBoolean())).ToImmutableArray();
            if (players.Length > 10 || actions.Length > 50 || players.Select(p => p.CellId).Distinct().Count() != players.Length ||
                actions.Any(a => a.Id < 0 || a.Kind == DraftActionKind.None) || actions.Select(a => a.Id).Distinct().Count() != actions.Length)
                throw new InvalidDataException("Invalid draft members or actions.");
            var timer = root.GetProperty("timer");
            return new(id, flow.QueueId, root.GetProperty("localPlayerCellId").GetInt64(), players, actions,
                timer.GetProperty("adjustedTimeLeftInPhase").GetInt64(), timer.GetProperty("isInfinite").GetBoolean(),
                root.GetProperty("isCustomGame").GetBoolean(), root.GetProperty("isSpectating").GetBoolean());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Malformed draft state."); }
    }

    private static DraftRole? ParseRole(string? value) => value switch
    {
        "top" => DraftRole.Top, "jungle" => DraftRole.Jungle, "middle" => DraftRole.Middle,
        "bottom" => DraftRole.Bottom, "utility" => DraftRole.Support, _ => null
    };
}
