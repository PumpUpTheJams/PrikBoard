using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Assignees are board members by principal id (a token's agent works for its owner); cards remember who created them.</summary>
public class AssigneeApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task Board_lists_the_people_who_can_be_assigned()
    {
        var owner = await SignInAsync("people-owner@example.com", "Owen");
        var mila = await SignInAsync("people-mila@example.com", "Mila V.");
        var stranger = await SignInAsync("people-stranger@example.com", "Stranger");
        var board = await owner.CreateBoardAsync("People");
        var elsewhere = await owner.CreateBoardAsync("Elsewhere");
        await owner.ShareAsync(board.Id, mila, BoardRole.Viewer);
        // Token agents work for their owners, who are assigned instead, so they aren't listed.
        await factory.TokenAsync(owner, "Claude Code");
        await factory.TokenAsync(owner, "Limited", boardIds: [elsewhere.Id]);
        await factory.TokenAsync(stranger, "Stranger's agent");

        var people = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.People;

        Assert.Equal(
            [("Owen", AssigneeKind.Person, true, (Guid?)null), ("Mila V.", AssigneeKind.Person, false, null)],
            people.Select(p => (p.Name, p.Kind, p.IsYou, p.OwnerId)));
        // Viewers see the same list, with themselves first.
        Assert.Equal("Mila V.", (await mila.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.People[0].Name);
    }

    [Fact]
    public async Task Cards_are_assigned_to_board_members_by_principal_id_and_show_their_current_name()
    {
        var owner = await SignInAsync("assign-owner@example.com", "Owen");
        var mila = await SignInAsync("assign-mila@example.com", "Mila V.");
        var board = await owner.CreateBoardAsync("Assign");
        await owner.ShareAsync(board.Id, mila, BoardRole.Editor);
        var (ownerId, milaId) = ((await owner.MeAsync()).Id, (await mila.MeAsync()).Id);

        var created = await PostCardAsync(owner, board.Id, new { title = "Pair", assigneeIds = new[] { milaId, ownerId, milaId } });

        Assert.Equal(
            [new Assignee(milaId, "Mila V.", AssigneeKind.Person, null), new Assignee(ownerId, "Owen", AssigneeKind.Person, null)],
            created.Assignees);
        await mila.RenameAsync("Mila van Dijk");
        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{created.Id}"))!;
        Assert.Equal("Mila van Dijk", saved.Assignees.Single(a => a.PrincipalId == milaId).Name);
        var cleared = await owner.PatchAsJsonAsync($"/api/cards/{created.Id}", new { assigneeIds = Array.Empty<Guid>() });
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<CardDetail>())!.Assignees);
    }

    [Fact]
    public async Task Only_people_who_can_reach_the_board_can_be_assigned()
    {
        var owner = await SignInAsync("reach-owner@example.com", "Owen");
        var stranger = await SignInAsync("reach-stranger@example.com", "Stranger");
        var board = await owner.CreateBoardAsync("Reach");
        var elsewhere = await owner.CreateBoardAsync("Reach elsewhere");
        var (_, reaching) = await factory.TokenAsync(owner, "Reaching");
        var (_, limited) = await factory.TokenAsync(owner, "Limited", boardIds: [elsewhere.Id]);
        var card = await owner.CreateCardAsync(board.Id);
        var url = $"/api/cards/{card.Id}";

        var strangerResponse = await owner.PatchAsJsonAsync(url, new { assigneeIds = new[] { (await stranger.MeAsync()).Id } });
        var limitedResponse = await owner.PatchAsJsonAsync(url, new { assigneeIds = new[] { limited.Details.AgentId } });
        var reachingResponse = await owner.PatchAsJsonAsync(url, new { assigneeIds = new[] { reaching.Details.AgentId } });

        Assert.Equal(HttpStatusCode.BadRequest, strangerResponse.StatusCode);
        Assert.Contains("assigneeIds", await strangerResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, limitedResponse.StatusCode);
        // Not even a token agent that reaches the board: it works for its owner, who is assigned instead.
        Assert.Equal(HttpStatusCode.BadRequest, reachingResponse.StatusCode);
    }

    [Fact]
    public async Task Removed_members_stay_assigned_and_can_be_kept()
    {
        var owner = await SignInAsync("removed-owner@example.com", "Owen");
        var mila = await SignInAsync("removed-mila@example.com", "Mila V.");
        var board = await owner.CreateBoardAsync("Removed");
        await owner.ShareAsync(board.Id, mila, BoardRole.Editor);
        var milaId = (await mila.MeAsync()).Id;
        var card = await PostCardAsync(owner, board.Id, new { title = "Hers", assigneeIds = new[] { milaId } });
        var other = await owner.CreateCardAsync(board.Id, "Other");

        (await owner.DeleteAsync($"/api/boards/{board.Id}/members/{milaId}")).EnsureSuccessStatusCode();

        var kept = await owner.PatchAsJsonAsync($"/api/cards/{card.Id}", new { title = "Still hers", assigneeIds = new[] { milaId } });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal("Mila V.", Assert.Single((await kept.Content.ReadFromJsonAsync<CardDetail>())!.Assignees).Name);
        var added = await owner.PatchAsJsonAsync($"/api/cards/{other.Id}", new { assigneeIds = new[] { milaId } });
        Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
        Assert.DoesNotContain((await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.People, p => p.PrincipalId == milaId);
    }

    [Fact]
    public async Task Cards_remember_who_created_them()
    {
        var owner = await SignInAsync("creator-owner@example.com", "Owen");
        var board = await owner.CreateBoardAsync("Creators");
        var (agent, token) = await factory.TokenAsync(owner, "Script");
        var ownerId = (await owner.MeAsync()).Id;

        var byOwner = await owner.CreateCardAsync(board.Id, "By a person");
        var byAgent = await agent.CreateCardAsync(board.Id, "By an agent");

        Assert.Equal(("Owen", ownerId), (byOwner.CreatedBy, byOwner.CreatedById));
        Assert.Equal(("Script", token.Details.AgentId), (byAgent.CreatedBy, byAgent.CreatedById));
        var snapshot = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(["Owen", "Script"], snapshot.Cards.OrderBy(n => n.CreatedAt).Select(n => n.CreatedBy));
    }

    [Fact]
    public async Task An_agent_says_it_is_working_over_rest()
    {
        var owner = await SignInAsync("working-owner@example.com", "Owen");
        var board = await owner.CreateBoardAsync("Working");
        var (agent, _) = await factory.TokenAsync(owner, "Claude Code");
        var ownerId = (await owner.MeAsync()).Id;
        var card = await owner.CreateCardAsync(board.Id, "Do it");
        var url = $"/api/cards/{card.Id}/working";

        var started = await (await agent.PostAsJsonAsync(url, new { working = true })).Content.ReadFromJsonAsync<CardDetail>();
        var again = await (await agent.PostAsJsonAsync(url, new { working = true })).Content.ReadFromJsonAsync<CardDetail>();
        var stopped = await (await agent.PostAsJsonAsync(url, new { working = false })).Content.ReadFromJsonAsync<CardDetail>();

        // The agent works for the person whose token it uses: they are assigned, and their avatar shows the agent at work.
        var owen = new Assignee(ownerId, "Owen", AssigneeKind.Person, null);
        Assert.True(started!.AgentWorking);
        Assert.Equal([owen], started.Assignees);
        Assert.Equal(ownerId, started.AgentWorkingFor);
        Assert.Equal([owen], again!.Assignees);
        Assert.False(stopped!.AgentWorking);
        Assert.Null(stopped.AgentWorkingFor);
        Assert.Equal([owen], stopped.Assignees);

        var (reader, _) = await factory.TokenAsync(owner, "Reader", BoardRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(url, new { working = true })).StatusCode);
    }

    [Fact]
    public async Task People_who_start_on_a_card_at_the_same_time_are_all_assigned()
    {
        var owner = await SignInAsync("working-race-owner@example.com", "Owen");
        var board = await owner.CreateBoardAsync("Working race");
        var members = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => SignInAsync($"working-race-{i}@example.com", $"Member {i}")));
        foreach (var member in members) await owner.ShareAsync(board.Id, member, BoardRole.Editor);
        var card = await owner.CreateCardAsync(board.Id, "Everyone on it");

        var responses = await Task.WhenAll(members.Select(m => m.PostAsJsonAsync($"/api/cards/{card.Id}/working", new { working = true })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = await Task.WhenAll(members.Select(async m => (await m.MeAsync()).Id));
        var assigned = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}"))!.Assignees.Select(a => a.PrincipalId);
        Assert.Equal(ids.Order(), assigned.Order());
    }

    private async Task<HttpClient> SignInAsync(string email, string name)
    {
        var client = await factory.CreateSignedInClientAsync(email);
        await client.RenameAsync(name);
        return client;
    }

    private static async Task<CardDetail> PostCardAsync(HttpClient client, Guid boardId, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/cards", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }
}
