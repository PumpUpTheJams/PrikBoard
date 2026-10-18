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
            A board holds ordered columns (by default "To do", "Doing" and "Done"); each column holds ordered notes (post-its).
            Start with listBoards, then getBoard for a complete snapshot of a project.
            Progress work by moving notes between columns with moveNote.
            Positions are zero-based; omit a position to append at the end.
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
api.MapColumnEndpoints();
api.MapNoteEndpoints();

app.Run();
