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
    private const string TestLinkPath = "/test/link";

    /// <summary>A fixed hash key, so tests can recompute the hashes the API stores.</summary>
    public static readonly byte[] HashKey = System.Text.Encoding.UTF8.GetBytes("prikboard-test-hash-key-0123456789abcdef");

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
        builder.UseSetting("Auth:HashKey", Convert.ToBase64String(HashKey));
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestLogin>());
    }

    /// <summary>A client without redirects, so tests can inspect login responses.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// A client holding a session cookie, as if the person had signed in with a provider. The subject defaults to the
    /// email, the provider's name to the email's local part, and the email counts as verified unless told otherwise.
    /// </summary>
    public async Task<HttpClient> CreateSignedInClientAsync(
        string email = "tester@example.com", string? subject = null, string provider = TestProvider, bool verified = true)
    {
        var client = CreateAnonymousClient();
        var response = await TestSignInAsync(client, email, subject, provider, verified);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    /// <summary>
    /// Signs in through the same user mapping as a real provider callback. Newcomers are let in, unless a
    /// <paramref name="returnUrl"/> is given: then they need an open invite link in it, as in a real sign-in.
    /// </summary>
    public static Task<HttpResponseMessage> TestSignInAsync(
        HttpClient client, string email, string? subject = null, string provider = TestProvider, bool verified = true,
        string? returnUrl = null) =>
        client.PostAsync(
            $"{TestLoginPath}?email={Uri.EscapeDataString(email)}&subject={Uri.EscapeDataString(subject ?? email)}" +
            $"&provider={Uri.EscapeDataString(provider)}&verified={verified}" +
            (returnUrl is null ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}"), null);

    /// <summary>Links another provider identity to the signed-in user, like a link round trip. Answers with the LinkResult name.</summary>
    public static async Task<string> TestLinkAsync(HttpClient client, string provider, string subject, string? email = null)
    {
        var response = await client.PostAsync(
            $"{TestLinkPath}?provider={Uri.EscapeDataString(provider)}&subject={Uri.EscapeDataString(subject)}" +
            $"&email={Uri.EscapeDataString(email ?? "")}", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Test-only stand-in for a provider round trip, added in front of the app so the API itself ships no sign-in shortcut.
    /// Answers 204 with a session cookie.
    /// </summary>
    private class TestLogin : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Not app.Map: that moves the path into PathBase, and the cookie handler would scope the cookie to it.
            app.Use(async (context, nextMiddleware) =>
            {
                var query = context.Request.Query;
                if (context.Request.Path == TestLinkPath)
                {
                    var session = await context.AuthenticateAsync(AuthSetup.CookieScheme);
                    var linkEmail = query["email"].ToString() is { Length: > 0 } e ? e : null;
                    var result = await UserSignIn.LinkAsync(context, session.Principal!.UserId(), query["provider"]!,
                        query["subject"]!, linkEmail);
                    await context.Response.WriteAsync(result.ToString());
                    return;
                }
                if (context.Request.Path != TestLoginPath)
                {
                    await nextMiddleware(context);
                    return;
                }

                var email = query["email"].ToString();
                var verified = query["verified"].ToString() != "False";
                // Without returnUrl the newcomer counts as let in already; with one, the real invite-only rule applies.
                var signUp = query.ContainsKey("returnUrl")
                    ? UserSignIn.SignUp.Invite(UserSignIn.InviteTokenIn(query["returnUrl"]))
                    : UserSignIn.SignUp.Always;
                var principal = await UserSignIn.SignInAsync(context, query["provider"].ToString() is { Length: > 0 } p ? p : TestProvider,
                    query["subject"].ToString() is { Length: > 0 } s ? s : email, email.Split('@')[0], verified ? email : null, signUp);
                if (principal is null)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync(UserSignIn.InviteRequiredError);
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
