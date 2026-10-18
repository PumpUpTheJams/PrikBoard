using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Tests;

/// <summary>
/// Runs the real API in memory against a throwaway PostgreSQL database, created on the server from
/// <c>compose.yaml</c> (or the one in <c>PRIKBOARD_TEST_POSTGRES</c>) and dropped again on dispose.
/// </summary>
public class PrikBoardApiFactory : WebApplicationFactory<Program>
{
    public const string TestProvider = "test";
    private const string TestLoginPath = "/test/login";

    private const string DefaultServer = "Host=localhost;Port=5432;Username=prikboard;Password=prikboard";

    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? DefaultServer)
    {
        Database = $"prikboard_tests_{Guid.NewGuid():N}",
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Startup migrations create the database.
        builder.UseSetting("ConnectionStrings:PrikBoard", _connectionString);
        // Not Development, so the developer's user secrets stay out. appsettings.json ships a GitHub client id;
        // clearing it starts every test without sign-in providers, and test classes add the ones they need.
        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:Providers:github:ClientId", "");
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestLogin>());
    }

    /// <summary>A client without redirects, so tests can inspect login responses.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A client holding a session cookie for the given user, as if they had signed in with a provider.</summary>
    public async Task<HttpClient> CreateSignedInClientAsync(string email = "tester@example.com")
    {
        var client = CreateAnonymousClient();
        var response = await TestSignInAsync(client, email);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    /// <summary>Signs in through the same user mapping and allowlist as a real provider callback.</summary>
    public static Task<HttpResponseMessage> TestSignInAsync(HttpClient client, string email) =>
        client.PostAsync($"{TestLoginPath}?email={Uri.EscapeDataString(email)}", null);

    /// <summary>
    /// Test-only stand-in for a provider round trip, added in front of the app so the API itself ships no sign-in shortcut.
    /// Answers 204 with a session cookie, or 403 when the allowlist rejects the email.
    /// </summary>
    private class TestLogin : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Not app.Map: that moves the path into PathBase, and the cookie handler would scope the cookie to it.
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path != TestLoginPath)
                {
                    await nextMiddleware(context);
                    return;
                }

                var email = context.Request.Query["email"].ToString();
                var principal = await UserSignIn.SignInAsync(context, TestProvider, email, email.Split('@')[0], email);
                if (principal is null)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await context.SignInAsync(AuthSetup.CookieScheme, principal);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            next(app);
        };
    }

    public override async ValueTask DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>().Database.EnsureDeletedAsync();
        }
        await base.DisposeAsync();
    }
}
