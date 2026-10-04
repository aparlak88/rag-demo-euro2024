using System.Text.Json;
using Euro2024Rag.Web.Configuration;
using Euro2024Rag.Web.Ingestion;
using Euro2024Rag.Web.Rag;
using Euro2024Rag.Web.Resilience;
using Euro2024Rag.Web.VectorStores;
using Google.GenAI;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));

// --- AI: Gemini behind the Microsoft.Extensions.AI abstractions -------------------------------
builder.Services.AddSingleton(sp => new Client(apiKey: sp.GetRequiredService<IOptions<GeminiOptions>>().Value.ApiKey));

// Two model chains, each an IChatClient with Polly retries + fallback:
// the answer chain, and a cheaper one for query rewriting (keyed service).
builder.Services.AddSingleton<IChatClient>(sp =>
    CreateChatChain(sp, sp.GetRequiredService<IOptions<GeminiOptions>>().Value.ChatModels, "Gemini:ChatModels"));
builder.Services.AddKeyedSingleton<IChatClient>(RagService.RewriteClientKey, (sp, _) =>
    CreateChatChain(sp, sp.GetRequiredService<IOptions<GeminiOptions>>().Value.RewriteModels, "Gemini:RewriteModels"));

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var gemini = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
    return new RetryingEmbeddingGenerator(
        sp.GetRequiredService<Client>().AsIEmbeddingGenerator(gemini.EmbeddingModel, KnowledgeChunk.EmbeddingDimensions),
        gemini.Resilience,
        sp.GetRequiredService<ILogger<RetryingEmbeddingGenerator>>());
});

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

api.MapGet("/status", (IngestionState state, IOptions<GeminiOptions> gemini, IOptions<VectorStoreOptions> vectorStore) => new
{
    ingestion = state.Current,
    vectorStore = new { provider = vectorStore.Value.Provider.ToString(), collection = vectorStore.Value.CollectionName },
    models = new
    {
        chat = gemini.Value.ChatModels,
        rewrite = gemini.Value.RewriteModels,
        embedding = gemini.Value.EmbeddingModel,
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
        await WriteAsync(new ChatEvent("error", ex is ApiException api ? $"Gemini error {api.StatusCode}: {api.Message}" : ex.Message));
    }

    return Results.Empty;
});

app.Run();

static ResilientChatClient CreateChatChain(IServiceProvider sp, IReadOnlyList<string> modelIds, string settingName)
{
    if (modelIds.Count == 0)
    {
        throw new InvalidOperationException($"{settingName} must list at least one model.");
    }

    var client = sp.GetRequiredService<Client>();
    return new ResilientChatClient(
        modelIds.Select(id => new ChatModelEndpoint(id, client.AsIChatClient(id))).ToList(),
        sp.GetRequiredService<IOptions<GeminiOptions>>().Value.Resilience,
        sp.GetRequiredService<ILogger<ResilientChatClient>>());
}

internal sealed record ChatRequest(IReadOnlyList<ChatTurn> Messages);
