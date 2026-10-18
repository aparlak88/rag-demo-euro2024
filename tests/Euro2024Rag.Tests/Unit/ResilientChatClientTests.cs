using System.Net;
using Euro2024Rag.Tests.Fakes;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Euro2024Rag.Tests.Unit;

public class ResilientChatClientTests
{
    private static readonly ChatMessage[] Question = [new(ChatRole.User, "hi")];

    private static readonly ResilienceOptions FastOptions = new()
    {
        MaxRetryAttempts = 1, // Polly rejects 0; one 1 ms retry per model before falling back
        BaseDelay = TimeSpan.FromMilliseconds(1),
        AttemptTimeout = TimeSpan.FromSeconds(2),
    };

    private static HttpRequestException Http(int status) => new("boom", null, (HttpStatusCode)status);

    private static ResilientChatClient Create(params (string Id, FakeChatClient Client)[] models) =>
        new(models.Select(m => new ChatModelEndpoint(m.Id, m.Client)).ToList(), FastOptions, NullLogger<ResilientChatClient>.Instance);

    private static FakeChatClient Failing(Exception ex) => new((int _) => throw ex);

    private static FakeChatClient Answering(string text) =>
        new(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    private static async Task<string> ReadAll(IAsyncEnumerable<ChatResponseUpdate> stream, CancellationToken ct)
    {
        var text = "";
        await foreach (var update in stream.WithCancellation(ct))
        {
            text += update.Text;
        }

        return text;
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Response_falls_back_to_the_next_model_on_503()
    {
        var first = Failing(Http(503));
        var second = Answering("from second");
        var client = Create(("m1", first), ("m2", second));

        var response = await client.GetResponseAsync(Question, cancellationToken: Timeout());

        Assert.Equal("from second", response.Text);
        Assert.Equal("m2", response.ModelId);
        Assert.Equal(2, first.Calls); // original attempt + one retry
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task Stream_falls_back_when_the_first_model_fails_before_any_update()
    {
        var first = new FakeChatClient((_, _) => FakeChatClient.Throw(Http(503)));
        var second = new FakeChatClient((_, _) => FakeChatClient.Stream("from ", "second"));
        var client = Create(("m1", first), ("m2", second));

        var text = await ReadAll(client.GetStreamingResponseAsync(Question, cancellationToken: Timeout()), Timeout());

        Assert.Equal("from second", text);
        Assert.Equal(2, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task Bad_request_does_not_fall_back()
    {
        var first = Failing(Http(400));
        var second = Answering("never");
        var client = Create(("m1", first), ("m2", second));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync(Question, cancellationToken: Timeout()));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Stream_failing_after_the_first_text_reaches_the_caller_without_fallback()
    {
        var first = new FakeChatClient((_, _) => FakeChatClient.TextThenThrow("partial", Http(503)));
        var second = new FakeChatClient((_, _) => FakeChatClient.Stream("other"));
        var client = Create(("m1", first), ("m2", second));
        var received = new List<string>();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(Question, cancellationToken: Timeout()))
            {
                received.Add(update.Text);
            }
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(["partial"], received);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Stream_without_any_text_counts_as_empty_response_and_falls_back()
    {
        var first = new FakeChatClient((_, _) => FakeChatClient.Stream());
        var second = new FakeChatClient((_, _) => FakeChatClient.Stream("answer"));
        var client = Create(("m1", first), ("m2", second));

        var text = await ReadAll(client.GetStreamingResponseAsync(Question, cancellationToken: Timeout()), Timeout());

        Assert.Equal("answer", text);
        Assert.Equal(2, first.Calls); // empty response is transient: retried once, then falls back
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task Empty_response_on_the_last_model_surfaces_as_EmptyResponseException()
    {
        var only = new FakeChatClient((_, _) => FakeChatClient.Stream());
        var client = Create(("m1", only));

        await Assert.ThrowsAsync<EmptyResponseException>(
            () => ReadAll(client.GetStreamingResponseAsync(Question, cancellationToken: Timeout()), Timeout()));
    }

    [Fact]
    public void Empty_model_list_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new ResilientChatClient([], FastOptions, NullLogger<ResilientChatClient>.Instance));
    }
}
