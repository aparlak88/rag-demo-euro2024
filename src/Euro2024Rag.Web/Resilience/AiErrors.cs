using System.ClientModel;
using Google.GenAI;
using OllamaSharp.Models.Exceptions;
using Polly.Timeout;

namespace Euro2024Rag.Web.Resilience;

/// <summary>
/// Classifies failures from any of the AI providers so Polly knows whether to retry, fall back, or give up.
/// Each SDK reports HTTP errors with its own exception type: Gemini <see cref="ApiException"/>,
/// OpenAI <see cref="ClientResultException"/>, Ollama <see cref="HttpRequestException"/> or <see cref="OllamaException"/>.
/// </summary>
public static class AiErrors
{
    /// <summary>Worth retrying on the same model: rate limits, server errors, timeouts, network blips.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        _ when IsDailyQuotaExhausted(exception) => false,
        _ when StatusCode(exception) is { } status => status is 408 or 429 or 500 or 502 or 503 or 504,
        TimeoutRejectedException or TimeoutException => true,
        HttpRequestException or IOException => true,

        // OpenAI: no HTTP response at all (connection refused / reset), status 0.
        ClientResultException => true,

        // The Gemini SDK throws this when a streamed response is cut off mid-way (server or connection dropped it).
        InvalidOperationException when exception.Message.StartsWith("Incomplete JSON segment", StringComparison.Ordinal) => true,
        _ => false,
    };

    /// <summary>
    /// Worth switching to the fallback model: everything transient, a model whose daily quota is used up,
    /// and an unknown / retired / not-pulled model id.
    /// 400/401/403 are not included: a bad request or API key fails the same way on every model.
    /// </summary>
    public static bool ShouldFallback(Exception exception) =>
        IsTransient(exception) || IsDailyQuotaExhausted(exception) || IsModelNotFound(exception);

    /// <summary>
    /// A 429 can mean "too many requests this minute" (retrying helps) or "quota used up" (retrying won't help).
    /// Gemini names the violated quota in the error details, e.g. "GenerateRequestsPerDayPerProjectPerModel-FreeTier";
    /// OpenAI returns the error code "insufficient_quota" when the account is out of credit.
    /// </summary>
    public static bool IsDailyQuotaExhausted(Exception exception) => exception switch
    {
        ApiException { StatusCode: 429 } api => api.Message.Contains("PerDay", StringComparison.Ordinal),
        ClientResultException { Status: 429 } openAi => openAi.Message.Contains("insufficient_quota", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>404, or Ollama's "model 'x' not found, try pulling it first".</summary>
    public static bool IsModelNotFound(Exception exception) =>
        StatusCode(exception) == 404 ||
        (exception is OllamaException && exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));

    public static bool IsRateLimited(Exception exception) => StatusCode(exception) == 429;

    /// <summary>Short reason for logs: "HTTP 503", "daily quota exhausted", "TimeoutRejectedException"…</summary>
    public static string Describe(Exception? exception) => exception switch
    {
        null => "unknown",
        _ when IsDailyQuotaExhausted(exception) => "quota exhausted",
        _ when StatusCode(exception) is { } status => $"HTTP {status}",
        OllamaException => exception.Message,
        _ => exception.GetType().Name,
    };

    /// <summary>Message for the UI, prefixed with the HTTP status when there is one.</summary>
    public static string ToUserMessage(Exception exception) =>
        StatusCode(exception) is { } status ? $"Model error {status}: {exception.Message}" : exception.Message;

    private static int? StatusCode(Exception exception) => exception switch
    {
        ApiException api => api.StatusCode,
        ClientResultException { Status: > 0 } openAi => openAi.Status,
        HttpRequestException { StatusCode: { } status } => (int)status,
        _ => null,
    };
}
