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
    public async Task The_creator_owns_a_board_and_others_do_not_see_it()
    {
        var owner = await factory.CreateSignedInClientAsync("creator@example.com");
        var stranger = await factory.CreateSignedInClientAsync("stranger@example.com");
        var board = await owner.CreateBoardAsync("Mine");

        Assert.Equal(BoardRole.Owner, board.YourRole);
        Assert.Contains(await owner.GetFromJsonAsync<List<BoardSummary>>("/api/boards") ?? [], b => b.Id == board.Id && b.YourRole == BoardRole.Owner);
        Assert.DoesNotContain(await stranger.GetFromJsonAsync<List<BoardSummary>>("/api/boards") ?? [], b => b.Id == board.Id);
    }

    [Fact]
    public async Task Each_role_can_do_exactly_what_it_should()
    {
        var owner = await factory.CreateSignedInClientAsync("matrix-owner@example.com");
        var editor = await factory.CreateSignedInClientAsync("matrix-editor@example.com");
        var viewer = await factory.CreateSignedInClientAsync("matrix-viewer@example.com");
        var stranger = await factory.CreateSignedInClientAsync("matrix-stranger@example.com");
        var board = await owner.CreateBoardAsync("Matrix");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        var note = await owner.CreateNoteAsync(board.Id);
        var bucket = board.Buckets[0];
        var image = await (await Upload(owner, board.Id)).Content.ReadFromJsonAsync<ImageDetail>();

        (string Method, string Url, Func<HttpContent?> Body, BoardRole Needs)[] cases =
        [
            ("GET", $"/api/boards/{board.Id}", () => null, BoardRole.Viewer),
            ("GET", $"/api/boards/{board.Id}/notes", () => null, BoardRole.Viewer),
            ("GET", $"/api/notes/{note.Id}", () => null, BoardRole.Viewer),
            ("GET", $"/api/images/{image!.Id}/content", () => null, BoardRole.Viewer),
            ("GET", $"/api/boards/{board.Id}/members", () => null, BoardRole.Viewer),
            ("POST", $"/api/boards/{board.Id}/notes", () => Json(new { title = "More" }), BoardRole.Editor),
            ("PATCH", $"/api/notes/{note.Id}", () => Json(new { title = "Renamed" }), BoardRole.Editor),
            ("POST", $"/api/notes/{note.Id}/move", () => Json(new { x = 1000, y = 1000 }), BoardRole.Editor),
            ("POST", $"/api/boards/{board.Id}/buckets", () => Json(new { name = "Review" }), BoardRole.Editor),
            ("PATCH", $"/api/buckets/{bucket.Id}", () => Json(new { name = "Backlog" }), BoardRole.Editor),
            ("PATCH", $"/api/images/{image.Id}", () => Json(new { caption = "c" }), BoardRole.Editor),
            ("POST", $"/api/boards/{board.Id}/images", () => Png(), BoardRole.Editor),
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
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
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
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new { title = "Now I can" })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_board_removes_its_members_and_invites()
    {
        var owner = await factory.CreateSignedInClientAsync("cleaner@example.com");
        var board = await owner.CreateBoardAsync("Gone");
        (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/invites", new { role = "Viewer" })).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/boards/{board.Id}")).EnsureSuccessStatusCode();

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
}
