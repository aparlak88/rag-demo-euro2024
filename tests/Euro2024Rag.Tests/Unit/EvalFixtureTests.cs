using Euro2024Rag.Tests.Eval;
using Euro2024Rag.Tests.Fakes;
using Euro2024Rag.Web.Rag;
using Microsoft.Extensions.AI;

namespace Euro2024Rag.Tests;

public class EvalFixtureTests
{
    private static readonly GoldenCase Case = new("c1", "Who won?", ExpectedChunkIds: ["team-spain", "match-51"], MustMention: ["Spain"]);

    private static IReadOnlyList<RetrievedChunk> Sources() =>
        [new(1, "group-b", "group", "Group B", "x", 0.9), new(2, "match-51", "match", "Final", "y", 0.8)];

    private static Func<IReadOnlyList<ChatTurn>, CancellationToken, IAsyncEnumerable<ChatEvent>> Events(params ChatEvent[] events) =>
        (_, _) => ToAsync(events);

    private static async IAsyncEnumerable<ChatEvent> ToAsync(IEnumerable<ChatEvent> events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatEvent> ThenThrow(IEnumerable<ChatEvent> events, Exception ex)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
        throw ex;
    }

    private static FakeChatClient Judge(string text) =>
        new(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    private static ChatEvent Done() =>
        new("done", Data: new { model = "m1", elapsedMs = 5L, inputTokens = (long?)11, outputTokens = (long?)22 });

    [Fact]
    public async Task Folds_events_computes_metrics_and_judges()
    {
        var judge = Judge("""{"correct": true, "faithful": false, "reason": "ok"}""");
        var r = await EvalFixture.RunAsync(Case, Events(
            new ChatEvent("query", "rewritten"),
            new ChatEvent("sources", Data: Sources()),
            new ChatEvent("delta", "Spain "),
            new ChatEvent("delta", "won."),
            Done()), judge, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal("rewritten", r.RewrittenQuery);
        Assert.Equal(["group-b", "match-51"], r.RetrievedIds);
        Assert.True(r.Hit);
        Assert.Equal(0.5, r.Recall);
        Assert.Equal(0.5, r.ReciprocalRank);
        Assert.Equal(new JudgeVerdict(true, false, "ok"), r.Verdict);
        Assert.False(r.JudgeError);
        Assert.Equal("m1", r.Model);
        Assert.Equal(11, r.InputTokens);
        Assert.Equal(22, r.OutputTokens);
    }

    [Fact]
    public async Task Builds_turns_from_history_plus_question()
    {
        IReadOnlyList<ChatTurn>? seen = null;
        var c = Case with { History = [new ChatTurnDto("user", "a"), new ChatTurnDto("assistant", "b")] };
        await EvalFixture.RunAsync(c, (turns, _) => { seen = turns; return ToAsync([Done()]); }, Judge("{}"), CancellationToken.None);

        Assert.Equal([new ChatTurn("user", "a"), new ChatTurn("assistant", "b"), new ChatTurn("user", "Who won?")], seen);
    }

    [Fact]
    public async Task Reset_clears_the_partial_answer()
    {
        string? graded = null;
        var judge = new FakeChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"correct":true,"faithful":true,"reason":""}""")));
        var capture = new CapturingChatClient(judge, m => graded = m);
        await EvalFixture.RunAsync(Case, Events(
            new ChatEvent("delta", "partial garbage"),
            new ChatEvent("reset", "interrupted"),
            new ChatEvent("delta", "Final answer."),
            Done()), capture, CancellationToken.None);

        Assert.NotNull(graded);
        Assert.Contains("Final answer.", graded);
        Assert.DoesNotContain("partial garbage", graded);
    }

    [Fact]
    public async Task Unparseable_judge_output_is_recorded_as_judge_error()
    {
        var r = await EvalFixture.RunAsync(Case, Events(new ChatEvent("delta", "x"), Done()), Judge("no json here"), CancellationToken.None);

        Assert.True(r.JudgeError);
        Assert.Null(r.Verdict);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Throwing_judge_is_recorded_as_judge_error()
    {
        var judge = new FakeChatClient(_ => throw new InvalidOperationException("boom"));
        var r = await EvalFixture.RunAsync(Case, Events(new ChatEvent("delta", "x"), Done()), judge, CancellationToken.None);

        Assert.True(r.JudgeError);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Exception_from_ask_is_recorded_in_Error_and_skips_the_judge()
    {
        var judge = Judge("""{"correct":true,"faithful":true,"reason":""}""");
        var r = await EvalFixture.RunAsync(Case,
            (_, _) => ThenThrow([new ChatEvent("query", "q")], new InvalidOperationException("kaput")), judge, CancellationToken.None);

        Assert.Contains("InvalidOperationException", r.Error);
        Assert.Contains("kaput", r.Error);
        Assert.Equal("q", r.RewrittenQuery);
        Assert.Equal(0, judge.Calls);
        Assert.False(r.JudgeError);
    }

    [Fact]
    public async Task Error_event_is_recorded_in_Error()
    {
        var r = await EvalFixture.RunAsync(Case, Events(new ChatEvent("error", "Model error 429")), Judge("{}"), CancellationToken.None);

        Assert.Equal("Model error 429", r.Error);
    }

    [Fact]
    public async Task Cases_without_expected_ids_have_no_retrieval_metrics()
    {
        var c = new GoldenCase("u", "Who scored?", Answerable: false);
        var r = await EvalFixture.RunAsync(c, Events(new ChatEvent("sources", Data: Sources()), Done()), Judge("{}"), CancellationToken.None);

        Assert.Null(r.Hit);
        Assert.Null(r.Recall);
        Assert.Null(r.ReciprocalRank);
    }

    private sealed class CapturingChatClient(IChatClient inner, Action<string> capture) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            capture(messages.Last().Text);
            return inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
