namespace Euro2024Rag.Web.Configuration;

public enum AiProvider
{
    /// <summary>Google Gemini API (AI Studio key), via the Google.GenAI SDK.</summary>
    Gemini,

    /// <summary>OpenAI API, or any OpenAI-compatible server when <see cref="OpenAIProviderOptions.Endpoint"/> is set.</summary>
    OpenAI,

    /// <summary>Local models served by Ollama, via OllamaSharp.</summary>
    Ollama,
}

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Which provider answers and rewrites questions. Only the matching section below is read.</summary>
    public AiProvider Provider { get; set; } = AiProvider.Gemini;

    /// <summary>
    /// Which provider embeds documents and questions. Empty means the same as <see cref="Provider"/>.
    /// Set it to mix providers, e.g. a local Ollama chat model with Gemini embeddings.
    /// </summary>
    public AiProvider? EmbeddingProvider { get; set; }

    public AiProvider ActiveEmbeddingProvider => EmbeddingProvider ?? Provider;

    public ResilienceOptions Resilience { get; set; } = new();

    public GeminiProviderOptions Gemini { get; set; } = new();

    public OpenAIProviderOptions OpenAI { get; set; } = new();

    public OllamaProviderOptions Ollama { get; set; } = new();

    public AiProviderOptions For(AiProvider provider) => provider switch
    {
        AiProvider.Gemini => Gemini,
        AiProvider.OpenAI => OpenAI,
        AiProvider.Ollama => Ollama,
        _ => throw new InvalidOperationException($"Unknown AI provider '{provider}'."),
    };

    public AiProviderOptions Chat => For(Provider);

    public AiProviderOptions Embedding => For(ActiveEmbeddingProvider);

    /// <summary>Why the configured providers can't be used (e.g. a missing API key), or null when they can.</summary>
    public string? GetConfigurationError()
    {
        foreach (var (provider, setting) in new[] { (Provider, "Provider"), (ActiveEmbeddingProvider, "EmbeddingProvider") })
        {
            if (For(provider).GetConfigurationError() is { } error)
            {
                // Point at the way out: every provider is optional, Ollama needs no account at all.
                var others = Enum.GetValues<AiProvider>().Where(p => p != provider).Select(p => p == AiProvider.Ollama ? "Ollama (local, no account)" : p.ToString());
                return $"{error} Or set {SectionName}:{setting} to another provider: {string.Join(" or ", others)}. See README.";
            }
        }

        if (Chat.ChatModels.Count == 0)
        {
            return $"{SectionName}:{Provider}:ChatModels must list at least one model.";
        }

        return string.IsNullOrWhiteSpace(Embedding.EmbeddingModel)
            ? $"{SectionName}:{ActiveEmbeddingProvider}:EmbeddingModel is not set."
            : null;
    }
}

/// <summary>Settings every provider has. Provider sections add their connection settings.</summary>
public abstract class AiProviderOptions
{
    /// <summary>
    /// Answer models, in fallback order: the first one is the primary, each next one is used when the
    /// previous stays unavailable (quota, outage, unknown model id…). Set in appsettings.json.
    /// (No default list here: the configuration binder appends to existing list items instead of replacing them.)
    /// </summary>
    public List<string> ChatModels { get; set; } = [];

    /// <summary>
    /// Models for query rewriting, in fallback order. A small, fast model is enough for this step.
    /// Empty means the chat models are used.
    /// </summary>
    public List<string> RewriteModels { get; set; } = [];

    /// <summary>
    /// Model used to turn documents and questions into vectors. Never falls back: vectors from different models are not comparable.
    /// It must return (or be able to truncate to) <c>KnowledgeChunk.EmbeddingDimensions</c> dimensions.
    /// </summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>
    /// Text put in front of every document before embedding. Some open embedding models are trained with
    /// task prefixes (e.g. nomic-embed-text: "search_document: ") and retrieve noticeably better with them.
    /// </summary>
    public string DocumentPrefix { get; set; } = "";

    /// <summary>Text put in front of every question before embedding (e.g. nomic-embed-text: "search_query: ").</summary>
    public string QueryPrefix { get; set; } = "";

    /// <summary>
    /// False for models that reject a custom temperature (OpenAI reasoning models such as gpt-5.x and o-series
    /// only accept the default); the request then leaves it out.
    /// </summary>
    public bool SupportsTemperature { get; set; } = true;

    /// <summary>
    /// Overrides <c>Rag:EmbeddingBatchSize</c> for this provider. A local model on a CPU embeds a few documents per
    /// minute, so a large batch would never finish within <see cref="AttemptTimeout"/> and be retried forever.
    /// </summary>
    public int? EmbeddingBatchSize { get; set; }

    /// <summary>Overrides <see cref="ResilienceOptions.AttemptTimeout"/>, e.g. for local models that need time to load.</summary>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>
    /// Overrides <c>Rag:MinScore</c> when this provider embeds. Every embedding model has its own score scale:
    /// Gemini scores relevant chunks around 0.6–0.75, text-embedding-3-small around 0.4–0.65 and embeddinggemma
    /// around 0.35–0.6, so one threshold tuned on Gemini would drop most of the relevant chunks for the others.
    /// </summary>
    public double? MinScore { get; set; }

    public virtual string? GetConfigurationError() => null;

    /// <summary>Empty, or still the placeholder from appsettings.Development.example.json.</summary>
    protected static bool IsMissing(string apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("paste-your-", StringComparison.Ordinal);
}

public sealed class GeminiProviderOptions : AiProviderOptions
{
    /// <summary>Google AI Studio API key. Keep it out of source control (see README).</summary>
    public string ApiKey { get; set; } = "";

    public override string? GetConfigurationError() => IsMissing(ApiKey)
        ? "Gemini API key is missing: set Ai:Gemini:ApiKey in appsettings.Development.json."
        : null;
}

public sealed class OpenAIProviderOptions : AiProviderOptions
{
    /// <summary>OpenAI API key. Keep it out of source control (see README).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Empty for api.openai.com. Set it to use another OpenAI-compatible API, e.g. Azure OpenAI
    /// (<c>https://{resource}.openai.azure.com/openai/v1/</c>), LM Studio or vLLM.
    /// </summary>
    public string Endpoint { get; set; } = "";

    public override string? GetConfigurationError() =>
        IsMissing(ApiKey) && string.IsNullOrWhiteSpace(Endpoint)
            ? "OpenAI API key is missing: set Ai:OpenAI:ApiKey in appsettings.Development.json."
            : null;
}

public sealed class OllamaProviderOptions : AiProviderOptions
{
    /// <summary>Ollama server. Models must be pulled first: <c>ollama pull &lt;model&gt;</c>.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    public override string? GetConfigurationError() => Uri.TryCreate(Endpoint, UriKind.Absolute, out _)
        ? null
        : $"Ai:Ollama:Endpoint '{Endpoint}' is not a valid URL.";
}

public sealed class ResilienceOptions
{
    /// <summary>Retries per model for transient errors (429, 5xx, timeouts).</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>First retry delay; grows exponentially with jitter.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Max time for a single attempt. For streaming this is the time to the first token.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public ResilienceOptions WithAttemptTimeout(TimeSpan? attemptTimeout) => attemptTimeout is { } timeout
        ? new ResilienceOptions { MaxRetryAttempts = MaxRetryAttempts, BaseDelay = BaseDelay, AttemptTimeout = timeout }
        : this;
}
