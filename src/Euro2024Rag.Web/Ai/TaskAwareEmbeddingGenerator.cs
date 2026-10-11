using Euro2024Rag.Web.Rag;
using Microsoft.Extensions.AI;

namespace Euro2024Rag.Web.Ai;

/// <summary>
/// Applies the <see cref="EmbeddingTask"/> mark set by the RAG code in the way the provider understands it:
/// a text prefix (open models) and/or provider-specific request options (Gemini task types). The mark itself is
/// removed, so providers never see an unknown option. Also checks that the model returned the dimensions the
/// vector store was created with, which fails much more clearly than an insert error from the database.
/// </summary>
public sealed class TaskAwareEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    string modelId,
    string documentPrefix,
    string queryPrefix,
    Action<EmbeddingTask, EmbeddingGenerationOptions>? configure = null)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var task = EmbeddingTasks.GetTask(options);
        if (task is { } t)
        {
            var prefix = t == EmbeddingTask.Document ? documentPrefix : queryPrefix;
            if (prefix.Length > 0)
            {
                values = values.Select(v => prefix + v);
            }

            options = options!.Clone();
            options.AdditionalProperties = null;
            configure?.Invoke(t, options);
        }

        var embeddings = await base.GenerateAsync(values, options, cancellationToken);

        var expected = options?.Dimensions;
        var wrong = embeddings.FirstOrDefault(e => expected is { } d && e.Vector.Length != d);
        if (wrong is not null)
        {
            throw new InvalidOperationException(
                $"Embedding model '{modelId}' returned {wrong.Vector.Length} dimensions, but the index uses {expected}. " +
                $"Use a model that outputs (or can truncate to) {expected} dimensions.");
        }

        return embeddings;
    }
}
