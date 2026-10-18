using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using PrikBoard.Api.Data;
using PrikBoard.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PrikBoardDbContext>((services, options) =>
    options.UseSqlite(services.GetRequiredService<IConfiguration>().GetConnectionString("PrikBoard")));

// Strict number handling keeps numbers typed as plain integers in the OpenAPI schema (no "integer or string").
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new OpenApiInfo
    {
        Title = "PrikBoard API",
        Version = "v1",
        Description = """
            PrikBoard is a post-it board for small project management.
            A board is a freeform canvas holding buckets (named rectangles, by default "To do", "Doing" and "Done")
            and notes (160x140 px post-its). A note belongs to the topmost bucket containing its centre, or is loose.
            Start with listBoards, then getBoard for a complete snapshot of a project.
            Progress work by moving notes between buckets with moveNote and a bucketId; the server finds a free spot.
            Coordinates are canvas pixels from the top-left and snap to a 20 px grid.
            """,
    };
    return Task.CompletedTask;
}));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// The OpenAPI document is the contract for AI agents and generated clients, so it is served in every environment.
app.MapOpenApi();
app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapBoardEndpoints();
api.MapBucketEndpoints();
api.MapNoteEndpoints();

app.Run();
