using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>A test server whose links live at <see cref="PublicUrl"/>, as the Discord bot sees them pasted.</summary>
public class DiscordApiFactory : PrikBoardApiFactory
{
    public const string PublicUrl = "https://board.example";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Discord:PublicUrl", PublicUrl);
        // Every exchange in this class comes from the same (missing) address, so its limit is raised.
        builder.UseSetting("RateLimits:DiscordConnectsPerMinute", "1000");
    }
}

/// <summary>
/// The Discord bot's side of PrikBoard (docs/discord.md): connecting a server to boards, and previewing pasted links
/// with the server's read-only token.
/// </summary>
public class DiscordApiTests(DiscordApiFactory factory) : IClassFixture<DiscordApiFactory>
{
    private const string OwnOrigin = "http://localhost";
    private const string Channel = "1100000000000000001";
    private const string OtherChannel = "1100000000000000002";
    private static int _guilds;

    /// <summary>A guild id of its own per test, since the tests in this class share a database.</summary>
    private static string NewGuildId() => (1200000000000000000 + Interlocked.Increment(ref _guilds)).ToString();

    [Fact]
    public async Task Connecting_makes_the_server_a_read_only_viewer_with_a_token_of_its_own()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-owner@example.com");
        var board = await owner.CreateBoardAsync("Roadmap");
        var ownerId = (await owner.MeAsync()).Id;
        var guildId = NewGuildId();

        var (connected, bot) = await ConnectAsync(owner, guildId, "Night owls", [new(board.Id)]);
        Assert.StartsWith("pb_pat_", connected.Token);
        Assert.Equal(DateTimeOffset.UtcNow.AddDays(365).Date, connected.ExpiresAt.UtcDateTime.Date);
        var connection = Assert.Single(connected.Boards);
        Assert.Equal((board.Id, "Roadmap"), (connection.BoardId, connection.BoardName));
        Assert.Equal([new DiscordChannelDetail(Channel, "general")], connection.Channels);

