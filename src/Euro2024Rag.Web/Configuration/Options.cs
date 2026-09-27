namespace Euro2024Rag.Web.Configuration;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Google AI Studio API key. Keep it out of source control (see README).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Answer models, in fallback order: the first one is the primary, each next one is used when the
    /// previous stays unavailable (quota, outage, unknown model id…). Set in appsettings.json.
    /// (No default list here: the configuration binder appends to existing list items instead of replacing them.)
    /// </summary>
    public List<string> ChatModels { get; set; } = [];

    /// <summary>
    /// Models for query rewriting, in fallback order. A small, fast Flash-Lite model is enough for this
    /// step, and on the free tier it uses its own quota instead of the answer models'.
    /// </summary>
    public List<string> RewriteModels { get; set; } = [];

    /// <summary>Model used to turn documents and questions into vectors. Never falls back: vectors from different models are not comparable.</summary>
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    public ResilienceOptions Resilience { get; set; } = new();
}

public sealed class ResilienceOptions
{
    /// <summary>Retries per model for transient errors (429, 5xx, timeouts).</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>First retry delay; grows exponentially with jitter.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Max time for a single attempt. For streaming this is the time to the first token.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>CSV exported from Kaggle (thamersekhri/euro-2024-matches).</summary>
    public string DataFile { get; set; } = "Data/Euro_2024_Matches.csv";

    /// <summary>Hand-curated facts that the CSV does not contain (stages, groups, cities, extra time / penalties).</summary>
    public string ContextFile { get; set; } = "Data/tournament-context.json";

    /// <summary>Embeddings are cached on disk so restarts don't re-embed unchanged documents.</summary>
    public string EmbeddingCacheFile { get; set; } = "App_Data/embedding-cache.json";

    /// <summary>How many chunks to retrieve per question.</summary>
    public int TopK { get; set; } = 8;

    /// <summary>Chunks with a cosine similarity below this value are dropped.</summary>
    public double MinScore { get; set; } = 0.5;

    /// <summary>When to rewrite the question into a standalone English search query before retrieval.</summary>
    public QueryRewriteMode QueryRewrite { get; set; } = QueryRewriteMode.FollowUps;

    /// <summary>How many previous chat messages are sent to the model.</summary>
    public int MaxHistoryMessages { get; set; } = 6;

    /// <summary>Documents per embedding request.</summary>
    public int EmbeddingBatchSize { get; set; } = 25;
}

public enum QueryRewriteMode
{
    /// <summary>Always search with the user's own words.</summary>
    Off,

    /// <summary>
    /// Rewrite only when there is chat history. A first question is already self-contained, and Gemini
    /// embeddings are multilingual, so a Turkish question retrieves English chunks just fine.
    /// Saves one LLM call per new conversation.
    /// </summary>
    FollowUps,

    /// <summary>Rewrite every question (also normalizes language and wording).</summary>
    Always,
}
