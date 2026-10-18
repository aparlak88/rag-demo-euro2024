using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Ingestion;
using Euro2024Rag.Web.Rag;
using Euro2024Rag.Web.Resilience;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Euro2024Rag.Tests.Eval;

[CollectionDefinition("Eval")]
public sealed class EvalCollection : ICollectionFixture<EvalFixture>;

/// <summary>
/// Boots the real app (Development environment) once per run, waits for indexing, answers golden cases through
/// <see cref="RagService"/> and writes the report on dispose. Does nothing unless EVAL=1.
/// </summary>
public sealed class EvalFixture : IAsyncLifetime
{
    private readonly ConcurrentBag<CaseResult> results = [];
    private WebApplicationFactory<Program>? factory;
    private RagService? rag;
    private IChatClient? chat;
    private EvalHeader? header;

    public bool Enabled { get; } = Environment.GetEnvironmentVariable("EVAL") == "1";

    public async ValueTask InitializeAsync()
    {
        if (!Enabled) return;

        var webRoot = FindWebProjectDirectory();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseContentRoot(webRoot);
        });
        using var _ = factory.CreateClient(); // starts the host (and the indexing hosted service)

        var sp = factory.Services;
        var state = sp.GetRequiredService<IngestionState>();
        var timeout = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("EVAL_INGEST_TIMEOUT"), out var s) && s > 0 ? s : 600);
        var clock = Stopwatch.StartNew();
        while (!state.IsReady)
        {
            var current = state.Current;
            if (current.Status == IngestionStatus.Failed)
                throw new InvalidOperationException(current.Message);
            if (clock.Elapsed > timeout)
                throw new TimeoutException($"Indexing did not finish within {timeout.TotalSeconds:0}s (EVAL_INGEST_TIMEOUT). Last status: {current.Message}");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        rag = sp.GetRequiredService<RagService>();
        chat = sp.GetRequiredService<IChatClient>();
        var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var ragOptions = sp.GetRequiredService<IOptions<RagOptions>>().Value;
        var store = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value;
        header = new EvalHeader(
            ai.Provider.ToString(),
            string.Join(", ", ai.Chat.ChatModels),
            ai.ActiveEmbeddingProvider.ToString(),
            ai.Embedding.EmbeddingModel,
            store.Provider.ToString(),
            store.CollectionName,
            ragOptions.TopK,
            ai.Embedding.MinScore ?? ragOptions.MinScore,
            ragOptions.QueryRewrite.ToString(),
            DateTimeOffset.Now);
    }

    public async Task<CaseResult> RunCaseAsync(GoldenCase c)
    {
        var result = await RunAsync(c, (turns, ct) => rag!.AskAsync(turns, ct), chat!, CancellationToken.None);
        results.Add(result);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (header is not null && !results.IsEmpty)
            {
                var ordered = results.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "eval");
                Directory.CreateDirectory(dir);
                var stem = Path.Combine(dir, EvalReport.FileStem(header));
                await File.WriteAllTextAsync(stem + ".md", EvalReport.ToMarkdown(header, ordered));
                await File.WriteAllTextAsync(stem + ".json", EvalReport.ToJson(header, ordered));
            }
        }
        finally
        {
            if (factory is not null) await factory.DisposeAsync();
        }
    }

    /// <summary>Folds the event stream of one question into a <see cref="CaseResult"/>. Separate from the host so it can be unit-tested.</summary>
    internal static async Task<CaseResult> RunAsync(
        GoldenCase c,
        Func<IReadOnlyList<ChatTurn>, CancellationToken, IAsyncEnumerable<ChatEvent>> ask,
        IChatClient judgeClient,
        CancellationToken ct)
    {
        var turns = c.History.Select(h => new ChatTurn(h.Role, h.Content)).Append(new ChatTurn("user", c.Question)).ToList();
        var answer = new StringBuilder();
        IReadOnlyList<RetrievedChunk> sources = [];
        string? rewritten = null, model = null, error = null;
        int? inTokens = null, outTokens = null;
        var clock = Stopwatch.StartNew();
        long elapsed;

        try
        {
            await foreach (var e in ask(turns, ct))
            {
                switch (e.Type)
                {
                    case "query": rewritten = e.Text; break;
                    case "sources": sources = e.Data as IReadOnlyList<RetrievedChunk> ?? []; break;
                    case "delta": answer.Append(e.Text); break;
                    case "reset": answer.Clear(); break;
                    case "error": error = e.Text ?? "unknown error"; break;
                    case "done":
                        if (e.Data is not null)
                        {
                            var d = JsonSerializer.SerializeToElement(e.Data);
                            model = d.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                            inTokens = Int(d, "inputTokens");
                            outTokens = Int(d, "outputTokens");
                        }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = AiErrors.Describe(ex) + ": " + ex.Message;
        }

        elapsed = clock.ElapsedMilliseconds;
        var ids = sources.Select(s => s.Id).ToList();
        bool? hit = null;
        double? recall = null, rr = null;
        if (c.ExpectedChunkIds.Count > 0)
        {
            hit = RetrievalMetrics.Hit(c.ExpectedChunkIds, ids);
            recall = RetrievalMetrics.Recall(c.ExpectedChunkIds, ids);
            rr = RetrievalMetrics.ReciprocalRank(c.ExpectedChunkIds, ids);
        }

        JudgeVerdict? verdict = null;
        var judgeError = false;
        if (error is null)
        {
            verdict = await AnswerJudge.JudgeAsync(judgeClient, c, sources, answer.ToString(), ct);
            judgeError = verdict is null;
        }

        return new CaseResult(c.Id, c.Question, rewritten, ids, hit, recall, rr, verdict, judgeError, model, elapsed, inTokens, outTokens, error);
    }

    private static int? Int(JsonElement d, string name) =>
        d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string FindWebProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Euro2024Rag.Web");
            if (File.Exists(Path.Combine(candidate, "Euro2024Rag.Web.csproj"))) return candidate;
        }

        throw new DirectoryNotFoundException("Could not find src/Euro2024Rag.Web above " + AppContext.BaseDirectory);
    }
}
