using Microsoft.Extensions.VectorData;

namespace Euro2024Rag.Web.Rag;

/// <summary>One retrievable unit of knowledge, stored in the vector store.</summary>
public sealed class KnowledgeChunk
{
    /// <summary>gemini-embedding-001 produces 3072 dimensions; we ask for a truncated 768 (Matryoshka) vector.</summary>
    public const int EmbeddingDimensions = 768;

    [VectorStoreKey]
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
}
