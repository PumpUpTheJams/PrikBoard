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

    public WebApplication FakeSlack { get; }
    public FakeWorkspace Workspace => FakeSlack.Services.GetRequiredService<FakeWorkspace>();

    public SlackApiFactory()
    {
        FakeSlack = FakeSlackApp.Build(["--urls", "http://127.0.0.1:0"]);
        FakeSlack.StartAsync().GetAwaiter().GetResult();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
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
        Assert.Equal((Fake.TeamId, Fake.TeamName, 2), (workspace.TeamId, workspace.Name, workspace.ChannelCount));
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
        Assert.Equal([(SlackApiFactory.Dev, "dev"), (SlackApiFactory.General, "general")], workspace.Channels.Select(c => (c.Id, c.Name)));

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
