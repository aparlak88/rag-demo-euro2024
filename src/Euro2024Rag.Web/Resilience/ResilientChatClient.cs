using System.Runtime.CompilerServices;
using Euro2024Rag.Web.Configuration;
using Microsoft.Extensions.AI;
using Polly;
using Polly.Fallback;
using Polly.Retry;

namespace Euro2024Rag.Web.Resilience;

/// <summary>A model id and the client that calls it.</summary>
public sealed record ChatModelEndpoint(string ModelId, IChatClient Client);

/// <summary>
/// An <see cref="IChatClient"/> over an ordered chain of models: it retries transient failures and,
/// when a model stays unavailable, falls back to the next one. The Gemini SDK only offers same-model
/// HTTP retries, so the chain is built with Polly — one pipeline per model:
///
///   model[0]:  Fallback (→ model[1] pipeline)
///                └─ Retry (exponential backoff + jitter)
///                     └─ Timeout (per attempt; for streaming: time to first token)
///   model[1]:  Fallback (→ model[2] pipeline) └─ Retry └─ Timeout
///   …
///   model[n]:  Retry └─ Timeout                      (last model: nothing left to fall back to)
///
/// On the Gemini free tier each model has its own daily quota, so every model in the chain adds capacity.
/// Streaming can only fall back before the first text has been sent to the caller, so the pipeline wraps
/// "open the stream and read up to the first update with visible content". Thinking models may send
/// empty or reasoning-only chunks first; those are buffered, not treated as a successful start.
/// </summary>
public sealed class ResilientChatClient : IChatClient
{
    private static readonly ResiliencePropertyKey<Delegate> OperationKey = new("chat-operation");

    private readonly IReadOnlyList<ChatModelEndpoint> _models;
    private readonly ResilienceOptions _options;
    private readonly ILogger<ResilientChatClient> _logger;
    private readonly ResiliencePipeline<ChatResponse>[] _responseChain;
    private readonly ResiliencePipeline<StreamStart>[] _streamChain;

    public ResilientChatClient(IReadOnlyList<ChatModelEndpoint> models, ResilienceOptions options, ILogger<ResilientChatClient> logger)
    {
        if (models.Count == 0)
        {
            throw new ArgumentException("At least one chat model is required.", nameof(models));
        }

        _models = models;
        _options = options;
        _logger = logger;
        _responseChain = BuildChain<ChatResponse>(withTimeout: true);

        // No Polly timeout for streaming: the stream outlives the pipeline execution, so the
        // first-token timeout is enforced by StartStreamAsync with its own token source.
        _streamChain = BuildChain<StreamStart>(withTimeout: false);
    }

