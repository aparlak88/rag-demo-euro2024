using System.Text.Json.Serialization;

namespace Euro2024Rag.Web.Ingestion;

[JsonConverter(typeof(JsonStringEnumConverter<IngestionStatus>))]
public enum IngestionStatus
{
    Pending,
    Running,
    Ready,
    Failed,
}

public sealed record IngestionSnapshot(
    IngestionStatus Status,
    string Message,
    int Matches = 0,
    int TotalChunks = 0,
    int EmbeddedChunks = 0,
    int CachedChunks = 0,
    IReadOnlyDictionary<string, int>? ChunksByKind = null,
    double? DurationSeconds = null);

/// <summary>Shared, thread-safe view of the indexing progress (read by /api/status).</summary>
public sealed class IngestionState
{
    private IngestionSnapshot _current = new(IngestionStatus.Pending, "Waiting to start indexing…");

    public IngestionSnapshot Current => Volatile.Read(ref _current);

    public bool IsReady => Current.Status == IngestionStatus.Ready;

    public void Update(IngestionSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}
