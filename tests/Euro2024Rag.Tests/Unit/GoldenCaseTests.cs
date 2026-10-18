using System.Text.Json;
using Euro2024Rag.Tests.Eval;

namespace Euro2024Rag.Tests.Unit;

public class GoldenCaseTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Minimal_case_gets_empty_lists_and_is_answerable()
    {
        var c = JsonSerializer.Deserialize<GoldenCase>("""{"id":"x","question":"q"}""", Options)!;

        Assert.Equal("x", c.Id);
        Assert.Equal("q", c.Question);
        Assert.NotNull(c.History);
        Assert.Empty(c.History);
        Assert.NotNull(c.ExpectedChunkIds);
        Assert.Empty(c.ExpectedChunkIds);
        Assert.NotNull(c.MustMention);
        Assert.Empty(c.MustMention);
        Assert.True(c.Answerable);
    }

    [Fact]
    public void Full_case_keeps_its_values()
    {
        var json = """
            {"id":"y","question":"q2","history":[{"role":"user","content":"hi"}],
             "expectedChunkIds":["a","b"],"mustMention":["Spain"],"answerable":false}
            """;

        var c = JsonSerializer.Deserialize<GoldenCase>(json, Options)!;

        Assert.Equal(new ChatTurnDto("user", "hi"), Assert.Single(c.History));
        Assert.Equal(["a", "b"], c.ExpectedChunkIds);
        Assert.Equal(["Spain"], c.MustMention);
        Assert.False(c.Answerable);
    }
}
