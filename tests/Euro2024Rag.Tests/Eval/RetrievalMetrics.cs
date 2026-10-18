namespace Euro2024Rag.Tests.Eval;

/// <summary>Retrieval metrics for one case. Callers skip cases that have no expected ids.</summary>
public static class RetrievalMetrics
{
    public static bool Hit(IReadOnlyCollection<string> expected, IReadOnlyList<string> retrievedIds) =>
        retrievedIds.Any(expected.Contains);

    public static double Recall(IReadOnlyCollection<string> expected, IReadOnlyList<string> retrievedIds)
    {
        if (expected.Count == 0) return 0;
        var found = expected.Distinct().Count(retrievedIds.Contains);
        return (double)found / expected.Distinct().Count();
    }

    public static double ReciprocalRank(IReadOnlyCollection<string> expected, IReadOnlyList<string> retrievedIds)
    {
        for (var i = 0; i < retrievedIds.Count; i++)
            if (expected.Contains(retrievedIds[i])) return 1.0 / (i + 1);
        return 0;
    }
}
