using Euro2024Rag.Tests.Eval;

namespace Euro2024Rag.Tests.Unit;

public class RetrievalMetricsTests
{
    private static readonly string[] Expected = ["a", "b"];

    [Fact]
    public void Expected_chunk_found_later_gives_full_recall_and_half_reciprocal_rank()
    {
        string[] retrieved = ["x", "b", "y", "a"];

        Assert.True(RetrievalMetrics.Hit(Expected, retrieved));
        Assert.Equal(1.0, RetrievalMetrics.Recall(Expected, retrieved));
        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRank(Expected, retrieved));
    }

    [Fact]
    public void No_expected_chunk_retrieved_scores_zero()
    {
        string[] retrieved = ["x", "y"];

        Assert.False(RetrievalMetrics.Hit(Expected, retrieved));
        Assert.Equal(0.0, RetrievalMetrics.Recall(Expected, retrieved));
        Assert.Equal(0.0, RetrievalMetrics.ReciprocalRank(Expected, retrieved));
    }

    [Fact]
    public void Partial_retrieval_gives_partial_recall_and_first_rank()
    {
        string[] retrieved = ["a"];

        Assert.True(RetrievalMetrics.Hit(Expected, retrieved));
        Assert.Equal(0.5, RetrievalMetrics.Recall(Expected, retrieved));
        Assert.Equal(1.0, RetrievalMetrics.ReciprocalRank(Expected, retrieved));
    }

    [Fact]
    public void Empty_retrieved_does_not_throw_and_scores_zero()
    {
        string[] retrieved = [];

        Assert.False(RetrievalMetrics.Hit(Expected, retrieved));
        Assert.Equal(0.0, RetrievalMetrics.Recall(Expected, retrieved));
        Assert.Equal(0.0, RetrievalMetrics.ReciprocalRank(Expected, retrieved));
    }
}
