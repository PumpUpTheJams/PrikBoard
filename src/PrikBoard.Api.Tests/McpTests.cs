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

/// <summary>The MCP endpoint at /mcp, through the official MCP client over the test server.</summary>
public class McpTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncDisposable
{
    private readonly List<McpClient> clients = [];

    [Fact]
    public async Task Lists_exactly_the_curated_tools_with_their_hints()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-list@example.com");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));

        var tools = (await mcp.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        Assert.Equal(
            ["create_bucket", "create_note", "create_tag", "delete_note", "delete_tag", "get_board", "get_board_rules", "get_note", "list_boards",
             "move_note", "rename_bucket", "rename_tag", "search_notes", "set_working", "update_note"],
            tools.Keys.Order());
        Assert.True(tools["get_board"].Annotations!.ReadOnlyHint);
        Assert.True(tools["delete_note"].Annotations!.DestructiveHint);
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

        var none = await CallAsync(reader, "get_board_rules", new() { ["boardId"] = board.Id });
        Assert.False(none.TryGetProperty("rules", out _));
        var plain = await CallRawAsync(writer, "create_note", new() { ["boardId"] = board.Id, ["title"] = "Before rules" });
        Assert.Single(plain.Content);

        const string rules = "Tag every note with a component.";
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules })).EnsureSuccessStatusCode();

        var overview = await CallAsync(reader, "get_board", new() { ["boardId"] = board.Id });
        Assert.Equal(rules, overview.GetProperty("rules").GetString());
        var read = await CallAsync(reader, "get_board_rules", new() { ["boardId"] = board.Id });
        Assert.Equal(rules, read.GetProperty("rules").GetString());
        var updatedAt = read.GetProperty("updatedAt").GetDateTimeOffset();

        var created = await CallRawAsync(writer, "create_note", new() { ["boardId"] = board.Id, ["title"] = "After rules" });
        Assert.False(created.IsError == true, Text(created));
        var reminder = created.Content.OfType<TextContentBlock>().Last().Text;
        Assert.Contains("agent rules", reminder);
        Assert.Contains($"{updatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z", reminder);
        Assert.DoesNotContain(rules, reminder);

        // Reads carry no reminder, and failed writes neither.
        Assert.Single((await CallRawAsync(reader, "get_board", new() { ["boardId"] = board.Id })).Content);
        var failed = await CallRawAsync(writer, "move_note", new() { ["noteId"] = Guid.NewGuid(), ["bucket"] = "Done" });
        Assert.True(failed.IsError);
        Assert.DoesNotContain("agent rules", Text(failed));
    }

    [Fact]
    public async Task An_agent_reads_the_board_and_moves_work_along_by_bucket_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-flow@example.com");
        var board = await owner.CreateBoardAsync("MCP flow");
        var note = await owner.CreateNoteAsync(board.Id, "Write the release notes");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var boards = await CallAsync(mcp, "list_boards");
        Assert.Contains(boards.EnumerateArray(), b => b.GetProperty("id").GetGuid() == board.Id);

        var overview = await CallAsync(mcp, "get_board", new() { ["boardId"] = board.Id });
        var todo = overview.GetProperty("buckets")[0];
        Assert.Equal("To do", todo.GetProperty("name").GetString());
        Assert.Equal(note.Id, todo.GetProperty("notes")[0].GetProperty("id").GetGuid());

        await CallAsync(mcp, "set_working", new() { ["noteId"] = note.Id, ["working"] = true });
        var moved = await CallAsync(mcp, "move_note", new() { ["noteId"] = note.Id, ["bucket"] = "doing" });
        var doing = board.Buckets.Single(b => b.Name == "Doing");
        Assert.Equal(doing.Id, moved.GetProperty("bucketId").GetGuid());

        // The REST API sees the same change, made by the token's agent.
        var saved = (await owner.GetFromJsonAsync<NoteDetail>($"/api/notes/{note.Id}"))!;
        Assert.Equal(doing.Id, saved.BucketId);
        Assert.True(saved.AgentWorking);
        var agentId = overview.GetProperty("people").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Claude Code").GetProperty("principalId").GetGuid();
        Assert.Contains(saved.Assignees, a => a == new Assignee(agentId, "Claude Code", AssigneeKind.Agent));
        Assert.Equal(saved.CreatedBy, overview.GetProperty("buckets")[0].GetProperty("notes")[0].GetProperty("createdBy").GetString());

        var created = await CallAsync(mcp, "create_note", new()
        {
            ["boardId"] = board.Id, ["title"] = "Follow-up", ["bucket"] = "Done",
            ["checklist"] = new[] { new { text = "Check links", done = false } },
        });
        Assert.Equal(board.Buckets.Single(b => b.Name == "Done").Id, created.GetProperty("bucketId").GetGuid());

        var unknown = await CallRawAsync(mcp, "move_note", new() { ["noteId"] = note.Id, ["bucket"] = "Someday" });
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
        var note = await owner.CreateNoteAsync(board.Id);
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        var readOnly = await ConnectAsync(await TokenAsync(owner, BoardRole.Viewer));
        var refused = await CallRawAsync(readOnly, "create_note", new() { ["boardId"] = board.Id, ["title"] = "Nope" });
        Assert.True(refused.IsError);
        Assert.Contains("read-only", Text(refused));

        // An Editor token cannot do more than its owner, a Viewer.
        var capped = await ConnectAsync(await TokenAsync(viewer, BoardRole.Editor));
        var tooLow = await CallRawAsync(capped, "delete_note", new() { ["noteId"] = note.Id });
        Assert.True(tooLow.IsError);
        Assert.Contains("Editor", Text(tooLow));

        var outsider = await ConnectAsync(await TokenAsync(stranger, BoardRole.Editor));
        var hidden = await CallRawAsync(outsider, "get_board", new() { ["boardId"] = board.Id });
        Assert.True(hidden.IsError);
        Assert.Contains("Not found", Text(hidden));

        var invalid = await CallRawAsync(await ConnectAsync(await TokenAsync(owner, BoardRole.Editor)), "create_note",
            new() { ["boardId"] = board.Id, ["title"] = new string('x', 300) });
        Assert.True(invalid.IsError);
    }

    [Fact]
    public async Task Agents_assign_people_by_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-assign-owner@example.com");
        var mila = await factory.CreateSignedInClientAsync("mcp-assign-mila@example.com");
        await mila.RenameAsync("Mila V.");
        var board = await owner.CreateBoardAsync("MCP assign");
        await owner.ShareAsync(board.Id, mila, BoardRole.Editor);
        var milaId = (await mila.MeAsync()).Id;
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var created = await CallAsync(mcp, "create_note", new()
        {
            ["boardId"] = board.Id, ["title"] = "Pair up", ["assignees"] = new[] { "mila v.", "Claude Code" }, ["tags"] = new[] { "Pairing" },
        });
        var names = created.GetProperty("assignees").EnumerateArray().Select(a => a.GetProperty("name").GetString());
        Assert.Equal(["Mila V.", "Claude Code"], names);
        Assert.Equal(milaId, created.GetProperty("assignees")[0].GetProperty("principalId").GetGuid());

        var byId = await CallAsync(mcp, "update_note", new() { ["noteId"] = created.GetProperty("id").GetGuid(), ["assignees"] = new[] { milaId.ToString() } });
        Assert.Single(byId.GetProperty("assignees").EnumerateArray());

        var found = await CallAsync(mcp, "search_notes", new() { ["boardId"] = board.Id, ["assignee"] = "Mila V.", ["tags"] = new[] { "pairing", "other" } });
        Assert.Single(found.EnumerateArray());

        var unknown = await CallRawAsync(mcp, "create_note", new() { ["boardId"] = board.Id, ["title"] = "Nope", ["assignees"] = new[] { "Nobody" } });
        Assert.True(unknown.IsError);
        Assert.Contains("\"Mila V.\"", Text(unknown));
    }

    [Fact]
    public async Task Agents_manage_tags_by_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-tags@example.com");
        var board = await owner.CreateBoardAsync("MCP tags");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var note = await CallAsync(mcp, "create_note", new() { ["boardId"] = board.Id, ["title"] = "Tagged", ["tags"] = new[] { "ui" } });

        await CallAsync(mcp, "create_tag", new() { ["boardId"] = board.Id, ["name"] = "Later" });
        var renamed = await CallAsync(mcp, "rename_tag", new() { ["boardId"] = board.Id, ["tag"] = "UI", ["name"] = "front-end" });
        Assert.Equal(1, renamed.GetProperty("noteCount").GetInt32());
        Assert.Equal("Deleted.", Text(await CallRawAsync(mcp, "delete_tag", new() { ["boardId"] = board.Id, ["tag"] = "later" })));

        var overview = await CallAsync(mcp, "get_board", new() { ["boardId"] = board.Id });
        Assert.Equal(["front-end"], overview.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        var saved = (await owner.GetFromJsonAsync<NoteDetail>($"/api/notes/{note.GetProperty("id").GetGuid()}"))!;
        Assert.Equal(["front-end"], saved.Tags);

        var missing = await CallRawAsync(mcp, "delete_tag", new() { ["boardId"] = board.Id, ["tag"] = "nope" });
        Assert.True(missing.IsError);
        Assert.Contains("\"front-end\"", Text(missing));
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

    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients) await client.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static HttpRequestMessage Initialize()
    {
        const string body = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    private static async Task<string> TokenAsync(HttpClient owner, BoardRole access, string name = "Test agent")
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, access });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedAccessToken>())!.Token;
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = factory.CreateAnonymousClient();
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

    private static Task<CallToolResult> CallRawAsync(McpClient mcp, string tool, Dictionary<string, object?>? args = null) =>
        mcp.CallToolAsync(tool, args ?? []).AsTask();

    /// <summary>Calls a tool that must succeed and parses its JSON result (the first block; a rules reminder may follow).</summary>
    private static async Task<JsonElement> CallAsync(McpClient mcp, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await CallRawAsync(mcp, tool, args);
        Assert.False(result.IsError == true, Text(result));
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text).RootElement.Clone();
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