        // A Viewer member named after the server, not someone to assign cards to.
        var members = (await owner.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members"))!;
        var member = Assert.Single(members, m => m.Issuer == Agent.DiscordIssuer);
        Assert.Equal((BoardRole.Viewer, "Night owls", guildId), (member.Role, member.DisplayName, member.ClientId));
        var detail = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.DoesNotContain(detail.People, p => p.PrincipalId == member.PrincipalId);

        // The token reads, and nothing else.
        var me = await bot.MeAsync();
        Assert.Equal((CallerKind.Token, PrincipalKind.Agent, member.PrincipalId), (me.Kind, me.PrincipalKind, me.Id));
        Assert.Equal(["boards:read"], me.Scopes);
        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Spam" })).StatusCode);

        // It shows in the owner's token list, as the server.
        var token = Assert.Single((await owner.GetFromJsonAsync<List<AccessTokenDetail>>("/api/me/tokens"))!, t => t.Kind == AccessTokenKind.Discord);
        Assert.Equal(("Night owls", member.PrincipalId), (token.Name, token.AgentId));

        var status = (await bot.GetFromJsonAsync<DiscordStatus>("/api/discord/status"))!;
        Assert.Equal((guildId, "Night owls"), (status.GuildId, status.GuildName));
        Assert.Equal(board.Id, Assert.Single(status.Boards).BoardId);

        // Owners see the connection, and the server's DiscordGuild row keeps who connected it.
        var listed = Assert.Single((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{board.Id}/discord"))!);
        Assert.Equal((guildId, "Night owls", member.PrincipalId), (listed.GuildId, listed.GuildName, listed.AgentId));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        Assert.Equal(ownerId, (await db.DiscordGuilds.SingleAsync(g => g.GuildId == guildId)).ConnectedById);
    }

    [Fact]
    public async Task Connecting_again_adds_channels_and_boards_and_replaces_the_token()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-again@example.com");
        var first = await owner.CreateBoardAsync("First");
        var second = await owner.CreateBoardAsync("Second");
        var guildId = NewGuildId();

        var (_, oldBot) = await ConnectAsync(owner, guildId, "Guild", [new(first.Id)]);
        var (connected, bot) = await ConnectAsync(owner, guildId, "Guild renamed", [new(first.Id), new(second.Id, WholeServer: true)],
            channelId: OtherChannel, channelName: "dev");

        Assert.Equal(HttpStatusCode.Unauthorized, (await oldBot.GetAsync("/api/discord/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync("/api/discord/status")).StatusCode);
        Assert.Equal([first.Id, second.Id], connected.Boards.Select(b => b.BoardId).Order());
        Assert.Equal([Channel, OtherChannel], connected.Boards.Single(b => b.BoardId == first.Id).Channels!.Select(c => c.Id));
        Assert.Null(connected.Boards.Single(b => b.BoardId == second.Id).Channels);
        Assert.Single((await owner.GetFromJsonAsync<List<AccessTokenDetail>>("/api/me/tokens"))!, t => t.Kind == AccessTokenKind.Discord);
        var member = Assert.Single((await owner.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{first.Id}/members"))!, m => m.Issuer == Agent.DiscordIssuer);
        Assert.Equal("Guild renamed", member.DisplayName);
    }

    [Fact]
    public async Task A_board_takes_a_limited_number_of_channels_per_server()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-channels@example.com");
        var board = await owner.CreateBoardAsync("Channels");
        var guildId = NewGuildId();
        await ConnectAsync(owner, guildId, "Guild", [new(board.Id)]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var limit = await db.DiscordBoardChannels.SingleAsync(c => c.GuildId == guildId);
            limit.Channels = [.. Enumerable.Range(0, DiscordBoardChannels.MaxChannels).Select(i => new DiscordChannel((1300000000000000000 + i).ToString(), $"c{i}"))];
            await db.SaveChangesAsync();
        }

        var (verifier, challenge) = Pkce();
        var code = await ApproveAsync(owner, challenge, [new(board.Id)]);
        Assert.Equal(HttpStatusCode.Conflict, (await Exchange(code.Code, verifier, guildId, channelId: OtherChannel)).StatusCode);

        // A channel it already has can still be renamed, and the whole server is always possible.
        await ConnectAsync(owner, guildId, "Guild", [new(board.Id)], channelId: "1300000000000000000", channelName: "renamed");
        var (connected, _) = await ConnectAsync(owner, guildId, "Guild", [new(board.Id, WholeServer: true)], channelId: OtherChannel);
        Assert.Null(Assert.Single(connected.Boards).Channels);
    }

    [Fact]
    public async Task A_code_works_once_with_its_verifier_for_5_minutes_and_only_for_owners()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-code@example.com");
        var editor = await factory.CreateSignedInClientAsync("discord-code-editor@example.com");
        var board = await owner.CreateBoardAsync();
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);

        // Owners only, from PrikBoard's own pages, with a session.
        var challenge = Pkce().Challenge;
        Assert.Equal(HttpStatusCode.BadRequest, (await Approve(editor, challenge, [new(board.Id)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Approve(owner, challenge, [new(Guid.NewGuid())])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Approve(owner, challenge, [new(board.Id)], "https://evil.example")).StatusCode);
        var (agent, _) = await factory.TokenAsync(owner, "Agent");
        Assert.Equal(HttpStatusCode.Forbidden, (await Approve(agent, challenge, [new(board.Id)])).StatusCode);

        // A code works once, and a wrong verifier (another server's) spends it too.
        var (verifier, ok) = Pkce();
        var code = await ApproveAsync(owner, ok, [new(board.Id)]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Exchange(code.Code, Pkce().Verifier, NewGuildId())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Exchange(code.Code, verifier, NewGuildId())).StatusCode);

        var (againVerifier, again) = Pkce();
        var used = await ApproveAsync(owner, again, [new(board.Id)]);
        Assert.Equal(HttpStatusCode.Created, (await Exchange(used.Code, againVerifier, NewGuildId())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Exchange(used.Code, againVerifier, NewGuildId())).StatusCode);

        var (lateVerifier, late) = Pkce();
        var expired = await ApproveAsync(owner, late, [new(board.Id)]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(expired.Code));
            var past = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.DiscordConnectGrants.Where(g => g.CodeHash == hash).ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, past));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Exchange(expired.Code, lateVerifier, NewGuildId())).StatusCode);
    }

