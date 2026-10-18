using Euro2024Rag.Tests.Eval;

namespace Euro2024Rag.Tests.Unit;

public class JudgeVerdictParserTests
{
    [Fact]
    public void Plain_json_is_parsed()
    {
        Assert.True(JudgeVerdict.TryParse("""{"correct":true,"faithful":false,"reason":"r"}""", out var v));
        Assert.Equal(new JudgeVerdict(true, false, "r"), v);
    }

    [Fact]
    public void Prose_and_json_fence_are_tolerated()
    {
        var text = "Here you go:\n```json\n{\"correct\":false,\"faithful\":true,\"reason\":\"nope\"}\n```";

        Assert.True(JudgeVerdict.TryParse(text, out var v));
        Assert.Equal(new JudgeVerdict(false, true, "nope"), v);
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        Assert.True(JudgeVerdict.TryParse("""{"Correct":true,"FAITHFUL":true,"Reason":"ok"}""", out var v));
        Assert.Equal(new JudgeVerdict(true, true, "ok"), v);
    }

    [Fact]
    public void Reason_with_braces_and_quotes_does_not_break_balancing()
    {
        var text = "{\"correct\":true,\"faithful\":true,\"reason\":\"said \\\"}{\\\" and {x}\"} trailing }";

        Assert.True(JudgeVerdict.TryParse(text, out var v));
        Assert.True(v!.Correct);
        Assert.Equal("said \"}{\" and {x}", v.Reason);
    }

    [Theory]
    [InlineData("I cannot decide.")]
    [InlineData("""{"faithful":true,"reason":"x"}""")]
    [InlineData("""{"correct":true,"reason":"x"}""")]
    [InlineData("""{"correct":"yes","faithful":true}""")]
    [InlineData("{not json}")]
    [InlineData("{\"correct\":true")]
    [InlineData("")]
    public void Unusable_text_is_rejected(string text)
    {
        Assert.False(JudgeVerdict.TryParse(text, out var v));
        Assert.Null(v);
    }
}
