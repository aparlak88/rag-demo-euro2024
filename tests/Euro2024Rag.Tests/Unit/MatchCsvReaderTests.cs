namespace Euro2024Rag.Tests.Unit;

public class MatchCsvReaderTests
{
    [Fact]
    public void Reads_all_51_matches_numbered_once()
    {
        Assert.Equal(51, TestData.Matches.Count);
        Assert.Equal(Enumerable.Range(1, 51), TestData.Matches.Select(m => m.MatchNumber).Order());
    }

    [Fact]
    public void Final_is_spain_beating_england()
    {
        var final = TestData.Matches.Single(m => m.MatchNumber == 51);

        Assert.Equal("Spain 2–1 England", final.Scoreline);
        Assert.Equal("Final", final.Stage);
    }

    [Fact]
    public void Match_46_was_decided_on_penalties_by_france()
    {
        var knockout = TestData.Matches.Single(m => m.MatchNumber == 46).Knockout;

        Assert.NotNull(knockout);
        Assert.Equal("France", knockout.PenaltyWinner);
        Assert.Equal("5-3", knockout.PenaltyScore);
    }
}
