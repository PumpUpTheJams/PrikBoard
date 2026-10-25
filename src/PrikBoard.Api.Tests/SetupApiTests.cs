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
    public async Task Setup_is_needed_until_the_setup_key_makes_the_first_sign_in_the_admin_and_then_it_closes_and_the_last_provider_stays()
    {
        await using var empty = new PrikBoardApiFactory();
        var key = empty.OpenSetupKey();
        var anonymous = empty.CreateAnonymousClient();
        var stranger = empty.CreateAnonymousClient();
        var owner = empty.CreateAnonymousClient();

        // The status is public.
        var before = (await anonymous.GetFromJsonAsync<SetupStatus>("/api/setup"))!;
        var early = await PrikBoardApiFactory.TestSignInAsync(stranger, "stranger@example.com", returnUrl: "/");
        // Lower case and without dashes works too.
        await owner.UnlockSetupAsync(key.Replace("-", "").ToLowerInvariant());
        var unlocked = (await owner.GetFromJsonAsync<SetupStatus>("/api/setup"))!;
        var signIn = await PrikBoardApiFactory.TestSignInAsync(owner, "owner@example.com", returnUrl: "/#/setup");
        var after = (await anonymous.GetFromJsonAsync<SetupStatus>("/api/setup"))!;
        var again = await empty.CreateAnonymousClient().PostAsJsonAsync("/api/setup/unlock", new { key });

        Assert.Equal(new SetupStatus(true, false, false), before);
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Equal(UserSignIn.SetupRequiredError, await early.Content.ReadAsStringAsync());
        Assert.True(unlocked.Unlocked);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        Assert.True((await owner.MeAsync()).IsAdmin);
        Assert.Equal(new SetupStatus(false, false, false), after);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Now that people have accounts, the last provider stays.
        await CreateGitHub(owner, "gh-one");
        var last = await owner.DeleteAsync("/api/setup/providers/gh-one");
        await CreateGitHub(owner, "gh-two");
        var notLast = await owner.DeleteAsync("/api/setup/providers/gh-one");

        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, notLast.StatusCode);
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
    public async Task A_provider_added_in_the_setup_signs_in_test_signs_in_and_can_be_made_the_default_without_a_restart_and_goes_again_when_deleted()
    {
        var admin = await Admin();

        var created = await CreateGitHub(admin, "gh-live");
        var json = await created.Content.ReadAsStringAsync();
        var providers = await factory.CreateAnonymousClient().GetFromJsonAsync<List<AuthProvider>>("/api/auth/providers");
        var login = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/gh-live?returnUrl=/");
        var test = await admin.GetAsync("/api/setup/providers/gh-live/test?returnUrl=/%23/admin");
        var updated = await admin.PatchAsJsonAsync("/api/setup/providers/gh-live", new { isDefault = true, displayName = "GitHub here" });
        var withDefault = (await factory.CreateAnonymousClient().GetFromJsonAsync<List<AuthProvider>>("/api/auth/providers"))!;
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
        Assert.Equal(HttpStatusCode.Redirect, test.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize?client_id=gh-live-client", test.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(new AuthProvider("gh-live", "GitHub here"), withDefault[0]);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Discord_and_Steam_providers_send_people_to_their_sign_in_pages()
    {
        var admin = await Admin();

        var discord = await admin.PostAsJsonAsync("/api/setup/providers",
            new { id = "discord-live", type = "Discord", clientId = "1234567890", clientSecret = "secret" });
        var discordWithoutSecret = await admin.PostAsJsonAsync("/api/setup/providers",
            new { id = "discord-x", type = "Discord", clientId = "1234567890" });
        // Steam has no client id; a Web API key is optional.
        var steam = await admin.PostAsJsonAsync("/api/setup/providers", new { id = "steam-live", type = "Steam", displayName = "Steam" });
        var toDiscord = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/discord-live?returnUrl=/");
        var toSteam = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/steam-live?returnUrl=/");

        Assert.Equal(HttpStatusCode.Created, discord.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, discordWithoutSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Created, steam.StatusCode);
        Assert.StartsWith("https://discord.com/oauth2/authorize?client_id=1234567890", toDiscord.Headers.Location!.ToString());
        var steamLogin = toSteam.Headers.Location!;
        Assert.Equal("https://steamcommunity.com/openid/login", steamLogin.GetLeftPart(UriPartial.Path));
        Assert.Contains(Uri.EscapeDataString("http://localhost/api/auth/callback/steam-live"), steamLogin.Query);
    }

    [Fact]
    public async Task Slack_providers_send_people_to_sign_in_with_slack()
    {
        var admin = await Admin();

        var slack = await admin.PostAsJsonAsync("/api/setup/providers",
            new { id = "slack-live", type = "Slack", clientId = "123.456", clientSecret = "secret" });
        var withoutSecret = await admin.PostAsJsonAsync("/api/setup/providers", new { id = "slack-x", type = "Slack", clientId = "123.456" });
        var withAuthority = await admin.PostAsJsonAsync("/api/setup/providers",
            new { id = "slack-y", type = "Slack", clientId = "123.456", clientSecret = "secret", authority = "https://slack.com" });
        var toSlack = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/slack-live?returnUrl=/");

        Assert.Equal(HttpStatusCode.Created, slack.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutSecret.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withAuthority.StatusCode);
        var login = toSlack.Headers.Location!;
        Assert.Equal("https://slack.com/openid/connect/authorize", login.GetLeftPart(UriPartial.Path));
        Assert.Contains("client_id=123.456", login.Query);
        Assert.Contains("scope=openid%20profile%20email", login.Query);
        Assert.Contains(Uri.EscapeDataString("http://localhost/api/auth/callback/slack-live"), login.Query);
    }

    [Fact]
    public void Slack_sign_ins_are_known_by_workspace_and_user()
    {
        using var info = System.Text.Json.JsonDocument.Parse("""
            {"ok": true, "sub": "U0JIMMY001", "https://slack.com/user_id": "U0JIMMY001", "https://slack.com/team_id": "T0FAKE0001",
             "email": "jimmy@example.com", "email_verified": true, "name": "Jimmy", "picture": "https://avatars.slack-edge.com/j.png"}
            """);
        var claims = AuthSetup.SlackClaims(info.RootElement).ToDictionary(c => c.Type, c => c.Value);
        Assert.Equal("T0FAKE0001:U0JIMMY001", claims["sub"]);
        Assert.Equal(("Jimmy", "jimmy@example.com", "true", "https://avatars.slack-edge.com/j.png"),
            (claims["name"], claims["email"], claims["email_verified"], claims["picture"]));

        using var refused = System.Text.Json.JsonDocument.Parse("""{"ok": false, "error": "invalid_auth"}""");
        Assert.Throws<Microsoft.AspNetCore.Authentication.AuthenticationFailureException>(() => AuthSetup.SlackClaims(refused.RootElement));
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