    [Fact]
    public async Task An_owner_who_was_demoted_before_the_exchange_connects_nothing()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-demoted@example.com");
        var other = await factory.CreateSignedInClientAsync("discord-demoted-other@example.com");
        var board = await owner.CreateBoardAsync();
        await owner.ShareAsync(board.Id, other, BoardRole.Owner);
        var (verifier, challenge) = Pkce();
        var code = await ApproveAsync(owner, challenge, [new(board.Id)]);

        var ownerId = (await owner.MeAsync()).Id;
        (await other.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{ownerId}", new { role = BoardRole.Editor })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.BadRequest, (await Exchange(code.Code, verifier, NewGuildId())).StatusCode);
        Assert.DoesNotContain((await other.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members"))!, m => m.Issuer == Agent.DiscordIssuer);
    }

    [Fact]
    public async Task Card_and_board_links_preview_with_their_description()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-preview@example.com");
        var board = await owner.CreateBoardAsync("Platform team");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { description = "Where **we** plan." })).EnsureSuccessStatusCode();
        var plain = await owner.CreateCardAsync(board.Id, "Empty card");
        var noted = await owner.CreateCardAsync(board.Id, "Retry uploads");
        await AddComponent(owner, noted.Id, new { kind = "Text", text = "Second note", x = 20, y = 300 });
        await AddComponent(owner, noted.Id, new { kind = "Text", text = "## Why\n\nUploads **fail** on [flaky](https://x.example) networks.", x = 20, y = 20 });
        var checklist = await owner.CreateCardAsync(board.Id, "Launch");
        await AddComponent(owner, checklist.Id, new { kind = "Checklist", items = new[] { new { text = "a", done = true }, new { text = "b", done = false } } });

        var (_, bot) = await ConnectAsync(owner, NewGuildId(), "Guild", [new(board.Id)]);

        var card = await PreviewAsync(bot, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}/cards/{noted.Number}");
        Assert.Equal(LinkPreviewKind.Card, card.Kind);
        Assert.Equal(($"{board.Key}-{noted.Number}", "Retry uploads", "Why Uploads fail on flaky networks.", CardColor.Butter, "Platform team"),
            (card.ReadableId, card.Title, card.Description, card.Color, card.BoardName));
        Assert.Equal(noted.Path, card.Path);

        // The older id form works too.
        var byId = await PreviewAsync(bot, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.Id}/cards/{noted.Id}");
        Assert.Equal(card, byId);

