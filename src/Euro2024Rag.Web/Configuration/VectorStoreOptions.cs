namespace Euro2024Rag.Web.Configuration;

public enum VectorStoreProvider
{
    /// <summary>Process memory. No setup; rebuilt from the embedding cache on every start.</summary>
    InMemory,

    /// <summary>PostgreSQL with the pgvector extension.</summary>
    PgVector,

    /// <summary>Qdrant, over gRPC.</summary>
    Qdrant,

    /// <summary>Chroma, over its v2 REST API.</summary>
    Chroma,
}

public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    /// <summary>Which database holds the chunks. Only the matching section below is read.</summary>
    public VectorStoreProvider Provider { get; set; } = VectorStoreProvider.InMemory;

    /// <summary>Table (pgvector) or collection (Qdrant, Chroma) name.</summary>
    public string CollectionName { get; set; } = "euro2024";

    public PgVectorOptions PgVector { get; set; } = new();

    public QdrantOptions Qdrant { get; set; } = new();

    public ChromaOptions Chroma { get; set; } = new();
}

public sealed class PgVectorOptions
{
    /// <summary>Npgsql connection string. The database needs the extension: <c>CREATE EXTENSION vector;</c></summary>
    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=euro2024";

    /// <summary>Postgres schema the table is created in.</summary>
    public string Schema { get; set; } = "public";
}

public sealed class QdrantOptions
{
    public string Host { get; set; } = "localhost";

    /// <summary>gRPC port (6334), not the REST port (6333).</summary>
    public int Port { get; set; } = 6334;

    public bool Https { get; set; }

    /// <summary>Only needed for Qdrant Cloud or a server started with an API key.</summary>
    public string ApiKey { get; set; } = "";
}

public sealed class ChromaOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";

    public string Tenant { get; set; } = "default_tenant";

    public string Database { get; set; } = "default_database";
}
