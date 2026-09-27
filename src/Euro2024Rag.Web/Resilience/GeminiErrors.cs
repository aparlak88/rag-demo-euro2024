using Google.GenAI;
using Polly.Timeout;

namespace Euro2024Rag.Web.Resilience;

/// <summary>Classifies Gemini failures so Polly knows whether to retry, fall back, or give up.</summary>
public static class GeminiErrors
{
    /// <summary>Worth retrying on the same model: rate limits, server errors, timeouts, network blips.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        _ when IsDailyQuotaExhausted(exception) => false,
        ApiException api => api.StatusCode is 408 or 429 or 500 or 502 or 503 or 504,
        TimeoutRejectedException or TimeoutException => true,
        HttpRequestException or IOException => true,

        // The SDK throws this when a streamed response is cut off mid-way (server or connection dropped it).
        InvalidOperationException when exception.Message.StartsWith("Incomplete JSON segment", StringComparison.Ordinal) => true,
        _ => false,
    };

    /// <summary>
    /// Worth switching to the fallback model: everything transient, a model whose daily quota is used up,
    /// and 404 (unknown / retired model id).
    /// 400/401/403 are not included: a bad request or API key fails the same way on every model.
    /// </summary>
    public static bool ShouldFallback(Exception exception) =>
        IsTransient(exception) || IsDailyQuotaExhausted(exception) || exception is ApiException { StatusCode: 404 };

    /// <summary>
    /// A 429 can mean "too many requests this minute" (retrying helps) or "daily quota used up"
    /// (retrying won't help until tomorrow). Gemini names the violated quota in the error details,
    /// e.g. "GenerateRequestsPerDayPerProjectPerModel-FreeTier".
    /// </summary>
    public static bool IsDailyQuotaExhausted(Exception exception) =>
        exception is ApiException { StatusCode: 429 } api && api.Message.Contains("PerDay", StringComparison.Ordinal);
}
