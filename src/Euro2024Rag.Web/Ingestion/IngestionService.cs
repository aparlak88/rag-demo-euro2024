using System.Diagnostics;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace Euro2024Rag.Web.Ingestion;

/// <summary>
/// Indexing pipeline, run once at startup:
///   CSV → EuroMatch rows → knowledge chunks → embeddings (cached) → in-memory vector store.
/// </summary>
public sealed class IngestionService(
    IServiceProvider services,
    IngestionState state,
    IOptions<GeminiOptions> geminiOptions,
    IOptions<RagOptions> ragOptions,
    IHostEnvironment environment,
    ILogger<IngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gemini = geminiOptions.Value;
        var rag = ragOptions.Value;

        if (string.IsNullOrWhiteSpace(gemini.ApiKey))
        {
            state.Update(new IngestionSnapshot(IngestionStatus.Failed,
                "Gemini API key is missing. Set Gemini:ApiKey in appsettings.Development.json (see README)."));
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            state.Update(new IngestionSnapshot(IngestionStatus.Running, "Reading dataset…"));
            var context = TournamentContext.Load(ResolvePath(rag.ContextFile));
            var matches = MatchCsvReader.Read(ResolvePath(rag.DataFile), context);
            var chunks = KnowledgeDocumentBuilder.Build(matches, context);
            logger.LogInformation("Built {Chunks} chunks from {Matches} matches", chunks.Count, matches.Count);

            var cache = new EmbeddingCache(ResolvePath(rag.EmbeddingCacheFile), gemini.EmbeddingModel, KnowledgeChunk.EmbeddingDimensions);
            await cache.LoadAsync(stoppingToken);

            var missing = new List<KnowledgeChunk>();
            foreach (var chunk in chunks)
            {
                if (cache.TryGet(chunk.Content, out var cached))
                {
                    chunk.Embedding = cached;
                }
                else
                {
                    missing.Add(chunk);
                }
            }

            var cachedCount = chunks.Count - missing.Count;
            var generator = services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            var embedded = 0;
            foreach (var batch in missing.Chunk(rag.EmbeddingBatchSize))
            {
                state.Update(new IngestionSnapshot(IngestionStatus.Running,
                    $"Embedding documents with {gemini.EmbeddingModel}… ({embedded}/{missing.Count})",
                    matches.Count, chunks.Count, embedded, cachedCount));

                var embeddings = await generator.GenerateAsync(
                    batch.Select(c => c.Content), GeminiEmbeddingOptions.ForDocuments, stoppingToken);

                for (var i = 0; i < batch.Length; i++)
                {
                    batch[i].Embedding = embeddings[i].Vector;
                    cache.Set(batch[i].Content, embeddings[i].Vector);
                }

                embedded += batch.Length;

                // Save after every batch: if a later batch hits a quota limit, a restart resumes from here.
                await cache.SaveAsync(chunks.Select(c => c.Content), stoppingToken);
            }

            var collection = services.GetRequiredService<VectorStoreCollection<string, KnowledgeChunk>>();
            await collection.EnsureCollectionExistsAsync(stoppingToken);
            await collection.UpsertAsync(chunks, stoppingToken);

            state.Update(new IngestionSnapshot(IngestionStatus.Ready,
                $"Indexed {chunks.Count} chunks ({embedded} embedded now, {cachedCount} from cache).",
                matches.Count, chunks.Count, embedded, cachedCount,
                chunks.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count()),
                stopwatch.Elapsed.TotalSeconds));

            logger.LogInformation("Vector store ready: {Chunks} chunks in {Seconds:0.0}s ({Embedded} embedded, {Cached} cached)",
                chunks.Count, stopwatch.Elapsed.TotalSeconds, embedded, cachedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing failed");
            state.Update(new IngestionSnapshot(IngestionStatus.Failed, $"Indexing failed: {ex.Message}"));
        }
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
}
