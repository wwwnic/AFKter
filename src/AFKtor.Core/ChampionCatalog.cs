using System.Collections.Immutable;
using System.Text.Json;

namespace AFKtor.Core;

public sealed record Champion(int Id, string Name)
{
    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>Riot Data Dragon en_US identity snapshot, updated with application releases.</summary>
public sealed class ChampionCatalog
{
    private readonly ImmutableDictionary<int, Champion> champions;
    public string Version { get; }
    public bool Available => champions.Count > 0;
    public IEnumerable<Champion> Champions => champions.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    private ChampionCatalog(string version, IEnumerable<Champion> values)
    { Version = version; champions = values.ToImmutableDictionary(c => c.Id); }
    public Champion? Find(int id) => champions.GetValueOrDefault(id);
    public static ChampionCatalog LoadBundled()
    {
        try
        {
            using var stream = typeof(ChampionCatalog).Assembly.GetManifestResourceStream("AFKtor.Core.Assets.champion.json");
            if (stream is null) return new("unavailable", []);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
        { return new("unavailable", []); }
    }
    public static ChampionCatalog Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = root.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(version) || version.Length > 64) throw new InvalidDataException("Invalid catalog version.");
            var values = root.GetProperty("data").EnumerateObject().Select(entry =>
            {
                var name = entry.Value.GetProperty("name").GetString();
                if (!int.TryParse(entry.Value.GetProperty("key").GetString(), out var id) || id <= 0 ||
                    string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new InvalidDataException("Invalid champion identity.");
                return new Champion(id, name);
            }).ToArray();
            if (values.Length is < 1 or > 1000 || values.Select(v => v.Id).Distinct().Count() != values.Length)
                throw new InvalidDataException("Invalid champion catalog.");
            return new(version!, values);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Invalid champion catalog.", e); }
    }
}
