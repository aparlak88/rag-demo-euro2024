using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Euro2024Rag.Web.Ingestion;

/// <summary>
/// Disk cache of document embeddings, keyed by a hash of (model, dimensions, text).
/// The index is rebuilt on every start (the in-memory store is empty, and switching to another vector store starts
/// from an empty collection); this avoids paying for the same embeddings again.
/// Changing a document's text or the embedding model automatically invalidates its entry.
/// </summary>
public sealed class EmbeddingCache(string path, string model, int dimensions)
{
    private Dictionary<string, float[]> _entries = [];

    public int Count => _entries.Count;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await using var stream = File.OpenRead(path);
        _entries = await JsonSerializer.DeserializeAsync<Dictionary<string, float[]>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    public async Task SaveAsync(IEnumerable<string> liveTexts, CancellationToken cancellationToken)
    {
        // Drop entries for documents that no longer exist.
        var live = liveTexts.Select(KeyFor).ToHashSet();
        var pruned = _entries.Where(e => live.Contains(e.Key)).ToDictionary();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, pruned, cancellationToken: cancellationToken);
    }

    public bool TryGet(string text, out ReadOnlyMemory<float> embedding)
    {
        var found = _entries.TryGetValue(KeyFor(text), out var vector);
        embedding = vector;
        return found;
    }

    public void Set(string text, ReadOnlyMemory<float> embedding) => _entries[KeyFor(text)] = embedding.ToArray();

    private string KeyFor(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{model}|{dimensions}|{text}")));
}
