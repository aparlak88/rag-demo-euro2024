using System.ClientModel;
using System.ClientModel.Primitives;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Rag;
using Euro2024Rag.Web.Resilience;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using GeminiClient = Google.GenAI.Client;

namespace Euro2024Rag.Web.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the chat clients and the embedding generator for the providers chosen in <c>Ai:Provider</c>
    /// and <c>Ai:EmbeddingProvider</c>. This is the only place that knows about concrete AI SDKs: everything else
    /// depends on <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, so switching
    /// providers is a configuration change.
    /// </summary>
    public static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));

        // Two model chains, each an IChatClient with Polly retries + fallback:
        // the answer chain, and a cheaper one for query rewriting (keyed service).
        services.AddSingleton<IChatClient>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return CreateChatChain(sp, ai, ai.Chat.ChatModels, $"{AiOptions.SectionName}:{ai.Provider}:ChatModels");
        });
        services.AddKeyedSingleton<IChatClient>(RagService.RewriteClientKey, (sp, _) =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var models = ai.Chat.RewriteModels.Count > 0 ? ai.Chat.RewriteModels : ai.Chat.ChatModels;
            return CreateChatChain(sp, ai, models, $"{AiOptions.SectionName}:{ai.Provider}:RewriteModels");
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var settings = ai.Embedding;
            var model = settings.EmbeddingModel;
            var dimensions = KnowledgeChunk.EmbeddingDimensions;

            var generator = ai.ActiveEmbeddingProvider switch
            {
                // Gemini embeddings are asymmetric: documents and questions get different task types.
                AiProvider.Gemini => new TaskAwareEmbeddingGenerator(
                    CreateGeminiClient(ai.Gemini).AsIEmbeddingGenerator(model, dimensions),
                    model, settings.DocumentPrefix, settings.QueryPrefix,
                    (task, options) => options.RawRepresentationFactory = _ => new EmbedContentConfig
                    {
                        TaskType = task == EmbeddingTask.Document ? "RETRIEVAL_DOCUMENT" : "RETRIEVAL_QUERY",
                        OutputDimensionality = dimensions,
                    }),

                AiProvider.OpenAI => new TaskAwareEmbeddingGenerator(
                    CreateOpenAIClient(ai.OpenAI).GetEmbeddingClient(model).AsIEmbeddingGenerator(dimensions),
                    model, settings.DocumentPrefix, settings.QueryPrefix),

                AiProvider.Ollama => new TaskAwareEmbeddingGenerator(
                    new OllamaApiClient(new Uri(ai.Ollama.Endpoint), model),
                    model, settings.DocumentPrefix, settings.QueryPrefix),

                _ => throw new InvalidOperationException($"Unknown Ai:EmbeddingProvider '{ai.ActiveEmbeddingProvider}'."),
            };

            return new RetryingEmbeddingGenerator(
                generator,
                ai.Resilience.WithAttemptTimeout(settings.AttemptTimeout),
                sp.GetRequiredService<ILogger<RetryingEmbeddingGenerator>>());
        });

        return services;
    }

    /// <summary>Identifies the embedding space in the embedding cache: switching provider, model or prefix re-embeds.</summary>
    public static string EmbeddingCacheModelKey(this AiOptions ai) =>
        $"{ai.ActiveEmbeddingProvider}:{ai.Embedding.EmbeddingModel}" +
        (ai.Embedding.DocumentPrefix.Length > 0 ? $"|{ai.Embedding.DocumentPrefix}" : "");

    private static ResilientChatClient CreateChatChain(IServiceProvider sp, AiOptions ai, IReadOnlyList<string> modelIds, string settingName)
    {
        if (modelIds.Count == 0)
        {
            throw new InvalidOperationException($"{settingName} must list at least one model.");
        }

        var settings = ai.Chat;
        Func<string, IChatClient> createClient;
        switch (ai.Provider)
        {
            case AiProvider.Gemini:
                var gemini = CreateGeminiClient(ai.Gemini);
                createClient = id => gemini.AsIChatClient(id);
                break;
            case AiProvider.OpenAI:
                var openAi = CreateOpenAIClient(ai.OpenAI);
                createClient = id => openAi.GetChatClient(id).AsIChatClient();
                break;
            case AiProvider.Ollama:
                createClient = id => new OllamaApiClient(new Uri(ai.Ollama.Endpoint), id);
                break;
            default:
                throw new InvalidOperationException($"Unknown Ai:Provider '{ai.Provider}'.");
        }

        IChatClient Create(string id)
        {
            var client = createClient(id);
            return settings.SupportsTemperature
                ? client
                : client.AsBuilder().ConfigureOptions(options => options.Temperature = null).Build();
        }

        return new ResilientChatClient(
            modelIds.Select(id => new ChatModelEndpoint(id, Create(id))).ToList(),
            ai.Resilience.WithAttemptTimeout(settings.AttemptTimeout),
            sp.GetRequiredService<ILogger<ResilientChatClient>>());
    }

    private static GeminiClient CreateGeminiClient(GeminiProviderOptions options) => new(apiKey: options.ApiKey);

    private static OpenAIClient CreateOpenAIClient(OpenAIProviderOptions options)
    {
        var clientOptions = new OpenAIClientOptions
        {
            // Retries and fallback are done by Polly (ResilientChatClient, RetryingEmbeddingGenerator).
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            clientOptions.Endpoint = new Uri(options.Endpoint);
        }

        // Local OpenAI-compatible servers don't check the key, but the SDK requires a non-empty one.
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? "unused" : options.ApiKey;
        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
    }
}
