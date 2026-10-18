namespace Euro2024Rag.Tests.Eval;

public sealed record CaseResult(
    string Id,
    string Question,
    string? RewrittenQuery,
    IReadOnlyList<string> RetrievedIds,
    bool? Hit,
    double? Recall,
    double? ReciprocalRank,
    JudgeVerdict? Verdict,
    bool JudgeError,
    string? Model,
    long LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    string? Error);
