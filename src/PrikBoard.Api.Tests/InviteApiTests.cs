using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class InviteApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
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
