using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class MembershipApiTests(MembershipApiTests.Factory factory) : IClassFixture<MembershipApiTests.Factory>
{
    /// <summary>A low lookup limit, so the rate limit can be seen without many requests.</summary>
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimits:LookupsPerMinute", "4");
        }
    }

    [Fact]
    public async Task The_creator_owns_a_board_others_do_not_see_it_and_each_role_can_do_exactly_what_it_should()
    {
        var owner = await factory.CreateSignedInClientAsync("matrix-owner@example.com");
        var editor = await factory.CreateSignedInClientAsync("matrix-editor@example.com");
        var viewer = await factory.CreateSignedInClientAsync("matrix-viewer@example.com");
        var stranger = await factory.CreateSignedInClientAsync("matrix-stranger@example.com");
        var board = await owner.CreateBoardAsync("Matrix");
        Assert.Equal(BoardRole.Owner, board.YourRole);
        Assert.Contains(await owner.GetFromJsonAsync<List<BoardSummary>>("/api/boards") ?? [], b => b.Id == board.Id && b.YourRole == BoardRole.Owner);
        Assert.DoesNotContain(await stranger.GetFromJsonAsync<List<BoardSummary>>("/api/boards") ?? [], b => b.Id == board.Id);
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        var card = await owner.CreateCardAsync(board.Id);
        var bucket = board.Buckets[0];
        var image = await (await Upload(owner, board.Id)).Content.ReadFromJsonAsync<ImageDetail>();
        var tag = (await (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "matrix" })).Content.ReadFromJsonAsync<BoardTagDetail>())!;

        var component = (await (await owner.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { kind = "Text" })).Content.ReadFromJsonAsync<CardComponentDetail>())!;

        (string Method, string Url, Func<HttpContent?> Body, BoardRole Needs)[] cases =
        [
            ("GET", $"/api/boards/{board.Id}", () => null, BoardRole.Viewer),
            ("GET", $"/api/boards/{board.Id}/cards", () => null, BoardRole.Viewer),
            ("GET", $"/api/cards/{card.Id}", () => null, BoardRole.Viewer),
            ("GET", $"/api/images/{image!.Id}/content", () => null, BoardRole.Viewer),
            ("GET", $"/api/boards/{board.Id}/members", () => null, BoardRole.Viewer),
            ("POST", $"/api/boards/{board.Id}/cards", () => Json(new { title = "More" }), BoardRole.Editor),
            ("PATCH", $"/api/cards/{card.Id}", () => Json(new { title = "Renamed" }), BoardRole.Editor),
            ("POST", $"/api/cards/{card.Id}/move", () => Json(new { x = 1000, y = 1000 }), BoardRole.Editor),
            ("POST", $"/api/cards/{card.Id}/components", () => Json(new { kind = "Checklist" }), BoardRole.Editor),
            ("PATCH", $"/api/cards/{card.Id}/components/{component.Id}", () => Json(new { text = "Edited" }), BoardRole.Editor),
            ("POST", $"/api/boards/{board.Id}/buckets", () => Json(new { name = "Review" }), BoardRole.Editor),
            ("PATCH", $"/api/buckets/{bucket.Id}", () => Json(new { name = "Backlog" }), BoardRole.Editor),
            ("PATCH", $"/api/images/{image.Id}", () => Json(new { caption = "c" }), BoardRole.Editor),
            ("POST", $"/api/boards/{board.Id}/images", () => Png(), BoardRole.Editor),
            ("POST", $"/api/cards/{card.Id}/working", () => Json(new { working = true }), BoardRole.Editor),
            ("GET", $"/api/boards/{board.Id}/tags", () => null, BoardRole.Viewer),
            ("POST", $"/api/boards/{board.Id}/tags", () => Json(new { name = $"t-{Guid.NewGuid():N}"[..12] }), BoardRole.Editor),
            ("PATCH", $"/api/boards/{board.Id}/tags/{tag.Id}", () => Json(new { name = $"t-{Guid.NewGuid():N}"[..12] }), BoardRole.Editor),
            ("PATCH", $"/api/boards/{board.Id}", () => Json(new { name = "Matrix 2" }), BoardRole.Owner),
            ("GET", $"/api/boards/{board.Id}/invites", () => null, BoardRole.Owner),
            ("POST", $"/api/boards/{board.Id}/invites", () => Json(new { role = "Viewer" }), BoardRole.Owner),
            ("POST", $"/api/boards/{board.Id}/members/lookup", () => Json(new { email = "nobody@example.com" }), BoardRole.Owner),
        ];

        foreach (var (method, url, body, needs) in cases)
        {
            foreach (var (client, role) in new (HttpClient, BoardRole?)[] { (stranger, null), (viewer, BoardRole.Viewer), (editor, BoardRole.Editor), (owner, BoardRole.Owner) })
            {
                var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = body() });
                var expected = role is null ? HttpStatusCode.NotFound : role < needs ? HttpStatusCode.Forbidden : (HttpStatusCode?)null;
                if (expected is null) Assert.True(response.IsSuccessStatusCode, $"{role} {method} {url}: {response.StatusCode}");
                else Assert.True(expected == response.StatusCode, $"{role?.ToString() ?? "stranger"} {method} {url}: {response.StatusCode}");
            }
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/cards/{card.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/cards/{card.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/boards/{board.Id}/tags/{tag.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/boards/{board.Id}/tags/{tag.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/boards/{board.Id}/tags/{tag.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/cards/{card.Id}")).StatusCode);
    }

    [Fact]
    public async Task Owners_find_people_by_exact_verified_email_only()
    {
        var owner = await factory.CreateSignedInClientAsync("finder@example.com");
        var findable = await factory.CreateSignedInClientAsync("findable@example.com");
        await factory.CreateSignedInClientAsync("unverified@example.com", verified: false);
        var board = await owner.CreateBoardAsync("Lookup");
        var findableId = (await findable.MeAsync()).Id;

        var hit = await Lookup(owner, board.Id, "  FINDABLE@Example.com ");
        Assert.Equal((findableId, (BoardRole?)null), (Assert.Single(hit).PrincipalId, hit[0].Role));
        Assert.Empty(await Lookup(owner, board.Id, "findable@example.co"));
        Assert.Empty(await Lookup(owner, board.Id, "unverified@example.com"));

        await owner.ShareAsync(board.Id, findable, BoardRole.Viewer);
        Assert.Equal(BoardRole.Viewer, Assert.Single(await Lookup(owner, board.Id, "findable@example.com")).Role);

        var asViewer = await findable.PostAsJsonAsync($"/api/boards/{board.Id}/members/lookup", new { email = "finder@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);
    }

    [Fact]
    public async Task Email_lookups_are_rate_limited()
    {
        var owner = await factory.CreateSignedInClientAsync("prober@example.com");
        var board = await owner.CreateBoardAsync("Probe");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await owner.PostAsJsonAsync($"/api/boards/{board.Id}/members/lookup", new { email = $"guess{i}@example.com" })).StatusCode);
        }

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.OK, 4), HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task A_board_keeps_an_owner_and_members_can_leave()
    {
        var first = await factory.CreateSignedInClientAsync("first-owner@example.com");
        var second = await factory.CreateSignedInClientAsync("second-owner@example.com");
        var viewer = await factory.CreateSignedInClientAsync("leaver@example.com");
        var board = await first.CreateBoardAsync("Owners");
        var firstId = (await first.MeAsync()).Id;
        var viewerId = (await viewer.MeAsync()).Id;
        await first.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        Assert.Equal(HttpStatusCode.Conflict, (await first.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{firstId}", new { role = "Editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await first.DeleteAsync($"/api/boards/{board.Id}/members/{firstId}")).StatusCode);
        // A viewer cannot remove others, but can leave.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/boards/{board.Id}/members/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await viewer.DeleteAsync($"/api/boards/{board.Id}/members/{viewerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/boards/{board.Id}")).StatusCode);

        await first.ShareAsync(board.Id, second, BoardRole.Owner);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/boards/{board.Id}/members/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync($"/api/boards/{board.Id}")).StatusCode);

        var members = await second.GetFromJsonAsync<List<BoardMemberDetail>>($"/api/boards/{board.Id}/members");
        var you = Assert.Single(members!);
        Assert.True(you.IsYou);
        Assert.Equal(BoardRole.Owner, you.Role);
    }

    [Fact]
    public async Task Owners_change_roles_and_adding_twice_conflicts()
    {
        var owner = await factory.CreateSignedInClientAsync("promoter@example.com");
        var member = await factory.CreateSignedInClientAsync("promoted@example.com");
        var board = await owner.CreateBoardAsync("Roles");
        await owner.ShareAsync(board.Id, member, BoardRole.Viewer);
        var memberId = (await member.MeAsync()).Id;

        var again = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/members", new { principalId = memberId, role = "Editor" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var unknown = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/members", new { principalId = Guid.NewGuid(), role = "Editor" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{memberId}", new { role = "Editor" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Now I can" })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_board_removes_its_cards_members_and_invites()
    {
        var owner = await factory.CreateSignedInClientAsync("cleaner@example.com");
        var board = await owner.CreateBoardAsync("Gone");
        var card = await owner.CreateCardAsync(board.Id, "Gone soon");
        (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/invites", new { role = "Viewer" })).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/boards/{board.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/cards/{card.Id}")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        Assert.False(await db.BoardMembers.AnyAsync(m => m.BoardId == board.Id));
        Assert.False(await db.BoardInvites.AnyAsync(i => i.BoardId == board.Id));
    }

    [Fact]
    public void Every_api_endpoint_declares_its_board_access()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith("api") == true);

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<BoardRoleRequirement>() is null
                        && e.Metadata.GetMetadata<NoBoardScopeMarker>() is null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.Empty(undeclared);
    }

    // Public boards: anyone with the link reads them, signed in or not, but tokens still need membership.
    [Fact]
    public async Task A_private_board_is_not_found_by_anyone_but_its_members()
    {
        var owner = await factory.CreateSignedInClientAsync("public-private-owner@example.com");
        var stranger = await factory.CreateSignedInClientAsync("public-private-stranger@example.com");
        var board = await owner.CreateBoardAsync("Private");
        var anonymous = factory.CreateAnonymousClient();

        Assert.False(board.IsPublic);
        foreach (var client in new[] { anonymous, stranger })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/boards/{board.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/boards/{board.ShortId}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/boards/{board.ShortId}")).StatusCode);
    }

    [Fact]
    public async Task Anyone_reads_a_public_board_and_sees_its_people_only_when_the_owner_shows_them()
    {
        var owner = await factory.CreateSignedInClientAsync("public-read-owner@example.com");
        var board = await owner.CreateBoardAsync("Docs");
        var me = await owner.MeAsync();
        var card = await Created<CardDetail>(await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Download", assigneeIds = new[] { me.Id } }));
        var image = await Created<ImageDetail>(await owner.PostAsync($"/api/boards/{board.Id}/images",
            new ByteArrayContent(TestImages.Png(10, 10)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }));
        var made = await Ok<BoardSummary>(await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true, rules = "Read me." }));
        Assert.Equal((true, false), (made.IsPublic, made.PublicShowsPeople));

        var anonymous = factory.CreateAnonymousClient();
        var stranger = await factory.CreateSignedInClientAsync("public-read-stranger@example.com");
        foreach (var client in new[] { anonymous, stranger })
        {
            var seen = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.ShortId}"))!;
            Assert.Equal((board.Id, true, BoardRole.Viewer, "Read me."), (seen.Id, seen.Visiting, seen.YourRole, seen.Rules));
            Assert.Empty(seen.People);
            var shown = seen.Cards.Single(n => n.Id == card.Id);
            Assert.Equal(new Assignee(Guid.Empty, "", AssigneeKind.Person, null), Assert.Single(shown.Assignees));
            Assert.Equal((null, null), (shown.CreatedBy, shown.CreatedById));
            Assert.Equal(("", null), (seen.Images.Single().AddedBy, seen.Images.Single().AddedById));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.ContentUrl)).StatusCode);
            // Nobody's picture, while the board hides people.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/boards/{board.Id}/avatars/{me.Id}")).StatusCode);
        }

        // Members still see everything, and aren't visiting.
        var mine = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal((false, me.Id), (mine.Visiting, Assert.Single(mine.Cards.Single(n => n.Id == card.Id).Assignees).PrincipalId));

        // Shown to visitors, assignees and creators have their names, but the board still lists no people.
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { publicShowsPeople = true })).EnsureSuccessStatusCode();
        var withPeople = (await anonymous.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        var named = Assert.Single(withPeople.Cards);
        Assert.Equal((me.Id, me.DisplayName), (Assert.Single(named.Assignees).PrincipalId, named.Assignees[0].Name));
        Assert.Equal(me.DisplayName, named.CreatedBy);
        Assert.Empty(withPeople.People);
    }

    [Fact]
    public async Task Visitors_only_read_the_board_its_images_and_drawings()
    {
        var owner = await factory.CreateSignedInClientAsync("public-write-owner@example.com");
        var board = await owner.CreateBoardAsync("Read only");
        var card = await owner.CreateCardAsync(board.Id);
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).EnsureSuccessStatusCode();
        var anonymous = factory.CreateAnonymousClient();
        var stranger = await factory.CreateSignedInClientAsync("public-write-stranger@example.com");

        // Writes need a caller, then membership; other reads (the trash, an export, one card) are for members.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = false })).StatusCode);
        foreach (var url in new[] { $"/api/boards/{board.Id}/trash", $"/api/boards/{board.Id}/export", $"/api/cards/{card.Id}", $"/api/boards/{board.Id}/members" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }
        // The board doesn't join the stranger's list.
        Assert.DoesNotContain((await stranger.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!, b => b.Id == board.Id);
    }

    [Fact]
    public async Task Tokens_need_membership_and_a_wrong_token_is_refused()
    {
        var owner = await factory.CreateSignedInClientAsync("public-token-owner@example.com");
        var stranger = await factory.CreateSignedInClientAsync("public-token-stranger@example.com");
        var board = await owner.CreateBoardAsync("Tokens");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).EnsureSuccessStatusCode();
        var (strangerToken, _) = await factory.TokenAsync(stranger, "stranger's agent");
        var (ownerToken, _) = await factory.TokenAsync(owner, "owner's agent");
        var wrong = factory.CreateAnonymousClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pb_pat_not-a-token");

        Assert.Equal(HttpStatusCode.NotFound, (await strangerToken.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.False((await ownerToken.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Visiting);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    // Invite links.
    [Fact]
    public async Task An_invite_link_works_once_and_only_its_hash_is_stored()
    {
        var owner = await factory.CreateSignedInClientAsync("inviter@example.com");
        var guest = await factory.CreateSignedInClientAsync("guest@example.com");
        var late = await factory.CreateSignedInClientAsync("late@example.com");
        var board = await owner.CreateBoardAsync("Invites");
        await owner.PatchAsJsonAsync("/api/me", new { displayName = "Inviter" });

        var invite = await Create(owner, board.Id, "Editor");
        Assert.Equal($"/#/invite/{invite.Token}", invite.Path);
        Assert.True(invite.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6.9) && invite.ExpiresAt < DateTimeOffset.UtcNow.AddDays(7.1));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>().BoardInvites.SingleAsync(i => i.Id == invite.Id);
            Assert.Equal(KeyedHasher.InviteToken(invite.Token), stored.TokenHash);
        }

        var preview = await (await guest.PostAsJsonAsync("/api/invites/preview", new { token = invite.Token })).Content.ReadFromJsonAsync<InvitePreview>();
        Assert.Equal(("Invites", BoardRole.Editor, "Inviter", (BoardRole?)null), (preview!.BoardName, preview.Role, preview.InvitedBy, preview.YourRole));

        var accepted = await Accept(guest, invite.Token);
        Assert.Equal((board.Id, BoardRole.Editor), (accepted.BoardId, accepted.Role));
        Assert.Equal(HttpStatusCode.Created, (await guest.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Joined" })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await late.PostAsJsonAsync("/api/invites/accept", new { token = invite.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await late.PostAsJsonAsync("/api/invites/preview", new { token = invite.Token })).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<BoardInviteDetail>>($"/api/boards/{board.Id}/invites"))!);
    }

    [Fact]
    public async Task Expired_and_revoked_links_do_nothing()
    {
        var owner = await factory.CreateSignedInClientAsync("expirer@example.com");
        var guest = await factory.CreateSignedInClientAsync("too-late@example.com");
        var board = await owner.CreateBoardAsync("Expiry");
        var expired = await Create(owner, board.Id, "Viewer");
        var revoked = await Create(owner, board.Id, "Viewer");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            await db.BoardInvites.Where(i => i.Id == expired.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var pending = await owner.GetFromJsonAsync<List<BoardInviteDetail>>($"/api/boards/{board.Id}/invites");
        Assert.Equal(revoked.Id, Assert.Single(pending!).Id);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{board.Id}/invites/{revoked.Id}")).StatusCode);

        foreach (var token in new[] { expired.Token, revoked.Token, "made-up-token" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync("/api/invites/accept", new { token })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task Accepting_keeps_the_higher_role()
    {
        var owner = await factory.CreateSignedInClientAsync("granter@example.com");
        var member = await factory.CreateSignedInClientAsync("collector@example.com");
        var board = await owner.CreateBoardAsync("Roles");
        await owner.ShareAsync(board.Id, member, BoardRole.Editor);

        Assert.Equal(BoardRole.Editor, (await Accept(member, (await Create(owner, board.Id, "Viewer")).Token)).Role);
        Assert.Equal(BoardRole.Owner, (await Accept(member, (await Create(owner, board.Id, "Owner")).Token)).Role);
    }

    [Fact]
    public async Task Only_owners_manage_invites()
    {
        var owner = await factory.CreateSignedInClientAsync("boss@example.com");
        var editor = await factory.CreateSignedInClientAsync("helper@example.com");
        var board = await owner.CreateBoardAsync("Owners only");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"/api/boards/{board.Id}/invites", new { role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/api/boards/{board.Id}/invites")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/invites", new { role = "Viewer", expiresInDays = 31 })).StatusCode);
    }

    private static async Task<List<UserMatch>> Lookup(HttpClient owner, Guid boardId, string email)
    {
        var response = await owner.PostAsJsonAsync($"/api/boards/{boardId}/members/lookup", new { email });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<UserMatch>>())!;
    }

    private static JsonContent Json(object value) => JsonContent.Create(value);

    private static ByteArrayContent Png() =>
        new(TestImages.Png(40, 40)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } };

    private static Task<HttpResponseMessage> Upload(HttpClient client, Guid boardId) =>
        client.PostAsync($"/api/boards/{boardId}/images", Png());

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<CreatedInvite> Create(HttpClient owner, Guid boardId, string role)
    {
        var response = await owner.PostAsJsonAsync($"/api/boards/{boardId}/invites", new { role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedInvite>())!;
    }

    private static async Task<AcceptedInvite> Accept(HttpClient client, string token)
    {
        var response = await client.PostAsJsonAsync("/api/invites/accept", new { token });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AcceptedInvite>())!;
    }
}
