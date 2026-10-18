using System.Globalization;
using System.Text.Json;
using Euro2024Rag.Tests.Eval;

namespace Euro2024Rag.Tests.Unit;

public class EvalReportTests
{
    private static EvalHeader Header(string chatProvider = "Gemini") => new(
        chatProvider, "gemini-2.5-flash", "Gemini", "gemini-embedding-001", "Qdrant", "euro2024",
        5, 0.35, "On", new DateTimeOffset(2026, 10, 18, 9, 30, 15, TimeSpan.Zero));

    private static CaseResult Result(string id, bool? hit, double? recall, double? rr, JudgeVerdict? verdict, bool judgeError) =>
        new(id, $"Question {id}?", null, ["c1", "c2"], hit, recall, rr, verdict, judgeError, "gemini-2.5-flash", 120, 100, 20, null);

    private static IReadOnlyList<CaseResult> Results() =>
    [
        Result("case-hit", true, 1.0, 0.5, new JudgeVerdict(true, true, "fine"), false),
        Result("case-miss", false, 0.0, 0.0, new JudgeVerdict(false, true, "wrong"), false),
        Result("case-unanswerable", null, null, null, null, true),
    ];

    [Fact]
    public void Markdown_contains_header_rows_note_and_totals_over_hit_eligible_cases()
    {
        var md = EvalReport.ToMarkdown(Header(), Results());

        Assert.Contains("Gemini", md);
        Assert.Contains("gemini-2.5-flash", md);
        Assert.Contains("gemini-embedding-001", md);
        Assert.Contains("Qdrant", md);
        Assert.Contains("euro2024", md);
        Assert.Contains("0.35", md);
        Assert.Contains("case-hit", md);
        Assert.Contains("case-miss", md);
        Assert.Contains("case-unanswerable", md);
        Assert.Contains("judge = answering model", md);
        Assert.Contains("1/2", md);          // hit rate: 1 of 2 hit-eligible cases
        Assert.Contains("0.25", md);         // MRR (0.5 + 0) / 2
        Assert.DoesNotContain("NaN", md);
        Assert.DoesNotContain("∞", md);
    }

    [Fact]
    public void Markdown_hit_rate_is_culture_invariant()
    {
        IReadOnlyList<CaseResult> all =
        [
            Result("a", true, 1.0, 1.0, null, false),
            Result("b", true, 1.0, 1.0, null, false),
        ];
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var md = EvalReport.ToMarkdown(Header(), all);

            Assert.Contains("2/2 (100%)", md);
            Assert.DoesNotContain("%100", md);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Markdown_shows_na_when_no_case_has_expected_ids()
    {
        IReadOnlyList<CaseResult> only =
        [
            Result("u1", null, null, null, new JudgeVerdict(true, true, "ok"), false),
        ];

        foreach (var list in new[] { only, Array.Empty<CaseResult>() })
        {
            var md = EvalReport.ToMarkdown(Header(), list);

            Assert.Contains("n/a", md);
            Assert.DoesNotContain("NaN", md);
            Assert.DoesNotContain("∞", md);
        }
    }

    [Fact]
    public void Json_round_trips_with_System_Text_Json()
    {
        var json = EvalReport.ToJson(Header(), Results());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Gemini", root.GetProperty("header").GetProperty("chatProvider").GetString());
        var results = root.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var back = results.Deserialize<List<CaseResult>>(options)!;
        Assert.Equal(Results().Select(r => r.Id), back.Select(r => r.Id));
        Assert.Equal(new JudgeVerdict(true, true, "fine"), back[0].Verdict);
        Assert.True(back[2].JudgeError);
        Assert.Null(back[2].Hit);
    }

    [Fact]
    public void FileStem_is_filename_safe()
    {
        var stem = EvalReport.FileStem(Header("Ollama:gemma3:4b"));

        Assert.StartsWith("20261018-093015_", stem);
        Assert.DoesNotContain(":", stem);
        Assert.DoesNotContain("/", stem);
        Assert.Equal("20261018-093015_Ollama-gemma3-4b_Gemini_Qdrant", stem);
    }
}
