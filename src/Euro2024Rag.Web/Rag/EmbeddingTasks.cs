using Microsoft.Extensions.AI;

namespace Euro2024Rag.Web.Rag;

public enum EmbeddingTask
{
    Document,
    Query,
}

/// <summary>
/// Retrieval embeddings work best when documents and questions are embedded differently: Gemini has task types
/// (RETRIEVAL_DOCUMENT / RETRIEVAL_QUERY), open models like nomic-embed-text or embeddinggemma expect text prefixes.
/// Microsoft.Extensions.AI has no such concept, so the RAG code only marks what is being embedded, and
/// <c>TaskAwareEmbeddingGenerator</c> turns the mark into whatever the active provider understands.
/// </summary>
public static class EmbeddingTasks
{
    private const string TaskKey = "euro2024.embedding_task";

    public static EmbeddingGenerationOptions ForDocuments { get; } = Create(EmbeddingTask.Document);

    public static EmbeddingGenerationOptions ForQuery { get; } = Create(EmbeddingTask.Query);

    public static EmbeddingTask? GetTask(EmbeddingGenerationOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(TaskKey, out var value) == true && value is EmbeddingTask task ? task : null;

    private static EmbeddingGenerationOptions Create(EmbeddingTask task) => new()
    {
        Dimensions = KnowledgeChunk.EmbeddingDimensions,
        AdditionalProperties = new() { [TaskKey] = task },
    };
}
