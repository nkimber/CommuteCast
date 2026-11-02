using CommuteCast.Core;

namespace CommuteCast.Web;

public static class WebApi
{
    public static void MapCommuteCastApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/state", (WebRuntime runtime) => runtime.State());
        api.MapGet("/jobs/{id}", (string id, WebRuntime runtime) => runtime.Details(id));
        api.MapGet("/draft", async (WebRuntime runtime) => await runtime.LoadDraftAsync());
        api.MapPost("/draft", async (DraftRequest request, WebRuntime runtime, CancellationToken ct) => await runtime.SaveDraftAsync(request, ct));
        api.MapPost("/preview", async (ComposeRequest request, WebRuntime runtime, CancellationToken ct) => await runtime.PreviewAsync(request, ct));
        api.MapPost("/jobs", async (ComposeRequest request, WebRuntime runtime, CancellationToken ct) => new { id = await runtime.SubmitAsync(request, ct) });
        api.MapPost("/defaults", async (NarrationOptions options, WebRuntime runtime, CancellationToken ct) => { await runtime.SaveDefaultsAsync(options, ct); return Results.Ok(); });
        api.MapPost("/providers/check", async (ProviderRequest request, WebRuntime runtime, CancellationToken ct) => { await runtime.CheckProviderAsync(request, ct); return Results.Ok(); });
        api.MapPost("/providers/repair", async (ProviderRequest request, WebRuntime runtime, CancellationToken ct) => { await runtime.RepairProviderAsync(request.Engine, ct); return Results.Ok(); });
        api.MapPost("/queue", async (QueueRequest request, WebRuntime runtime, CancellationToken ct) => { await runtime.PauseAsync(request.Paused, ct); return Results.Ok(); });
        api.MapPost("/jobs/{id}/cancel", async (string id, WebRuntime runtime) => { await runtime.Queue.CancelAsync(id); return Results.Ok(); });
        api.MapPost("/jobs/{id}/retry", async (string id, WebRuntime runtime) => { await runtime.Queue.RetryAsync(id); return Results.Ok(); });
        api.MapPost("/jobs/{id}/earlier", async (string id, WebRuntime runtime) => { await runtime.Queue.MovePendingAsync(id, -1); return Results.Ok(); });
        api.MapPost("/jobs/{id}/later", async (string id, WebRuntime runtime) => { await runtime.Queue.MovePendingAsync(id, 1); return Results.Ok(); });
        api.MapPost("/jobs/{id}/publish", async (string id, PublicationRequest request, WebRuntime runtime, CancellationToken ct) => { await runtime.PublishAsync(id, request.UseCurrentDestination, ct); return Results.Ok(); });
        api.MapPost("/jobs/{id}/delete", async (string id, WebRuntime runtime, CancellationToken ct) => { await runtime.DeleteAsync(id, ct); return Results.Ok(); });
        api.MapPost("/destination", async (DestinationRequest request, WebRuntime runtime, CancellationToken ct) => { await runtime.SaveDestinationAsync(request, ct); return Results.Ok(); });
        api.MapPost("/destination/check", async (WebRuntime runtime, CancellationToken ct) => { await runtime.CheckDestinationAsync(false, ct); return Results.Ok(); });
        api.MapPost("/destination/share", async (WebRuntime runtime, CancellationToken ct) => { await runtime.CheckDestinationAsync(true, ct); return Results.Ok(); });
        api.MapPost("/prompt", (PromptRequest request) => new { prompt = NarrationPrompt.Build(request.Brief, DateOnly.FromDateTime(DateTime.Today)) });
        api.MapGet("/health", () => new { status = "ready" });
    }
}
