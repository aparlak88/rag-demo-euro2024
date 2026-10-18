using Euro2024Rag.Web.Rag;

namespace Euro2024Rag.Tests.Unit;

public class KnowledgeDocumentBuilderTests
{
    [Fact]
    public void Builds_the_expected_number_of_chunks_per_kind()
    {
        var counts = TestData.Chunks.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(51, counts["match"]);
        Assert.Equal(24, counts["team"]);
        Assert.Equal(6, counts["group"]);
        Assert.Equal(10, counts["venue"]);
        Assert.Equal(13, counts["leaderboard"]);
        Assert.Equal(2, counts["tournament"]);
    }

    [Fact]
    public void Chunks_have_unique_ids_keys_derived_from_id_and_non_empty_text()
    {
        Assert.Equal(TestData.Chunks.Count, TestData.Chunks.Select(c => c.Id).Distinct().Count());
        Assert.All(TestData.Chunks, c =>
        {
            Assert.Equal(KnowledgeChunk.CreateKey(c.Id), c.Key);
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Content));
        });
    }

    [Theory]
    [InlineData("match-51")]
    [InlineData("team-spain")]
    [InlineData("group-f")]
    [InlineData("venue-berlin")]
    [InlineData("leaderboard-goals-scored")]
    [InlineData("tournament-overview")]
    [InlineData("tournament-records")]
    public void Well_known_chunk_exists(string id) => Assert.Contains(TestData.Chunks, c => c.Id == id);

    [Fact]
    public void Spain_team_chunk_mentions_the_title()
    {
        Assert.Contains("Champions", TestData.Chunks.Single(c => c.Id == "team-spain").Content);
    }
}
