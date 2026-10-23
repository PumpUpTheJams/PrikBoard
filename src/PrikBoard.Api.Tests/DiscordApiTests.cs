using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Integrations;

namespace PrikBoard.Api.Tests;

/// <summary>
/// A test server whose Discord bot never reaches Discord: <see cref="Gateway"/> stands in for its connection, and a fake
/// Discord API answers the token check, accepting <see cref="Token"/>.
/// </summary>
public class DiscordApiFactory : PrikBoardApiFactory
{
    public const string PublicUrl = "https://board.example";
    public const string Token = "MTUw.good.bot-token";
    public const string TokenWithoutIntent = "MTUw.no-intent.bot-token";
    public const string ApplicationId = "1500000000000000001";

    public FakeDiscordGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDiscordGateway>(Gateway);
            services.AddHttpClient<DiscordApplications>().ConfigurePrimaryHttpMessageHandler(() => new FakeDiscordApi());
        });
    }

    private class FakeDiscordApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/v10/applications/@me" || request.Headers.Authorization?.Scheme != "Bot")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            // Bit 19: the Message Content intent, below 100 servers.
            var flags = request.Headers.Authorization.Parameter switch
            {
                Token => 1 << 19,
                TokenWithoutIntent => 0,
                _ => -1,
            };
            return Task.FromResult(flags < 0
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = ApplicationId, name = "PrikBoard app", bot_public = true, flags, bot = new { username = "PrikBot" },
                    }),
                });
        }
    }
}

/// <summary>The bot's connection as the tests set it: in the servers of <see cref="InServers"/>, online once it has a token.</summary>
public sealed class FakeDiscordGateway : IDiscordGateway
{
    public DiscordBotState State { get; private set; }
    public string? Problem => null;
    public DiscordApplication? Application { get; private set; }
    public ConcurrentDictionary<string, DiscordServerInfo> InServers { get; } = new();
    public IReadOnlyList<DiscordServerInfo> Servers => InServers.Values.OrderBy(s => s.Name).ToList();

    /// <summary>What <see cref="DiscordBot"/> handed over last.</summary>
    public string? Token { get; private set; }
    public string? PublicUrl { get; private set; }

    public Task ApplyAsync(string? token, string? publicUrl)
    {
        (Token, PublicUrl) = (token, publicUrl);
        State = token is null ? DiscordBotState.Off : DiscordBotState.Online;
        Application = token is null ? null : new DiscordApplication(DiscordApiFactory.ApplicationId, "PrikBot", false);
        return Task.CompletedTask;
    }

    public Task<bool> LeaveAsync(string guildId) => Task.FromResult(InServers.TryRemove(guildId, out _));
}

/// <summary>
/// The Discord bot (docs/discord.md): admins set it up while PrikBoard runs, board owners pick the servers and channels
/// that preview their boards, and pasted links preview only where they may.
/// </summary>
public class DiscordApiTests(DiscordApiFactory factory) : IClassFixture<DiscordApiFactory>
{
    private const string Channel = "1100000000000000001";
    private const string OtherChannel = "1100000000000000002";
    private const string HiddenChannel = "1100000000000000003";
    private static int _guilds;

    /// <summary>The first user of the class's database, so its admin, as every test signs it in first.</summary>
    private Task<HttpClient> Admin() => factory.CreateSignedInClientAsync("discord-admin@example.com");

    /// <summary>A server of its own per test, since the tests in this class share the bot, with two channels the bot may post in.</summary>
    private string BotJoinsServer(string name = "Night owls")
    {
        var id = (1200000000000000000 + Interlocked.Increment(ref _guilds)).ToString();
        factory.Gateway.InServers[id] = new DiscordServerInfo(id, name, [new(Channel, "general"), new(OtherChannel, "dev")]);
        return id;
    }

