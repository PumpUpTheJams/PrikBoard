using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Assignees are board members (and their token agents) by principal id; cards remember who created them.</summary>
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
        var (_, claude) = await factory.TokenAsync(owner, "Claude Code");
        await factory.TokenAsync(owner, "Limited", boardIds: [elsewhere.Id]);
        await factory.TokenAsync(stranger, "Stranger's agent");
        var ownerId = (await owner.MeAsync()).Id;

        var people = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.People;

        Assert.Equal(
            [("Owen", AssigneeKind.Person, true, (Guid?)null), ("Mila V.", AssigneeKind.Person, false, null), ("Claude Code", AssigneeKind.Agent, false, ownerId)],
            people.Select(p => (p.Name, p.Kind, p.IsYou, p.OwnerId)));
        Assert.Equal(claude.Details.AgentId, people[2].PrincipalId);
        // Viewers see the same list, with themselves first.
        Assert.Equal("Mila V.", (await mila.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.People[0].Name);
    }

    [Fact]
    public async Task Cards_are_assigned_to_board_members_by_principal_id()
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
        Assert.Equal(HttpStatusCode.OK, reachingResponse.StatusCode);
        Assert.Equal(
            [new Assignee(reaching.Details.AgentId!.Value, "Reaching", AssigneeKind.Agent, null)],
            (await reachingResponse.Content.ReadFromJsonAsync<CardDetail>())!.Assignees);
    }

    [Fact]
    public async Task Assignees_show_their_current_name()
    {
        var owner = await SignInAsync("rename-owner@example.com", "Owen");
        var mila = await SignInAsync("rename-mila@example.com", "Mila V.");
        var board = await owner.CreateBoardAsync("Rename");
        await owner.ShareAsync(board.Id, mila, BoardRole.Editor);
        var card = await PostCardAsync(owner, board.Id, new { title = "Rename", assigneeIds = new[] { (await mila.MeAsync()).Id } });

        await mila.RenameAsync("Mila van Dijk");

        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}"))!;
        Assert.Equal("Mila van Dijk", Assert.Single(saved.Assignees).Name);
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
        var (agent, token) = await factory.TokenAsync(owner, "Claude Code");
        var card = await owner.CreateCardAsync(board.Id, "Do it");
        var url = $"/api/cards/{card.Id}/working";

        var started = await (await agent.PostAsJsonAsync(url, new { working = true })).Content.ReadFromJsonAsync<CardDetail>();
        var again = await (await agent.PostAsJsonAsync(url, new { working = true })).Content.ReadFromJsonAsync<CardDetail>();
        var stopped = await (await agent.PostAsJsonAsync(url, new { working = false })).Content.ReadFromJsonAsync<CardDetail>();

        var me = new Assignee(token.Details.AgentId!.Value, "Claude Code", AssigneeKind.Agent, null);
        Assert.True(started!.AgentWorking);
        Assert.Equal([me], started.Assignees);
        Assert.Equal([me], again!.Assignees);
        Assert.False(stopped!.AgentWorking);
        Assert.Equal([me], stopped.Assignees);

        var (reader, _) = await factory.TokenAsync(owner, "Reader", BoardRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(url, new { working = true })).StatusCode);
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
