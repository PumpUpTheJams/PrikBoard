using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>
/// The MCP endpoint at /mcp, through the official MCP client over the test server. Its tests are in two classes, each
/// with an API of its own, so they run side by side.
/// </summary>
public abstract class McpTestBase : IAsyncDisposable
{
    protected abstract PrikBoardApiFactory Factory { get; }

    private readonly List<McpClient> clients = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients) await client.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected static HttpRequestMessage Initialize()
    {
        const string body = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    protected static async Task<string> TokenAsync(HttpClient owner, BoardRole access, string name = "Test agent", List<Guid>? boardIds = null)
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, access, boardIds });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedAccessToken>())!.Token;
    }

    protected async Task<McpClient> ConnectAsync(string token)
    {
        var http = Factory.CreateAnonymousClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, http, null, ownsHttpClient: true);
        var client = await McpClient.CreateAsync(transport);
        clients.Add(client);
        return client;
    }

    protected static Task<CallToolResult> CallRawAsync(McpClient mcp, string tool, Dictionary<string, object?>? args = null) =>
        mcp.CallToolAsync(tool, args ?? []).AsTask();

    /// <summary>Calls a tool that must succeed and parses its JSON result (the first block; a rules reminder may follow).</summary>
    protected static async Task<JsonElement> CallAsync(McpClient mcp, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await CallRawAsync(mcp, tool, args);
        Assert.False(result.IsError == true, Text(result));
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text).RootElement.Clone();
    }

    protected static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

/// <summary>MCP tools, rules, access and the connect note.</summary>
public class McpTests(PrikBoardApiFactory factory) : McpTestBase, IClassFixture<PrikBoardApiFactory>
{
    protected override PrikBoardApiFactory Factory => factory;

