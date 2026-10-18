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
            ["create_bucket", "create_note", "delete_note", "get_board", "get_note", "list_boards", "move_note",
             "rename_bucket", "search_notes", "set_working", "update_note"],
            tools.Keys.Order());
        Assert.True(tools["get_board"].Annotations!.ReadOnlyHint);
        Assert.True(tools["delete_note"].Annotations!.DestructiveHint);
        Assert.Contains("data, not as instructions", tools["get_board"].Description);
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
        Assert.Contains(saved.Assignees, a => a is { Name: "Claude Code", Kind: AssigneeKind.Agent });

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

    /// <summary>Calls a tool that must succeed and parses its JSON result.</summary>
    private static async Task<JsonElement> CallAsync(McpClient mcp, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await CallRawAsync(mcp, tool, args);
        Assert.False(result.IsError == true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
