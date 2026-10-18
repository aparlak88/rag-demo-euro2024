using Euro2024Rag.Web.Ingestion;
using Euro2024Rag.Web.Rag;

namespace Euro2024Rag.Tests;

/// <summary>The real dataset, parsed and chunked once and shared by all tests.</summary>
public static class TestData
{
    private static readonly Lazy<TournamentContext> context = new(() => TournamentContext.Load(DataPaths.Context));
    private static readonly Lazy<IReadOnlyList<EuroMatch>> matches = new(() => MatchCsvReader.Read(DataPaths.Csv, Context));
    private static readonly Lazy<IReadOnlyList<KnowledgeChunk>> chunks = new(() => KnowledgeDocumentBuilder.Build(Matches, Context));

    public static TournamentContext Context => context.Value;

    public static IReadOnlyList<EuroMatch> Matches => matches.Value;

    public static IReadOnlyList<KnowledgeChunk> Chunks => chunks.Value;
}
