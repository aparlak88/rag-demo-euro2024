using Microsoft.Extensions.AI;

namespace Euro2024Rag.Tests.Fakes;

/// <summary>Scripted chat client: each call gets its zero-based index so a test can fail the first call and answer the next.</summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Func<int, IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>>? streaming;
    private readonly Func<int, ChatResponse>? nonStreaming;
    private int calls;

    public FakeChatClient(Func<int, IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> streaming) => this.streaming = streaming;

    public FakeChatClient(Func<int, ChatResponse> nonStreaming) => this.nonStreaming = nonStreaming;

    public int Calls => Volatile.Read(ref calls);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref calls) - 1;
        return nonStreaming is null
            ? throw new NotSupportedException("This fake was built for streaming.")
            : Task.FromResult(nonStreaming(index));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref calls) - 1;
        return streaming is null
            ? throw new NotSupportedException("This fake was built for non-streaming.")
            : streaming(index, messages);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> Stream(params string[] texts)
    {
        foreach (var text in texts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        await Task.CompletedTask;
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> Throw(Exception exception)
    {
        await Task.CompletedTask;
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> TextThenThrow(string text, Exception exception)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        await Task.CompletedTask;
        throw exception;
    }
}
