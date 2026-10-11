using System.Text.Json;
using Euro2024Rag.Web.Ai;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Ingestion;
using Euro2024Rag.Web.Rag;
using Euro2024Rag.Web.Resilience;
using Euro2024Rag.Web.VectorStores;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));

// --- AI: Gemini, OpenAI or Ollama behind the Microsoft.Extensions.AI abstractions, chosen by Ai:Provider ---
builder.Services.AddAiProviders(builder.Configuration);

// --- Vector store: InMemory, PgVector, Qdrant or Chroma, chosen by VectorStore:Provider -------------
builder.Services.AddKnowledgeVectorStore(builder.Configuration);

// --- RAG ----------------------------------------------------------------------------------------
builder.Services.AddSingleton<IngestionState>();
builder.Services.AddHostedService<IngestionService>();
builder.Services.AddSingleton<RagService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", (IngestionState state, IOptions<AiOptions> ai, IOptions<VectorStoreOptions> vectorStore) => new
{
    ingestion = state.Current,
    vectorStore = new { provider = vectorStore.Value.Provider.ToString(), collection = vectorStore.Value.CollectionName },
    ai = new { provider = ai.Value.Provider.ToString(), embeddingProvider = ai.Value.ActiveEmbeddingProvider.ToString() },
    models = new
    {
        chat = ai.Value.Chat.ChatModels,
        rewrite = ai.Value.Chat.RewriteModels.Count > 0 ? ai.Value.Chat.RewriteModels : ai.Value.Chat.ChatModels,
        embedding = ai.Value.Embedding.EmbeddingModel,
        dimensions = KnowledgeChunk.EmbeddingDimensions,
    },
});

// Retrieval only, no generation: handy for inspecting what the model would see.
api.MapGet("/search", async (string q, int? top, IngestionState state, HttpContext http, IOptions<RagOptions> rag, CancellationToken ct) =>
{
    if (!state.IsReady)
    {
        return Results.Problem(state.Current.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var results = await http.RequestServices.GetRequiredService<RagService>()
        .SearchAsync(q, Math.Clamp(top ?? rag.Value.TopK, 1, 50), ct);
    return Results.Ok(results);
});

// Streams the answer as NDJSON: query → sources → delta* → done (or error).
api.MapPost("/chat", async (ChatRequest request, IngestionState state, HttpContext http, IOptions<JsonOptions> json, ILogger<Program> logger) =>
{
    var ct = http.RequestAborted;
    if (request.Messages is not { Count: > 0 } || request.Messages[^1].Role != "user" || string.IsNullOrWhiteSpace(request.Messages[^1].Content))
    {
        return Results.BadRequest("The last message must be a non-empty user message.");
    }

    if (!state.IsReady)
    {
        return Results.Problem(state.Current.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var rag = http.RequestServices.GetRequiredService<RagService>();
    http.Response.ContentType = "application/x-ndjson; charset=utf-8";
    http.Response.Headers.CacheControl = "no-cache";

    async Task WriteAsync(ChatEvent chatEvent)
    {
        await JsonSerializer.SerializeAsync(http.Response.Body, chatEvent, json.Value.SerializerOptions, ct);
        await http.Response.Body.WriteAsync("\n"u8.ToArray(), ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await foreach (var chatEvent in rag.AskAsync(request.Messages, ct))
        {
            await WriteAsync(chatEvent);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // The user pressed "stop" or closed the tab.
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Chat request failed");
        await WriteAsync(new ChatEvent("error", AiErrors.ToUserMessage(ex)));
    }

    return Results.Empty;
});

app.Run();

internal sealed record ChatRequest(IReadOnlyList<ChatTurn> Messages);
