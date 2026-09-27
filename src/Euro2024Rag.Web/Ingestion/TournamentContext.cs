using System.Text.Json;

namespace Euro2024Rag.Web.Ingestion;

/// <summary>Facts the CSV doesn't contain, loaded from Data/tournament-context.json.</summary>
public sealed record TournamentContext(
    IReadOnlyList<StageRange> Stages,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Groups,
    IReadOnlyDictionary<string, StadiumInfo> Stadiums,
    IReadOnlyDictionary<int, KnockoutDetail> KnockoutDetails)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TournamentContext Load(string path) =>
        JsonSerializer.Deserialize<TournamentContext>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Could not read tournament context from {path}.");

    public string StageOf(int matchNumber) =>
        Stages.FirstOrDefault(s => matchNumber >= s.FirstMatch && matchNumber <= s.LastMatch)?.Name
        ?? throw new InvalidDataException($"Match {matchNumber} is outside every configured stage.");

    public string? GroupOf(string team) =>
        Groups.FirstOrDefault(g => g.Value.Contains(team)).Key;
}

public sealed record StageRange(string Name, int FirstMatch, int LastMatch);

public sealed record StadiumInfo(string Name, string City);
