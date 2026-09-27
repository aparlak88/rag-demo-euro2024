using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace Euro2024Rag.Web.Rag;

public sealed record ChatTurn(string Role, string Content);

public sealed record RetrievedChunk(int Index, string Id, string Kind, string Title, string Content, double Score);

/// <summary>One line of the NDJSON stream sent to the browser.</summary>
public sealed record ChatEvent(string Type, string? Text = null, object? Data = null);

/// <summary>
/// The RAG loop for one question:
///   1. Rewrite   – turn a follow-up / non-English question into a standalone English search query.
///   2. Retrieve  – embed the query and run a cosine-similarity search over the vector store.
///   3. Augment   – put the retrieved chunks, numbered, into the prompt.
///   4. Generate  – stream a grounded answer that cites the chunk numbers.
/// </summary>
public sealed class RagService(
    IChatClient chatClient,
    [FromKeyedServices(RagService.RewriteClientKey)] IChatClient rewriteClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStoreCollection<string, KnowledgeChunk> collection,
    IOptions<RagOptions> options,
    ILogger<RagService> logger)
{
    private const string AnswerSystemPrompt =
        """
        You are "Euro 2024 Analyst", an assistant that answers questions about the UEFA Euro 2024 football tournament.

        Rules:
        - Answer ONLY with facts from the numbered sources provided in the user's message. Do not use outside knowledge.
        - The data is team-level match statistics. It has no player names, goal scorers, minutes or dates; if asked, say so.
        - If the sources don't contain the answer, say that plainly and suggest a related question the data can answer.
        - Cite the sources you used inline with their numbers, e.g. [1] or [2][4].
        - Reply in the same language as the user's question. Team names in the data are English (e.g. "Turkiye" = Türkiye).
        - Be concise. Use Markdown; use a table when comparing several teams or matches.
        """;

    private const string RewriteSystemPrompt =
        """
        You turn the user's latest question about UEFA Euro 2024 into ONE standalone English search query
        for a semantic search index of match reports, team summaries, group tables, venues and team leaderboards.
        - Resolve pronouns and follow-ups ("what about them?", "and in the final?") using the conversation.
        - Translate to English and use English team names: Turkiye, Czechia, Netherlands, Germany, Spain, England, etc.
        - Keep it short and keyword-rich. Output only the query, without quotes or explanations.
        """;

    public const string RewriteClientKey = "rewrite";

    private const int MaxAnswerRestarts = 1;

    private readonly RagOptions _options = options.Value;

    public async IAsyncEnumerable<ChatEvent> AskAsync(
        IReadOnlyList<ChatTurn> conversation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var question = conversation[^1].Content.Trim();
        var history = conversation
            .Take(conversation.Count - 1)
            .TakeLast(_options.MaxHistoryMessages)
            .Select(t => new ChatMessage(t.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, t.Content))
            .ToList();

        var searchQuery = await RewriteQueryAsync(history, question, cancellationToken);
        yield return new ChatEvent("query", searchQuery);

        var sources = await SearchAsync(searchQuery, _options.TopK, cancellationToken);
        yield return new ChatEvent("sources", Data: sources);

        List<ChatMessage> messages = [new(ChatRole.System, AnswerSystemPrompt), .. history, new(ChatRole.User, BuildGroundedPrompt(question, sources))];

        string? model = null;
        UsageDetails? usage = null;

        // The chat client can only fall back to another model before the first token. If the stream breaks
        // after that (seen under heavy load: the server cuts the response mid-way), the answer is regenerated
        // from scratch and the browser is told to discard the partial text with a "reset" event.
        for (var attempt = 0; ; attempt++)
        {
            Exception? interruption = null;
            var stream = chatClient.GetStreamingResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    try
                    {
                        if (!await stream.MoveNextAsync())
                        {
                            break;
                        }
                    }
                    catch (Exception ex) when (attempt < MaxAnswerRestarts && GeminiErrors.IsTransient(ex) && !cancellationToken.IsCancellationRequested)
                    {
                        interruption = ex;
                        break;
                    }

                    var update = stream.Current;
                    model = update.ModelId ?? model;
                    usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        yield return new ChatEvent("delta", update.Text);
                    }
                }
            }
            finally
            {
                await stream.DisposeAsync();
            }

            if (interruption is null)
            {
                break;
            }

            logger.LogWarning(interruption, "Answer stream from {Model} was interrupted, regenerating", model);
            yield return new ChatEvent("reset", $"{model ?? "The model"} was interrupted mid-answer, regenerating…");
            model = null;
            usage = null;
        }

        yield return new ChatEvent("done", Data: new
        {
            model,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            inputTokens = usage?.InputTokenCount,
            outputTokens = usage?.OutputTokenCount,
        });
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top, CancellationToken cancellationToken)
    {
        var queryEmbedding = await embeddingGenerator.GenerateAsync(query, GeminiEmbeddingOptions.ForQuery, cancellationToken);

        var results = new List<RetrievedChunk>();
        await foreach (var result in collection.SearchAsync(queryEmbedding.Vector, top, cancellationToken: cancellationToken))
        {
            if (result.Score is { } score && score >= _options.MinScore)
            {
                var chunk = result.Record;
                results.Add(new RetrievedChunk(results.Count + 1, chunk.Id, chunk.Kind, chunk.Title, chunk.Content, Math.Round(score, 4)));
            }
        }

        logger.LogInformation("Retrieved {Count} chunks for \"{Query}\": {Ids}", results.Count, query, string.Join(", ", results.Select(r => $"{r.Id}={r.Score:0.000}")));
        return results;
    }

    private async Task<string> RewriteQueryAsync(IReadOnlyList<ChatMessage> history, string question, CancellationToken cancellationToken)
    {
        if (_options.QueryRewrite == QueryRewriteMode.Off || (_options.QueryRewrite == QueryRewriteMode.FollowUps && history.Count == 0))
        {
            return question;
        }

        var transcript = new StringBuilder();
        foreach (var message in history)
        {
            transcript.AppendLine($"{message.Role}: {message.Text}");
        }

        transcript.AppendLine($"user (latest question): {question}");

        try
        {
            var response = await rewriteClient.GetResponseAsync(
                [new(ChatRole.System, RewriteSystemPrompt), new(ChatRole.User, transcript.ToString())],
                new ChatOptions { Temperature = 0f },
                cancellationToken);

            var rewritten = response.Text.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(rewritten) ? question : rewritten;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Retrieval still works on the raw question (Gemini embeddings are multilingual), just less precisely.
            logger.LogWarning(ex, "Query rewrite failed, searching with the original question");
            return question;
        }
    }

    private static string BuildGroundedPrompt(string question, IReadOnlyList<RetrievedChunk> sources)
    {
        var sb = new StringBuilder();
        if (sources.Count == 0)
        {
            sb.AppendLine("Sources: (no relevant documents were found)");
        }
        else
        {
            sb.AppendLine("Sources:");
            foreach (var source in sources)
            {
                sb.AppendLine($"[{source.Index}] {source.Title}");
                sb.AppendLine(source.Content);
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }
}
