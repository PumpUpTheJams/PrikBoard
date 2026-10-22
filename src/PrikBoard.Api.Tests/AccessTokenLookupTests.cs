using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using PrikBoard.Api.Auth;

namespace PrikBoard.Api.Tests;

/// <summary>Unknown personal access tokens: each costs a lookup, so one address may only try a few.</summary>
public class AccessTokenLookupTests(AccessTokenLookupTests.Factory factory) : IClassFixture<AccessTokenLookupTests.Factory>
{
    /// <summary>A low limit, so it can be reached in a test. Test requests all come from the same (unknown) address.</summary>
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting(AccessTokenLookups.Setting, "2");
        }
    }

    [Fact]
    public async Task Too_many_unknown_tokens_get_429_for_uncached_tokens_while_cached_ones_keep_working()
    {
        var owner = await factory.CreateSignedInClientAsync("lookups@example.com");
        var (known, _) = await factory.TokenAsync(owner, "Looked up");
        var (later, _) = await factory.TokenAsync(owner, "Not looked up yet");
        Assert.Equal(HttpStatusCode.OK, (await known.GetAsync("/api/me")).StatusCode);

        // Well-formed (the checksum is right) but never issued.
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(AccessTokens.Generate().Token).GetAsync("/api/me")).StatusCode);
        }
        var limited = await Bearer(AccessTokens.Generate().Token).GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        // The token looked up before is cached and still works; one that would need a lookup has to wait too.
        Assert.Equal(HttpStatusCode.OK, (await known.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await later.GetAsync("/api/me")).StatusCode);
    }

    private HttpClient Bearer(string token)
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
