namespace Euro2024Rag.Web.Configuration;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>CSV exported from Kaggle (thamersekhri/euro-2024-matches).</summary>
    public string DataFile { get; set; } = "Data/Euro_2024_Matches.csv";

    /// <summary>Hand-curated facts that the CSV does not contain (stages, groups, cities, extra time / penalties).</summary>
    public string ContextFile { get; set; } = "Data/tournament-context.json";

    /// <summary>
    /// Embeddings are cached on disk so restarts don't re-embed unchanged documents.
    /// The embedding model is added to the file name, one file per model.
    /// </summary>
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
    /// Rewrite only when there is chat history. A first question is already self-contained, and multilingual
    /// embedding models (Gemini, OpenAI, embeddinggemma) handle it, so a Turkish question retrieves English chunks just fine.
    /// Saves one LLM call per new conversation.
    /// </summary>
    FollowUps,

    /// <summary>Rewrite every question (also normalizes language and wording).</summary>
    Always,
}