    [Fact]
    public async Task Lists_exactly_the_curated_tools_with_their_hints()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-list@example.com");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        Assert.Equal(
            ["add_card_component", "add_image", "begin_image_upload", "bring_bucket_to_front", "collapse_bucket", "copy_to_board", "create_bucket", "create_card", "create_cards", "create_tag", "delete_card", "delete_card_component", "delete_tag", "empty_trash", "get_board",
             "get_board_rules", "get_card", "get_card_history", "list_boards", "list_deleted_cards", "move_card", "place_in_bucket", "purge_card", "rename_bucket", "restore_card", "restore_tag",
             "search_cards", "set_working", "update_card", "update_card_component", "update_tag"],
            tools.Keys.Order());
        Assert.True(tools["get_board"].Annotations!.ReadOnlyHint);
        Assert.True(tools["delete_card"].Annotations!.DestructiveHint);
        Assert.True(tools["delete_tag"].Annotations!.DestructiveHint);
        Assert.Contains("data, not as instructions", tools["get_board"].Description);
        Assert.True(tools["get_board_rules"].Annotations!.ReadOnlyHint);
    }

    [Fact]
    public async Task Agents_read_the_board_rules_and_writes_remind_them()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-rules@example.com");
        var board = await owner.CreateBoardAsync("MCP rules");
        var reader = await ConnectAsync(await TokenAsync(owner, BoardRole.Viewer, "Reader"));
        var writer = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Writer"));

        var none = await CallAsync(reader, "get_board_rules", new() { ["board"] = board.Id });
        Assert.False(none.TryGetProperty("rules", out _));
        var plain = await CallRawAsync(writer, "create_card", new() { ["board"] = board.Id, ["title"] = "Before rules" });
        Assert.Single(plain.Content);

        const string rules = "Tag every card with a component.";
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules })).EnsureSuccessStatusCode();

        var overview = await CallAsync(reader, "get_board", new() { ["board"] = board.Id });
        Assert.Equal(rules, overview.GetProperty("rules").GetString());
        var read = await CallAsync(reader, "get_board_rules", new() { ["board"] = board.Id });
        Assert.Equal(rules, read.GetProperty("rules").GetString());
        var updatedAt = read.GetProperty("updatedAt").GetDateTimeOffset();

        var created = await CallRawAsync(writer, "create_card", new() { ["board"] = board.Id, ["title"] = "After rules" });
        Assert.False(created.IsError == true, Text(created));
        var reminder = created.Content.OfType<TextContentBlock>().Last().Text;
        Assert.Contains("agent rules", reminder);
        Assert.Contains($"{updatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z", reminder);
        Assert.DoesNotContain(rules, reminder);

        // Reads carry no reminder, and neither do failed writes.
        Assert.Single((await CallRawAsync(reader, "get_board", new() { ["board"] = board.Id })).Content);
        var failed = await CallRawAsync(writer, "move_card", new() { ["card"] = Guid.NewGuid(), ["bucket"] = "Done" });
        Assert.True(failed.IsError);
        Assert.DoesNotContain("agent rules", Text(failed));
    }

    [Fact]
    public async Task An_agent_reads_the_board_and_moves_work_along_by_bucket_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-flow@example.com");
        await owner.RenameAsync("Owen");
        var board = await owner.CreateBoardAsync("MCP flow");
        var card = await owner.CreateCardAsync(board.Id, "Write the release notes");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var boards = await CallAsync(mcp, "list_boards");
        Assert.Contains(boards.EnumerateArray(), b => b.GetProperty("id").GetGuid() == board.Id);

        var overview = await CallAsync(mcp, "get_board", new() { ["board"] = board.Id });
        var todo = overview.GetProperty("buckets")[0];
        Assert.Equal("To do", todo.GetProperty("name").GetString());
        Assert.Equal(card.Id, todo.GetProperty("cards")[0].GetProperty("id").GetGuid());

        await CallAsync(mcp, "set_working", new() { ["card"] = card.Id, ["working"] = true });
        var moved = await CallAsync(mcp, "move_card", new() { ["card"] = card.Id, ["bucket"] = "doing" });
        var doing = board.Buckets.Single(b => b.Name == "Doing");
        Assert.Equal(doing.Id, moved.GetProperty("bucketId").GetGuid());

        // The REST API sees the same change, made by the token's agent.
        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}"))!;
        Assert.Equal(doing.Id, saved.BucketId);
        Assert.True(saved.AgentWorking);
        // The agent works for the person whose token it uses: they are assigned, and the card says whom it works for.
        var ownerId = (await owner.MeAsync()).Id;
        Assert.Equal([new Assignee(ownerId, "Owen", AssigneeKind.Person, null)], saved.Assignees);
        Assert.Equal(ownerId, saved.AgentWorkingFor);
        Assert.Equal(saved.CreatedBy, overview.GetProperty("buckets")[0].GetProperty("cards")[0].GetProperty("createdBy").GetString());

        // Its history says the agent did it.
        var history = await CallAsync(mcp, "get_card_history", new() { ["card"] = card.Id });
        var move = history[0];
        Assert.Equal("Claude Code", move.GetProperty("by").GetProperty("name").GetString());
        Assert.Equal("Bucket", move.GetProperty("entries")[0].GetProperty("kind").GetString());
        Assert.Equal("Doing", move.GetProperty("entries")[0].GetProperty("after").GetString());

        var created = await CallAsync(mcp, "create_card", new()
        {
            ["board"] = board.Id, ["title"] = "Follow-up", ["bucket"] = "Done",
            ["checklist"] = new[] { new { text = "Check links", done = false } },
        });
        Assert.Equal(board.Buckets.Single(b => b.Name == "Done").Id, created.GetProperty("bucketId").GetGuid());
        var component = Assert.Single(created.GetProperty("components").EnumerateArray().ToList());
        Assert.Equal("Checklist", component.GetProperty("kind").GetString());

        var unknown = await CallRawAsync(mcp, "move_card", new() { ["card"] = card.Id, ["bucket"] = "Someday" });
        Assert.True(unknown.IsError);
        Assert.Contains("\"To do\", \"Doing\", \"Done\"", Text(unknown));
    }

    [Fact]
    public async Task Tools_respect_scopes_roles_and_membership()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-limits-owner@example.com");
        var viewer = await factory.CreateSignedInClientAsync("mcp-limits-viewer@example.com");
        var stranger = await factory.CreateSignedInClientAsync("mcp-limits-stranger@example.com");
        var board = await owner.CreateBoardAsync("MCP limits");
        var card = await owner.CreateCardAsync(board.Id);
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        var readOnly = await ConnectAsync(await TokenAsync(owner, BoardRole.Viewer));
        var refused = await CallRawAsync(readOnly, "create_card", new() { ["board"] = board.Id, ["title"] = "Nope" });
        Assert.True(refused.IsError);
        Assert.Contains("read-only", Text(refused));

        // An Editor token cannot do more than its owner, a Viewer.
        var capped = await ConnectAsync(await TokenAsync(viewer, BoardRole.Editor));
        var tooLow = await CallRawAsync(capped, "delete_card", new() { ["card"] = card.Id });
        Assert.True(tooLow.IsError);
        Assert.Contains("Editor", Text(tooLow));

        var outsider = await ConnectAsync(await TokenAsync(stranger, BoardRole.Editor));
        var hidden = await CallRawAsync(outsider, "get_board", new() { ["board"] = board.Id });
        Assert.True(hidden.IsError);
        Assert.Contains("Not found", Text(hidden));

        var invalid = await CallRawAsync(await ConnectAsync(await TokenAsync(owner, BoardRole.Editor)), "create_card",
            new() { ["board"] = board.Id, ["title"] = new string('x', 300) });
        Assert.True(invalid.IsError);
    }

    [Fact]
    public async Task Tools_take_short_ids_readable_ids_and_paths()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-refs-owner@example.com");
        var stranger = await factory.CreateSignedInClientAsync("mcp-refs-stranger@example.com");
        var board = await owner.CreateBoardAsync("MCP refs");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key = "REFS" })).EnsureSuccessStatusCode();
        var card = await owner.CreateCardAsync(board.Id, "Referenced");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));

        var overview = await CallAsync(mcp, "get_board", new() { ["board"] = board.ShortId });
        Assert.Equal((board.Id, board.ShortId, "REFS"), (overview.GetProperty("id").GetGuid(), overview.GetProperty("shortId").GetString(),
            overview.GetProperty("key").GetString()));
        var summary = overview.GetProperty("buckets")[0].GetProperty("cards")[0];
        Assert.Equal("REFS-1", summary.GetProperty("readableId").GetString());

        string[] refs = ["REFS-1", "refs-1", card.Path, $"https://prikboard.example/{card.Path[1..]}", $"/#/boards/{board.Id}/cards/{card.Id}"];
        foreach (var value in refs)
        {
            var found = await CallAsync(mcp, "get_card", new() { ["card"] = value });
            Assert.Equal(card.Id, found.GetProperty("id").GetGuid());
        }
        var moved = await CallAsync(mcp, "move_card", new() { ["card"] = card.Path, ["bucket"] = "Done" });
        Assert.Equal(board.Buckets.Single(b => b.Name == "Done").Id, moved.GetProperty("bucketId").GetGuid());
        Assert.StartsWith("Deleted.", Text(await CallRawAsync(mcp, "delete_card", new() { ["card"] = "REFS-1" })));
        Assert.Contains("Not found", Text(await CallRawAsync(mcp, "get_card", new() { ["card"] = "REFS-1" })));
        // A deleted card's history, by readable id and path as well as by id.
        foreach (var value in new[] { "REFS-1", card.Path, card.Id.ToString() })
        {
            var history = await CallAsync(mcp, "get_card_history", new() { ["card"] = value });
            Assert.Equal("Deleted", history[0].GetProperty("entries")[0].GetProperty("kind").GetString());
        }
        await CallAsync(mcp, "restore_card", new() { ["card"] = "REFS-1" });

        Assert.Contains("not a card", Text(await CallRawAsync(mcp, "get_card", new() { ["card"] = "REFS-01" })));
        Assert.Contains("not a board", Text(await CallRawAsync(mcp, "get_board", new() { ["board"] = "MCP refs" })));
        Assert.Contains("Not found", Text(await CallRawAsync(mcp, "get_board", new() { ["board"] = "zzzzzz" })));

        // Keys are not unique: a second board of yours with the same key makes REFS-1 ambiguous, and the paths tell them apart.
        var twin = await owner.CreateBoardAsync("MCP refs twin");
        (await owner.PatchAsJsonAsync($"/api/boards/{twin.Id}", new { key = "REFS" })).EnsureSuccessStatusCode();
        var twinCard = await owner.CreateCardAsync(twin.Id);
        var ambiguous = await CallRawAsync(mcp, "get_card", new() { ["card"] = "REFS-1" });
        Assert.True(ambiguous.IsError);
        Assert.Contains(card.Path, Text(ambiguous));
        Assert.Contains(twinCard.Path, Text(ambiguous));

        // Someone else's board with that key does not count, and theirs stays hidden.
        var outsider = await ConnectAsync(await TokenAsync(stranger, BoardRole.Editor));
        var theirs = await stranger.CreateBoardAsync("Stranger refs");
        (await stranger.PatchAsJsonAsync($"/api/boards/{theirs.Id}", new { key = "REFS" })).EnsureSuccessStatusCode();
        var own = await stranger.CreateCardAsync(theirs.Id);
        Assert.Equal(own.Id, (await CallAsync(outsider, "get_card", new() { ["card"] = "REFS-1" })).GetProperty("id").GetGuid());
        Assert.Contains("Not found", Text(await CallRawAsync(outsider, "get_card", new() { ["card"] = card.Path })));
        Assert.Contains("Not found", Text(await CallRawAsync(outsider, "get_board", new() { ["board"] = board.ShortId })));
    }

    [Fact]
    public async Task The_session_cookie_alone_does_not_open_mcp()
    {
        var person = await factory.CreateSignedInClientAsync("mcp-cookie@example.com");
        var response = await person.SendAsync(Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_foreign_origin_is_refused()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-origin@example.com");
        var token = await TokenAsync(owner, BoardRole.Editor);
        var client = factory.CreateAnonymousClient();

        var foreign = Initialize();
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);

        var own = Initialize();
        own.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        own.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(own)).StatusCode);
    }

    [Fact]
    public async Task Get_board_is_compact_and_only_counts_the_cards_of_collapsed_buckets()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-compact@example.com");
        await owner.RenameAsync("Owen");
        var board = await owner.CreateBoardAsync("MCP compact");
        await owner.CreateCardAsync(board.Id, "Plain");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));
        await CallAsync(mcp, "create_card", new()
        {
            ["board"] = board.Id, ["title"] = "Tagged", ["tags"] = new[] { "ui" }, ["assignees"] = new[] { "Claude Code" }, ["body"] = "Details",
        });
        await CallAsync(mcp, "create_card", new() { ["board"] = board.Id, ["title"] = "Shipped", ["bucket"] = "Done" });
        var done = board.Buckets.Single(b => b.Name == "Done");
        (await owner.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { collapsed = true })).EnsureSuccessStatusCode();

        var overview = await CallAsync(mcp, "get_board", new() { ["board"] = board.ShortId });
        var todo = overview.GetProperty("buckets")[0];
        Assert.Equal(2, todo.GetProperty("cardCount").GetInt32());
        Assert.False(todo.TryGetProperty("collapsed", out _));
        var cards = todo.GetProperty("cards").EnumerateArray().ToDictionary(c => c.GetProperty("title").GetString()!);

        // Defaults are left out, and assignees are names (their ids are in people).
        var plain = cards["Plain"];
        foreach (var name in new[] { "tags", "assignees", "hasContent", "agentWorking", "checklist" }) Assert.False(plain.TryGetProperty(name, out _), name);
        var tagged = cards["Tagged"];
        Assert.Equal(["ui"], tagged.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["Owen"], tagged.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));
        Assert.True(tagged.GetProperty("hasContent").GetBoolean());

        var folded = overview.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "Done");
        Assert.True(folded.GetProperty("collapsed").GetBoolean());
        Assert.Equal(1, folded.GetProperty("cardCount").GetInt32());
        Assert.False(folded.TryGetProperty("cards", out _));
        var shipped = await CallAsync(mcp, "search_cards", new() { ["board"] = board.Id, ["bucket"] = "Done" });
        Assert.Equal("Shipped", Assert.Single(shipped.EnumerateArray()).GetProperty("title").GetString());

        // You are the person you work for, as whom you are assigned.
        var me = overview.GetProperty("people").EnumerateArray().Single(p => p.TryGetProperty("isYou", out _));
        Assert.Equal("Owen", me.GetProperty("name").GetString());
        Assert.All(overview.GetProperty("people").EnumerateArray(), p => Assert.False(p.TryGetProperty("avatarUrl", out _)));
    }

    [Fact]
    public async Task Agents_are_told_which_board_they_used_last()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-last-board@example.com");
        var older = await owner.CreateBoardAsync("Launch \"Q4\"");
        var newer = await owner.CreateBoardAsync("MCP newer");
        var token = await TokenAsync(owner, BoardRole.Editor);

        var first = await ConnectAsync(token);
        Assert.DoesNotContain("last used", first.ServerInstructions);
        await CallAsync(first, "get_board", new() { ["board"] = older.ShortId });

        // A new connection is told about it (the name in quotes, without its own), and list_boards puts it first.
        var next = await ConnectAsync(token);
        Assert.Contains($"You last used the board \"Launch Q4\" (short id {older.ShortId}, your role Editor)", next.ServerInstructions);
        var boards = (await CallAsync(next, "list_boards")).EnumerateArray().ToList();
        Assert.Equal(older.Id, boards[0].GetProperty("id").GetGuid());
        Assert.True(boards[0].GetProperty("lastUsed").GetBoolean());
        Assert.Contains(boards, b => b.GetProperty("id").GetGuid() == newer.Id && !b.TryGetProperty("lastUsed", out _));

        // Another token is another agent, with a last board of its own.
        Assert.DoesNotContain("last used", (await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Other"))).ServerInstructions);

        // A board that is gone isn't mentioned.
        (await owner.DeleteAsync($"/api/boards/{older.Id}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain("last used", (await ConnectAsync(token)).ServerInstructions);
    }

    [Fact]
    public async Task A_token_for_one_board_needs_no_board_named()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-one-board@example.com");
        var board = await owner.CreateBoardAsync("MCP only board");
        await owner.CreateBoardAsync("MCP other board");

        var only = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Scoped", [board.Id]));
        Assert.Contains($"Your token works on one board only: \"MCP only board\" (short id {board.ShortId}", only.ServerInstructions);
        var created = await CallAsync(only, "create_card", new() { ["title"] = "No board needed", ["bucket"] = "Doing" });
        Assert.Equal(board.Id, created.GetProperty("boardId").GetGuid());
        Assert.Equal("MCP only board", (await CallAsync(only, "get_board")).GetProperty("name").GetString());

        var any = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var refused = await CallRawAsync(any, "create_card", new() { ["title"] = "Where?" });
        Assert.True(refused.IsError);
        Assert.Contains("Give the board", Text(refused));
    }

    [Fact]
    public async Task Get_board_can_leave_out_the_cards()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-no-cards@example.com");
        var board = await owner.CreateBoardAsync("MCP no cards");
        await owner.CreateCardAsync(board.Id, "Hidden");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));

        var overview = await CallAsync(mcp, "get_board", new() { ["board"] = board.Id, ["cards"] = false });
        var todo = overview.GetProperty("buckets")[0];
        Assert.Equal(1, todo.GetProperty("cardCount").GetInt32());
        Assert.False(todo.TryGetProperty("cards", out _));
        Assert.False(overview.TryGetProperty("looseCards", out _));
        Assert.True(overview.TryGetProperty("people", out _));
    }

    [Fact]
    public async Task The_connect_note_carries_the_buckets_tags_and_rules()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-brief@example.com");
        var board = await owner.CreateBoardAsync("MCP brief");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "Keep titles short." })).EnsureSuccessStatusCode();
        var token = await TokenAsync(owner, BoardRole.Editor);
        var first = await ConnectAsync(token);
        await CallAsync(first, "create_card", new() { ["board"] = board.ShortId, ["title"] = "Tagged", ["tags"] = new[] { "ui" } });

        var next = (await ConnectAsync(token)).ServerInstructions!;
        Assert.Contains("Its buckets, in order: \"To do\", \"Doing\", \"Done\".", next);
        Assert.Contains("Its tags: \"ui\".", next);
        Assert.Contains("<board-rules>\nKeep titles short.\n</board-rules>", next);

        // New rules show at once.
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "Ask before deleting." })).EnsureSuccessStatusCode();
        Assert.Contains("Ask before deleting.", (await ConnectAsync(token)).ServerInstructions);
    }
}
