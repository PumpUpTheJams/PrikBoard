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

/// <summary>Agents working on cards through MCP.</summary>
public class McpCardTests(PrikBoardApiFactory factory) : McpTestBase, IClassFixture<PrikBoardApiFactory>
{
    protected override PrikBoardApiFactory Factory => factory;

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
    public async Task Agents_assign_people_by_name()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-assign-owner@example.com");
        await owner.RenameAsync("Owen");
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
        // An agent naming itself is assigned as the person it works for.
        var names = created.GetProperty("assignees").EnumerateArray().Select(a => a.GetString());
        Assert.Equal(["Mila V.", "Owen"], names);
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
    public async Task Agents_manage_tags_by_name_and_restore_deleted_tags_and_cards()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-tags@example.com");
        var board = await owner.CreateBoardAsync("MCP tags");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var card = await CallAsync(mcp, "create_card", new() { ["board"] = board.Id, ["title"] = "Tagged", ["tags"] = new[] { "ui" } });
        var cardId = card.GetProperty("id").GetGuid();

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
        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{cardId}"))!;
        Assert.Equal(["front-end"], saved.Tags);

        var missing = await CallRawAsync(mcp, "delete_tag", new() { ["board"] = board.Id, ["tag"] = "nope" });
        Assert.True(missing.IsError);
        Assert.Contains("\"front-end\"", Text(missing));

        // Deleting says how to undo it, and both come back, the tag by name in any case.
        Assert.Contains("restore_tag", Text(await CallRawAsync(mcp, "delete_tag", new() { ["board"] = board.Id, ["tag"] = "front-end" })));
        Assert.Contains("restore_card", Text(await CallRawAsync(mcp, "delete_card", new() { ["card"] = cardId })));
        var restoredTag = await CallAsync(mcp, "restore_tag", new() { ["board"] = board.Id, ["tag"] = "Front-End" });
        var restoredCard = await CallAsync(mcp, "restore_card", new() { ["card"] = cardId });

        Assert.Equal("front-end", restoredTag.GetProperty("name").GetString());
        Assert.Equal(cardId, restoredCard.GetProperty("id").GetGuid());
        Assert.Equal(["front-end"], restoredCard.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        var again = await CallRawAsync(mcp, "restore_tag", new() { ["board"] = board.Id, ["tag"] = "front-end" });
        Assert.True(again.IsError);
        Assert.Contains("No deleted tag", Text(again));
    }

    [Fact]
    public async Task Agents_search_restore_and_purge_deleted_cards_and_collapse_buckets()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-trash@example.com");
        var board = await owner.CreateBoardAsync("MCP trash");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor));
        var card = await owner.CreateCardAsync(board.Id, "An idea we dropped");
        var first = await owner.CreateCardAsync(board.Id, "First");
        var second = await owner.CreateCardAsync(board.Id, "Second");
        var third = await owner.CreateCardAsync(board.Id, "Third");
        foreach (var deleted in new[] { card, first, second, third }) await CallRawAsync(mcp, "delete_card", new() { ["card"] = deleted.Id });

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

        // The rest goes for good: one by its readable id, then the whole trash.
        await CallRawAsync(mcp, "purge_card", new() { ["card"] = first.ReadableId });
        var left = await CallAsync(mcp, "list_deleted_cards", new() { ["board"] = board.Id });
        var emptied = await CallAsync(mcp, "empty_trash", new() { ["board"] = board.ShortId });

        Assert.Equal(2, left.GetArrayLength());
        Assert.Equal(2, emptied.GetProperty("purged").GetInt32());
        Assert.Equal(0, (await CallAsync(mcp, "list_deleted_cards", new() { ["board"] = board.Id })).GetArrayLength());
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
        var byId = await CallAsync(mcp, "search_cards", new() { ["board"] = board.Id, ["text"] = created.GetProperty("readableId").GetString() });
        Assert.Equal(cardId, Assert.Single(byId.EnumerateArray().ToList()).GetProperty("id").GetGuid());

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
    public async Task Agents_start_work_as_they_create_or_move_a_card()
    {
        var owner = await factory.CreateSignedInClientAsync("mcp-working-create@example.com");
        await owner.RenameAsync("Rosa");
        var board = await owner.CreateBoardAsync("MCP working");
        var mcp = await ConnectAsync(await TokenAsync(owner, BoardRole.Editor, "Claude Code"));

        var created = await CallAsync(mcp, "create_card", new()
        {
            ["board"] = board.ShortId, ["title"] = "Start right away", ["assignees"] = new[] { "Rosa" }, ["working"] = true,
        });
        Assert.True(created.GetProperty("agentWorking").GetBoolean());
        // Rosa is the one the agent works for, already assigned.
        Assert.Equal(["Rosa"], created.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));
        var saved = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{created.GetProperty("id").GetGuid()}"))!;
        Assert.Equal((await owner.MeAsync()).Id, saved.AgentWorkingFor);

        var plain = await CallAsync(mcp, "create_card", new() { ["board"] = board.ShortId, ["title"] = "Later" });
        Assert.False(plain.TryGetProperty("agentWorking", out _));
        Assert.False(plain.TryGetProperty("assignees", out _));

        // Moving a card can start and stop work in the same call.
        var cardId = plain.GetProperty("id").GetGuid();
        var started = await CallAsync(mcp, "move_card", new() { ["card"] = cardId, ["bucket"] = "Doing", ["working"] = true });
        Assert.Equal("Doing", started.GetProperty("bucket").GetString());
        Assert.True(started.GetProperty("agentWorking").GetBoolean());
        Assert.Equal(["Rosa"], started.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));

        var done = await CallAsync(mcp, "move_card", new() { ["card"] = cardId, ["bucket"] = "Done", ["working"] = false });
        Assert.Equal("Done", done.GetProperty("bucket").GetString());
        Assert.False(done.TryGetProperty("agentWorking", out _));
        Assert.True(done.TryGetProperty("assignees", out _));
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
        await owner.RenameAsync("Owen");
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
        Assert.Equal(["Owen"], created[1].GetProperty("assignees").EnumerateArray().Select(a => a.GetString()));

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
}
