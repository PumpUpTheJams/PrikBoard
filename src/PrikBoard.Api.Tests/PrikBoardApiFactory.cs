using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;

namespace PrikBoard.Api.Tests;

/// <summary>Runs the real API in memory against a throwaway SQLite database file.</summary>
public class PrikBoardApiFactory : WebApplicationFactory<Program>
{
    public const string TestProvider = "test";
    private const string TestLoginPath = "/test/login";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"prikboard-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Pooling off so the file is released (and deletable) when the factory is disposed.
        builder.UseSetting("ConnectionStrings:PrikBoard", $"Data Source={_databasePath};Pooling=False");
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        File.Delete(_databasePath);
    }
}
