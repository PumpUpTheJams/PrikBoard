using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;

namespace PrikBoard.Api.Tests;

/// <summary>
/// The first-run setup: the setup key, sign-in providers added while the app runs, the first admin, checks and the
/// recovery kit. Tests about a server without users use a factory of their own; the others sign in <see cref="Admin"/>
/// first, so that person is the admin of the class's database.
/// </summary>
public class SetupApiTests(SetupApiTests.Factory factory) : IClassFixture<SetupApiTests.Factory>
{
    /// <summary>A server with one provider in its configuration, which the setup can't change.</summary>
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Providers:corp:DisplayName", "Corp SSO");
            builder.UseSetting("Auth:Providers:corp:Authority", "https://corp.idp.test/");
            builder.UseSetting("Auth:Providers:corp:ClientId", "corp-client");
        }
    }

    private Task<HttpClient> Admin() => factory.CreateSignedInClientAsync("setup-admin@example.com");

    private static Task<HttpResponseMessage> CreateGitHub(HttpClient client, string id, string? secret = "secret") =>
        client.PostAsJsonAsync("/api/setup/providers", new { id, type = "GitHub", clientId = $"{id}-client", clientSecret = secret });

    [Fact]
    public async Task The_status_is_public_and_says_setup_is_needed_until_someone_has_an_account()
    {
        await using var empty = new PrikBoardApiFactory();
        var anonymous = empty.CreateAnonymousClient();

        var before = (await anonymous.GetFromJsonAsync<SetupStatus>("/api/setup"))!;
        await empty.CreateSignedInClientAsync("someone@example.com");
        var after = (await anonymous.GetFromJsonAsync<SetupStatus>("/api/setup"))!;

        Assert.Equal(new SetupStatus(true, false, false), before);
        Assert.Equal(new SetupStatus(false, false, false), after);
    }

    [Fact]
    public async Task The_setup_key_lets_the_first_sign_in_become_the_admin_and_then_closes()
    {
        await using var empty = new PrikBoardApiFactory();
        var key = empty.OpenSetupKey();
        var stranger = empty.CreateAnonymousClient();
        var owner = empty.CreateAnonymousClient();

        var early = await PrikBoardApiFactory.TestSignInAsync(stranger, "stranger@example.com", returnUrl: "/");
        // Lower case and without dashes works too.
        await owner.UnlockSetupAsync(key.Replace("-", "").ToLowerInvariant());
        var unlocked = (await owner.GetFromJsonAsync<SetupStatus>("/api/setup"))!;
        var signIn = await PrikBoardApiFactory.TestSignInAsync(owner, "owner@example.com", returnUrl: "/#/setup");
        var again = await empty.CreateAnonymousClient().PostAsJsonAsync("/api/setup/unlock", new { key });

        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Equal(UserSignIn.SetupRequiredError, await early.Content.ReadAsStringAsync());
        Assert.True(unlocked.Unlocked);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        Assert.True((await owner.MeAsync()).IsAdmin);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_wrong_setup_key_is_refused_and_guessing_is_rate_limited()
    {
        await using var empty = new PrikBoardApiFactory();
        empty.OpenSetupKey();
        var guesser = empty.CreateAnonymousClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await guesser.PostAsJsonAsync("/api/setup/unlock", new { key = "0000-0000-0000-0000-0000" })).StatusCode);
        }

        Assert.All(statuses.Take(10), status => Assert.Equal(HttpStatusCode.Forbidden, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Fact]
    public async Task Providers_are_managed_with_the_setup_key_or_by_admins_only()
    {
        var admin = await Admin();
        var other = await factory.CreateSignedInClientAsync("setup-other@example.com");
        var (token, _) = await factory.TokenAsync(admin, "Setup agent");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateAnonymousClient().GetAsync("/api/setup/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/setup/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/setup/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateGitHub(other, "sneaky")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/setup/providers")).StatusCode);
    }

    [Fact]
    public async Task A_provider_added_in_the_setup_signs_in_without_a_restart_and_goes_again_when_deleted()
    {
        var admin = await Admin();

        var created = await CreateGitHub(admin, "gh-live");
        var json = await created.Content.ReadAsStringAsync();
        var providers = await factory.CreateAnonymousClient().GetFromJsonAsync<List<AuthProvider>>("/api/auth/providers");
        var login = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/gh-live?returnUrl=/");
        var deleted = await admin.DeleteAsync("/api/setup/providers/gh-live");
        var after = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/gh-live");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain("secret\"", json.Replace("hasSecret\"", ""));
        var detail = JsonSerializer.Deserialize<SignInProviderDetail>(json, JsonSerializerOptions.Web)!;
        Assert.True(detail.HasSecret);
        Assert.Equal(ProviderSource.Setup, detail.Source);
        Assert.Contains(providers!, p => p.Id == "gh-live");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize?client_id=gh-live-client", login.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Provider_settings_are_checked_and_configured_providers_are_read_only()
    {
        var admin = await Admin();

        var badId = await CreateGitHub(admin, "Not OK");
        var noSecret = await CreateGitHub(admin, "gh-nosecret", secret: null);
        var noAuthority = await admin.PostAsJsonAsync("/api/setup/providers", new { id = "oidc-x", type = "OpenIdConnect", clientId = "x" });
        var plainHttp = await admin.PostAsJsonAsync("/api/setup/providers",
            new { id = "oidc-y", type = "OpenIdConnect", clientId = "y", authority = "http://idp.example.com" });
        var configured = await CreateGitHub(admin, "corp");
        var changeConfigured = await admin.PatchAsJsonAsync("/api/setup/providers/corp", new { displayName = "Mine" });
        await CreateGitHub(admin, "gh-twice");
        var twice = await CreateGitHub(admin, "gh-twice");
        var list = (await admin.GetFromJsonAsync<List<SignInProviderDetail>>("/api/setup/providers"))!;

        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noSecret.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noAuthority.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, plainHttp.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, configured.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changeConfigured.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        var corp = Assert.Single(list, p => p.Id == "corp");
        Assert.Equal(ProviderSource.Configuration, corp.Source);
        Assert.Equal("Corp SSO", corp.DisplayName);
    }

    [Fact]
    public async Task A_provider_made_the_default_is_offered_first()
    {
        var admin = await Admin();
        await CreateGitHub(admin, "gh-default");

        var updated = await admin.PatchAsJsonAsync("/api/setup/providers/gh-default", new { isDefault = true, displayName = "GitHub here" });
        var providers = (await factory.CreateAnonymousClient().GetFromJsonAsync<List<AuthProvider>>("/api/auth/providers"))!;
        await admin.PatchAsJsonAsync("/api/setup/providers/gh-default", new { isDefault = false });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(new AuthProvider("gh-default", "GitHub here"), providers[0]);
    }

    [Fact]
    public async Task The_last_provider_stays_once_people_have_accounts()
    {
        await using var server = new PrikBoardApiFactory();
        var admin = await server.CreateSignedInClientAsync("only-admin@example.com");
        await CreateGitHub(admin, "gh-one");

        var last = await admin.DeleteAsync("/api/setup/providers/gh-one");
        await CreateGitHub(admin, "gh-two");
        var notLast = await admin.DeleteAsync("/api/setup/providers/gh-one");

        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, notLast.StatusCode);
    }

    [Fact]
    public async Task A_test_sign_in_goes_to_the_provider()
    {
        var admin = await Admin();
        await CreateGitHub(admin, "gh-test");

        var test = await admin.GetAsync("/api/setup/providers/gh-test/test?returnUrl=/%23/admin");

        Assert.Equal(HttpStatusCode.Redirect, test.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize?client_id=gh-test-client", test.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_GitHub_App_manifest_creates_the_provider_when_GitHub_comes_back()
    {
        var admin = await Admin();

        var begun = await admin.PostAsJsonAsync("/api/setup/github-app", new { id = "gh-app", name = "PrikBoard test", returnUrl = "/#/setup" });
        var started = (await begun.Content.ReadFromJsonAsync<GitHubAppManifest>())!;
        using var manifest = JsonDocument.Parse(started.Manifest);
        var state = Uri.UnescapeDataString(started.PostUrl[(started.PostUrl.IndexOf("state=", StringComparison.Ordinal) + 6)..]);
        var refused = await admin.GetAsync($"/api/setup/github-app/callback?code=wrong&state={Uri.EscapeDataString(state)}");
        var forged = await admin.GetAsync($"/api/setup/github-app/callback?code={PrikBoardApiFactory.GitHubAppCode}&state=forged");
        var back = await admin.GetAsync($"/api/setup/github-app/callback?code={PrikBoardApiFactory.GitHubAppCode}&state={Uri.EscapeDataString(state)}");
        var list = (await admin.GetFromJsonAsync<List<SignInProviderDetail>>("/api/setup/providers"))!;

        Assert.Equal(HttpStatusCode.OK, begun.StatusCode);
        Assert.StartsWith("https://github.com/settings/apps/new?state=", started.PostUrl);
        Assert.Equal("http://localhost/api/auth/callback/gh-app", manifest.RootElement.GetProperty("callback_urls")[0].GetString());
        Assert.Equal("read", manifest.RootElement.GetProperty("default_permissions").GetProperty("email_addresses").GetString());
        Assert.StartsWith("/?setupGitHub=failed", refused.Headers.Location!.ToString());
        Assert.StartsWith("/?setupGitHub=failed", forged.Headers.Location!.ToString());
        Assert.Equal("/?setupGitHub=ok&setupProvider=gh-app#/setup", back.Headers.Location!.ToString());
        var app = Assert.Single(list, p => p.Id == "gh-app");
        Assert.Equal(PrikBoardApiFactory.GitHubAppClientId, app.ClientId);
        Assert.Equal("https://github.com/apps/prikboard-test", app.GitHubAppUrl);
        Assert.True(app.HasSecret);
    }

    [Fact]
    public async Task The_checks_report_how_the_server_runs()
    {
        var admin = await Admin();

        var checks = (await admin.GetFromJsonAsync<List<SystemCheck>>("/api/setup/checks"))!;

        Assert.Equal(["database", "migrations", "storage", "keys", "hashKey", "https", "providers", "version"], checks.Select(c => c.Id));
        Assert.All(checks.Where(c => c.Id is "database" or "migrations" or "storage" or "keys" or "hashKey" or "https" or "providers"),
            c => Assert.Equal(CheckStatus.Ok, c.Status));
    }

    [Fact]
    public async Task The_recovery_kit_holds_the_hash_key_for_admins_only()
    {
        var admin = await Admin();
        var other = await factory.CreateSignedInClientAsync("setup-kit@example.com");

        var kit = await admin.GetAsync("/api/setup/recovery-kit");
        var refused = await other.GetAsync("/api/setup/recovery-kit");

        Assert.Equal(HttpStatusCode.OK, kit.StatusCode);
        Assert.True(kit.Headers.CacheControl!.NoStore);
        Assert.Equal("prikboard-recovery-kit.txt", kit.Content.Headers.ContentDisposition!.FileName);
        Assert.Contains(Convert.ToBase64String(PrikBoardApiFactory.HashKey), await kit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Recovery_mode_opens_the_providers_again_while_people_have_accounts()
    {
        await using var server = new RecoveryFactory();
        await server.CreateSignedInClientAsync("recovering@example.com");
        var key = server.OpenSetupKey();
        var rescuer = server.CreateAnonymousClient();

        await rescuer.UnlockSetupAsync(key);
        var status = (await rescuer.GetFromJsonAsync<SetupStatus>("/api/setup"))!;
        var created = await CreateGitHub(rescuer, "gh-rescue");

        Assert.Equal(new SetupStatus(false, true, true), status);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private class RecoveryFactory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting(SetupKey.RecoveryKey, "true");
        }
    }

    [Fact]
    public async Task Without_a_configured_hash_key_one_is_generated_into_the_data_folder_and_kept()
    {
        await using var first = new GeneratedKeyFactory();
        // Signing in makes the first Data Protection key.
        await first.CreateSignedInClientAsync("keeper@example.com");
        var file = Path.Combine(first.FilesPath, KeyedHasher.KeyFileName);
        var written = await File.ReadAllTextAsync(file);
        var key = first.Services.GetRequiredService<KeyedHasher>();

        await using var second = new GeneratedKeyFactory { FilesPath = first.FilesPath };
        second.CreateClient();
        var again = second.Services.GetRequiredService<KeyedHasher>();

        Assert.Equal(32, Convert.FromBase64String(written).Length);
        Assert.Equal(HashKeySource.File, key.Source);
        Assert.Equal(Convert.FromBase64String(written), key.Key);
        Assert.Equal(key.Key, again.Key);
        // The Data Protection keys are in the data folder too.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(first.FilesPath, "keys"), "*.xml"));
    }

    /// <summary>Not Development or Testing, and no Auth:HashKey: as in the container image.</summary>
    private class GeneratedKeyFactory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.UseSetting(KeyedHasher.ConfigKey, "");
        }
    }
}
