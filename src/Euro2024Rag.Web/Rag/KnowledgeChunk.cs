using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.VectorData;

namespace Euro2024Rag.Web.Rag;

/// <summary>One retrievable unit of knowledge, stored in the vector store.</summary>
public sealed class KnowledgeChunk
{
    /// <summary>gemini-embedding-001 produces 3072 dimensions; we ask for a truncated 768 (Matryoshka) vector.</summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>
    /// Store key. A Guid rather than the readable <see cref="Id"/> because Qdrant only accepts Guid or ulong keys;
    /// it is derived from <see cref="Id"/>, so re-indexing overwrites the same records instead of duplicating them.
    /// </summary>
    [VectorStoreKey]
    public Guid Key { get; set; }

    /// <summary>Readable id, e.g. "team-spain" or "match-51". Shown in the UI and the logs.</summary>
    [VectorStoreData]
    public string Id { get; set; } = "";

    /// <summary>match | team | group | venue | leaderboard | tournament</summary>
    [VectorStoreData(IsIndexed = true)]
    public string Kind { get; set; } = "";

    [VectorStoreData]
    public string Title { get; set; } = "";

    /// <summary>The text that is embedded and shown to the model as context.</summary>
    [VectorStoreData]
    public string Content { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public List<string> Teams { get; set; } = [];

    [VectorStoreVector(EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>Deterministic, name-based Guid (RFC 9562 version 8): the same id always maps to the same key.</summary>
    public static Guid CreateKey(string id)
    {
        Span<byte> bytes = stackalloc byte[16];
        SHA256.HashData(Encoding.UTF8.GetBytes(id))[..16].CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}
