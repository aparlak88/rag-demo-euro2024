namespace Euro2024Rag.Tests.Eval;

public sealed record ChatTurnDto(string Role, string Content);

/// <summary>One golden question. JSON uses camelCase; history/mustMention/expectedChunkIds are optional.</summary>
public sealed record GoldenCase(
    string Id,
    string Question,
    IReadOnlyList<ChatTurnDto> History,
    IReadOnlyList<string> ExpectedChunkIds,
    IReadOnlyList<string> MustMention,
    bool Answerable);
