using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;
using PrikBoard.Api.Endpoints;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PrikBoardDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("PrikBoard")));

// Strict number handling keeps numbers typed as plain integers in the OpenAPI schema (no "integer or string").
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddHealthChecks();
builder.AddPrikBoardAuth();
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
            Machines authenticate with an OAuth 2.0 access token from a trusted identity provider, usually obtained with
            the Client Credentials grant for the audience api://prikboard, sent as "Authorization: Bearer <token>".
            Scope boards:read allows GET requests; boards:write allows everything.
            """,
    };

    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes[AuthSetup.BearerSecurityScheme] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "An OAuth 2.0 access token from a trusted issuer (for example via Client Credentials), " +
                      "with scope boards:read (GET only) or boards:write. Browsers use a session cookie instead.",
    };
    document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(AuthSetup.BearerSecurityScheme, document)] = [] }];
    return Task.CompletedTask;
}).AddOperationTransformer((operation, context, _) =>
{
    // Anonymous operations opt out of the document-wide security requirement.
    if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) operation.Security = [];
    return Task.CompletedTask;
}));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// The OpenAPI document is the contract for AI agents and generated clients, so it is served in every environment
// and without signing in.
app.MapOpenApi().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();

// Interactive API reference at /scalar. People sign in first (with the default provider, GitHub), so "Try it"
// requests carry their session cookie. Machines can paste a Bearer token in its authentication panel instead.
app.MapScalarApiReference("/scalar", options => options
        .WithTitle("PrikBoard API")
        .DisableTelemetry()
        .DisableAgent())
    .RedirectToSignIn();

// Everything under /api requires a signed-in user (the fallback policy) unless an endpoint allows anonymous access.
var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapBoardEndpoints();
api.MapBucketEndpoints();
api.MapNoteEndpoints();

app.Run();
