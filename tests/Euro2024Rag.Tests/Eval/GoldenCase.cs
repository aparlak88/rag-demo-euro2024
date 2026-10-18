using System.Text.Json.Serialization;

namespace Euro2024Rag.Tests.Eval;

public sealed record ChatTurnDto(string Role, string Content);

/// <summary>
/// One golden question. JSON uses camelCase; history/mustMention/expectedChunkIds are optional and never null.
/// Answerable defaults to true when omitted (most cases are answerable).
/// </summary>
public sealed record GoldenCase
{
    [JsonConstructor]
    public GoldenCase(
        string Id,
        string Question,
        IReadOnlyList<ChatTurnDto>? History = null,
        IReadOnlyList<string>? ExpectedChunkIds = null,
        IReadOnlyList<string>? MustMention = null,
        bool Answerable = true)
    {
        this.Id = Id;
        this.Question = Question;
        this.History = History ?? [];
        this.ExpectedChunkIds = ExpectedChunkIds ?? [];
        this.MustMention = MustMention ?? [];
        this.Answerable = Answerable;
    }

    public string Id { get; init; }
    public string Question { get; init; }
    public IReadOnlyList<ChatTurnDto> History { get; init; }
    public IReadOnlyList<string> ExpectedChunkIds { get; init; }
    public IReadOnlyList<string> MustMention { get; init; }
    public bool Answerable { get; init; }
}
