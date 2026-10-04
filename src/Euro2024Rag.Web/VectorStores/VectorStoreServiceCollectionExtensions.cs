using CommunityToolkit.VectorData.InMemory;
using CommunityToolkit.VectorData.PgVector;
using CommunityToolkit.VectorData.Qdrant;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Rag;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Qdrant.Client;

namespace Euro2024Rag.Web.VectorStores;

public static class VectorStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the knowledge collection for the database chosen in <c>VectorStore:Provider</c>.
    /// This is the only place that knows about concrete databases: everything else depends on
    /// <see cref="VectorStoreCollection{TKey,TRecord}"/>, so switching databases is a configuration change.
    /// </summary>
    public static IServiceCollection AddKnowledgeVectorStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VectorStoreOptions>(configuration.GetSection(VectorStoreOptions.SectionName));

        services.AddSingleton<VectorStoreCollection<Guid, KnowledgeChunk>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value;
            return options.Provider switch
            {
                VectorStoreProvider.InMemory =>
                    new InMemoryCollection<Guid, KnowledgeChunk>(options.CollectionName),

                VectorStoreProvider.PgVector =>
                    new PostgresCollection<Guid, KnowledgeChunk>(
                        options.PgVector.ConnectionString,
                        options.CollectionName,
                        new PostgresCollectionOptions { Schema = options.PgVector.Schema }),

                VectorStoreProvider.Qdrant =>
                    new QdrantCollection<Guid, KnowledgeChunk>(
                        new QdrantClient(options.Qdrant.Host, options.Qdrant.Port, options.Qdrant.Https, NullIfEmpty(options.Qdrant.ApiKey)),
                        options.CollectionName,
                        ownsClient: true),

                VectorStoreProvider.Chroma =>
                    new ChromaKnowledgeCollection(options.Chroma, options.CollectionName),

                _ => throw new InvalidOperationException($"Unknown VectorStore:Provider '{options.Provider}'."),
            };
        });

        return services;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