    public IReadOnlyList<string> ModelIds => _models.Select(m => m.ModelId).ToList();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        return await ExecuteAsync(_responseChain, 0,
            (model, ct) => GetResponseFromAsync(model, messageList, options, ct), cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();

        // The stream is bound to the caller's token, not to Polly's pooled per-execution token.
        var start = await ExecuteAsync(_streamChain, 0,
            (model, _) => StartStreamAsync(model, messageList, options, cancellationToken), cancellationToken);

        await using (start)
        {
            foreach (var buffered in start.Buffered)
            {
                yield return buffered;
            }

            while (await start.Enumerator.MoveNextAsync())
            {
                var update = start.Enumerator.Current;
                update.ModelId ??= start.ModelId;
                yield return update;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _models[0].Client.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        foreach (var model in _models)
        {
            model.Client.Dispose();
        }
    }

    /// <summary>Runs <paramref name="operation"/> against model <paramref name="index"/> through its pipeline.</summary>
    private async ValueTask<T> ExecuteAsync<T>(
        ResiliencePipeline<T>[] chain, int index, Func<ChatModelEndpoint, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        try
        {
            // The fallback strategy reads the operation from the context to replay it on the next model.
            context.Properties.Set(OperationKey, operation);
            return await chain[index].ExecuteAsync(
                static (ctx, state) => state.operation(state.model, ctx.CancellationToken),
                context,
                (operation, model: _models[index]));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private ResiliencePipeline<T>[] BuildChain<T>(bool withTimeout)
    {
        var chain = new ResiliencePipeline<T>[_models.Count];
        for (var i = _models.Count - 1; i >= 0; i--)
        {
            var builder = new ResiliencePipelineBuilder<T>();
            if (i < _models.Count - 1)
            {
                builder.AddFallback(CreateFallback(chain, i));
            }

            builder.AddRetry(CreateRetry<T>(_models[i].ModelId));
            if (withTimeout)
            {
                builder.AddTimeout(_options.AttemptTimeout);
            }

            chain[i] = builder.Build();
        }

        return chain;
    }

    private FallbackStrategyOptions<T> CreateFallback<T>(ResiliencePipeline<T>[] chain, int index) => new()
    {
        ShouldHandle = new PredicateBuilder<T>().Handle<Exception>(GeminiErrors.ShouldFallback),
        FallbackAction = async args =>
        {
            var operation = (Func<ChatModelEndpoint, CancellationToken, ValueTask<T>>)args.Context.Properties.GetValue(OperationKey, null!);
            return Outcome.FromResult(await ExecuteAsync(chain, index + 1, operation, args.Context.CancellationToken));
        },
        OnFallback = args =>
        {
            _logger.LogWarning("Chat model {Model} unavailable ({Reason}), falling back to {Next}",
                _models[index].ModelId, Describe(args.Outcome.Exception), _models[index + 1].ModelId);
            return default;
        },
    };

    private RetryStrategyOptions<T> CreateRetry<T>(string modelId) => new()
    {
        ShouldHandle = new PredicateBuilder<T>().Handle<Exception>(GeminiErrors.IsTransient),
        MaxRetryAttempts = _options.MaxRetryAttempts,
        Delay = _options.BaseDelay,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        OnRetry = args =>
        {
            _logger.LogWarning("Chat model {Model} failed ({Reason}), retry {Attempt}/{Max} in {Delay}",
                modelId, Describe(args.Outcome.Exception), args.AttemptNumber + 1, _options.MaxRetryAttempts, args.RetryDelay);
            return default;
        },
    };

    private static string Describe(Exception? exception) => exception switch
    {
        null => "unknown",
        _ when GeminiErrors.IsDailyQuotaExhausted(exception) => "daily quota exhausted",
        Google.GenAI.ApiException api => $"HTTP {api.StatusCode}",
        _ => exception.GetType().Name,
    };

    private static async ValueTask<ChatResponse> GetResponseFromAsync(
        ChatModelEndpoint model, IList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var response = await model.Client.GetResponseAsync(messages, options, cancellationToken);
        response.ModelId ??= model.ModelId;
        return response;
    }

    private async ValueTask<StreamStart> StartStreamAsync(
        ChatModelEndpoint model, IList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.AttemptTimeout);
        var enumerator = model.Client.GetStreamingResponseAsync(messages, options, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var buffered = new List<ChatResponseUpdate>();
            while (await enumerator.MoveNextAsync())
            {
                var update = enumerator.Current;
                update.ModelId ??= model.ModelId;
                buffered.Add(update);
                if (HasVisibleContent(update))
                {
                    break;
                }
            }

            // Content arrived: from now on only the caller may cancel, a long answer must not be cut off.
            cts.CancelAfter(Timeout.InfiniteTimeSpan);
            return new StreamStart(model.ModelId, enumerator, buffered, cts);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync();
            cts.Dispose();
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{model.ModelId} did not start streaming within {_options.AttemptTimeout}.", ex);
            }

            throw;
        }
    }

    private static bool HasVisibleContent(ChatResponseUpdate update) =>
        update.Contents.Any(c => c is TextContent { Text.Length: > 0 } or FunctionCallContent);

    /// <summary>An opened stream whose updates up to the first visible content have already been read.</summary>
    private sealed class StreamStart(
        string modelId, IAsyncEnumerator<ChatResponseUpdate> enumerator, IReadOnlyList<ChatResponseUpdate> buffered, CancellationTokenSource cts)
        : IAsyncDisposable
    {
        public string ModelId { get; } = modelId;
        public IAsyncEnumerator<ChatResponseUpdate> Enumerator { get; } = enumerator;
        public IReadOnlyList<ChatResponseUpdate> Buffered { get; } = buffered;

        public async ValueTask DisposeAsync()
        {
            await Enumerator.DisposeAsync();
            cts.Dispose();
        }
    }
}
