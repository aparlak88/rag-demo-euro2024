using Microsoft.Extensions.AI;

namespace Euro2024Rag.Tests.Fakes;

/// <summary>Records what it was asked to embed and returns vectors of a configurable length.</summary>
public sealed class FakeEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string> ReceivedValues { get; } = [];

    public EmbeddingGenerationOptions? ReceivedOptions { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.ToList();
        ReceivedValues.AddRange(list);
        ReceivedOptions = options;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            list.Select(_ => new Embedding<float>(new float[dimensions])).ToList()));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
