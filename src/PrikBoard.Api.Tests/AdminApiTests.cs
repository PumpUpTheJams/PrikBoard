using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>
/// The admin role and invite-only sign-up. Every test signs in <see cref="Admin"/> first, so that person is the first
/// user of the class's database (and so its admin) whichever test runs first.
/// </summary>
public class AdminApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    private Task<HttpClient> Admin() => factory.CreateSignedInClientAsync("first-admin@example.com");

    [Fact]
    public async Task The_first_user_is_the_admin_and_later_ones_are_not()
    {
        var admin = await Admin();
        var other = await factory.CreateSignedInClientAsync("not-admin@example.com");

        Assert.True((await admin.MeAsync()).IsAdmin);
        Assert.False((await other.MeAsync()).IsAdmin);
    }

    [Fact]
    public async Task Only_signed_in_admins_reach_the_admin_endpoints()
    {
        var admin = await Admin();
        var other = await factory.CreateSignedInClientAsync("curious@example.com");
        var (token, _) = await factory.TokenAsync(admin, "Admin's agent");
        var otherId = (await other.MeAsync()).Id;
        var board = await admin.CreateBoardAsync("Guarded");

        foreach (var client in new[] { other, token })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/boards")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PutAsJsonAsync($"/api/admin/boards/{board.Id}/members/{otherId}", new { role = "Owner" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PatchAsJsonAsync($"/api/admin/users/{otherId}", new { isAdmin = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/admin/users/{otherId}")).StatusCode);
        }
        Assert.False((await other.MeAsync()).IsAdmin);
    }

    [Fact]
    public async Task Admins_see_every_board_and_user_but_not_what_is_on_the_boards()
    {
        var admin = await Admin();
        var owner = await factory.CreateSignedInClientAsync("lister@example.com");
        await owner.RenameAsync("Lister");
        var board = await owner.CreateBoardAsync("Not the admin's");
        var ownerId = (await owner.MeAsync()).Id;

        var boards = (await admin.GetFromJsonAsync<List<AdminBoard>>("/api/admin/boards"))!;
        var seen = Assert.Single(boards, b => b.Id == board.Id);
        Assert.Equal(new AdminBoardMember(ownerId, PrincipalKind.User, "Lister", BoardRole.Owner, null), Assert.Single(seen.Members));
        using var raw = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/boards"));
        Assert.False(raw.RootElement[0].TryGetProperty("cards", out _));

        var users = (await admin.GetFromJsonAsync<List<AdminUser>>("/api/admin/users"))!;
        var lister = Assert.Single(users, u => u.Id == ownerId);
        Assert.Equal((false, false, 1), (lister.IsAdmin, lister.IsYou, lister.BoardCount));
        Assert.Equal(new AdminBoardRef(board.Id, "Not the admin's"), Assert.Single(lister.SoleOwnerOf));
        Assert.True(Assert.Single(users, u => u.IsYou).IsAdmin);
    }

    [Fact]
    public async Task Admins_set_and_remove_board_roles_but_a_board_keeps_an_owner()
    {
        var admin = await Admin();
        var owner = await factory.CreateSignedInClientAsync("roles-owner@example.com");
        var member = await factory.CreateSignedInClientAsync("roles-member@example.com");
        var board = await owner.CreateBoardAsync("Roles");
        var ownerId = (await owner.MeAsync()).Id;
        var memberId = (await member.MeAsync()).Id;
        var path = $"/api/admin/boards/{board.Id}/members";

        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        var added = await admin.PutAsJsonAsync($"{path}/{memberId}", new { role = "Viewer" });
        Assert.Equal(BoardRole.Viewer, (await added.Content.ReadFromJsonAsync<AdminBoardMember>())!.Role);
        Assert.Equal(BoardRole.Viewer, (await member.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.YourRole);

        (await admin.PutAsJsonAsync($"{path}/{memberId}", new { role = "Owner" })).EnsureSuccessStatusCode();
        Assert.Equal(BoardRole.Owner, (await member.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.YourRole);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{path}/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{path}/{memberId}", new { role = "Editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{path}/{memberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{path}/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync($"/api/admin/boards/{Guid.NewGuid()}/members/{memberId}", new { role = "Viewer" })).StatusCode);
    }

    [Fact]
    public async Task Admins_delete_any_board_without_being_on_it()
    {
        var admin = await Admin();
        var owner = await factory.CreateSignedInClientAsync("doomed-owner@example.com");
        var board = await owner.CreateBoardAsync("Doomed");
        await owner.CreateCardAsync(board.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/admin/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/boards/{board.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<List<AdminBoard>>("/api/admin/boards"))!, b => b.Id == board.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task There_is_always_an_admin()
    {
        var admin = await Admin();
        var helper = await factory.CreateSignedInClientAsync("second-admin@example.com");
        var adminId = (await admin.MeAsync()).Id;
        var helperId = (await helper.MeAsync()).Id;

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/admin/users/{adminId}", new { isAdmin = false })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/admin/users/{helperId}", new { isAdmin = true })).StatusCode);
        Assert.True((await helper.MeAsync()).IsAdmin);
        (await helper.PatchAsJsonAsync($"/api/admin/users/{adminId}", new { isAdmin = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/users")).StatusCode);

        // Hand it back, so the other tests keep their admin.
        (await helper.PatchAsJsonAsync($"/api/admin/users/{adminId}", new { isAdmin = true })).EnsureSuccessStatusCode();
        (await helper.PatchAsJsonAsync($"/api/admin/users/{helperId}", new { isAdmin = false })).EnsureSuccessStatusCode();
        Assert.False((await helper.MeAsync()).IsAdmin);
    }

    [Fact]
    public async Task Deleting_a_user_waits_for_their_boards_to_have_another_owner_and_ends_their_sessions_and_tokens()
    {
        var admin = await Admin();
        var leaving = await factory.CreateSignedInClientAsync("leaving@example.com");
        var colleague = await factory.CreateSignedInClientAsync("colleague@example.com");
        var board = await leaving.CreateBoardAsync("Theirs");
        await leaving.ShareAsync(board.Id, colleague, BoardRole.Editor);
        var card = await leaving.CreateCardAsync(board.Id, "Stays");
        var (token, _) = await factory.TokenAsync(leaving, "Leaving's agent");
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/boards")).StatusCode);
        var leavingId = (await leaving.MeAsync()).Id;

        var refused = await admin.DeleteAsync($"/api/admin/users/{leavingId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()))
        {
            Assert.Equal(board.Id, Assert.Single(problem.RootElement.GetProperty("boards").EnumerateArray()).GetProperty("id").GetGuid());
        }

        var colleagueId = (await colleague.MeAsync()).Id;
        (await admin.PutAsJsonAsync($"/api/admin/boards/{board.Id}/members/{colleagueId}", new { role = "Owner" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{leavingId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await leaving.GetAsync("/api/me")).StatusCode);
        // Signed out, they get what anyone gets: not found, as the board isn't public.
        Assert.Equal(HttpStatusCode.NotFound, (await leaving.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await token.GetAsync("/api/boards")).StatusCode);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<List<AdminUser>>("/api/admin/users"))!, u => u.Id == leavingId);

        var kept = (await colleague.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}"))!;
        Assert.Equal("Stays", kept.Title);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/users/{leavingId}")).StatusCode);
    }

    [Fact]
    public async Task Admins_see_avatars_and_deleting_a_user_deletes_their_picture()
    {
        var admin = await Admin();
        var pictured = await factory.CreateSignedInClientAsync("pictured@example.com", avatar: PrikBoardApiFactory.GitHubAvatar);
        var me = await pictured.MeAsync();
        Assert.NotNull(me.AvatarUrl);

        var users = await admin.GetFromJsonAsync<List<AdminUser>>("/api/admin/users");
        Assert.Equal(me.AvatarUrl, users!.Single(u => u.Id == me.Id).AvatarUrl);
        var picture = (await factory.AvatarPathAsync(me.Id))!;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{me.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(me.AvatarUrl)).StatusCode);
        // The file itself goes at the next sweep.
        await factory.SweepFilesAsync();
        Assert.False(File.Exists(picture));
    }

    [Fact]
    public async Task Admins_cannot_delete_themselves()
    {
        var admin = await Admin();
        var adminId = (await admin.MeAsync()).Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/admin/users/{adminId}")).StatusCode);
    }

    [Fact]
    public async Task Newcomers_need_an_open_invite_link_to_get_an_account()
    {
        var admin = await Admin();
        var board = await admin.CreateBoardAsync("Invite only");
        var before = (await admin.GetFromJsonAsync<List<AdminUser>>("/api/admin/users"))!.Count;

        var stranger = factory.CreateAnonymousClient();
        var refused = await PrikBoardApiFactory.TestSignInAsync(stranger, "stranger@example.com", returnUrl: "/#/boards/" + board.Id);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/me")).StatusCode);
        Assert.Equal(before, (await admin.GetFromJsonAsync<List<AdminUser>>("/api/admin/users"))!.Count);

        var invite = await Invite(admin, board.Id);
        var invited = factory.CreateAnonymousClient();
        var joined = await PrikBoardApiFactory.TestSignInAsync(invited, "invited@example.com", returnUrl: invite.Path);
        Assert.Equal(HttpStatusCode.NoContent, joined.StatusCode);
        (await invited.PostAsJsonAsync("/api/invites/accept", new { token = invite.Token })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, (await invited.PostAsJsonAsync("/api/boards", new { name = "My own" })).StatusCode);

        // The link is spent now; it lets nobody else in.
        var late = await PrikBoardApiFactory.TestSignInAsync(factory.CreateAnonymousClient(), "late@example.com", returnUrl: invite.Path);
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);

        // Someone with an account signs in without any invite.
        var again = await PrikBoardApiFactory.TestSignInAsync(factory.CreateAnonymousClient(), "invited@example.com", returnUrl: "/");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task A_revoked_invite_link_lets_nobody_in()
    {
        var admin = await Admin();
        var board = await admin.CreateBoardAsync("Revoked");
        var invite = await Invite(admin, board.Id);
        (await admin.DeleteAsync($"/api/boards/{board.Id}/invites/{invite.Id}")).EnsureSuccessStatusCode();

        var response = await PrikBoardApiFactory.TestSignInAsync(factory.CreateAnonymousClient(), "too-late@example.com", returnUrl: invite.Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Of_several_first_sign_ins_at_once_exactly_one_gets_in_and_becomes_the_admin()
    {
        await using var empty = new PrikBoardApiFactory();
        var clients = Enumerable.Range(0, 5).Select(_ => empty.CreateAnonymousClient()).ToList();
        var key = empty.OpenSetupKey();
        foreach (var client in clients) await client.UnlockSetupAsync(key);

        var responses = await Task.WhenAll(clients.Select((client, i) =>
            PrikBoardApiFactory.TestSignInAsync(client, $"racer{i}@example.com", returnUrl: "/")));

        var winner = clients[Assert.Single(Enumerable.Range(0, 5), i => responses[i].StatusCode == HttpStatusCode.NoContent)];
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.NoContent), r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.True((await winner.MeAsync()).IsAdmin);
        Assert.Single((await winner.GetFromJsonAsync<List<AdminUser>>("/api/admin/users"))!);
    }

    private static async Task<CreatedInvite> Invite(HttpClient owner, Guid boardId)
    {
        var response = await owner.PostAsJsonAsync($"/api/boards/{boardId}/invites", new { role = "Editor" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedInvite>())!;
    }
}
