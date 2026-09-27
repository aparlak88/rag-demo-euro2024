using Euro2024Rag.Web.Configuration;
using Google.GenAI;
using Microsoft.Extensions.AI;
using Polly;
using Polly.Retry;

namespace Euro2024Rag.Web.Resilience;

/// <summary>
/// Retries transient embedding failures. Deliberately has no model fallback: vectors produced by a
/// different embedding model live in a different vector space and cannot be compared with the index.
/// </summary>
public sealed class RetryingEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ResiliencePipeline _pipeline;

    public RetryingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner, ResilienceOptions options, ILogger<RetryingEmbeddingGenerator> logger)
        : base(inner)
    {
        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(GeminiErrors.IsTransient),
                MaxRetryAttempts = options.MaxRetryAttempts + 4,
                Delay = options.BaseDelay * 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                // Free-tier embedding quotas are per minute: a few seconds of backoff never clears a 429,
                // so wait 15s, 30s, 45s, then 60s — long enough for the quota window to reset.
                DelayGenerator = args => new ValueTask<TimeSpan?>(args.Outcome.Exception is ApiException { StatusCode: 429 }
                    ? TimeSpan.FromSeconds(Math.Min(15 * (args.AttemptNumber + 1), 60))
                    : null),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "Embedding request failed, retry {Attempt} in {Delay}",
                        args.AttemptNumber + 1, args.RetryDelay);
                    return default;
                },
            })
            .AddTimeout(options.AttemptTimeout)
            .Build();
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var valueList = values as IList<string> ?? values.ToList();
        return await _pipeline.ExecuteAsync(
            async ct => await base.GenerateAsync(valueList, options, ct), cancellationToken);
    }
}
