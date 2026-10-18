using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Euro2024Rag.Tests.Eval;

public sealed record EvalHeader(
    string ChatProvider,
    string ChatModels,
    string EmbeddingProvider,
    string EmbeddingModel,
    string VectorStore,
    string Collection,
    int TopK,
    double MinScore,
    string QueryRewrite,
    DateTimeOffset Timestamp);

public static class EvalReport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string FileStem(EvalHeader h) =>
        Safe($"{h.Timestamp.ToString("yyyyMMdd-HHmmss", Inv)}_{h.ChatProvider}_{h.EmbeddingProvider}_{h.VectorStore}");

    private static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9._-]", "-");

    public static string ToJson(EvalHeader header, IReadOnlyList<CaseResult> results) =>
        JsonSerializer.Serialize(new { header, summary = Summarize(results), results }, JsonOptions);

    public static string ToMarkdown(EvalHeader h, IReadOnlyList<CaseResult> results)
    {
        var s = Summarize(results);
        var sb = new StringBuilder();
        sb.AppendLine("# Euro 2024 RAG evaluation");
        sb.AppendLine();
        sb.AppendLine($"- Run: {h.Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv)}");
        sb.AppendLine($"- Chat: {h.ChatProvider} ({h.ChatModels})");
        sb.AppendLine($"- Embeddings: {h.EmbeddingProvider} ({h.EmbeddingModel})");
        sb.AppendLine($"- Vector store: {h.VectorStore}, collection `{h.Collection}`");
        sb.AppendLine($"- TopK: {h.TopK}, MinScore: {h.MinScore.ToString(Inv)}, query rewrite: {h.QueryRewrite}");
        sb.AppendLine();
        sb.AppendLine("Note: judge = answering model (the model that wrote each answer also graded it), so correctness and faithfulness scores are optimistic.");
        sb.AppendLine();
        sb.AppendLine("| Case | Hit | Recall | RR | Correct | Faithful | Latency ms | Tokens in/out | Model |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var correct = r.JudgeError ? "judge error" : r.Verdict is null ? "-" : YesNo(r.Verdict.Correct);
            var faithful = r.JudgeError ? "judge error" : r.Verdict is null ? "-" : YesNo(r.Verdict.Faithful);
            sb.AppendLine($"| {Cell(r.Id)} | {(r.Hit is null ? "-" : YesNo(r.Hit.Value))} | {Num(r.Recall)} | {Num(r.ReciprocalRank)} | {correct} | {faithful} | {r.LatencyMs} | {r.InputTokens?.ToString(Inv) ?? "-"}/{r.OutputTokens?.ToString(Inv) ?? "-"} | {Cell(r.Model ?? "-")} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Totals");
        sb.AppendLine();
        sb.AppendLine($"- Cases: {results.Count} ({s.WithExpected} with expected chunks)");
        sb.AppendLine($"- Hit rate: {(s.WithExpected == 0 ? "n/a" : $"{s.Hits}/{s.WithExpected} ({Pct((double)s.Hits / s.WithExpected)})")}");
        sb.AppendLine($"- Mean recall: {(s.MeanRecall is { } mr ? mr.ToString("0.00", Inv) : "n/a")}");
        sb.AppendLine($"- MRR: {(s.Mrr is { } mrr ? mrr.ToString("0.00", Inv) : "n/a")}");
        sb.AppendLine($"- Judged correct: {s.Correct}/{s.Judged}, faithful: {s.Faithful}/{s.Judged}");
        sb.AppendLine($"- Judge errors: {s.JudgeErrors} (not counted as incorrect)");
        sb.AppendLine($"- Case errors: {s.Errors}");

        var failures = results.Where(r => r.Error is not null).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Errors");
            sb.AppendLine();
            foreach (var r in failures) sb.AppendLine($"- {r.Id}: {r.Error!.ReplaceLineEndings(" ")}");
        }

        return sb.ToString();
    }

    private sealed record Summary(
        int Cases, int WithExpected, int Hits, double? HitRate, double? MeanRecall, double? Mrr,
        int Judged, int Correct, int Faithful, int JudgeErrors, int Errors);

    private static Summary Summarize(IReadOnlyList<CaseResult> results)
    {
        var eligible = results.Where(r => r.Hit is not null).ToList();
        var hits = eligible.Count(r => r.Hit == true);
        var judged = results.Where(r => r.Verdict is not null && !r.JudgeError).ToList();
        return new Summary(
            results.Count,
            eligible.Count,
            hits,
            eligible.Count == 0 ? null : (double)hits / eligible.Count,
            eligible.Count == 0 ? null : eligible.Average(r => r.Recall ?? 0),
            eligible.Count == 0 ? null : eligible.Average(r => r.ReciprocalRank ?? 0),
            judged.Count,
            judged.Count(r => r.Verdict!.Correct),
            judged.Count(r => r.Verdict!.Faithful),
            results.Count(r => r.JudgeError),
            results.Count(r => r.Error is not null));
    }

    private static string Pct(double d) => $"{(d * 100).ToString("0", Inv)}%";
    private static string YesNo(bool b) => b ? "yes" : "no";
    private static string Num(double? d) => d is { } v ? v.ToString("0.00", Inv) : "-";
    private static string Cell(string s) => s.Replace("|", "\\|").ReplaceLineEndings(" ");
}
