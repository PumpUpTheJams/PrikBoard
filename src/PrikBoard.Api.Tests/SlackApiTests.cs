using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeSlack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Integrations;

namespace PrikBoard.Api.Tests;

/// <summary>
/// A test server whose Slack app talks to the fake Slack in tools/FakeSlack, on a port of its own: the real
/// <see cref="SlackGateway"/> opens its Socket Mode connection there, and tests post messages in the fake workspace
/// (<see cref="Workspace"/>) and look at what the app unfurled.
/// </summary>
public class SlackApiFactory : PrikBoardApiFactory
{
    /// <summary>What PrikBoard is reached at in tests, so the app's public address and the fake's unfurl domain.</summary>
    public const string PublicUrl = "http://localhost";

    public const string General = "C0GENERAL1";
    public const string Dev = "C0DEVELOP1";
    public const string Random = "C0RANDOM01";
    public const string Partners = "C0PARTNER1";
    public const string Jimmy = "U0JIMMY001";

    /// <summary>A channel of the tests' own, for the one test that needs a board to be the only one there.</summary>
    public const string Snapshot = "C0SNAPSHT1";

    public WebApplication FakeSlack { get; }
    public FakeWorkspace Workspace => FakeSlack.Services.GetRequiredService<FakeWorkspace>();

    public SlackApiFactory()
    {
        FakeSlack = FakeSlackApp.Build(["--urls", "http://127.0.0.1:0"], o => o.Channels.Add(new FakeChannel(Snapshot, "snapshot")));
        FakeSlack.StartAsync().GetAwaiter().GetResult();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Tests tick the activity themselves (ChatActivity.TickAsync), and updates are posted at once.
        builder.UseSetting("Chat:ActivitySeconds", "0");
        builder.UseSetting("Chat:UpdatesDelaySeconds", "0");
        builder.UseSetting(SlackApi.ApiUrlSetting, FakeSlack.Urls.First() + "/api/");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await FakeSlack.StopAsync();
        await FakeSlack.DisposeAsync();
    }
}

