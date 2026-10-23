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
        var agentId = overview.GetProperty("people").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Claude Code").GetProperty("principalId").GetGuid();
        Assert.Contains(saved.Assignees, a => a == new Assignee(agentId, "Claude Code", AssigneeKind.Agent, null));
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
    public async Task An_agent_puts_several_cards_into_a_bucket_by_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-place@example.com");
        var board = await owner.CreateBoardAsync("MCP place");
        var first = await owner.CreateCardAsync(board.Id, "First");
        var second = await owner.CreateCardAsync(board.Id, "Second");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));

        var placed = await CallAsync(mcp, "place_in_bucket", new()
        {
            ["board"] = board.ShortId, ["bucket"] = "done", ["cards"] = new[] { first.ReadableId, second.Path }, ["grow"] = "Right",
        });

        var done = board.Buckets.Single(b => b.Name == "Done");
        Assert.Equal(done.Id, placed.GetProperty("bucket").GetProperty("id").GetGuid());
        Assert.Equal([first.Id, second.Id], placed.GetProperty("cards").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()));
        Assert.All(placed.GetProperty("cards").EnumerateArray(), n => Assert.Equal(done.Id, n.GetProperty("bucketId").GetGuid()));
    }

    [Fact]
    public async Task An_agent_copies_cards_to_another_board()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-copy@example.com");
        var from = await owner.CreateBoardAsync("MCP copy from");
        var to = await owner.CreateBoardAsync("MCP copy to");
        var first = await owner.CreateCardAsync(from.Id, "First");
        var second = await owner.CreateCardAsync(from.Id, "Second");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));

        var copied = await CallAsync(mcp, "copy_to_board", new()
        {
            ["board"] = to.ShortId, ["cards"] = new[] { first.ReadableId, second.Path }, ["bucket"] = "Doing",
        });

        var doing = to.Buckets.Single(b => b.Name == "Doing");
        var cards = copied.GetProperty("cards").EnumerateArray().ToList();
        Assert.Equal(["First", "Second"], cards.Select(n => n.GetProperty("title").GetString()));
        Assert.All(cards, n => Assert.Equal(doing.Id, n.GetProperty("bucketId").GetGuid()));
        Assert.Equal(2, (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{from.Id}"))!.Cards.Count);

        // A bucket by name, with its cards, to a place on the other board.
        var withBucket = await CallAsync(mcp, "copy_to_board", new()
        {
            ["board"] = to.ShortId, ["from"] = from.ShortId, ["buckets"] = new[] { "To do" }, ["x"] = 0, ["y"] = 4000,
        });
        var pastedBucket = Assert.Single(withBucket.GetProperty("buckets").EnumerateArray());
        Assert.Equal(("To do", 0, 4000), (pastedBucket.GetProperty("name").GetString(), pastedBucket.GetProperty("x").GetInt32(), pastedBucket.GetProperty("y").GetInt32()));
        Assert.Equal(2, withBucket.GetProperty("cards").GetArrayLength());
        Assert.All(withBucket.GetProperty("cards").EnumerateArray(),
            n => Assert.Equal(pastedBucket.GetProperty("id").GetGuid(), n.GetProperty("bucketId").GetGuid()));

        // Moved: the original goes to the trash.
        var moved = await CallAsync(mcp, "copy_to_board", new() { ["board"] = to.ShortId, ["cards"] = new[] { second.ReadableId }, ["move"] = true });
        Assert.Equal(second.Id, Assert.Single(moved.GetProperty("moved").GetProperty("cards").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.DoesNotContain((await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{from.Id}"))!.Cards, n => n.Id == second.Id);

        var elsewhere = await owner.CreateBoardAsync("MCP copy elsewhere");
        var stranger = await owner.CreateCardAsync(elsewhere.Id, "Stranger");
        var mixed = await CallRawAsync(mcp, "copy_to_board", new() { ["board"] = to.Id, ["cards"] = new[] { first.ReadableId, stranger.ReadableId } });
        Assert.True(mixed.IsError);
        Assert.Contains("one board at a time", Text(mixed));
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
    public async Task Agents_assign_people_by_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-assign-owner@example.com");
        var mila = await factory.CreateSignedInClientAsync("mcp-assign-mila@example.com");
        await mila.RenameAsync("Mila V.");
        var board = await owner.CreateBoardAsync("MCP assign");
        await owner.ShareAsync(board.Id, mila, BoardRole.Editor);
        var milaId = (await mila.MeAsync()).Id;
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var created = await CallAsync(mcp, "create_card", new()
        {
            ["board"] = board.Id, ["title"] = "Pair up", ["assignees"] = new[] { "mila v.", "Claude Code" }, ["tags"] = new[] { "Pairing" },
        });
        var names = created.GetProperty("assignees").EnumerateArray().Select(a => a.GetString());
        Assert.Equal(["Mila V.", "Claude Code"], names);
        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{created.GetProperty("id").GetGuid()}"))!;
        Assert.Equal(milaId, saved.Assignees[0].PrincipalId);

        var byId = await CallAsync(mcp, "update_card", new() { ["card"] = created.GetProperty("id").GetGuid(), ["assignees"] = new[] { milaId.ToString() } });
        Assert.Single(byId.GetProperty("assignees").EnumerateArray());

        var found = await CallAsync(mcp, "search_cards", new() { ["board"] = board.Id, ["assignee"] = "Mila V.", ["tags"] = new[] { "pairing", "other" } });
        Assert.Single(found.EnumerateArray());

        var unknown = await CallRawAsync(mcp, "create_card", new() { ["board"] = board.Id, ["title"] = "Nope", ["assignees"] = new[] { "Nobody" } });
        Assert.True(unknown.IsError);
        Assert.Contains("\"Mila V.\"", Text(unknown));
    }

    [Fact]
    public async Task Agents_manage_tags_by_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-tags@example.com");
        var board = await owner.CreateBoardAsync("MCP tags");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var card = await CallAsync(mcp, "create_card", new() { ["board"] = board.Id, ["title"] = "Tagged", ["tags"] = new[] { "ui" } });

        var later = await CallAsync(mcp, "create_tag", new() { ["board"] = board.Id, ["name"] = "Later", ["color"] = "Ink" });
        Assert.Equal("Ink", later.GetProperty("color").GetString());
        var renamed = await CallAsync(mcp, "update_tag", new() { ["board"] = board.Id, ["tag"] = "UI", ["name"] = "front-end" });
        Assert.Equal(1, renamed.GetProperty("cardCount").GetInt32());
        var recolored = await CallAsync(mcp, "update_tag", new() { ["board"] = board.Id, ["tag"] = "front-end", ["color"] = "Brick" });
        Assert.Equal(("front-end", "Brick"), (recolored.GetProperty("name").GetString(), recolored.GetProperty("color").GetString()));
        var nothing = await CallRawAsync(mcp, "update_tag", new() { ["board"] = board.Id, ["tag"] = "front-end" });
        Assert.True(nothing.IsError);
        Assert.StartsWith("Deleted.", Text(await CallRawAsync(mcp, "delete_tag", new() { ["board"] = board.Id, ["tag"] = "later" })));

        var overview = await CallAsync(mcp, "get_board", new() { ["board"] = board.Id });
        Assert.Equal(["front-end"], overview.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{card.GetProperty("id").GetGuid()}"))!;
        Assert.Equal(["front-end"], saved.Tags);

        var missing = await CallRawAsync(mcp, "delete_tag", new() { ["board"] = board.Id, ["tag"] = "nope" });
        Assert.True(missing.IsError);
        Assert.Contains("\"front-end\"", Text(missing));
    }

    [Fact]
    public async Task Agents_list_deleted_cards_put_one_back_elsewhere_and_collapse_buckets()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-trash@example.com");
        var board = await owner.CreateBoardAsync("MCP trash");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var card = await owner.CreateCardAsync(board.Id, "An idea we dropped");
        await CallRawAsync(mcp, "delete_card", new() { ["card"] = card.Id });

        var found = await CallAsync(mcp, "list_deleted_cards", new() { ["board"] = board.Id, ["text"] = "IDEA" });
        var back = await CallAsync(mcp, "restore_card", new() { ["card"] = card.Id, ["x"] = 2000, ["y"] = 2000 });
        var collapsed = await CallAsync(mcp, "collapse_bucket", new() { ["bucketId"] = board.Buckets[0].Id, ["collapsed"] = true });
        var overview = await CallAsync(mcp, "get_board", new() { ["board"] = board.Id });

        Assert.Equal(card.Id, Assert.Single(found.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(card.Id, back.GetProperty("id").GetGuid());
        var restored = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}"))!;
        Assert.Equal((2000, 2000), (restored.X, restored.Y));
        Assert.True(collapsed.GetProperty("collapsed").GetBoolean());
        Assert.True(overview.GetProperty("buckets")[0].GetProperty("collapsed").GetBoolean());
    }

    [Fact]
    public async Task Agents_delete_cards_in_the_trash_for_good()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-purge@example.com");
        var board = await owner.CreateBoardAsync("MCP purge");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var first = await owner.CreateCardAsync(board.Id, "First");
        var second = await owner.CreateCardAsync(board.Id, "Second");
        var third = await owner.CreateCardAsync(board.Id, "Third");
        foreach (var card in new[] { first, second, third }) await CallRawAsync(mcp, "delete_card", new() { ["card"] = card.Id });

        await CallRawAsync(mcp, "purge_card", new() { ["card"] = first.ReadableId });
        var left = await CallAsync(mcp, "list_deleted_cards", new() { ["board"] = board.Id });
        var emptied = await CallAsync(mcp, "empty_trash", new() { ["board"] = board.ShortId });

        Assert.Equal(2, left.GetArrayLength());
        Assert.Equal(2, emptied.GetProperty("purged").GetInt32());
        Assert.Equal(0, (await CallAsync(mcp, "list_deleted_cards", new() { ["board"] = board.Id })).GetArrayLength());
    }

    [Fact]
    public async Task Agents_restore_deleted_cards_and_tags()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-restore@example.com");
        var board = await owner.CreateBoardAsync("MCP restore");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var card = await CallAsync(mcp, "create_card", new() { ["board"] = board.Id, ["title"] = "Oops", ["tags"] = new[] { "keep" } });
        var cardId = card.GetProperty("id").GetGuid();

        Assert.Contains("restore_tag", Text(await CallRawAsync(mcp, "delete_tag", new() { ["board"] = board.Id, ["tag"] = "keep" })));
        Assert.Contains("restore_card", Text(await CallRawAsync(mcp, "delete_card", new() { ["card"] = cardId })));
        var restoredTag = await CallAsync(mcp, "restore_tag", new() { ["board"] = board.Id, ["tag"] = "Keep" });
        var restoredCard = await CallAsync(mcp, "restore_card", new() { ["card"] = cardId });

        Assert.Equal("keep", restoredTag.GetProperty("name").GetString());
        Assert.Equal(cardId, restoredCard.GetProperty("id").GetGuid());
        Assert.Equal(["keep"], restoredCard.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        var again = await CallRawAsync(mcp, "restore_tag", new() { ["board"] = board.Id, ["tag"] = "keep" });
        Assert.True(again.IsError);
        Assert.Contains("No deleted tag", Text(again));
    }

    [Fact]
    public async Task Agents_edit_card_content_as_components_by_kind_or_title()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-components@example.com");
        var board = await owner.CreateBoardAsync("MCP components");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var created = await CallAsync(mcp, "create_card", new()
        {
            ["board"] = board.Id, ["title"] = "Ingest", ["body"] = "Retry tiles",
            ["checklist"] = new[] { new { text = "Backoff", done = false }, new { text = "DLQ", done = true } },
        });
        var cardId = created.GetProperty("id").GetGuid();

        await CallAsync(mcp, "update_card_component", new()
        {
            ["card"] = cardId, ["component"] = "checklist",
            ["items"] = new[] { new { text = "Backoff", done = true }, new { text = "DLQ", done = true } },
        });
        await CallAsync(mcp, "add_card_component", new() { ["card"] = cardId, ["kind"] = "Checklist", ["title"] = "Follow-up", ["items"] = new[] { new { text = "Alerting" } } });
        var ambiguous = await CallRawAsync(mcp, "update_card_component", new() { ["card"] = cardId, ["component"] = "Checklist", ["title"] = "x" });
        Assert.True(ambiguous.IsError);
        Assert.Contains("\"Follow-up\" (Checklist", Text(ambiguous));
        var misfit = await CallRawAsync(mcp, "update_card_component", new() { ["card"] = cardId, ["component"] = "follow-up", ["text"] = "x" });
        Assert.True(misfit.IsError);
        var tooLong = await CallRawAsync(mcp, "add_card_component", new() { ["card"] = cardId, ["kind"] = "Checklist", ["items"] = new[] { new { text = new string('x', 301) } } });
        Assert.True(tooLong.IsError);

        var hits = await CallAsync(mcp, "search_cards", new() { ["board"] = board.Id, ["text"] = "alerting" });
        var summary = Assert.Single(hits.EnumerateArray().ToList());
        Assert.Equal("2/3", summary.GetProperty("checklist").GetString());
        Assert.True(summary.GetProperty("hasContent").GetBoolean());

        Assert.Equal("Deleted.", Text(await CallRawAsync(mcp, "delete_card_component", new() { ["card"] = cardId, ["component"] = "Text" })));
        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{cardId}"))!;
        Assert.Equal([CardComponentKind.Checklist, CardComponentKind.Checklist], saved.Components.Select(b => b.Kind));

        var readOnly = await ConnectAsync(await TokenAsync(owner, BoardRole.Viewer, "Reader"));
        Assert.True((await CallRawAsync(readOnly, "add_card_component", new() { ["card"] = cardId, ["kind"] = "Text" })).IsError);
    }

    [Fact]
    public async Task Agents_add_images_to_cards_and_boards()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-images@example.com");
        var board = await owner.CreateBoardAsync("MCP images");
        var card = await owner.CreateCardAsync(board.Id, "Ingest errors");
        var other = await owner.CreateBoardAsync("MCP images elsewhere");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));
        var png = TestImages.Png(800, 600);

        var attached = await CallAsync(mcp, "add_image", new()
        {
            ["card"] = card.Id, ["data"] = Convert.ToBase64String(png), ["fileName"] = "errors.png", ["caption"] = "Last night",
        });
        Assert.Equal(card.Id, attached.GetProperty("cardId").GetGuid());
        Assert.Equal(board.Id, attached.GetProperty("boardId").GetGuid());
        Assert.Equal(("errors.png", "image/png", 360), (attached.GetProperty("fileName").GetString(),
            attached.GetProperty("contentType").GetString(), attached.GetProperty("width").GetInt32()));
        Assert.Equal("Claude Code", attached.GetProperty("addedBy").GetString());

        var loose = await CallAsync(mcp, "add_image", new()
        {
            ["board"] = board.Id, ["data"] = "data:image/png;base64," + Convert.ToBase64String(png), ["x"] = 1000, ["y"] = 1010, ["width"] = 200,
        });
        Assert.False(loose.TryGetProperty("cardId", out _));
        Assert.Equal((1000, 1020, 200), (loose.GetProperty("x").GetInt32(), loose.GetProperty("y").GetInt32(), loose.GetProperty("width").GetInt32()));

        // Both are real images on the board, with the same bytes as sent.
        var saved = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(2, saved.Images.Count);
        Assert.Equal(png, await owner.GetByteArrayAsync($"/api/images/{attached.GetProperty("id").GetGuid()}/content"));

        async Task Fails(Dictionary<string, object?> args, string message)
        {
            var result = await CallRawAsync(mcp, "add_image", args);
            Assert.True(result.IsError, Text(result));
            Assert.Contains(message, Text(result));
        }
        await Fails(new() { ["data"] = Convert.ToBase64String(png) }, "Give card");
        await Fails(new() { ["card"] = card.Id, ["data"] = "not base64!" }, "base64");
        await Fails(new() { ["card"] = card.Id, ["data"] = Convert.ToBase64String("<svg/>"u8.ToArray()) }, "not a PNG");
        await Fails(new() { ["card"] = card.Id, ["board"] = other.Id, ["data"] = Convert.ToBase64String(png) }, "not on that board");
        await Fails(new() { ["card"] = card.Id, ["data"] = Convert.ToBase64String(png), ["x"] = 20 }, "together");

        var readOnly = await ConnectAsync(await TokenAsync(owner, BoardRole.Viewer, "Reader"));
        var refused = await CallRawAsync(readOnly, "add_image", new() { ["card"] = card.Id, ["data"] = Convert.ToBase64String(png) });
        Assert.Contains("read-only", Text(refused));
    }

    [Fact]
    public async Task Agents_upload_image_files_through_a_one_time_url()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-upload@example.com");
        var board = await owner.CreateBoardAsync("MCP uploads");
        var card = await owner.CreateCardAsync(board.Id, "Screenshots");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));
        var png = TestImages.Png(800, 600);

        var upload = await CallAsync(mcp, "begin_image_upload", new()
        {
            ["card"] = card.ReadableId, ["fileName"] = "shot.png", ["caption"] = "Before the fix",
        });
        var url = upload.GetProperty("uploadUrl").GetString()!;
        Assert.Contains(url, upload.GetProperty("command").GetString());

        // What curl -T does: a plain PUT of the file, without the token.
        var response = await factory.CreateAnonymousClient().PutAsync(url, new ByteArrayContent(png));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var image = (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
        Assert.Equal((card.Id, "shot.png", "Before the fix", "Claude Code"), (image.CardId, image.FileName, image.Caption, image.AddedBy));
        Assert.Equal(png, await owner.GetByteArrayAsync(image.ContentUrl));

        async Task Fails(Dictionary<string, object?> args, string message)
        {
            var result = await CallRawAsync(mcp, "begin_image_upload", args);
            Assert.True(result.IsError, Text(result));
            Assert.Contains(message, Text(result));
        }
        await Fails(new(), "Give card");
        await Fails(new() { ["board"] = board.Id, ["x"] = 20 }, "together");
        await Fails(new() { ["board"] = board.Id, ["width"] = 5 }, "Width");

        var readOnly = await ConnectAsync(await TokenAsync(owner, BoardRole.Viewer, "Reader"));
        var refused = await CallRawAsync(readOnly, "begin_image_upload", new() { ["board"] = board.Id });
        Assert.Contains("read-only", Text(refused));
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
        Assert.Equal(["Claude Code"], tagged.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));
        Assert.True(tagged.GetProperty("hasContent").GetBoolean());

        var folded = overview.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "Done");
        Assert.True(folded.GetProperty("collapsed").GetBoolean());
        Assert.Equal(1, folded.GetProperty("cardCount").GetInt32());
        Assert.False(folded.TryGetProperty("cards", out _));
        var shipped = await CallAsync(mcp, "search_cards", new() { ["board"] = board.Id, ["bucket"] = "Done" });
        Assert.Equal("Shipped", Assert.Single(shipped.EnumerateArray()).GetProperty("title").GetString());

        var me = overview.GetProperty("people").EnumerateArray().Single(p => p.TryGetProperty("isYou", out _));
        Assert.Equal("Claude Code", me.GetProperty("name").GetString());
        Assert.All(overview.GetProperty("people").EnumerateArray(), p => Assert.False(p.TryGetProperty("avatarUrl", out _)));
    }

    [Fact]
    public async Task Create_card_with_working_assigns_the_agent_and_shows_the_halo()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-working-create@example.com");
        await owner.RenameAsync("Rosa");
        var board = await owner.CreateBoardAsync("MCP working create");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var created = await CallAsync(mcp, "create_card", new()
        {
            ["board"] = board.ShortId, ["title"] = "Start right away", ["assignees"] = new[] { "Rosa" }, ["working"] = true,
        });
        Assert.True(created.GetProperty("agentWorking").GetBoolean());
        Assert.Equal(["Rosa", "Claude Code"], created.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));

        var plain = await CallAsync(mcp, "create_card", new() { ["board"] = board.ShortId, ["title"] = "Later" });
        Assert.False(plain.TryGetProperty("agentWorking", out _));
        Assert.False(plain.TryGetProperty("assignees", out _));
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
    public async Task Agents_tick_off_add_and_append_without_reading_the_card()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-increments@example.com");
        var board = await owner.CreateBoardAsync("MCP increments");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var card = await CallAsync(mcp, "create_card", new()
        {
            ["board"] = board.Id, ["title"] = "Ship it", ["body"] = "The plan.",
            ["checklist"] = new[] { new { text = "Write tests", done = false }, new { text = "Open the PR", done = false } },
        });
        var readableId = card.GetProperty("readableId").GetString();

        var list = await CallAsync(mcp, "update_card_component", new()
        {
            ["card"] = readableId, ["component"] = "checklist", ["checkItems"] = new[] { "write TESTS" },
            ["addItems"] = new[] { new { text = "Merge", done = false } },
        });
        Assert.Equal([("Write tests", true), ("Open the PR", false), ("Merge", false)],
            list.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("text").GetString(), i.GetProperty("done").GetBoolean())));
        Assert.False(list.TryGetProperty("x", out _));

        var text = await CallAsync(mcp, "update_card_component", new() { ["card"] = readableId, ["component"] = "Text", ["appendText"] = "Done: all green." });
        Assert.Equal("The plan.\n\nDone: all green.", text.GetProperty("text").GetString());

        var unknown = await CallRawAsync(mcp, "update_card_component", new() { ["card"] = readableId, ["component"] = "Checklist", ["checkItems"] = new[] { "Deploy" } });
        Assert.True(unknown.IsError);
        Assert.Contains("\"Open the PR\"", Text(unknown));
        var misfit = await CallRawAsync(mcp, "update_card_component", new() { ["card"] = readableId, ["component"] = "Checklist", ["appendText"] = "No" });
        Assert.True(misfit.IsError);
        Assert.Contains("appendText", Text(misfit));
    }

    [Fact]
    public async Task Create_cards_adds_several_at_once_or_none()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-batch@example.com");
        var board = await owner.CreateBoardAsync("MCP batch");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var created = (await CallAsync(mcp, "create_cards", new()
        {
            ["board"] = board.ShortId,
            ["cards"] = new object[]
            {
                new { title = "First", tags = new[] { "ui" } },
                new { title = "Second", bucket = "Doing", working = true },
                new { title = "Third", bucket = "Doing", body = "Notes" },
            },
        })).EnumerateArray().ToList();
        Assert.Equal(["First", "Second", "Third"], created.Select(c => c.GetProperty("title").GetString()));
        Assert.Equal(["To do", "Doing", "Doing"], created.Select(c => c.GetProperty("bucket").GetString()));
        Assert.Equal([1, 2, 3], created.Select(c => int.Parse(c.GetProperty("readableId").GetString()!.Split('-')[1])));
        Assert.Equal(["Claude Code"], created[1].GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));

        // They don't land on each other.
        var cards = (await owner.GetFromJsonAsync<List<CardDetail>>($"/api/boards/{board.Id}/cards"))!;
        Assert.Equal(3, cards.Select(c => (c.X, c.Y)).Distinct().Count());

        // REST: all or nothing, with the problem keyed on the card.
        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards/batch", new
        {
            cards = new object[] { new { title = "Fine" }, new { title = "Lost", bucketId = Guid.NewGuid() } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cards[1].bucketId", await response.Content.ReadAsStringAsync());
        Assert.Equal(3, (await owner.GetFromJsonAsync<List<CardDetail>>($"/api/boards/{board.Id}/cards"))!.Count);
    }

    [Fact]
    public async Task Move_card_can_start_and_stop_work_in_the_same_call()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-move-working@example.com");
        var board = await owner.CreateBoardAsync("MCP move working");
        var card = await owner.CreateCardAsync(board.Id, "Task");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var started = await CallAsync(mcp, "move_card", new() { ["card"] = card.Id, ["bucket"] = "Doing", ["working"] = true });
        Assert.Equal("Doing", started.GetProperty("bucket").GetString());
        Assert.True(started.GetProperty("agentWorking").GetBoolean());
        Assert.Equal(["Claude Code"], started.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));

        var done = await CallAsync(mcp, "move_card", new() { ["card"] = card.Id, ["bucket"] = "Done", ["working"] = false });
        Assert.Equal("Done", done.GetProperty("bucket").GetString());
        Assert.False(done.TryGetProperty("agentWorking", out _));
        Assert.True(done.TryGetProperty("assignees", out _));
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

    private static async Task<string> TokenAsync(HttpClient owner, BoardRole access, string name = "Test agent", List<Guid>? boardIds = null)
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, access, boardIds });
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
