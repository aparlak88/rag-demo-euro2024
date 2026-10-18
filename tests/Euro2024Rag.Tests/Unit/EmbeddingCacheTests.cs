using Euro2024Rag.Web.Ingestion;

namespace Euro2024Rag.Tests.Unit;

public sealed class EmbeddingCacheTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "euro2024-cache-" + Guid.NewGuid().ToString("N"));

    private string BasePath => Path.Combine(dir, "embedding-cache.json");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Saved_vector_is_found_after_reload_and_changed_text_misses()
    {
        var cache = new EmbeddingCache(BasePath, "Fake:model", 3);
        cache.Set("hello", new float[] { 1, 2, 3 });
        await cache.SaveAsync(["hello"], CancellationToken.None);

        var reloaded = new EmbeddingCache(BasePath, "Fake:model", 3);
        await reloaded.LoadAsync(CancellationToken.None);

        Assert.True(reloaded.TryGet("hello", out var vector));
        Assert.Equal(new float[] { 1, 2, 3 }, vector.ToArray());
        Assert.False(reloaded.TryGet("hello!", out _));
    }

    [Fact]
    public async Task Texts_not_live_at_save_time_are_pruned()
    {
        var cache = new EmbeddingCache(BasePath, "Fake:model", 3);
        cache.Set("keep", new float[] { 1, 1, 1 });
        cache.Set("drop", new float[] { 2, 2, 2 });
        await cache.SaveAsync(["keep"], CancellationToken.None);

        var reloaded = new EmbeddingCache(BasePath, "Fake:model", 3);
        await reloaded.LoadAsync(CancellationToken.None);

        Assert.True(reloaded.TryGet("keep", out _));
        Assert.False(reloaded.TryGet("drop", out _));
    }

    [Fact]
    public async Task Different_models_use_different_files()
    {
        var a = new EmbeddingCache(BasePath, "Fake:a", 3);
        a.Set("hello", new float[] { 1, 2, 3 });
        await a.SaveAsync(["hello"], CancellationToken.None);

        // Different live texts: in a shared file this save would prune model a's entry.
        var b = new EmbeddingCache(BasePath, "Fake:b", 3);
        b.Set("other", new float[] { 4, 5, 6 });
        await b.SaveAsync(["other"], CancellationToken.None);

        var reloadedA = new EmbeddingCache(BasePath, "Fake:a", 3);
        await reloadedA.LoadAsync(CancellationToken.None);

        Assert.True(reloadedA.TryGet("hello", out var vector));
        Assert.Equal(new float[] { 1, 2, 3 }, vector.ToArray());
        Assert.Equal(2, Directory.GetFiles(dir).Distinct().Count());
    }

    [Fact]
    public async Task File_name_has_no_colon_and_contains_the_model_slug()
    {
        var cache = new EmbeddingCache(BasePath, "Ollama:gemma3:4b", 3);
        cache.Set("x", new float[] { 1 });
        await cache.SaveAsync(["x"], CancellationToken.None);

        var name = Path.GetFileName(Assert.Single(Directory.GetFiles(dir)));
        Assert.DoesNotContain(':', name);
        Assert.Contains("ollama", name);
        Assert.Contains("gemma3", name);
    }
}
