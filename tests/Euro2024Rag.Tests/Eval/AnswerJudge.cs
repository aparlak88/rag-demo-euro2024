using System.Text;
using Euro2024Rag.Web.Rag;
using Microsoft.Extensions.AI;

namespace Euro2024Rag.Tests.Eval;

/// <summary>LLM-as-judge: grades one answer for correctness (against the expected facts) and faithfulness (against the sources).</summary>
public static class AnswerJudge
{
    private const string SystemPrompt =
        """
        You are a strict grader for a question-answering system about the UEFA Euro 2024 football tournament.
        You get the question, the numbered sources the system was given, the facts the answer must contain
        (or a note that the question cannot be answered from the data), and the system's answer.

        Grade two things:
        - "correct": if the question is answerable, true only when EVERY expected fact is present in the answer
          (paraphrase, translation and different number formatting are fine). If the question is NOT answerable
          from the data, true only when the answer states plainly that the data does not contain it and does not invent one.
        - "faithful": true only when every claim in the answer is supported by the numbered sources
          (no outside knowledge, no invented numbers).

        Reply with ONE JSON object and nothing else: {"correct": true|false, "faithful": true|false, "reason": "<one short sentence>"}
        """;

    public static async Task<JudgeVerdict?> JudgeAsync(
        IChatClient client, GoldenCase c, IReadOnlyList<RetrievedChunk> sources, string answer, CancellationToken ct)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Question: {c.Question}");
        prompt.AppendLine();
        prompt.AppendLine("Sources:");
        if (sources.Count == 0) prompt.AppendLine("(none)");
        foreach (var s in sources)
        {
            prompt.AppendLine($"[{s.Index}] {s.Title}");
            prompt.AppendLine(s.Content);
            prompt.AppendLine();
        }

        prompt.AppendLine(c.Answerable
            ? $"Expected facts (all must be present): {string.Join("; ", c.MustMention)}"
            : "The data does NOT contain the answer; a correct answer says so.");
        prompt.AppendLine();
        prompt.AppendLine("Answer to grade:");
        prompt.AppendLine(answer);

        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, prompt.ToString())],
                new ChatOptions { Temperature = 0f },
                ct);
            return JudgeVerdict.TryParse(response.Text, out var verdict) ? verdict : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