/// <summary>
/// The Slack app (docs/slack.md): admins set it up while PrikBoard runs, it keeps a Socket Mode connection, board owners
/// pick the channels that preview their boards, and posted links unfurl only where they may.
/// </summary>
public class SlackApiTests(SlackApiFactory factory) : IClassFixture<SlackApiFactory>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>JSON as Slack reads it, without escaping &lt;, &amp; and the like, to compare text in it.</summary>
    private static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private FakeSlackOptions Fake => factory.Workspace.Options;

    /// <summary>The first user of the class's database, so its admin, as every test signs it in first.</summary>
    private Task<HttpClient> Admin() => factory.CreateSignedInClientAsync("slack-admin@example.com");

    [Fact]
    public async Task Admins_set_up_the_app_and_it_connects_over_socket_mode()
    {
        var admin = await Admin();
        var other = await factory.CreateSignedInClientAsync("slack-not-admin@example.com");
        var (agent, _) = await factory.TokenAsync(admin, "Admin's agent");
        foreach (var client in new[] { other, agent })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/slack")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/admin/slack", new { botToken = Fake.BotToken })).StatusCode);
        }
        // From scratch, whichever test set the app up before.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/slack")).StatusCode);
        await Eventually(() => factory.Workspace.Connections == 0);

        // The address alone can come first, so the manifest names its unfurl domain and Sign in with Slack's redirect.
        var addressFirst = await admin.PutAsJsonAsync("/api/admin/slack", new { publicUrl = "https://board.example/" });
        Assert.Equal(HttpStatusCode.OK, addressFirst.StatusCode);
        var unconnected = (await addressFirst.Content.ReadFromJsonAsync<SlackAppDetail>())!;
        Assert.Equal((SlackAppState.Off, "https://board.example"), (unconnected.State, unconnected.PublicUrl));
        Assert.Contains("\"board.example\"", unconnected.Manifest);
        Assert.Contains("\"https://board.example/api/auth/callback/slack\"", unconnected.Manifest);
        // Never as user scopes: Slack won't install an app that lists Sign in with Slack's scopes.
        Assert.DoesNotContain("openid", unconnected.Manifest);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/slack")).StatusCode);

        // Both tokens the first time, each the right kind, and each accepted by Slack.
        await AssertInvalidAsync(admin, new { botToken = Fake.BotToken }, "appToken");
        await AssertInvalidAsync(admin, new { appToken = Fake.AppToken }, "botToken");
        await AssertInvalidAsync(admin, new { botToken = Fake.AppToken, appToken = Fake.AppToken }, "botToken");
        await AssertInvalidAsync(admin, new { botToken = "xoxb-wrong", appToken = Fake.AppToken }, "botToken");
        await AssertInvalidAsync(admin, new { botToken = Fake.BotToken, appToken = "xapp-wrong" }, "appToken");
        await AssertInvalidAsync(admin, new { botToken = Fake.BotToken, appToken = Fake.AppToken, publicUrl = "https://board.example/#x" }, "publicUrl");

        var saved = await admin.PutAsJsonAsync("/api/admin/slack", new { botToken = $" {Fake.BotToken} ", appToken = Fake.AppToken });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var app = (await saved.Content.ReadFromJsonAsync<SlackAppDetail>())!;
        Assert.Equal((SlackAppSource.Setup, Fake.TeamId, Fake.TeamName, Fake.BotName), (app.Source, app.TeamId, app.TeamName, app.BotName));
        // The address the admin uses is the one people paste links from, and its host is the unfurl domain.
        Assert.Equal(SlackApiFactory.PublicUrl, app.PublicUrl);
        Assert.Contains("\"localhost\"", app.Manifest);
        Assert.Contains("link_shared", app.Manifest);
        Assert.StartsWith("https://api.slack.com/apps?new_app=1&manifest_json=", app.CreateAppUrl);

        // Online over Socket Mode, in the fake workspace, with the channels it was added to.
        var online = await OnlineAsync(admin);
        var workspace = Assert.Single(online.Workspaces);
        Assert.Equal((Fake.TeamId, Fake.TeamName, 3), (workspace.TeamId, workspace.Name, workspace.ChannelCount));
        Assert.Equal(1, factory.Workspace.Connections);
        var json = await (await admin.GetAsync("/api/admin/slack")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Fake.BotToken, json);
        Assert.DoesNotContain(Fake.AppToken, json);

        // Stored encrypted.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var row = await db.SlackAppSettings.SingleAsync();
            Assert.DoesNotContain(Fake.BotToken, row.ProtectedBotToken);
            Assert.DoesNotContain(Fake.AppToken, row.ProtectedAppToken);
        }

        // The public address can change on its own; the tokens stay.
        var moved = await admin.PutAsJsonAsync("/api/admin/slack", new { publicUrl = "https://board.example/" });
        Assert.Equal("https://board.example", (await moved.Content.ReadFromJsonAsync<SlackAppDetail>())!.PublicUrl);
        Assert.Equal(SlackApiFactory.PublicUrl,
            (await (await admin.PutAsJsonAsync("/api/admin/slack", new { publicUrl = SlackApiFactory.PublicUrl })).Content.ReadFromJsonAsync<SlackAppDetail>())!.PublicUrl);

        // Removing it disconnects; it can be set up again.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/slack")).StatusCode);
        await Eventually(() => factory.Workspace.Connections == 0);
        var off = (await admin.GetFromJsonAsync<SlackAppDetail>("/api/admin/slack"))!;
        Assert.Equal((SlackAppState.Off, null), (off.State, off.Source));
        await SetUpAppAsync(admin);
    }

    [Fact]
    public async Task The_app_reconnects_when_slack_asks_and_shows_in_the_system_checks()
    {
        var admin = await Admin();
        await SetUpAppAsync(admin);

        Assert.Equal(1, await factory.Workspace.DisconnectAllAsync());
        // A new connection, from a new apps.connections.open.
        var opened = factory.Workspace.Calls.Count(c => c.Method == "apps.connections.open");
        await Eventually(() => factory.Workspace.Calls.Count(c => c.Method == "apps.connections.open") > opened && factory.Workspace.Connections == 1);
        await OnlineAsync(admin);

        var checks = (await admin.GetFromJsonAsync<List<SystemCheck>>("/api/setup/checks"))!;
        var slack = Assert.Single(checks, c => c.Id == "slack");
        Assert.Equal(CheckStatus.Ok, slack.Status);
    }

    [Fact]
    public async Task Owners_pick_a_channel_or_the_whole_workspace()
    {
        await SetUpAppAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("slack-owner@example.com");
        var board = await owner.CreateBoardAsync("Roadmap");

        // Only channels the app was added to, and none shared with other organizations.
        var workspaces = (await owner.GetFromJsonAsync<SlackWorkspaces>($"/api/boards/{board.Id}/slack/workspaces"))!;
        Assert.Equal(SlackAppState.Online, workspaces.State);
        var workspace = Assert.Single(workspaces.Workspaces);
        Assert.Equal([(SlackApiFactory.Dev, "dev"), (SlackApiFactory.General, "general"), (SlackApiFactory.Snapshot, "snapshot")],
            workspace.Channels.Select(c => (c.Id, c.Name)));

        var connection = Assert.Single(await ConnectAsync(owner, board.Id, SlackApiFactory.General));
        Assert.Equal((Fake.TeamId, Fake.TeamName, false), (connection.TeamId, connection.TeamName, connection.WholeWorkspace));
        Assert.Equal([SlackApiFactory.General], connection.Channels.Select(c => c.Id));

        // A Viewer member named after the workspace, never someone to assign cards to.
        var members = (await owner.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members"))!;
        var member = Assert.Single(members, m => m.Issuer == Agent.SlackIssuer);
        Assert.Equal((BoardRole.Viewer, Fake.TeamName, Fake.TeamId, connection.AgentId), (member.Role, member.DisplayName, member.ClientId, member.PrincipalId));
        var detail = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.DoesNotContain(detail.People, p => p.PrincipalId == member.PrincipalId);

        var whole = Assert.Single(await ConnectAsync(owner, board.Id, null));
        Assert.True(whole.WholeWorkspace);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}/whole-workspace")).StatusCode);
        Assert.False(Assert.Single((await owner.GetFromJsonAsync<List<SlackConnection>>($"/api/boards/{board.Id}/slack"))!).WholeWorkspace);

        // Only the app's workspace and channels.
        Assert.Equal(HttpStatusCode.BadRequest, (await Connect(owner, board.Id, SlackApiFactory.Random)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Connect(owner, board.Id, SlackApiFactory.Partners)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/slack", new { teamId = "T0OTHER001" })).StatusCode);

        // The last channel takes the workspace off the board.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}/channels/{SlackApiFactory.General}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<SlackConnection>>($"/api/boards/{board.Id}/slack"))!);
        Assert.DoesNotContain((await owner.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members"))!, m => m.Issuer == Agent.SlackIssuer);
    }

    [Fact]
    public async Task Only_owners_in_the_browser_connect_boards()
    {
        await SetUpAppAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("slack-only-owner@example.com");
        var editor = await factory.CreateSignedInClientAsync("slack-only-editor@example.com");
        var board = await owner.CreateBoardAsync();
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        var (token, _) = await factory.TokenAsync(owner, "Owner's agent");

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/api/boards/{board.Id}/slack/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Connect(editor, board.Id, SlackApiFactory.General)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Connect(token, board.Id, SlackApiFactory.General)).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<SlackConnection>>($"/api/boards/{board.Id}/slack"))!);
    }

    [Fact]
    public async Task Posted_links_unfurl_only_in_the_channels_of_their_board()
    {
        await SetUpAppAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("slack-unfurl@example.com");
        var board = await owner.CreateBoardAsync("Platform team");
        var card = await owner.CreateCardAsync(board.Id, "Retry <uploads> & more");
        var other = await owner.CreateBoardAsync("Not connected");
        var hidden = await owner.CreateCardAsync(other.Id, "Secret");
        await ConnectAsync(owner, board.Id, SlackApiFactory.General);
        var link = SlackApiFactory.PublicUrl + card.Path;

        // Not where the board isn't connected, nor a board that isn't, nor another host.
        var elsewhere = await factory.Workspace.PostAsync(SlackApiFactory.Dev, SlackApiFactory.Jimmy, $"over here {link}");
        var unconnected = await factory.Workspace.PostAsync(SlackApiFactory.General, SlackApiFactory.Jimmy, SlackApiFactory.PublicUrl + hidden.Path);
        var shown = await factory.Workspace.PostAsync(SlackApiFactory.General, SlackApiFactory.Jimmy, $"see {link} and {SlackApiFactory.PublicUrl}/#/boards/{board.ShortId}");

        var unfurls = await UnfurlsAsync(shown);
        Assert.Equal(2, unfurls.Count);
        var cardUnfurl = unfurls[link]!.ToJsonString(Plain);
        Assert.Contains($"{card.ReadableId} · Retry &lt;uploads&gt; &amp; more", cardUnfurl);
        Assert.Contains("Platform team", cardUnfurl);
        Assert.Equal("#f3d77a", unfurls[link]!["color"]!.GetValue<string>());
        Assert.Contains("Platform team", unfurls[$"{SlackApiFactory.PublicUrl}/#/boards/{board.ShortId}"]!.ToJsonString());

        await Task.Delay(300);
        Assert.Empty(Message(elsewhere).Unfurls);
        Assert.Empty(Message(unconnected).Unfurls);

        // The whole workspace previews everywhere, except a channel shared with another organization.
        await ConnectAsync(owner, board.Id, null);
        var shared = await factory.Workspace.PostAsync(SlackApiFactory.Partners, SlackApiFactory.Jimmy, link);
        var dev = await factory.Workspace.PostAsync(SlackApiFactory.Dev, SlackApiFactory.Jimmy, link);
        Assert.Single(await UnfurlsAsync(dev));
        await Task.Delay(300);
        Assert.Empty(Message(shared).Unfurls);

        // Links being typed say nothing about their channel, so they are left alone.
        var unfurlsBefore = factory.Workspace.Calls.Count(c => c.Method == "chat.unfurl");
        Assert.Equal(1, await factory.Workspace.ComposeAsync(SlackApiFactory.Jimmy, link));
        await Task.Delay(300);
        Assert.Equal(unfurlsBefore, factory.Workspace.Calls.Count(c => c.Method == "chat.unfurl"));

        // Disconnected, nothing new unfurls.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}")).StatusCode);
        var gone = await factory.Workspace.PostAsync(SlackApiFactory.General, SlackApiFactory.Jimmy, link);
        var invite = await InviteLinkAsync(owner, other.Id);
        var invited = await factory.Workspace.PostAsync(SlackApiFactory.General, SlackApiFactory.Jimmy, invite);
        // Invites preview wherever the app is connected to some board, so connect another one for this.
        Assert.Contains("You're invited to join Not connected", (await UnfurlsAsync(invited, connectFirst: () => ConnectAsync(owner, other.Id, SlackApiFactory.Dev)))[invite]!.ToJsonString(Plain));
        Assert.Empty(Message(gone).Unfurls);
    }

    [Fact]
    public async Task Unfurled_previews_follow_their_cards()
    {
        await SetUpAppAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("slack-live@example.com");
        var board = await owner.CreateBoardAsync("Live previews");
        var card = await owner.CreateCardAsync(board.Id, "Follow me");
        await ConnectAsync(owner, board.Id, SlackApiFactory.General);
        await TickAsync();
        var link = SlackApiFactory.PublicUrl + card.Path;

        // Unfurled, and remembered so it can be unfurled again.
        var posted = await factory.Workspace.PostAsync(SlackApiFactory.General, SlackApiFactory.Jimmy, link);
        await UnfurlsAsync(posted);
        await Eventually(() => RememberedAsync(posted));

        (await owner.PatchAsJsonAsync($"/api/cards/{card.Id}", new { tags = new[] { "urgent" } })).EnsureSuccessStatusCode();
        await TickAsync();
        Assert.Contains("urgent", Message(posted).Unfurls[link]!.ToJsonString(Plain));

        // Disconnected: the preview says so instead of showing a stale card.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}")).StatusCode);
        await TickAsync();
        var gone = Message(posted).Unfurls[link]!.ToJsonString(Plain);
        Assert.Contains("No longer available", gone);
        Assert.DoesNotContain("Follow me", gone);

        // A message deleted in Slack is forgotten.
        Assert.True(factory.Workspace.Delete(SlackApiFactory.General, posted.Ts));
        await ConnectAsync(owner, board.Id, SlackApiFactory.General);
        (await owner.PatchAsJsonAsync($"/api/cards/{card.Id}", new { tags = new[] { "later" } })).EnsureSuccessStatusCode();
        await TickAsync();
        Assert.False(await RememberedAsync(posted));
    }

    [Fact]
    public async Task Channels_post_the_updates_they_follow()
    {
        await SetUpAppAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("slack-updates@example.com");
        await owner.RenameAsync("Jimmy");
        var board = await owner.CreateBoardAsync("Slack updates");
        var other = await owner.CreateBoardAsync("Not followed");
        var done = board.Buckets.Single(b => b.Name == "Done");
        await ConnectAsync(owner, board.Id, SlackApiFactory.Dev);

        var set = await owner.PutAsJsonAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}/channels/{SlackApiFactory.Dev}",
            new { updates = new[] { "NewCards", "Moves" } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal([ChatUpdate.NewCards, ChatUpdate.Moves], Assert.Single((await set.Content.ReadFromJsonAsync<List<SlackConnection>>())!).Channels[0].Updates);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}/channels/{SlackApiFactory.General}",
            new { updates = Array.Empty<string>() })).StatusCode);
        await TickAsync();
        var before = BotPosts(SlackApiFactory.Dev).Count;

        var card = await owner.CreateCardAsync(board.Id, "Fix <login>");
        (await owner.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { bucketId = done.Id })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/cards/{card.Id}", new { tags = new[] { "not-followed" } })).EnsureSuccessStatusCode();
        await owner.CreateCardAsync(other.Id, "Elsewhere");
        await TickAsync();

        var post = Assert.Single(BotPosts(SlackApiFactory.Dev).Skip(before));
        Assert.Equal("Updates on Slack updates", post.Text);
        var json = post.Attachments!.ToJsonString(Plain);
        Assert.Contains($"|{card.ReadableId}> Fix &lt;login&gt; added to *Done* by Jimmy", json);
        Assert.Contains("To do → *Done* · Jimmy", json);
        Assert.DoesNotContain("not-followed", json);
        Assert.DoesNotContain("Elsewhere", json);

        // Nothing new, nothing posted.
        await TickAsync();
        Assert.Equal(before + 1, BotPosts(SlackApiFactory.Dev).Count);
    }

    [Fact]
    public async Task Slash_command_shows_the_board_only_to_whoever_asked()
    {
        await SetUpAppAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("slack-snapshot@example.com");
        var board = await owner.CreateBoardAsync("Snapshot board");
        await owner.CreateCardAsync(board.Id, "First thing");
        await ConnectAsync(owner, board.Id, SlackApiFactory.Snapshot);

        var answer = await CommandAsync(SlackApiFactory.Snapshot, "/prikboard board");
        Assert.Equal(SlackApiFactory.Jimmy, answer.User);
        Assert.Equal("ephemeral", answer.Body!["response_type"]!.GetValue<string>());
        var json = answer.Body.ToJsonString(Plain);
        Assert.Contains("Snapshot board", json);
        Assert.Contains("To do (1)", json);
        Assert.Contains("First thing", json);

        Assert.Contains("No PrikBoard board is connected", (await CommandAsync(SlackApiFactory.Random, "/prikboard board")).Body!.ToJsonString(Plain));
        Assert.Contains("shared with other organizations", (await CommandAsync(SlackApiFactory.Partners, "/prikboard board")).Body!.ToJsonString(Plain));
        Assert.Contains("Try `/prikboard board`", (await CommandAsync(SlackApiFactory.Snapshot, "/prikboard")).Body!.ToJsonString(Plain));
    }

    [Fact]
    public async Task Save_to_PrikBoard_links_the_account_and_saves_as_that_person()
    {
        await SetUpAppAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("slack-save@example.com");
        var ownerId = (await owner.MeAsync()).Id;
        var viewer = await factory.CreateSignedInClientAsync("slack-save-viewer@example.com");
        var board = await owner.CreateBoardAsync("Inbox");
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        var doing = board.Buckets.Single(b => b.Name == "Doing");
        await ConnectAsync(owner, board.Id, SlackApiFactory.Snapshot);
        // Saved messages from this channel go to Doing first.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}/channels/{SlackApiFactory.Snapshot}",
            new { updates = Array.Empty<string>(), saveBucketId = Guid.NewGuid() })).StatusCode);
        (await owner.PutAsJsonAsync($"/api/boards/{board.Id}/slack/{Fake.TeamId}/channels/{SlackApiFactory.Snapshot}",
            new { updates = Array.Empty<string>(), saveBucketId = doing.Id })).EnsureSuccessStatusCode();
        var image = factory.Workspace.Upload("screenshot.png", "image/png", TestImages.Png(3, 2));
        var message = await factory.Workspace.PostAsync(SlackApiFactory.Snapshot, SlackApiFactory.Jimmy,
            "Login breaks on *Safari* &amp; Edge, says <@U0ADMIN001>: <https://example.com/log|the log>\nSecond line", [image]);

        // Not known yet: a link to connect, which the owner checks and accepts in PrikBoard.
        var connect = await ShortcutAsync(message, SlackApiFactory.Jimmy);
        Assert.Null(connect["callback_id"]);
        var code = System.Text.RegularExpressions.Regex.Match(connect.ToJsonString(Plain), "connect/slack-account\\?code=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.NotEmpty(code);
        Assert.Equal("Jimmy", (await owner.GetFromJsonAsync<SlackAccountLinkPreview>($"/api/me/slack-account/links/{code}"))!.SlackName);
        Assert.Equal(HttpStatusCode.Forbidden, (await LinkAccount(owner, code, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LinkAccount(owner, code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkAccount(owner, code)).StatusCode);
        Assert.True((await owner.GetFromJsonAsync<SlackAccountStatus>("/api/me/slack-account"))!.Linked);
        // A Discord code isn't one: each platform redeems its own.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/me/discord-account/links/{code}")).StatusCode);

        // Known now: the modal offers the board's buckets, the first one picked, and the message's first line as title.
        var save = await ShortcutAsync(message, SlackApiFactory.Jimmy);
        Assert.Equal(SlackBlocks.SaveCallback, save["callback_id"]!.GetValue<string>());
        var json = save.ToJsonString(Plain);
        Assert.Contains("\"Inbox\"", json);
        Assert.Equal(SlackBlocks.WhereValue(board.Id, doing.Id), save["blocks"]![0]!["element"]!["initial_option"]!["value"]!.GetValue<string>());
        Assert.Contains("Login breaks on *Safari* & Edge, says @Ada (admin): [the log](https://example.com/log)", json);

        var answer = await SubmitAsync(new() { ["where"] = SlackBlocks.WhereValue(board.Id, doing.Id), ["title"] = "Login on Safari" });
        Assert.Equal(SlackApiFactory.Jimmy, answer.User);
        Assert.Contains($"Saved as {board.Key}-1 on Inbox", answer.Body!.ToJsonString(Plain));
        Assert.Contains("pushpin", Message(message).Reactions);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var card = await db.Cards.SingleAsync(c => c.BoardId == board.Id);
            Assert.Equal(("Login on Safari", doing.Id, ownerId), (card.Title, card.BucketId, card.CreatedById));
            var text = card.Components.Single(c => c.Kind == CardComponentKind.Text).Text!;
            Assert.StartsWith("> Login breaks on *Safari* & Edge, says @Ada (admin): [the log](https://example.com/log)\n> Second line", text);
            Assert.Contains("From **Jimmy** in #snapshot", text);
            var link = Assert.Single(card.Components.Single(c => c.Kind == CardComponentKind.Links).Links);
            Assert.Equal(("Message in Slack", factory.Workspace.Permalink(SlackApiFactory.Snapshot, message.Ts)), (link.Title, link.Url));
            // The image came from the fake's file host, with the bot token.
            Assert.Equal(1, await db.Images.CountAsync(i => i.CardId == card.Id));
        }

        // Someone who can only view the board has nowhere to save.
        var viewerCode = (await ShortcutAsync(message, "U0ADMIN001")).ToJsonString(Plain);
        Assert.Equal(HttpStatusCode.OK, (await LinkAccount(viewer,
            System.Text.RegularExpressions.Regex.Match(viewerCode, "code=([A-Za-z0-9_-]+)").Groups[1].Value)).StatusCode);
        Assert.Contains("There is no board here you can add cards to", (await ShortcutAsync(message, "U0ADMIN001")).ToJsonString(Plain));

        // Unlinked, the next shortcut asks to connect again.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/me/slack-account")).StatusCode);
        Assert.False((await owner.GetFromJsonAsync<SlackAccountStatus>("/api/me/slack-account"))!.Linked);
        Assert.Contains("connect/slack-account", (await ShortcutAsync(message, SlackApiFactory.Jimmy)).ToJsonString(Plain));
    }

    [Fact]
    public async Task Signing_in_with_Slack_needs_no_link_to_save()
    {
        var admin = await Admin();
        await SetUpAppAsync(admin);
        // Sign in with Slack, with the app's own credentials; the provider id the manifest's redirect URL is for.
        var provider = await admin.PostAsJsonAsync("/api/setup/providers",
            new { id = SlackApp.SignInProviderId, type = "Slack", clientId = "123.456", clientSecret = "secret" });
        Assert.Equal(HttpStatusCode.Created, provider.StatusCode);

        // Signed in as Jimmy of the fake workspace: the same key the app knows people by.
        var jimmy = await factory.CreateSignedInClientAsync("slack-signin@example.com",
            subject: AuthSetup.SlackSubject(Fake.TeamId, SlackApiFactory.Jimmy), provider: SlackApp.SignInProviderId);
        var status = (await jimmy.GetFromJsonAsync<SlackAccountStatus>("/api/me/slack-account"))!;
        Assert.Equal((true, SlackAccountSource.SignIn), (status.Linked, status.Source));

        var board = await jimmy.CreateBoardAsync("Signed in with Slack");
        await ConnectAsync(jimmy, board.Id, SlackApiFactory.Snapshot);
        var message = await factory.Workspace.PostAsync(SlackApiFactory.Snapshot, SlackApiFactory.Jimmy, "Straight to a card");

        // No connect link: the modal to save opens at once.
        var save = await ShortcutAsync(message, SlackApiFactory.Jimmy);
        Assert.Equal(SlackBlocks.SaveCallback, save["callback_id"]!.GetValue<string>());
        Assert.Contains("Signed in with Slack", save.ToJsonString(Plain));
        var answer = await SubmitAsync(new() { ["where"] = SlackBlocks.WhereValue(board.Id, board.Buckets[0].Id) });
        Assert.Contains($"Saved as {board.Key}-1", answer.Body!.ToJsonString(Plain));

        // Someone of another workspace with the same user id is somebody else.
        var other = await factory.CreateSignedInClientAsync("slack-other-team@example.com",
            subject: AuthSetup.SlackSubject("T0OTHER001", SlackApiFactory.Jimmy), provider: SlackApp.SignInProviderId);
        var otherStatus = (await other.GetFromJsonAsync<SlackAccountStatus>("/api/me/slack-account"))!;
        Assert.Equal((true, SlackAccountSource.SignIn), (otherStatus.Linked, otherStatus.Source));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var accounts = factory.Services.GetRequiredService<ChatAccounts>();
        Assert.Equal((await jimmy.MeAsync()).Id,
            await accounts.FindUserAsync(db, ChatPlatform.Slack, AuthSetup.SlackSubject(Fake.TeamId, SlackApiFactory.Jimmy), CancellationToken.None));
        Assert.Equal((await other.MeAsync()).Id,
            await accounts.FindUserAsync(db, ChatPlatform.Slack, AuthSetup.SlackSubject("T0OTHER001", SlackApiFactory.Jimmy), CancellationToken.None));
    }

    [Fact]
    public void Slack_text_turns_into_names_and_markdown()
    {
        var names = new Dictionary<string, string?> { ["U1"] = "Ada" };
        Assert.Equal("@Ada and @U2 in #general, see [docs](https://x.example) or https://y.example; @here &amp; <b>",
            SlackText.Plain("<@U1> and <@U2> in <#C1|general>, see <https://x.example|docs> or <https://y.example>; <!here> &amp;amp; &lt;b&gt;",
                id => names.GetValueOrDefault(id)));
        Assert.Equal("@team", SlackText.Plain("<!subteam^S1|@team>", _ => null));
    }

    [Fact]
    public void Blocks_escape_what_people_typed()
    {
        var blocks = new SlackBlocks(SlackApiFactory.PublicUrl);
        var card = new LinkPreview(LinkPreviewKind.Card, "<!channel> board", ReadableId: "PRIK-1", Title: "Ping <@U123> | now",
            Description: "a & b", Color: CardColor.Sky, Path: "/#/boards/abcdef/cards/1", Tags: ["x>y"], Assignees: ["Ann"]);
        var json = JsonSerializer.Serialize(blocks.For(card, "ignored"), Plain);
        Assert.Contains("PRIK-1 · Ping &lt;@U123&gt; ¦ now", json);
        Assert.Contains("a &amp; b", json);
        Assert.Contains("&lt;!channel&gt; board", json);
        Assert.Contains("#9ccbf5", json);
        Assert.DoesNotContain("<@U123>", json);
    }

    private async Task SetUpAppAsync(HttpClient admin)
    {
        var response = await admin.PutAsJsonAsync("/api/admin/slack", new { botToken = Fake.BotToken, appToken = Fake.AppToken, publicUrl = SlackApiFactory.PublicUrl });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await OnlineAsync(admin);
    }

    private static async Task<SlackAppDetail> OnlineAsync(HttpClient admin)
    {
        SlackAppDetail? app = null;
        await Eventually(async () => (app = await admin.GetFromJsonAsync<SlackAppDetail>("/api/admin/slack"))!.State == SlackAppState.Online);
        return app!;
    }

    private static async Task AssertInvalidAsync(HttpClient admin, object request, string field)
    {
        var response = await admin.PutAsJsonAsync("/api/admin/slack", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> Connect(HttpClient owner, Guid boardId, string? channelId) =>
        owner.PostAsJsonAsync($"/api/boards/{boardId}/slack", new { teamId = Fake.TeamId, channelId });

    private async Task<List<SlackConnection>> ConnectAsync(HttpClient owner, Guid boardId, string? channelId)
    {
        var response = await Connect(owner, boardId, channelId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<SlackConnection>>())!;
    }

    private static async Task<string> InviteLinkAsync(HttpClient owner, Guid boardId)
    {
        var response = await owner.PostAsJsonAsync($"/api/boards/{boardId}/invites", new { role = "Editor" });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
        return $"{SlackApiFactory.PublicUrl}/#/invite/{token}";
    }

    private FakeMessage Message(FakeMessage posted) => factory.Workspace.Messages.Single(m => m.Channel == posted.Channel && m.Ts == posted.Ts);

    /// <summary>The unfurls the app attached to a message, once it did; for an invite, after <paramref name="connectFirst"/> posts it again.</summary>
    private async Task<Dictionary<string, JsonNode?>> UnfurlsAsync(FakeMessage posted, Func<Task>? connectFirst = null)
    {
        if (connectFirst is not null)
        {
            await connectFirst();
            posted = await factory.Workspace.PostAsync(posted.Channel, posted.User, posted.Text);
        }
        await Eventually(() => Message(posted).Unfurls.Count > 0);
        return Message(posted).Unfurls;
    }

    private Task TickAsync() => factory.Services.GetRequiredService<ChatActivity>().TickAsync(CancellationToken.None);

    private async Task<bool> RememberedAsync(FakeMessage posted)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        return await db.ChatPreviewMessages.AnyAsync(m => m.Platform == ChatPlatform.Slack && m.ChannelId == posted.Channel && m.MessageId == posted.Ts);
    }

    private List<FakeMessage> BotPosts(string channel) =>
        factory.Workspace.Messages.Where(m => m.Channel == channel && m.User == Fake.BotUserId).ToList();

    /// <summary>Uses Save to PrikBoard on a message as someone, and waits for the modal the app opens.</summary>
    private async Task<JsonNode> ShortcutAsync(FakeMessage message, string user)
    {
        var before = factory.Workspace.Views.Count;
        Assert.True(await factory.Workspace.ShortcutAsync(message.Channel, message.Ts, user));
        await Eventually(() => factory.Workspace.Views.Count > before);
        var view = factory.Workspace.Views[before];
        Assert.Equal(user, view.User);
        _lastView = view.Id;
        return view.View;
    }

    private string? _lastView;

    /// <summary>Submits the last modal opened, and waits for the app's answer only the person sees.</summary>
    private async Task<FakeResponse> SubmitAsync(Dictionary<string, string> values)
    {
        var before = factory.Workspace.Responses.Count;
        Assert.True(await factory.Workspace.SubmitAsync(_lastView!, values));
        await Eventually(() => factory.Workspace.Responses.Count > before);
        return factory.Workspace.Responses[before];
    }

    private static Task<HttpResponseMessage> LinkAccount(HttpClient client, string code, string origin = "http://localhost")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/slack-account") { Content = JsonContent.Create(new { code }) };
        request.Headers.Add("Origin", origin);
        return client.SendAsync(request);
    }

    /// <summary>Runs a slash command as Jimmy and waits for the app's answer.</summary>
    private async Task<FakeResponse> CommandAsync(string channel, string text)
    {
        var before = factory.Workspace.Responses.Count;
        Assert.True(await factory.Workspace.CommandAsync(channel, SlackApiFactory.Jimmy, text));
        await Eventually(() => factory.Workspace.Responses.Count > before);
        return factory.Workspace.Responses[before];
    }

    private static Task Eventually(Func<bool> done) => Eventually(() => Task.FromResult(done()));

    private static async Task Eventually(Func<Task<bool>> done)
    {
        var until = DateTimeOffset.UtcNow + Patience;
        while (!await done())
        {
            Assert.True(DateTimeOffset.UtcNow < until, "Timed out waiting for the Slack app.");
            await Task.Delay(50);
        }
    }
}
