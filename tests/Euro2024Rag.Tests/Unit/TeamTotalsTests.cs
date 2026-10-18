using Euro2024Rag.Web.Ingestion;

namespace Euro2024Rag.Tests.Unit;

public class TeamTotalsTests
{
    private static TeamTotals Totals(string team) => new(team, TestData.Context.GroupOf(team), TestData.Matches);

    [Fact]
    public void Spain_totals_are_consistent()
    {
        var spain = Totals("Spain");

        Assert.Equal(7, spain.Played);
        Assert.Equal("Champions", spain.Finish);
        Assert.Equal(spain.Played, spain.Wins + spain.Draws + spain.Losses);
        Assert.Equal(spain.GoalDifference, spain.GoalsFor - spain.GoalsAgainst);
    }

    [Fact]
    public void Penalty_shootout_result_is_described_from_each_side()
    {
        var match = TestData.Matches.Single(m => m.MatchNumber == 46);

        Assert.Equal("draw after extra time, won 5-3 on penalties", Totals("France").ResultDescription(match));
        Assert.Equal("draw after extra time, lost 3-5 on penalties", Totals("Portugal").ResultDescription(match));
    }
}