        Assert.Null((await PreviewAsync(bot, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}/cards/{plain.Number}")).Description);
        Assert.Equal("1/2 done", (await PreviewAsync(bot, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}/cards/{checklist.Number}")).Description);

        var boardPreview = await PreviewAsync(bot, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}");
        Assert.Equal((LinkPreviewKind.Board, "Platform team", "Where we plan.", $"/#/boards/{board.ShortId}"),
            (boardPreview.Kind, boardPreview.BoardName, boardPreview.Description, boardPreview.Path));
        Assert.Equal(boardPreview, await PreviewAsync(bot, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.Id}/"));

        var bare = await owner.CreateBoardAsync("No description");
        var (_, other) = await ConnectAsync(owner, NewGuildId(), "Other", [new(bare.Id)]);
        Assert.Null((await PreviewAsync(other, $"{DiscordApiFactory.PublicUrl}/#/boards/{bare.ShortId}")).Description);
    }

    [Fact]
    public async Task Open_invites_preview_without_being_spent()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-invite@example.com");
        await owner.RenameAsync("Daniel");
        var board = await owner.CreateBoardAsync("Secret plans");
        var connectedTo = await owner.CreateBoardAsync("Connected");
        var (_, bot) = await ConnectAsync(owner, NewGuildId(), "Guild", [new(connectedTo.Id)]);

        // An invite to a board the server isn't connected to still previews: holding the link is what counts.
        var invite = (await (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/invites", new { role = BoardRole.Editor }))
            .Content.ReadFromJsonAsync<CreatedInvite>())!;
        var preview = await PreviewAsync(bot, DiscordApiFactory.PublicUrl + invite.Path, OtherChannel);
        Assert.Equal((LinkPreviewKind.Invite, "Secret plans", BoardRole.Editor, "Daniel"),
            (preview.Kind, preview.BoardName, preview.Role, preview.InvitedBy));
        Assert.Equal(invite.ExpiresAt, preview.ExpiresAt!.Value, TimeSpan.FromMilliseconds(1));

        var joiner = await factory.CreateSignedInClientAsync("discord-invite-joiner@example.com");
        Assert.Equal(HttpStatusCode.OK, (await joiner.PostAsJsonAsync("/api/invites/accept", new { token = invite.Token })).StatusCode);

        // Used now, so it previews no more.
        Assert.Equal(HttpStatusCode.NotFound, (await Preview(bot, DiscordApiFactory.PublicUrl + invite.Path)).StatusCode);
    }

    [Fact]
    public async Task Everything_else_is_the_same_404()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-404@example.com");
        var board = await owner.CreateBoardAsync("Visible");
        var card = await owner.CreateCardAsync(board.Id);
        var hidden = await owner.CreateBoardAsync("Not connected");
        var hiddenCard = await owner.CreateCardAsync(hidden.Id);
        var limited = await owner.CreateBoardAsync("Channel only");
        var guildId = NewGuildId();
        var (_, bot) = await ConnectAsync(owner, guildId, "Guild", [new(board.Id, WholeServer: true), new(limited.Id)]);
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
        HashSet<string> bodies = [];
        foreach (var link in links)
        {
            var response = await Preview(bot, link);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{link}: {response.StatusCode}");
            // The same problem details every time, apart from the trace id.
            bodies.Add(Regex.Replace(await response.Content.ReadAsStringAsync(), "\"traceId\":\"[^\"]*\"", ""));
        }
        Assert.Single(bodies);

        // The whole server sees the board everywhere; the other board only in its channel.
        Assert.Equal(HttpStatusCode.OK, (await Preview(bot, $"{site}/#/boards/{board.ShortId}/cards/{card.Number}", OtherChannel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Preview(bot, $"{site}/#/boards/{limited.ShortId}", Channel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Preview(bot, $"{site}/#/boards/{limited.ShortId}", OtherChannel)).StatusCode);

        // A removed connection previews nothing, and neither does an unknown token, a person or another kind of token.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Preview(bot, $"{site}/#/boards/{board.ShortId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Preview(owner, $"{site}/#/boards/{limited.ShortId}")).StatusCode);
        var (agent, _) = await factory.TokenAsync(owner, "Agent");
        Assert.Equal(HttpStatusCode.Forbidden, (await Preview(agent, $"{site}/#/boards/{limited.ShortId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Preview(factory.CreateAnonymousClient(), $"{site}/#/boards/{limited.ShortId}")).StatusCode);
    }

    [Fact]
    public async Task Owners_remove_channels_and_servers()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-remove@example.com");
        var editor = await factory.CreateSignedInClientAsync("discord-remove-editor@example.com");
        var board = await owner.CreateBoardAsync();
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        var guildId = NewGuildId();
        await ConnectAsync(owner, guildId, "Guild", [new(board.Id)]);
        var (_, bot) = await ConnectAsync(owner, guildId, "Guild", [new(board.Id)], channelId: OtherChannel, channelName: "dev");
        var link = $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}";

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/api/boards/{board.Id}/discord")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}/channels/{Channel}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Preview(bot, link, Channel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Preview(bot, link, OtherChannel)).StatusCode);

        // The last channel takes the server off the board.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}/channels/{OtherChannel}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<DiscordConnection>>($"/api/boards/{board.Id}/discord"))!);
        Assert.DoesNotContain((await owner.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members"))!, m => m.Issuer == Agent.DiscordIssuer);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/boards/{board.Id}/discord/{guildId}")).StatusCode);

        // Connecting again starts afresh, from the new channel only.
        var (connected, _) = await ConnectAsync(owner, guildId, "Guild", [new(board.Id)], channelId: OtherChannel, channelName: "dev");
        Assert.Equal([OtherChannel], Assert.Single(connected.Boards).Channels!.Select(c => c.Id));
    }

    [Fact]
    public async Task Removing_the_server_as_a_member_disconnects_it()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-member@example.com");
        var board = await owner.CreateBoardAsync();
        var (_, bot) = await ConnectAsync(owner, NewGuildId(), "Guild", [new(board.Id, WholeServer: true)]);
        var agentId = (await bot.MeAsync()).Id;

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/members/{agentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Preview(bot, $"{DiscordApiFactory.PublicUrl}/#/boards/{board.ShortId}")).StatusCode);
        Assert.Empty((await bot.GetFromJsonAsync<DiscordStatus>("/api/discord/status"))!.Boards);
    }

    [Fact]
    public async Task The_bot_revokes_its_own_token()
    {
        var owner = await factory.CreateSignedInClientAsync("discord-revoke@example.com");
        var board = await owner.CreateBoardAsync();
        var (_, bot) = await ConnectAsync(owner, NewGuildId(), "Guild", [new(board.Id)]);

        Assert.Equal(HttpStatusCode.NoContent, (await bot.DeleteAsync("/api/discord/token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.GetAsync("/api/discord/status")).StatusCode);
        Assert.DoesNotContain((await owner.GetFromJsonAsync<List<AccessTokenDetail>>("/api/me/tokens"))!, t => t.Kind == AccessTokenKind.Discord);
    }

    private async Task<(DiscordConnected Connected, HttpClient Bot)> ConnectAsync(
        HttpClient owner, string guildId, string guildName, DiscordBoardChoice[] boards, string channelId = Channel, string channelName = "general")
    {
        var (verifier, challenge) = Pkce();
        var code = await ApproveAsync(owner, challenge, boards);
        var response = await Exchange(code.Code, verifier, guildId, guildName, channelId, channelName);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connected = (await response.Content.ReadFromJsonAsync<DiscordConnected>())!;
        var bot = factory.CreateAnonymousClient();
        bot.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connected.Token);
        return (connected, bot);
    }

    private static async Task<DiscordConnectCode> ApproveAsync(HttpClient owner, string challenge, DiscordBoardChoice[] boards)
    {
        var response = await Approve(owner, challenge, boards);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DiscordConnectCode>())!;
    }

    private static Task<HttpResponseMessage> Approve(HttpClient client, string challenge, DiscordBoardChoice[] boards, string origin = OwnOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/discord-connects")
        {
            Content = JsonContent.Create(new { codeChallenge = challenge, boards }),
        };
        request.Headers.Add("Origin", origin);
        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Exchange(
        string code, string codeVerifier, string guildId, string guildName = "Guild", string channelId = Channel, string channelName = "general") =>
        factory.CreateAnonymousClient().PostAsJsonAsync("/api/discord/connect/token",
            new { code, codeVerifier, guildId, guildName, channelId, channelName });

    private static Task<HttpResponseMessage> Preview(HttpClient bot, string url, string channelId = Channel) =>
        bot.PostAsJsonAsync("/api/link-previews", new { url, channelId });

    private static async Task<LinkPreview> PreviewAsync(HttpClient bot, string url, string channelId = Channel)
    {
        var response = await Preview(bot, url, channelId);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<LinkPreview>())!;
    }

    private static async Task AddComponent(HttpClient client, Guid cardId, object component) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/cards/{cardId}/components", component)).StatusCode);

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
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
