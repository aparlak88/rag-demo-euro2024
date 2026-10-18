using System.Net;
using Euro2024Rag.Web.Resilience;
using Google.GenAI;
using System.ClientModel;
using OllamaSharp.Models.Exceptions;

namespace Euro2024Rag.Tests.Unit;

public class AiErrorsTests
{
    private static HttpRequestException Http(int status) => new("boom", null, (HttpStatusCode)status);

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(408)]
    public void Transient_http_status_is_retried_and_falls_back(int status)
    {
        var ex = Http(status);

        Assert.True(AiErrors.IsTransient(ex));
        Assert.True(AiErrors.ShouldFallback(ex));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    public void Bad_request_or_key_is_neither_retried_nor_falls_back(int status)
    {
        var ex = Http(status);

        Assert.False(AiErrors.IsTransient(ex));
        Assert.False(AiErrors.ShouldFallback(ex));
    }

    [Fact]
    public void Http_404_is_model_not_found_and_falls_back_without_retry()
    {
        var ex = Http(404);

        Assert.True(AiErrors.IsModelNotFound(ex));
        Assert.True(AiErrors.ShouldFallback(ex));
        Assert.False(AiErrors.IsTransient(ex));
    }

    [Fact]
    public void Timeout_and_empty_response_are_transient()
    {
        Assert.True(AiErrors.IsTransient(new TimeoutException()));
        Assert.True(AiErrors.IsTransient(new EmptyResponseException("m")));
    }

    [Fact]
    public void Ollama_model_not_found_message_is_detected()
    {
        var ex = new OllamaException("model 'x' not found");

        Assert.True(AiErrors.IsModelNotFound(ex));
        Assert.True(AiErrors.ShouldFallback(ex));
    }

    [Fact]
    public void Truncated_json_is_transient()
    {
        Assert.True(AiErrors.IsTransient(new InvalidOperationException("Incomplete JSON segment at the end")));
    }

    [Fact]
    public void Describe_and_user_message_include_the_http_status()
    {
        var ex = Http(503);

        Assert.Equal("HTTP 503", AiErrors.Describe(ex));
        Assert.StartsWith("Model error 503:", AiErrors.ToUserMessage(ex));
    }

    [Fact]
    public void Gemini_per_minute_429_is_transient()
    {
        var ex = new ApiException("RPM exceeded", 429, "RESOURCE_EXHAUSTED", null!);

        Assert.True(AiErrors.IsTransient(ex));
        Assert.False(AiErrors.IsDailyQuotaExhausted(ex));
    }

    [Fact]
    public void Gemini_daily_quota_429_skips_retry_but_falls_back()
    {
        var ex = new ApiException("GenerateRequestsPerDayPerProjectPerModel-FreeTier", 429, "RESOURCE_EXHAUSTED", null!);

        Assert.True(AiErrors.IsDailyQuotaExhausted(ex));
        Assert.False(AiErrors.IsTransient(ex));
        Assert.True(AiErrors.ShouldFallback(ex));
        Assert.Equal("quota exhausted", AiErrors.Describe(ex));
    }

    [Fact]
    public void Gemini_503_is_transient_and_400_is_not()
    {
        Assert.True(AiErrors.IsTransient(new ApiException("down", 503, "UNAVAILABLE", null!)));
        Assert.False(AiErrors.ShouldFallback(new ApiException("bad", 400, "INVALID_ARGUMENT", null!)));
    }

    [Fact]
    public void OpenAI_error_without_http_response_is_transient()
    {
        // Status 0: connection refused / reset before any response.
        Assert.True(AiErrors.IsTransient(new ClientResultException("connection reset", null!, null!)));
    }
}
