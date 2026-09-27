using Google.GenAI.Types;
using Microsoft.Extensions.AI;

namespace Euro2024Rag.Web.Rag;

/// <summary>
/// Gemini embeddings are asymmetric: documents and queries are embedded with different task types,
/// which noticeably improves retrieval quality. Microsoft.Extensions.AI has no "task type" concept,
/// so the Gemini-specific config is passed through <see cref="EmbeddingGenerationOptions.RawRepresentationFactory"/>.
/// </summary>
public static class GeminiEmbeddingOptions
{
    public static EmbeddingGenerationOptions ForDocuments { get; } = Create("RETRIEVAL_DOCUMENT");

    public static EmbeddingGenerationOptions ForQuery { get; } = Create("RETRIEVAL_QUERY");

    private static EmbeddingGenerationOptions Create(string taskType) => new()
    {
        Dimensions = KnowledgeChunk.EmbeddingDimensions,
        RawRepresentationFactory = _ => new EmbedContentConfig
        {
            TaskType = taskType,
            OutputDimensionality = KnowledgeChunk.EmbeddingDimensions,
        },
    };
}
