using Euro2024Rag.Tests.Fakes;
using Euro2024Rag.Web.Ai;
using Euro2024Rag.Web.Rag;
using Microsoft.Extensions.AI;

namespace Euro2024Rag.Tests.Unit;

public class TaskAwareEmbeddingGeneratorTests
{
    private static TaskAwareEmbeddingGenerator Create(
        FakeEmbeddingGenerator inner, Action<EmbeddingTask, EmbeddingGenerationOptions>? configure = null) =>
        new(inner, "test-model", "doc: ", "query: ", configure);

    [Fact]
    public async Task Document_task_adds_the_document_prefix()
    {
        var inner = new FakeEmbeddingGenerator(768);

        await Create(inner).GenerateAsync(["x"], EmbeddingTasks.ForDocuments, TestContext.Current.CancellationToken);

        Assert.Equal(["doc: x"], inner.ReceivedValues);
    }

    [Fact]
    public async Task Query_task_adds_the_query_prefix()
    {
        var inner = new FakeEmbeddingGenerator(768);

        await Create(inner).GenerateAsync(["x"], EmbeddingTasks.ForQuery, TestContext.Current.CancellationToken);

        Assert.Equal(["query: x"], inner.ReceivedValues);
    }

    [Fact]
    public async Task Inner_generator_keeps_dimensions_but_never_sees_the_task_mark()
    {
        var inner = new FakeEmbeddingGenerator(768);

        await Create(inner).GenerateAsync(["x"], EmbeddingTasks.ForDocuments, TestContext.Current.CancellationToken);

        Assert.NotNull(inner.ReceivedOptions);
        Assert.Equal(768, inner.ReceivedOptions!.Dimensions);
        Assert.Null(EmbeddingTasks.GetTask(inner.ReceivedOptions));
        // The shared static options must not be mutated.
        Assert.Equal(EmbeddingTask.Document, EmbeddingTasks.GetTask(EmbeddingTasks.ForDocuments));
    }

    [Fact]
    public async Task Configure_is_invoked_with_the_task_and_its_changes_reach_the_inner_generator()
    {
        var inner = new FakeEmbeddingGenerator(768);
        var seen = new List<EmbeddingTask>();

        await Create(inner, (task, options) =>
        {
            seen.Add(task);
            options.AdditionalProperties = new() { ["gemini_task"] = task.ToString() };
        }).GenerateAsync(["x"], EmbeddingTasks.ForQuery, TestContext.Current.CancellationToken);

        Assert.Equal([EmbeddingTask.Query], seen);
        Assert.Equal("Query", inner.ReceivedOptions!.AdditionalProperties!["gemini_task"]);
    }

    [Fact]
    public async Task Wrong_dimension_count_throws_naming_the_model_and_expected_size()
    {
        var inner = new FakeEmbeddingGenerator(3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(inner).GenerateAsync(["x"], EmbeddingTasks.ForDocuments, TestContext.Current.CancellationToken));

        Assert.Contains("test-model", ex.Message);
        Assert.Contains("768", ex.Message);
    }

    [Fact]
    public async Task Call_without_a_task_mark_passes_values_through_untouched()
    {
        var inner = new FakeEmbeddingGenerator(5);
        var options = new EmbeddingGenerationOptions { Dimensions = 5 };
        var invoked = false;

        await Create(inner, (_, _) => invoked = true).GenerateAsync(["x", "y"], options, TestContext.Current.CancellationToken);

        Assert.Equal(["x", "y"], inner.ReceivedValues);
        Assert.Same(options, inner.ReceivedOptions);
        Assert.False(invoked);
    }
}
