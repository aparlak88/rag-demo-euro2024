using System.Text.Json;

namespace Euro2024Rag.Tests.Eval;

/// <summary>Opt-in end-to-end evaluation against the configured providers. Run with EVAL=1 (see README).</summary>
[Collection("Eval")]
[Trait("Category", "Eval")]
public sealed class RagEvalTests(EvalFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> CaseIds => [.. LoadCases().Select(c => c.Id)];

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task Answers_are_retrieved_and_correct(string caseId)
    {
        Assert.SkipUnless(fixture.Enabled, "Set EVAL=1 to run the evaluation");

        var c = LoadCases().Single(x => x.Id == caseId);
        var r = await fixture.RunCaseAsync(c);

        var context =
            $"Question: {r.Question}\nRewritten query: {r.RewrittenQuery}\nRetrieved: {string.Join(", ", r.RetrievedIds)}\n" +
            $"Expected: {string.Join(", ", c.ExpectedChunkIds)}\nJudge: {r.Verdict?.Reason}";

        if (r.Error is not null) Assert.Fail($"Chat error: {r.Error}\n{context}");
        if (r.Hit == false) Assert.Fail($"Retrieval miss.\n{context}");
        if (r.Verdict is { Correct: false }) Assert.Fail($"Judged incorrect.\n{context}");
    }

    private static List<GoldenCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Eval", "golden-set.json");
        return JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(path), Json)
               ?? throw new InvalidOperationException("golden-set.json is empty");
    }
}