    [Fact]
    public async Task Admins_set_up_the_bot_and_it_connects_at_once()
    {
        var admin = await Admin();
        var other = await factory.CreateSignedInClientAsync("discord-not-admin@example.com");
        var (agent, _) = await factory.TokenAsync(admin, "Admin's agent");
        foreach (var client in new[] { other, agent })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/discord")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/admin/discord", new { botToken = DiscordApiFactory.Token })).StatusCode);
        }
        // From scratch, whichever test set the bot up before.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/discord")).StatusCode);

        // Discord has to accept the token, with the Message Content intent on.
        var wrong = await admin.PutAsJsonAsync("/api/admin/discord", new { botToken = "MTUw.wrong.token" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("botToken", await wrong.Content.ReadAsStringAsync());
        var noIntent = await admin.PutAsJsonAsync("/api/admin/discord", new { botToken = DiscordApiFactory.TokenWithoutIntent });
        Assert.Contains("Message Content", await noIntent.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/discord", new { publicUrl = "https://board.example/?x" })).StatusCode);

        var saved = await admin.PutAsJsonAsync("/api/admin/discord", new { botToken = $" {DiscordApiFactory.Token} " });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var bot = (await saved.Content.ReadFromJsonAsync<DiscordBotDetail>())!;
        Assert.Equal((DiscordBotState.Online, DiscordBotSource.Setup, "PrikBot", DiscordApiFactory.ApplicationId),
            (bot.State, bot.Source, bot.BotName, bot.ApplicationId));
        Assert.Equal($"https://discord.com/oauth2/authorize?client_id={DiscordApiFactory.ApplicationId}&scope=bot&permissions=93184", bot.InviteUrl);
        // The address the admin uses is the one people paste links from, until they change it.
        Assert.Equal("http://localhost", bot.PublicUrl);
        Assert.Equal((DiscordApiFactory.Token, "http://localhost"), (factory.Gateway.Token, factory.Gateway.PublicUrl));
        Assert.DoesNotContain(DiscordApiFactory.Token, await (await admin.GetAsync("/api/admin/discord")).Content.ReadAsStringAsync());

        // Stored encrypted.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var row = await db.DiscordBotSettings.SingleAsync();
            Assert.DoesNotContain(DiscordApiFactory.Token, row.ProtectedBotToken);
        }

        var moved = await admin.PutAsJsonAsync("/api/admin/discord", new { publicUrl = $"{DiscordApiFactory.PublicUrl}/" });
        Assert.Equal(DiscordApiFactory.PublicUrl, (await moved.Content.ReadFromJsonAsync<DiscordBotDetail>())!.PublicUrl);
        Assert.Equal((DiscordApiFactory.Token, DiscordApiFactory.PublicUrl), (factory.Gateway.Token, factory.Gateway.PublicUrl));

        // Removing it logs the bot out; it can be set up again.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/discord")).StatusCode);
        Assert.Null(factory.Gateway.Token);
        var off = (await admin.GetFromJsonAsync<DiscordBotDetail>("/api/admin/discord"))!;
        Assert.Equal((DiscordBotState.Off, null, null), (off.State, off.Source, off.InviteUrl));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/discord", new { publicUrl = DiscordApiFactory.PublicUrl })).StatusCode);
        await SetUpBotAsync(admin);
    }

    [Fact]
    public async Task Owners_pick_a_channel_or_the_whole_server()
    {
        await SetUpBotAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("discord-owner@example.com");
        var ownerId = (await owner.MeAsync()).Id;
        var board = await owner.CreateBoardAsync("Roadmap");
        var guildId = BotJoinsServer();

        var servers = (await owner.GetFromJsonAsync<DiscordServers>($"/api/boards/{board.Id}/discord/servers"))!;
        Assert.Equal(DiscordBotState.Online, servers.State);
        var server = Assert.Single(servers.Servers, s => s.GuildId == guildId);
        Assert.Equal("Night owls", server.Name);
        Assert.Equal([Channel, OtherChannel], server.Channels.Select(c => c.Id));

        var connected = await ConnectAsync(owner, board.Id, guildId, Channel);
        var connection = Assert.Single(connected);
        Assert.Equal((guildId, "Night owls"), (connection.GuildId, connection.GuildName));
        Assert.Equal([new DiscordChannelDetail(Channel, "general")], connection.Channels);

        // A Viewer member named after the server, not someone to assign cards to.
        var members = (await owner.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members"))!;
        var member = Assert.Single(members, m => m.Issuer == Agent.DiscordIssuer);
        Assert.Equal((BoardRole.Viewer, "Night owls", guildId, member.PrincipalId), (member.Role, member.DisplayName, member.ClientId, connection.AgentId));
        var detail = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.DoesNotContain(detail.People, p => p.PrincipalId == member.PrincipalId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            Assert.Equal(ownerId, (await db.DiscordGuilds.SingleAsync(g => g.GuildId == guildId)).ConnectedById);
        }

        // Another channel adds to the list; the whole server replaces it; a channel then narrows it again.
        Assert.Equal([Channel, OtherChannel], Assert.Single(await ConnectAsync(owner, board.Id, guildId, OtherChannel)).Channels!.Select(c => c.Id));
        Assert.Null(Assert.Single(await ConnectAsync(owner, board.Id, guildId, null)).Channels);
        Assert.Equal([OtherChannel], Assert.Single(await ConnectAsync(owner, board.Id, guildId, OtherChannel)).Channels!.Select(c => c.Id));

        // Only servers and channels the bot is in and may post in.
        Assert.Equal(HttpStatusCode.BadRequest, (await Connect(owner, board.Id, "1299999999999999999", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Connect(owner, board.Id, guildId, HiddenChannel)).StatusCode);

        // A renamed server and channel show their new names.
        factory.Gateway.InServers[guildId] = new DiscordServerInfo(guildId, "Early birds", [new(Channel, "general"), new(OtherChannel, "development")]);
        var renamed = Assert.Single((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{board.Id}/discord"))!);
        Assert.Equal(("Early birds", "development"), (renamed.GuildName, Assert.Single(renamed.Channels!).Name));
    }

    [Fact]
    public async Task Only_owners_in_the_browser_connect_boards()
    {
        await SetUpBotAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("discord-only-owner@example.com");
        var editor = await factory.CreateSignedInClientAsync("discord-only-editor@example.com");
        var board = await owner.CreateBoardAsync();
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        var guildId = BotJoinsServer();
        var (token, _) = await factory.TokenAsync(owner, "Owner's agent");

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/api/boards/{board.Id}/discord/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Connect(editor, board.Id, guildId, Channel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Connect(token, board.Id, guildId, Channel)).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{board.Id}/discord"))!);
    }

    [Fact]
    public async Task A_board_takes_a_limited_number_of_channels_per_server()
    {
        await SetUpBotAsync(await Admin());
        var owner = await factory.CreateSignedInClientAsync("discord-channels@example.com");
        var board = await owner.CreateBoardAsync("Channels");
        var guildId = BotJoinsServer();
        await ConnectAsync(owner, board.Id, guildId, Channel);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var limit = await db.DiscordBoardChannels.SingleAsync(c => c.GuildId == guildId);
            limit.Channels = [.. Enumerable.Range(0, DiscordBoardChannels.MaxChannels - 1).Select(i => new DiscordChannel((1300000000000000000 + i).ToString(), $"c{i}")),
                new DiscordChannel(Channel, "general")];
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Conflict, (await Connect(owner, board.Id, guildId, OtherChannel)).StatusCode);
        // A channel it already has can still be picked again, and the whole server is always possible.
        await ConnectAsync(owner, board.Id, guildId, Channel);
        Assert.Null(Assert.Single(await ConnectAsync(owner, board.Id, guildId, null)).Channels);
    }

    [Fact]
    public async Task Card_and_board_links_preview_with_their_description()
    {
        await Admin();
        var owner = await factory.CreateSignedInClientAsync("discord-preview@example.com");
        var board = await owner.CreateBoardAsync("Platform team");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { description = "Where **we** plan." })).EnsureSuccessStatusCode();
        var plain = await owner.CreateCardAsync(board.Id, "Empty card");
        var noted = await owner.CreateCardAsync(board.Id, "Retry uploads");
        await AddComponent(owner, noted.Id, new { kind = "Text", text = "Second note", x = 20, y = 300 });
        await AddComponent(owner, noted.Id, new { kind = "Text", text = "## Why\n\nUploads **fail** on [flaky](https://x.example) networks.", x = 20, y = 20 });
        var checklist = await owner.CreateCardAsync(board.Id, "Launch");
        await AddComponent(owner, checklist.Id, new { kind = "Checklist", items = new[] { new { text = "a", done = true }, new { text = "b", done = false } } });
        var guildId = BotJoinsServer();
        await ConnectAsync(owner, board.Id, guildId, Channel);

        var card = await PreviewAsync(guildId, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}/cards/{noted.Number}");
        Assert.Equal(LinkPreviewKind.Card, card.Kind);
        Assert.Equal(($"{board.Key}-{noted.Number}", "Retry uploads", "Why Uploads fail on flaky networks.", CardColor.Butter, "Platform team"),
            (card.ReadableId, card.Title, card.Description, card.Color, card.BoardName));
        Assert.Equal(noted.Path, card.Path);

        // The older id form works too.
        Assert.Equal(card, await PreviewAsync(guildId, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.Id}/cards/{noted.Id}"));

        Assert.Null((await PreviewAsync(guildId, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}/cards/{plain.Number}")).Description);
        Assert.Equal("1/2 done", (await PreviewAsync(guildId, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}/cards/{checklist.Number}")).Description);

        var boardPreview = await PreviewAsync(guildId, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}");
        Assert.Equal((LinkPreviewKind.Board, "Platform team", "Where we plan.", $"/#/boards/{board.ShortId}"),
            (boardPreview.Kind, boardPreview.BoardName, boardPreview.Description, boardPreview.Path));
        Assert.Equal(boardPreview, await PreviewAsync(guildId, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.Id}/"));
    }

    [Fact]
    public async Task Open_invites_preview_without_being_spent()
    {
        await Admin();
        var owner = await factory.CreateSignedInClientAsync("discord-invite@example.com");
        await owner.RenameAsync("Daniel");
        var board = await owner.CreateBoardAsync("Secret plans");
        var connectedTo = await owner.CreateBoardAsync("Connected");
        var guildId = BotJoinsServer();
        await ConnectAsync(owner, connectedTo.Id, guildId, Channel);

        // An invite to a board the server isn't connected to still previews: holding the link is what counts.
        var invite = (await (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/invites", new { role = BoardRole.Editor }))
            .Content.ReadFromJsonAsync<CreatedInvite>())!;
        var preview = await PreviewAsync(guildId, DiscordApiFactory.PublicUrl + invite.Path, OtherChannel);
        Assert.Equal((LinkPreviewKind.Invite, "Secret plans", BoardRole.Editor, "Daniel"),
            (preview.Kind, preview.BoardName, preview.Role, preview.InvitedBy));
        Assert.Equal(invite.ExpiresAt, preview.ExpiresAt!.Value, TimeSpan.FromMilliseconds(1));

        var joiner = await factory.CreateSignedInClientAsync("discord-invite-joiner@example.com");
        Assert.Equal(HttpStatusCode.OK, (await joiner.PostAsJsonAsync("/api/invites/accept", new { token = invite.Token })).StatusCode);

        // Used now, so it previews no more.
        Assert.Null(await FindAsync(guildId, DiscordApiFactory.PublicUrl + invite.Path));
    }

    [Fact]
    public async Task Everything_else_previews_nothing()
    {
        await Admin();
        var owner = await factory.CreateSignedInClientAsync("discord-nothing@example.com");
        var board = await owner.CreateBoardAsync("Visible");
        var card = await owner.CreateCardAsync(board.Id);
        var hidden = await owner.CreateBoardAsync("Not connected");
        var hiddenCard = await owner.CreateCardAsync(hidden.Id);
        var limited = await owner.CreateBoardAsync("Channel only");
        var guildId = BotJoinsServer();
        await ConnectAsync(owner, board.Id, guildId, null);
        await ConnectAsync(owner, limited.Id, guildId, Channel);
        var site = DiscordApiFactory.PublicUrl;

        string[] links =
        [
            $"{site}/#/boards/zzzzzz",
            $"{site}/#/boards/{board.ShortId}/cards/9999",
            $"{site}/#/boards/{hidden.ShortId}",
            $"{site}/#/boards/{hidden.ShortId}/cards/{hiddenCard.Number}",
            $"{site}/#/boards/{board.Id}/cards/{hiddenCard.Id}",
            $"https://evil.example/#/boards/{board.ShortId}",
            $"http://board.example/#/boards/{board.ShortId}",
            $"{site}/other/#/boards/{board.ShortId}",
            $"{site}/#/invite/not-a-token",
            $"{site}/#/admin",
            $"/#/boards/{board.ShortId}",
            "not a link",
        ];
        foreach (var link in links) Assert.True(await FindAsync(guildId, link) is null, link);

        // The whole server sees the board everywhere; the other board only in its channel; another server nothing.
        Assert.NotNull(await FindAsync(guildId, $"{site}/#/boards/{board.ShortId}/cards/{card.Number}", OtherChannel));
        Assert.NotNull(await FindAsync(guildId, $"{site}/#/boards/{limited.ShortId}", Channel));
        Assert.Null(await FindAsync(guildId, $"{site}/#/boards/{limited.ShortId}", OtherChannel));
        Assert.Null(await FindAsync(BotJoinsServer(), $"{site}/#/boards/{board.ShortId}"));
        Assert.Null(await FindAsync(guildId, $"{site}/#/boards/{board.ShortId}", publicUrl: null));

        // A removed connection previews nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}")).StatusCode);
        Assert.Null(await FindAsync(guildId, $"{site}/#/boards/{board.ShortId}"));
    }

    [Fact]
    public async Task Owners_remove_channels_and_servers()
    {
        await Admin();
        var owner = await factory.CreateSignedInClientAsync("discord-remove@example.com");
        var editor = await factory.CreateSignedInClientAsync("discord-remove-editor@example.com");
        var board = await owner.CreateBoardAsync();
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        var guildId = BotJoinsServer();
        await ConnectAsync(owner, board.Id, guildId, Channel);
        await ConnectAsync(owner, board.Id, guildId, OtherChannel);
        var link = $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}";

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/api/boards/{board.Id}/discord")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}/channels/{Channel}")).StatusCode);
        Assert.Null(await FindAsync(guildId, link, Channel));
        Assert.NotNull(await FindAsync(guildId, link, OtherChannel));

        // The last channel takes the server off the board.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}/channels/{OtherChannel}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{board.Id}/discord"))!);
        Assert.DoesNotContain((await owner.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members"))!, m => m.Issuer == Agent.DiscordIssuer);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}")).StatusCode);

        // Connecting again starts afresh, from the new channel only.
        Assert.Equal([OtherChannel], Assert.Single(await ConnectAsync(owner, board.Id, guildId, OtherChannel)).Channels!.Select(c => c.Id));

        // Removing the server as a member disconnects it too.
        var agentId = Assert.Single((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{board.Id}/discord"))!).AgentId;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/members/{agentId}")).StatusCode);
        Assert.Null(await FindAsync(guildId, link, OtherChannel));
    }

    [Fact]
    public async Task Admins_make_the_bot_leave_a_server_and_its_boards()
    {
        var admin = await Admin();
        await SetUpBotAsync(admin);
        var owner = await factory.CreateSignedInClientAsync("discord-leave@example.com");
        var first = await owner.CreateBoardAsync("First");
        var second = await owner.CreateBoardAsync("Second");
        var guildId = BotJoinsServer("Leaving");
        await ConnectAsync(owner, first.Id, guildId, Channel);
        await ConnectAsync(owner, second.Id, guildId, null);

        var listed = Assert.Single((await admin.GetFromJsonAsync<DiscordBotDetail>("/api/admin/discord"))!.Servers, s => s.GuildId == guildId);
        Assert.Equal(("Leaving", 2), (listed.Name, listed.ChannelCount));
        Assert.Equal(["First", "Second"], listed.Boards.Select(b => b.BoardName));
        Assert.Null(listed.Boards.Single(b => b.BoardName == "Second").Channels);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/discord/servers/{guildId}")).StatusCode);
        Assert.False(factory.Gateway.InServers.ContainsKey(guildId));
        Assert.Empty((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{first.Id}/discord"))!);
        Assert.Empty((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{second.Id}/discord"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/discord/servers/{guildId}")).StatusCode);
    }

    private async Task SetUpBotAsync(HttpClient admin)
    {
        var response = await admin.PutAsJsonAsync("/api/admin/discord", new { botToken = DiscordApiFactory.Token, publicUrl = DiscordApiFactory.PublicUrl });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Connect(HttpClient owner, Guid boardId, string guildId, string? channelId) =>
        owner.PostAsJsonAsync($"/api/boards/{boardId}/discord", new { guildId, channelId });

    private static async Task<List<DiscordConnection>> ConnectAsync(HttpClient owner, Guid boardId, string guildId, string? channelId)
    {
        var response = await Connect(owner, boardId, guildId, channelId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<DiscordConnection>>())!;
    }

    private async Task<LinkPreview?> FindAsync(string guildId, string url, string channelId = Channel, string? publicUrl = DiscordApiFactory.PublicUrl)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        return await LinkPreviews.FindAsync(db, guildId, url, channelId, publicUrl, CancellationToken.None);
    }

    private async Task<LinkPreview> PreviewAsync(string guildId, string url, string channelId = Channel)
    {
        var preview = await FindAsync(guildId, url, channelId);
        Assert.True(preview is not null, url);
        return preview;
    }

    private static async Task AddComponent(HttpClient client, Guid cardId, object component) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/cards/{cardId}/components", component)).StatusCode);
}

/// <summary>Finding links in a Discord message.</summary>
public class LinkFinderTests
{
    [Fact]
    public void Links_to_the_site_are_found_once_each_without_trailing_punctuation()
    {
        var finder = new LinkFinder("https://board.example/");
        var found = finder.Find("See https://board.example/#/boards/abc/cards/1, and (https://BOARD.example/#/boards/abc). " +
                                "Not <https://board.example/#/boards/hidden>; again https://board.example/#/boards/abc/cards/1!");
        Assert.Equal(["https://board.example/#/boards/abc/cards/1", "https://BOARD.example/#/boards/abc"], found);
        Assert.Empty(finder.Find("nothing here"));
    }
}

/// <summary>The description rule of link previews, on its own.</summary>
public class CardPreviewTests
{
    [Fact]
    public void The_first_note_from_the_top_is_the_description_as_plain_text()
    {
        var card = new Card
        {
            Title = "t",
            Components =
            [
                new CardComponent { Kind = CardComponentKind.Text, X = 400, Y = 20, Text = "Right" },
                new CardComponent { Kind = CardComponentKind.Checklist, Y = 0, Items = [new ChecklistEntry { Text = "x" }] },
                new CardComponent { Kind = CardComponentKind.Text, X = 20, Y = 20, Text = "   " },
                new CardComponent { Kind = CardComponentKind.Text, X = 20, Y = 40, Text = "Below" },
            ],
        };
        Assert.Equal("Right", CardPreview.Describe(card));
    }

    [Theory]
    [InlineData("# Title\n\nSome *emphasis* and `code`.", "Title Some emphasis and code.")]
    [InlineData("- [x] done\n- [ ] open\n1. first", "done open first")]
    [InlineData("> quoted\n\n![alt](https://i.example/a.png) [text](/#/boards/abc/cards/1)", "quoted alt text")]
    [InlineData("```\nvar snake_case = 2*3;\n```", "var snake_case = 2*3;")]
    [InlineData("<b>bold</b> ~~gone~~", "bold gone")]
    public void Markdown_becomes_plain_text(string markdown, string expected) =>
        Assert.Equal(expected, CardPreview.PlainText(markdown));

    [Fact]
    public void Long_text_is_cut_at_a_word_with_an_ellipsis()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100));
        var cut = CardPreview.Cut(text);
        Assert.True(cut.Length <= CardPreview.MaxLength);
        Assert.EndsWith("word…", cut);
        Assert.Equal("short", CardPreview.Cut("short"));
        Assert.Equal(new string('a', CardPreview.MaxLength - 1) + "…", CardPreview.Cut(new string('a', 400)));
    }

    [Fact]
    public void A_checklist_card_says_how_much_is_done()
    {
        var card = new Card
        {
            Title = "t",
            Components =
            [
                new CardComponent { Kind = CardComponentKind.Checklist, Items = [new ChecklistEntry { Text = "a", Done = true }, new ChecklistEntry { Text = "b" }] },
                new CardComponent { Kind = CardComponentKind.Checklist, Items = [new ChecklistEntry { Text = "c", Done = true }] },
            ],
        };
        Assert.Equal("2/3 done", CardPreview.Describe(card));
        Assert.Null(CardPreview.Describe(new Card { Title = "empty" }));
    }
}
