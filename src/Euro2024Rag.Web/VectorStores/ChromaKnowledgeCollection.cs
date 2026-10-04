using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Euro2024Rag.Web.VectorStores;

/// <summary>
/// <see cref="VectorStoreCollection{TKey,TRecord}"/> for Chroma, over its v2 REST API.
/// There is no Microsoft.Extensions.VectorData provider for Chroma (the Semantic Kernel Chroma package
/// only implements the legacy memory-store API), so this is a small one written for <see cref="KnowledgeChunk"/>.
/// Because it implements the same abstract class as the pgvector and Qdrant providers, RagService and
/// IngestionService use it without knowing which database is behind it.
/// <para>
/// Mapping: the Guid key is the Chroma id, <see cref="KnowledgeChunk.Content"/> is the Chroma document,
/// the other fields are metadata. The collection uses cosine distance; scores are returned as similarity (1 − distance),
/// the same scale the other providers use with <see cref="DistanceFunction.CosineSimilarity"/>.
/// </para>
/// </summary>
public sealed class ChromaKnowledgeCollection : VectorStoreCollection<Guid, KnowledgeChunk>
{
    private const string TeamSeparator = "|";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _collectionsPath;
    private readonly string _database;
    private string? _collectionId;

    public ChromaKnowledgeCollection(ChromaOptions options, string name)
    {
        Name = name;
        _database = options.Database;
        _http = new HttpClient { BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/") };
        _collectionsPath = $"api/v2/tenants/{Uri.EscapeDataString(options.Tenant)}/databases/{Uri.EscapeDataString(options.Database)}/collections";
    }

    public override string Name { get; }

    // ---------- Collection ------------------------------------------------------------------------

    public override async Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) =>
        await FindCollectionIdAsync(cancellationToken) is not null;

    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(_collectionsPath, new CreateCollectionRequest(
            Name,
            GetOrCreate: true,
            Configuration: new CollectionConfiguration(new HnswConfiguration("cosine"))), Json, cancellationToken);
        _collectionId = (await ReadAsync<CollectionResponse>(response, "create collection", cancellationToken)).Id;
    }

    public override async Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"{_collectionsPath}/{Uri.EscapeDataString(Name)}", cancellationToken);
        _collectionId = null;
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, "delete collection", cancellationToken);
        }
    }

    // ---------- Records -----------------------------------------------------------------------------

    public override async Task UpsertAsync(KnowledgeChunk record, CancellationToken cancellationToken = default) =>
        await UpsertAsync([record], cancellationToken);

    public override async Task UpsertAsync(IEnumerable<KnowledgeChunk> records, CancellationToken cancellationToken = default)
    {
        var batch = records.ToList();
        if (batch.Count == 0)
        {
            return;
        }

        var request = new UpsertRequest(
            Ids: batch.Select(r => r.Key.ToString()).ToList(),
            Embeddings: batch.Select(r => r.Embedding.ToArray()).ToList(),
            Documents: batch.Select(r => r.Content).ToList(),
            Metadatas: batch.Select(ToMetadata).ToList());

        using var response = await PostRecordsAsync("upsert", request, cancellationToken);
    }

    public override async Task<KnowledgeChunk?> GetAsync(Guid key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        await foreach (var record in GetAsync([key], options, cancellationToken))
        {
            return record;
        }

        return null;
    }

    public override async IAsyncEnumerable<KnowledgeChunk> GetAsync(
        IEnumerable<Guid> keys, RecordRetrievalOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var include = Include(options?.IncludeVectors == true, distances: false);
        using var response = await PostRecordsAsync("get", new GetRequest(keys.Select(k => k.ToString()).ToList(), include), cancellationToken);
        var result = await ReadAsync<GetResponse>(response, "get", cancellationToken);

        for (var i = 0; i < result.Ids.Count; i++)
        {
            yield return ToRecord(result.Ids[i], result.Documents?[i], result.Metadatas?[i], result.Embeddings?[i]);
        }
    }

    public override IAsyncEnumerable<KnowledgeChunk> GetAsync(
        Expression<Func<KnowledgeChunk, bool>> filter, int top, FilteredRecordRetrievalOptions<KnowledgeChunk>? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Filtered retrieval is not implemented for Chroma in this demo; the RAG pipeline only needs vector search.");

    public override async Task DeleteAsync(Guid key, CancellationToken cancellationToken = default) =>
        await DeleteAsync([key], cancellationToken);

    public override async Task DeleteAsync(IEnumerable<Guid> keys, CancellationToken cancellationToken = default)
    {
        using var response = await PostRecordsAsync("delete", new DeleteRequest(keys.Select(k => k.ToString()).ToList()), cancellationToken);
    }

    // ---------- Vector search -----------------------------------------------------------------------

    public override async IAsyncEnumerable<VectorSearchResult<KnowledgeChunk>> SearchAsync<TInput>(
        TInput searchValue, int top, VectorSearchOptions<KnowledgeChunk>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options?.Filter is not null)
        {
            throw new NotSupportedException("Filtered search is not implemented for Chroma in this demo.");
        }

        float[] vector = searchValue switch
        {
            ReadOnlyMemory<float> memory => memory.ToArray(),
            float[] array => array,
            Embedding<float> embedding => embedding.Vector.ToArray(),
            _ => throw new NotSupportedException(
                $"Search input of type {typeof(TInput).Name} is not supported; pass an embedding (ReadOnlyMemory<float>, float[] or Embedding<float>)."),
        };

        var skip = options?.Skip ?? 0;
        var request = new QueryRequest([vector], top + skip, Include(options?.IncludeVectors == true, distances: true));
        using var response = await PostRecordsAsync("query", request, cancellationToken);
        var result = await ReadAsync<QueryResponse>(response, "query", cancellationToken);

        // Chroma answers a batch of queries; we sent one, so read row 0.
        var ids = result.Ids[0];
        for (var i = skip; i < ids.Count; i++)
        {
            var record = ToRecord(ids[i], result.Documents?[0][i], result.Metadatas?[0][i], result.Embeddings?[0][i]);
            double? score = result.Distances?[0][i] is { } distance ? 1 - distance : null;
            yield return new VectorSearchResult<KnowledgeChunk>(record, score);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(VectorStoreCollectionMetadata)
            ? new VectorStoreCollectionMetadata { VectorStoreSystemName = "chroma", VectorStoreName = _database, CollectionName = Name }
            : serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------- Mapping -----------------------------------------------------------------------------

    private static Dictionary<string, object> ToMetadata(KnowledgeChunk chunk) => new()
    {
        ["id"] = chunk.Id,
        ["kind"] = chunk.Kind,
        ["title"] = chunk.Title,
        // Chroma metadata values are scalars, so the team list is stored as one string.
        ["teams"] = string.Join(TeamSeparator, chunk.Teams),
    };

    private static KnowledgeChunk ToRecord(string id, string? document, Dictionary<string, JsonElement>? metadata, float[]? embedding)
    {
        string Text(string field) => metadata is not null && metadata.TryGetValue(field, out var value) ? value.GetString() ?? "" : "";

        return new KnowledgeChunk
        {
            Key = Guid.Parse(id),
            Id = Text("id"),
            Kind = Text("kind"),
            Title = Text("title"),
            Content = document ?? "",
            Teams = [.. Text("teams").Split(TeamSeparator, StringSplitOptions.RemoveEmptyEntries)],
            Embedding = embedding ?? ReadOnlyMemory<float>.Empty,
        };
    }

    private static List<string> Include(bool vectors, bool distances) =>
    [
        "documents",
        "metadatas",
        .. vectors ? ["embeddings"] : Array.Empty<string>(),
        .. distances ? ["distances"] : Array.Empty<string>(),
    ];

    // ---------- HTTP --------------------------------------------------------------------------------

    /// <summary>Record operations address the collection by its server-side id, which is looked up once by name.</summary>
    private async Task<HttpResponseMessage> PostRecordsAsync<TRequest>(string operation, TRequest request, CancellationToken cancellationToken)
    {
        var collectionId = await FindCollectionIdAsync(cancellationToken)
            ?? throw new VectorStoreException($"Chroma collection '{Name}' does not exist; call EnsureCollectionExistsAsync first.")
            {
                VectorStoreSystemName = "chroma",
                CollectionName = Name,
            };

        var response = await _http.PostAsJsonAsync($"{_collectionsPath}/{collectionId}/{operation}", request, Json, cancellationToken);
        await EnsureSuccessAsync(response, operation, cancellationToken);
        return response;
    }

    private async Task<string?> FindCollectionIdAsync(CancellationToken cancellationToken)
    {
        if (_collectionId is not null)
        {
            return _collectionId;
        }

        using var response = await _http.GetAsync($"{_collectionsPath}/{Uri.EscapeDataString(Name)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return _collectionId = (await ReadAsync<CollectionResponse>(response, "get collection", cancellationToken)).Id;
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, operation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new VectorStoreException($"Chroma returned an empty response to '{operation}'.");
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new VectorStoreException($"Chroma '{operation}' on collection '{Name}' failed with {(int)response.StatusCode}: {body}")
            {
                VectorStoreSystemName = "chroma",
                CollectionName = Name,
                OperationName = operation,
            };
        }
    }

    // ---------- Wire format (snake_case) ------------------------------------------------------------

    private sealed record CreateCollectionRequest(string Name, bool GetOrCreate, CollectionConfiguration Configuration);

    private sealed record CollectionConfiguration(HnswConfiguration Hnsw);

    private sealed record HnswConfiguration(string Space);

    private sealed record CollectionResponse(string Id, string Name);

    private sealed record UpsertRequest(List<string> Ids, List<float[]> Embeddings, List<string> Documents, List<Dictionary<string, object>> Metadatas);

    private sealed record GetRequest(List<string> Ids, List<string> Include);

    private sealed record DeleteRequest(List<string> Ids);

    private sealed record QueryRequest(List<float[]> QueryEmbeddings, int NResults, List<string> Include);

    private sealed record GetResponse(
        List<string> Ids, List<string?>? Documents, List<Dictionary<string, JsonElement>?>? Metadatas, List<float[]?>? Embeddings);

    private sealed record QueryResponse(
        List<List<string>> Ids,
        List<List<string?>>? Documents,
        List<List<Dictionary<string, JsonElement>?>>? Metadatas,
        List<List<float[]?>>? Embeddings,
        List<List<double?>>? Distances);
}
