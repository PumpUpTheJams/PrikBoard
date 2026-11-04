using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class LabelApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static object Words(string text) => new[] { new { kind = "Paragraph", runs = new[] { new { text } } } };

    [Fact]
    public async Task A_label_snaps_its_corner_keeps_its_formatting_and_shows_on_the_board()
    {
        var board = await _client.CreateBoardAsync("Labels");

        var label = await Create(board.Id, new
        {
            x = 131,
            y = 289,
            ink = "Sky",
            size = "L",
            width = 321,
            height = 54,
            content = new object[]
            {
                new { kind = "Paragraph", runs = new object[] { new { text = "Before we " }, new { text = "ship", bold = true, ink = "Coral", size = "XL" } } },
                new { kind = "Bullet", runs = new object[] { new { text = "Read the " }, new { text = "repo", link = "https://example.com/docs" } } },
                new { kind = "Numbered", runs = new object[] { new { text = "done", strike = true, underline = true, italic = true } } },
            },
        });

        Assert.Equal((140, 280, 321, 54), (label.X, label.Y, label.Width, label.Height));
        Assert.Equal((InkColor.Sky, LabelSize.L), (label.Ink, label.Size));
        Assert.Null(label.CardId);
        Assert.Equal("Before we ship\nRead the repo\ndone", label.Text);
        Assert.Equal([LabelBlockKind.Paragraph, LabelBlockKind.Bullet, LabelBlockKind.Numbered], label.Content.Select(b => b.Kind));
        Assert.Equal(new LabelRun("ship", Bold: true, Ink: InkColor.Coral, Size: LabelSize.XL), label.Content[0].Runs[1]);
        Assert.Equal("https://example.com/docs", label.Content[1].Runs[1].Link);
        var me = await _client.MeAsync();
        Assert.Equal((me.DisplayName, me.Id), (label.AddedBy, label.AddedById));

        var shown = Assert.Single((await Board(board.Id)).Labels);
        Assert.Equal(new LabelRun("ship", Bold: true, Ink: InkColor.Coral, Size: LabelSize.XL), shown.Content[0].Runs[1]);

        // Without a measured size it estimates one from the text; ink and size have defaults.
        var plain = await Create(board.Id, new { x = 0, y = 0, content = Words("Short") });
        Assert.Equal((InkColor.Theme, LabelSize.S), (plain.Ink, plain.Size));
        Assert.InRange(plain.Width, 40, 120);
        Assert.InRange(plain.Height, 30, 50);
    }

    [Fact]
    public async Task Empty_runs_go_and_runs_styled_alike_join()
    {
        var board = await _client.CreateBoardAsync("Clean");

        var label = await Create(board.Id, new
        {
            x = 0,
            y = 0,
            content = new object[]
            {
                new { kind = "Paragraph", runs = Array.Empty<object>() },
                new { kind = "Paragraph", runs = new object[] { new { text = "" }, new { text = "a", bold = false }, new { text = "b" }, new { text = "c", bold = true } } },
                new { kind = "Paragraph", runs = new object[] { new { text = "  " } } },
            },
        });

        var block = Assert.Single(label.Content);
        Assert.Equal([new LabelRun("ab"), new LabelRun("c", Bold: true)], block.Runs);
    }

    [Fact]
    public async Task Bad_labels_are_rejected_with_the_field_that_is_wrong()
    {
        var board = await _client.CreateBoardAsync("Rejects");
        var other = await _client.CreateBoardAsync("Other");
        var foreign = await _client.CreateCardAsync(other.Id, "Elsewhere");
        var url = $"/api/boards/{board.Id}/labels";

        await AssertInvalid(url, new { x = 0, y = 0 }, "content");
        await AssertInvalid(url, new { x = 0, y = 0, content = Array.Empty<object>() }, "content");
        await AssertInvalid(url, new { x = 0, y = 0, content = Words("   ") }, "content");
        await AssertInvalid(url, new { x = 0, y = 0, content = Words("two"), markdown = "two" }, "markdown");
        await AssertInvalid(url, new { x = 0, content = Words("half") }, "y");
        await AssertInvalid(url, new { x = 0, y = 0, width = 10, content = Words("half") }, "height");
        await AssertInvalid(url, new { x = 0, y = 0, content = Words(new string('x', BoardLabel.MaxTextLength + 1)) }, "content");
        await AssertInvalid(url, new { x = 0, y = 0, cardId = foreign.Id, content = Words("away") }, "cardId");

        foreach (var link in new[] { "javascript:alert(1)", "data:text/html,x", "ftp://example.com", "/relative" })
        {
            await AssertInvalid(url, new { x = 0, y = 0, content = new[] { new { kind = "Paragraph", runs = new[] { new { text = "x", link } } } } }, "content[0].runs[0].link");
        }

        // A kind, ink or size that doesn't exist is refused when the request is read.
        foreach (var body in new object[]
        {
            new { x = 0, y = 0, content = Words("x"), ink = "Plaid" },
            new { x = 0, y = 0, content = Words("x"), size = "Huge" },
            new { x = 0, y = 0, content = new[] { new { kind = "Quote", runs = Array.Empty<object>() } } },
            new { x = 0, y = 0, content = new[] { new { kind = "Paragraph", runs = new[] { new { text = "x", ink = "Plaid" } } } } },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(url, body)).StatusCode);
        }
        Assert.Empty((await Board(board.Id)).Labels);
    }

    [Fact]
    public async Task Labels_change_move_and_go_between_the_board_and_cards()
    {
        var board = await _client.CreateBoardAsync("Edits");
        var other = await _client.CreateBoardAsync("Other");
        var card = await _client.CreateCardAsync(board.Id, "Target");
        var foreign = await _client.CreateCardAsync(other.Id, "Foreign");
        var label = await Create(board.Id, new { x = 0, y = 0, content = Words("Hello"), width = 100, height = 30 });

        var moved = await Patch(label.Id, new { x = 1201, y = 99, ink = "Mint" });
        Assert.Equal((1200, 100, InkColor.Mint, 100), (moved.X, moved.Y, moved.Ink, moved.Width));

        // A new size or text without a measured box gets an estimate, and a measured one is taken as it is.
        Assert.NotEqual(100, (await Patch(label.Id, new { size = "XL" })).Width);
        var measured = await Patch(label.Id, new { content = Words("Bye"), width = 77, height = 33 });
        Assert.Equal(("Bye", 77, 33), (measured.Text, measured.Width, measured.Height));

        var attached = await Patch(label.Id, new { cardId = card.Id, x = 61, y = 39 });
        Assert.Equal((card.Id, 60, 40), (attached.CardId, attached.X, attached.Y));
        // On a card's canvas it stays on the canvas.
        var pushed = await Patch(label.Id, new { x = 20_000, y = -40 });
        Assert.Equal((Layout.CardCanvasMax - 80, 0), (pushed.X, pushed.Y));
        var detached = await Patch(label.Id, new { detach = true, x = 1001, y = 2001 });
        Assert.Equal((null, 1000, 2000), (detached.CardId, detached.X, detached.Y));

        // Put on a card without a place, it finds a free spot.
        Assert.Equal(card.Id, (await Patch(label.Id, new { cardId = card.Id })).CardId);
        await AssertPatchInvalid(label.Id, new { detach = true }, "x");
        (await _client.PatchAsJsonAsync($"/api/labels/{label.Id}", new { detach = true, x = 0, y = 0 })).EnsureSuccessStatusCode();

        await AssertPatchInvalid(label.Id, new { cardId = card.Id, detach = true }, "detach");
        await AssertPatchInvalid(label.Id, new { cardId = foreign.Id, x = 0, y = 0 }, "cardId");
        await AssertPatchInvalid(label.Id, new { cardId = card.Id, x = 40 }, "y");
        await AssertPatchInvalid(label.Id, new { width = 40 }, "height");
        await AssertPatchInvalid(label.Id, new { content = Words("two"), markdown = "two" }, "markdown");
    }

    [Fact]
    public async Task A_deleted_label_comes_back_with_the_same_id_until_it_is_purged()
    {
        var board = await _client.CreateBoardAsync("Delete");
        var label = await Create(board.Id, new { x = 40, y = 40, content = Words("Keep me"), ink = "Coral" });

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/labels/{label.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/labels/{label.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/labels/{label.Id}")).StatusCode);
        Assert.Empty((await Board(board.Id)).Labels);

        var restored = await _client.PostAsync($"/api/labels/{label.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var back = (await restored.Content.ReadFromJsonAsync<LabelDetail>())!;
        Assert.Equal((label.Id, 40, 40, InkColor.Coral, "Keep me"), (back.Id, back.X, back.Y, back.Ink, back.Text));

        (await _client.DeleteAsync($"/api/labels/{label.Id}")).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var longAgo = DateTimeOffset.UtcNow - Tombstones.Retention - TimeSpan.FromDays(1);
        await db.Labels.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).Where(l => l.Id == label.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DeletedAt, longAgo));
        Assert.True(await Tombstones.PurgeAsync(db, DateTimeOffset.UtcNow - Tombstones.Retention, CancellationToken.None) >= 1);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/labels/{label.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Labels_on_a_card_go_and_come_back_with_the_card_and_show_in_its_history()
    {
        var board = await _client.CreateBoardAsync("Cascade");
        var card = await _client.CreateCardAsync(board.Id, "Annotated");
        var onCard = await Create(board.Id, new { x = 505, y = 611, content = Words("On it"), cardId = card.Id });
        var removedEarlier = await Create(board.Id, new { x = 0, y = 0, content = Words("Gone"), cardId = card.Id });
        var onBoard = await Create(board.Id, new { x = 0, y = 0, content = Words("Beside") });
        Assert.Equal((card.Id, 500, 620), (onCard.CardId, onCard.X, onCard.Y));
        (await _client.DeleteAsync($"/api/labels/{removedEarlier.Id}")).EnsureSuccessStatusCode();

        var history = await _client.GetStringAsync($"/api/cards/{card.Id}/history");
        Assert.Contains("LabelAdded", history);
        Assert.Contains("LabelRemoved", history);

        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(onBoard.Id, Assert.Single((await Board(board.Id)).Labels).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/labels/{onCard.Id}/restore", null)).StatusCode);

        (await _client.PostAsync($"/api/cards/{card.Id}/restore", null)).EnsureSuccessStatusCode();
        // The one deleted on its own earlier stays deleted.
        Assert.Equal(new[] { onCard.Id, onBoard.Id }.Order(), (await Board(board.Id)).Labels.Select(l => l.Id).Order());
    }

    [Fact]
    public async Task A_card_with_only_a_label_is_not_blank()
    {
        var board = await _client.CreateBoardAsync("Not blank");
        var empty = (await (await _client.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "" })).Content.ReadFromJsonAsync<CardDetail>())!;
        await Create(board.Id, new { x = 0, y = 0, content = Words("Something"), cardId = empty.Id });

        (await _client.DeleteAsync($"/api/cards/{empty.Id}")).EnsureSuccessStatusCode();

        // Not gone for good: it can come back, with its label.
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/cards/{empty.Id}/restore", null)).StatusCode);
        Assert.Single((await Board(board.Id)).Labels);
    }

    [Fact]
    public async Task Viewers_can_look_editors_can_write_and_outsiders_see_nothing()
    {
        var board = await _client.CreateBoardAsync("Roles");
        var label = await Create(board.Id, new { x = 0, y = 0, content = Words("Mine") });
        var viewer = await factory.CreateSignedInClientAsync("label-viewer@example.com");
        var editor = await factory.CreateSignedInClientAsync("label-editor@example.com");
        var outsider = await factory.CreateSignedInClientAsync("label-outsider@example.com");
        await _client.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        await _client.ShareAsync(board.Id, editor, BoardRole.Editor);
        var request = new { x = 0, y = 0, content = Words("New") };

        Assert.Single((await viewer.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Labels);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/boards/{board.Id}/labels", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"/api/labels/{label.Id}", new { x = 40 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/labels/{label.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync($"/api/boards/{board.Id}/labels", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PatchAsJsonAsync($"/api/labels/{label.Id}", new { x = 40 })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/boards/{board.Id}/labels", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PatchAsJsonAsync($"/api/labels/{label.Id}", new { x = 40 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/api/labels/{label.Id}")).StatusCode);
    }

    [Fact]
    public async Task Visitors_of_a_public_board_see_labels_without_who_added_them()
    {
        var board = await _client.CreateBoardAsync("Open");
        await Create(board.Id, new { x = 0, y = 0, content = Words("For all") });
        (await _client.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).EnsureSuccessStatusCode();

        var seen = (await factory.CreateAnonymousClient().GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;

        var label = Assert.Single(seen.Labels);
        Assert.Equal(("For all", "", (Guid?)null), (label.Text, label.AddedBy, label.AddedById));
    }

    [Fact]
    public async Task A_label_joins_a_bucket_by_its_centre_or_by_where_it_was_dropped()
    {
        var board = await _client.CreateBoardAsync("Buckets");
        var todo = board.Buckets[0];
        var card = await _client.CreateCardAsync(board.Id, "Canvas");

        var inside = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, content = Words("In"), width = 100, height = 40 });
        var outside = await Create(board.Id, new { x = 3000, y = 100, content = Words("Out"), width = 100, height = 40 });
        var onCard = await Create(board.Id, new { cardId = card.Id, x = 20, y = 20, content = Words("Card"), width = 100, height = 40 });
        Assert.Equal(todo.Id, inside.BucketId);
        Assert.Null(outside.BucketId);
        Assert.Null(onCard.BucketId);

        // Moving it out leaves the bucket; new text alone keeps it where it is.
        Assert.Null((await Patch(inside.Id, new { x = 3000 })).BucketId);
        Assert.Equal(todo.Id, (await Patch(outside.Id, new { x = todo.X + 20, y = todo.Y + 200 })).BucketId);
        Assert.Equal(todo.Id, (await Patch(outside.Id, new { content = Words("Still in"), width = 100, height = 40 })).BucketId);
        // Put on the board from a card, it joins the bucket it lands in.
        Assert.Equal(todo.Id, (await Patch(onCard.Id, new { detach = true, x = todo.X + 20, y = todo.Y + 300 })).BucketId);

        // The web app's drop says which: loose even over a bucket, or into one it only hangs over.
        Assert.Null((await Patch(outside.Id, new { x = todo.X + 20, y = todo.Y + 200, loose = true })).BucketId);
        Assert.Equal(todo.Id, (await Patch(outside.Id, new { x = todo.X - 60, y = todo.Y + 200, bucketId = todo.Id })).BucketId);
        var placed = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, content = Words("Placed"), loose = true });
        Assert.Null(placed.BucketId);
        await AssertPatchInvalid(outside.Id, new { x = 3000, y = 3000, bucketId = todo.Id }, "bucketId");
    }

    [Fact]
    public async Task Buckets_carry_let_go_and_are_drawn_around_labels_like_other_items()
    {
        var board = await _client.CreateBoardAsync("Docked");
        var todo = board.Buckets[0];
        var inside = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, content = Words("In"), width = 100, height = 40 });
        var low = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 400, content = Words("Low"), width = 100, height = 40 });
        var outside = await Create(board.Id, new { x = 3000, y = 100, content = Words("Out"), width = 100, height = 40 });

        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = todo.X, y = todo.Y + 1000 })).EnsureSuccessStatusCode();
        var labels = (await Board(board.Id)).Labels;
        Assert.Equal((todo.X + 20, todo.Y + 1100, todo.Id), Spot(labels, inside.Id));
        Assert.Equal((3000, 100, (Guid?)null), Spot(labels, outside.Id));

        // Undo moves it back with exactly the labels that came along: here none, so they stay behind, loose.
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = todo.X, y = todo.Y, carryLabelIds = Array.Empty<Guid>() })).EnsureSuccessStatusCode();
        Assert.Equal((todo.X + 20, todo.Y + 1100, (Guid?)null), Spot((await Board(board.Id)).Labels, inside.Id));
        var resizeWithCarry = await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = todo.X, width = 400, carryLabelIds = new[] { inside.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, resizeWithCarry.StatusCode);
        Assert.Contains("carryLabelIds", await resizeWithCarry.Content.ReadAsStringAsync());

        // A bucket drawn around a loose label takes it.
        var around = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/buckets", new { name = "Around", x = 2900, y = 0, width = 600, height = 600 });
        var bucket = (await around.Content.ReadFromJsonAsync<BucketDetail>())!;
        Assert.Equal(bucket.Id, (await Board(board.Id)).Labels.Single(l => l.Id == outside.Id).BucketId);

        // Deleted, it lets it go; restored, it takes nothing back.
        (await _client.DeleteAsync($"/api/buckets/{bucket.Id}")).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/buckets/{bucket.Id}/restore", null)).EnsureSuccessStatusCode();
        Assert.Null((await Board(board.Id)).Labels.Single(l => l.Id == outside.Id).BucketId);
        Assert.Equal(low.Id, (await Board(board.Id)).Labels.Single(l => l.Id == low.Id).Id);
    }

    [Fact]
    public async Task Labels_go_into_a_bucket_by_id_at_their_own_size()
    {
        var board = await _client.CreateBoardAsync("Packed");
        var todo = board.Buckets[0];
        var label = await Create(board.Id, new { x = 3000, y = 100, content = Words("Packed"), width = 100, height = 40 });
        var card = await _client.CreateCardAsync(board.Id, "On a card");
        var attached = await Create(board.Id, new { cardId = card.Id, x = 0, y = 0, content = Words("Attached") });

        var response = await _client.PostAsJsonAsync($"/api/buckets/{todo.Id}/items", new { labelIds = new[] { label.Id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var placed = (await response.Content.ReadFromJsonAsync<PlacedItems>())!;
        var packed = Assert.Single(placed.Labels);
        Assert.Equal(todo.Id, packed.BucketId);
        Assert.InRange(packed.X, todo.X, todo.X + todo.Width);

        var onCard = await _client.PostAsJsonAsync($"/api/buckets/{todo.Id}/items", new { labelIds = new[] { attached.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, onCard.StatusCode);
        Assert.Contains("labelIds", await onCard.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Labels_are_deleted_and_restored_in_one_request_with_other_items()
    {
        var board = await _client.CreateBoardAsync("Bulk");
        var card = await _client.CreateCardAsync(board.Id, "Beside");
        var loose = await Create(board.Id, new { x = 0, y = 0, content = Words("Loose") });
        var onCard = await Create(board.Id, new { x = 0, y = 0, content = Words("On a card"), cardId = card.Id });
        var ids = new { cardIds = new[] { card.Id }, labelIds = new[] { loose.Id } };

        (await _client.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", ids)).EnsureSuccessStatusCode();
        Assert.Empty((await Board(board.Id)).Labels);

        var restored = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/items/restore", ids);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(loose.Id, Assert.Single((await restored.Content.ReadFromJsonAsync<RestoredItems>())!.Labels).Id);
        // The label on the card comes back with its card.
        Assert.Equal(new[] { loose.Id, onCard.Id }.Order(), (await Board(board.Id)).Labels.Select(l => l.Id).Order());

        var unknown = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new { labelIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("labelIds", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Labels_travel_with_copied_cards_and_buckets_and_their_links_follow_the_copies()
    {
        var board = await _client.CreateBoardAsync("Source");
        var target = await _client.CreateBoardAsync("Target");
        var todo = board.Buckets[0];
        var card = await _client.CreateCardAsync(board.Id, "Linked");
        var kept = await _client.CreateCardAsync(board.Id, "Not copied");
        (await _client.PostAsJsonAsync($"/api/cards/{kept.Id}/move", new { bucketId = board.Buckets[1].Id })).EnsureSuccessStatusCode();
        var loose = await Create(board.Id, new
        {
            x = 3000,
            y = 4000,
            ink = "Lilac",
            size = "XL",
            width = 321,
            height = 70,
            content = new[]
            {
                new { kind = "Paragraph", runs = new object[]
                {
                    new { text = "to copy", link = card.Path },
                    new { text = " to other", link = kept.Path },
                    new { text = " to web", link = "https://example.com/" },
                } },
            },
        });
        await Create(board.Id, new { x = 20, y = 40, content = Words("On the card"), ink = "Violet", cardId = card.Id });
        var inBucket = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, content = Words("In the bucket"), width = 100, height = 40 });

        var clip = await CopyAsync(board.Id, new { cardIds = new[] { card.Id }, bucketIds = new[] { todo.Id }, labelIds = new[] { loose.Id } });
        Assert.Contains($"\"version\":{BoardClip.CurrentVersion}", clip);
        var pasted = await _client.PostAsync($"/api/boards/{target.Id}/paste", Json($$"""{"clip":{{clip}},"x":0,"y":0}"""));
        Assert.Equal(HttpStatusCode.OK, pasted.StatusCode);
        Assert.Equal(3, (await pasted.Content.ReadFromJsonAsync<PastedItems>())!.Labels!.Count);

        var copy = await Board(target.Id);
        var linked = copy.Labels.Single(l => l.Text.StartsWith("to copy"));
        Assert.Equal((InkColor.Lilac, LabelSize.XL, 321, 70), (linked.Ink, linked.Size, linked.Width, linked.Height));
        var copiedCard = copy.Cards.Single(n => n.Title == "Linked");
        Assert.Equal(copiedCard.Path, linked.Content[0].Runs[0].Link);
        // A card that wasn't copied keeps its link on the same server, and a web link stays.
        Assert.Equal(kept.Path, linked.Content[0].Runs[1].Link);
        Assert.Equal("https://example.com/", linked.Content[0].Runs[2].Link);
        Assert.Equal(copiedCard.Id, copy.Labels.Single(l => l.Text == "On the card").CardId);
        var pastedBucket = copy.Labels.Single(l => l.Text == "In the bucket").BucketId;
        Assert.Equal(todo.Name, copy.Buckets.Single(b => b.Id == pastedBucket).Name);
        Assert.NotEqual(target.Buckets[0].Id, pastedBucket);
        Assert.NotEqual(inBucket.Id, copy.Labels.Single(l => l.Text == "In the bucket").Id);

        // A label on a card that isn't in the clip, or with a bad link, refuses the whole paste.
        var before = copy.Labels.Count;
        var badLink = clip.Replace("https://example.com/", "javascript:alert(1)");
        Assert.NotEqual(clip, badLink);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/boards/{target.Id}/paste", Json($$"""{"clip":{{badLink}},"x":0,"y":0}"""))).StatusCode);
        var badCard = clip.Replace($"\"cardId\":\"{card.Id}\"", $"\"cardId\":\"{Guid.NewGuid()}\"");
        Assert.NotEqual(clip, badCard);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/boards/{target.Id}/paste", Json($$"""{"clip":{{badCard}},"x":0,"y":0}"""))).StatusCode);
        Assert.Equal(before, (await Board(target.Id)).Labels.Count);
    }

    [Fact]
    public async Task A_clip_from_before_labels_still_pastes_and_a_cut_moves_a_label()
    {
        var board = await _client.CreateBoardAsync("Cut from");
        var target = await _client.CreateBoardAsync("Cut to");
        var card = await _client.CreateCardAsync(board.Id, "Old");
        var label = await Create(board.Id, new { x = 100, y = 100, content = Words("Moving") });

        var old = await CopyAsync(board.Id, new { cardIds = new[] { card.Id } });
        var withoutLabels = old.Replace($"\"version\":{BoardClip.CurrentVersion}", "\"version\":6").Replace(",\"labels\":[]", "");
        Assert.DoesNotContain("labels", withoutLabels);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/boards/{target.Id}/paste", Json($$"""{"clip":{{withoutLabels}},"x":0,"y":0}"""))).StatusCode);

        var cut = await CopyAsync(board.Id, new { labelIds = new[] { label.Id }, cut = true });
        var moved = await _client.PostAsync($"/api/boards/{target.Id}/paste", Json($$"""{"clip":{{cut}},"x":0,"y":0}"""));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var result = (await moved.Content.ReadFromJsonAsync<PastedItems>())!;
        Assert.Equal(label.Id, Assert.Single(result.Moved!.Labels!).Id);
        Assert.Empty((await Board(board.Id)).Labels);
        Assert.Equal("Moving", Assert.Single((await Board(target.Id)).Labels).Text);
    }

    [Fact]
    public async Task Markdown_writes_keep_the_labels_own_ink_and_size_and_older_card_links_get_shorter()
    {
        var board = await _client.CreateBoardAsync("Markdown");
        var card = await _client.CreateCardAsync(board.Id, "Target");
        var label = await Create(board.Id, new { x = 0, y = 0, ink = "Coral", size = "L", markdown = "**Bold** and [link](https://example.com/)\n\n- one\n- two" });

        Assert.Equal([LabelBlockKind.Paragraph, LabelBlockKind.Bullet, LabelBlockKind.Bullet], label.Content.Select(b => b.Kind));
        Assert.Equal(new LabelRun("Bold", Bold: true), label.Content[0].Runs[0]);

        var rewritten = await Patch(label.Id, new { markdown = "*Only this*" });
        Assert.Equal((InkColor.Coral, LabelSize.L), (rewritten.Ink, rewritten.Size));
        Assert.Equal(new LabelRun("Only this", Italic: true), Assert.Single(Assert.Single(rewritten.Content).Runs));

        var older = await Patch(label.Id, new { content = new[] { new { kind = "Paragraph", runs = new[] { new { text = "x", link = $"/#/boards/{board.Id}/cards/{card.Id}" } } } } });
        Assert.Equal(card.Path, older.Content[0].Runs[0].Link);

        await AssertPatchInvalid(label.Id, new { markdown = "   " }, "markdown");
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("**bold** *italic* ~~gone~~", "**bold** *italic* ~~gone~~")]
    [InlineData("***both***", "***both***")]
    [InlineData("**bold with *italic* inside**", "**bold with *italic* inside**")]
    [InlineData("**bold** then *italic*", "**bold** then *italic*")]
    [InlineData("[link](https://example.com/a)", "[link](https://example.com/a)")]
    [InlineData("[card](/#/boards/abc123/cards/7)", "[card](/#/boards/abc123/cards/7)")]
    [InlineData("- a\n- b", "- a\n- b")]
    [InlineData("1. a\n1. b", "1. a\n2. b")]
    [InlineData("first\n\nsecond", "first\n\nsecond")]
    [InlineData("one\ntwo", "one\ntwo")]
    [InlineData("before\n- item\n\nafter", "before\n\n- item\n\nafter")]
    [InlineData("## Status\nIn review", "## Status\n\nIn review")]
    [InlineData("Use `dotnet test` here", "Use `dotnet test` here")]
    [InlineData("`` a`b ``", "``a`b``")]
    [InlineData("- a\n  - b\n    - c\n- d", "- a\n    - b\n        - c\n- d")]
    [InlineData("1. a\n   - b\n2. c", "1. a\n    - b\n2. c")]
    [InlineData("- [ ] open\n- [x] done", "- [ ] open\n- [x] done")]
    [InlineData("- item\n  goes on", "- item\ngoes on")]
    [InlineData("> quoted\n> more\n>\n> again", "> quoted\n> more\n>\n> again")]
    [InlineData("```js\nconst a = 1\n\n  b *not bold*\n```", "```\nconst a = 1\n\n  b *not bold*\n```")]
    [InlineData("a\n\n---\n\nb", "a\n\n---\n\nb")]
    [InlineData("| a | b |\n|---|:-:|\n| **c** | d\\|e |", "| a | b |\n| --- | --- |\n| **c** | d\\|e |")]
    [InlineData("\\# not a heading", "\\# not a heading")]
    [InlineData("See https://example.com/a_b?c=1, (or https://example.com/x).", "See https://example.com/a_b?c=1, (or https://example.com/x).")]
    [InlineData("**https://example.com/b**", "**https://example.com/b**")]
    [InlineData("xhttps://example.com", "xhttps://example.com")]
    [InlineData("\\> not a quote", "\\> not a quote")]
    [InlineData("\\---", "\\---")]
    public void Markdown_goes_both_ways(string markdown, string back) =>
        Assert.Equal(back, LabelMarkdown.From(LabelMarkdown.Parse(markdown)));

    [Theory]
    [InlineData("2 * 3 * 4")]
    [InlineData("a ** b")]
    [InlineData("*not closed")]
    [InlineData("[not a link](javascript:alert(1))")]
    [InlineData("[no url]")]
    public void What_only_looks_like_markdown_stays_text(string markdown)
    {
        var run = Assert.Single(Assert.Single(LabelMarkdown.Parse(markdown)).Runs);
        Assert.Equal(new LabelRun(markdown), run);
    }

    [Theory]
    [InlineData("*stars* and [brackets] and ~~tildes~~ and back\\slash")]
    [InlineData("- not a list")]
    [InlineData("1. not a list")]
    public void Text_with_markdown_marks_survives_a_round_trip(string text)
    {
        List<LabelBlock> blocks = [new(LabelBlockKind.Paragraph, [new(text)])];
        Assert.Equal(blocks[0].Runs, LabelMarkdown.Parse(LabelMarkdown.From(blocks))[0].Runs);
        Assert.Equal(LabelBlockKind.Paragraph, LabelMarkdown.Parse(LabelMarkdown.From(blocks))[0].Kind);
    }

    private static (int, int, Guid?) Spot(IEnumerable<LabelDetail> labels, Guid id)
    {
        var label = labels.Single(l => l.Id == id);
        return (label.X, label.Y, label.BucketId);
    }

    [Fact]
    public void Markdown_reads_each_kind_of_block()
    {
        var blocks = LabelMarkdown.Parse("### Head\n\n- [x] done\n  - inner\n\n> said\n\n```\ncode\n```\n\n| a | b |\n|---|---|\n| [c](https://example.com) | `d` |\n\n---");
        Assert.Equal(
            [LabelBlockKind.Heading, LabelBlockKind.Task, LabelBlockKind.Bullet, LabelBlockKind.Quote, LabelBlockKind.Code, LabelBlockKind.TableRow, LabelBlockKind.TableRow, LabelBlockKind.Rule],
            blocks.Select(b => b.Kind));
        Assert.Equal(3, blocks[0].Level);
        Assert.True(blocks[1].Done);
        Assert.Equal(1, blocks[2].Level);
        Assert.Equal("https://example.com", blocks[6].Cells![0][0].Link);
        Assert.True(blocks[6].Cells![1][0].Code);
    }

    [Fact]
    public async Task Every_kind_of_block_is_kept_and_checked()
    {
        var board = await _client.CreateBoardAsync("Blocks");
        var label = await Create(board.Id, new
        {
            content = new object[]
            {
                new { kind = "Heading", level = 2, runs = new[] { new { text = "Head" } } },
                new { kind = "Task", done = true, level = 1, runs = new[] { new { text = "Tick" } } },
                new { kind = "Code", runs = new object[] { new { text = "a" }, new { text = "b", bold = true } } },
                new { kind = "TableRow", cells = new[] { new[] { new { text = "x" } }, new[] { new { text = "y" } } } },
                new { kind = "Rule", runs = new[] { new { text = "dropped" } } },
            },
        });
        Assert.Equal(2, label.Content[0].Level);
        Assert.Equal((true, 1), (label.Content[1].Done, label.Content[1].Level));
        Assert.Equal(new LabelRun("ab"), Assert.Single(label.Content[2].Runs));
        Assert.Equal("x | y", label.Text.Split('\n')[3]);
        Assert.Empty(label.Content[4].Runs);

        await AssertInvalid($"/api/boards/{board.Id}/labels", new { content = new[] { new { kind = "Heading", level = 7, runs = new[] { new { text = "a" } } } } }, "content[0].level");
        await AssertInvalid($"/api/boards/{board.Id}/labels", new { content = new[] { new { kind = "Bullet", level = 6, runs = new[] { new { text = "a" } } } } }, "content[0].level");
        await AssertInvalid($"/api/boards/{board.Id}/labels", new { content = new[] { new { kind = "TableRow", runs = new[] { new { text = "a" } } } } }, "content[0].cells");
        await AssertInvalid($"/api/boards/{board.Id}/labels", new { content = new[] { new { kind = "TableRow", cells = new[] { new[] { new { text = "a", link = "javascript:alert(1)" } } } } } }, "content[0].cells[0][0].link");
    }

    [Fact]
    public async Task Long_text_nobody_measured_wraps_and_a_chosen_width_stays()
    {
        var board = await _client.CreateBoardAsync("Wrapping");
        var longText = string.Join(' ', Enumerable.Repeat("word", 200));

        var agent = await Create(board.Id, new { markdown = longText, size = "S" });
        Assert.Equal((BoardLabel.DefaultWrapWidth, BoardLabel.DefaultWrapWidth), (agent.WrapWidth, agent.Width));
        Assert.True(agent.Height > 100, $"Height {agent.Height}");

        var measured = await Create(board.Id, new { markdown = longText, width = 900, height = 30 });
        Assert.Null(measured.WrapWidth);

        var chosen = await Create(board.Id, new { markdown = longText, wrapWidth = 500 });
        Assert.Equal((500, 500), (chosen.WrapWidth, chosen.Width));

        var unwrapped = await Update(chosen.Id, new { wrapWidth = 0 });
        Assert.Null(unwrapped.WrapWidth);
        Assert.True(unwrapped.Width > 500, $"Width {unwrapped.Width}");
        Assert.Equal(BoardLabel.MinWrapWidth, (await Update(chosen.Id, new { wrapWidth = 1 })).WrapWidth);
    }

    [Fact]
    public async Task A_label_has_a_background_on_a_card_and_none_on_the_board_unless_asked()
    {
        var board = await _client.CreateBoardAsync("Backgrounds");
        var card = await _client.CreateCardAsync(board.Id, "With labels");

        var loose = await Create(board.Id, new { markdown = "loose" });
        var onCard = await Create(board.Id, new { cardId = card.Id, markdown = "on a card" });
        Assert.False(loose.Background);
        Assert.True(onCard.Background);
        Assert.Equal(loose.Height, (await Create(board.Id, new { markdown = "loose", background = true })).Height);

        Assert.True((await Create(board.Id, new { markdown = "asked", background = true })).Background);
        Assert.False((await Create(board.Id, new { cardId = card.Id, markdown = "asked", background = false })).Background);

        Assert.True((await Update(loose.Id, new { background = true })).Background);
        Assert.False((await Update(onCard.Id, new { background = false })).Background);

        // Moving it between the board and a card leaves it as it is.
        Assert.True((await Update(loose.Id, new { cardId = card.Id })).Background);
        Assert.False((await Update(onCard.Id, new { detach = true, x = 0, y = 0 })).Background);
    }

    private async Task<Guid> CardWithRawComponents(Guid boardId, string title, string componentsJson, bool deleted = false)
    {
        var card = await _client.CreateCardAsync(boardId, title);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE \"Cards\" SET \"Components\" = {componentsJson}::jsonb WHERE \"Id\" = {card.Id}");
        if (deleted) (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        return card.Id;
    }

    private static string Note(Guid id, int x, int y, int width, int height, string text, string? title = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { id, kind = "Text", x, y, width, height, title, text, items = Array.Empty<object>(), links = Array.Empty<object>() });

    [Fact]
    public async Task Migrating_notes_keeps_their_text_formatting_and_place_with_a_backup()
    {
        var board = await _client.CreateBoardAsync("Old notes");
        var (rich, empty, kept) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        const string markdown = "## Status\nIn **review**: `free-text`.\n\n- first\n  - nested\n\n- [x] done\n\n> quoted\n\n```\ncode()\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n---\n\n[link](https://example.com)";
        var checklist = $$"""{"id":"{{kept}}","kind":"Checklist","x":20,"y":300,"width":340,"height":200,"items":[{"text":"Keep me","done":true}],"links":[]}""";
        var cardId = await CardWithRawComponents(board.Id, "Old card", $"[{Note(rich, 20, 20, 400, 100, markdown, "Why")},{Note(empty, 500, 20, 340, 220, "")},{checklist}]");
        var deletedCard = await CardWithRawComponents(board.Id, "In the trash", $"[{Note(Guid.CreateVersion7(), 20, 20, 340, 220, "from the trash")}]", deleted: true);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var report = await NoteMigration.RunAsync(db, TestContext.Current.CancellationToken);
        Assert.True(report.Cards >= 2 && report.Labels >= 2 && report.Empty >= 1, report.ToString());
        Assert.Equal(0, (await NoteMigration.RunAsync(db, TestContext.Current.CancellationToken)).Cards);

        var label = Assert.Single(await db.Labels.AsNoTracking().Where(l => l.CardId == cardId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal((20, 20, 400, 400, true, InkColor.Theme, LabelSize.S), (label.X, label.Y, label.Width, label.WrapWidth!.Value, label.Background, label.Ink, label.Size));
        Assert.Equal(
            [LabelBlockKind.Heading, LabelBlockKind.Heading, LabelBlockKind.Paragraph, LabelBlockKind.Bullet, LabelBlockKind.Bullet, LabelBlockKind.Task, LabelBlockKind.Quote, LabelBlockKind.Code, LabelBlockKind.TableRow, LabelBlockKind.TableRow, LabelBlockKind.Rule, LabelBlockKind.Paragraph],
            label.Content.Select(b => b.Kind));
        Assert.Contains("### Why", LabelMarkdown.From(label.Content));
        Assert.Contains("[link](https://example.com)", LabelMarkdown.From(label.Content));

        // The note grew past its old height, so the checklist below it moved; only the checklist is left in the components.
        var card = (await _client.GetFromJsonAsync<CardDetail>($"/api/cards/{cardId}"))!;
        var left = Assert.Single(card.Components);
        Assert.Equal((kept, CardComponentKind.Checklist, "Keep me"), (left.Id, left.Kind, left.Items![0].Text));
        Assert.True(left.Y >= label.Y + label.Height, $"Checklist at {left.Y}, label to {label.Y + label.Height}");
        Assert.Equal(0, left.Y % Layout.Grid);

        var backups = await db.NoteBackups.AsNoTracking().Where(b => b.CardId == cardId).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, backups.Count);
        var backup = backups.Single(b => b.Id == rich);
        Assert.Equal((markdown, "Why", label.Id, 20, 100), (backup.Text, backup.Title, backup.LabelId!.Value, backup.Y, backup.Height));
        Assert.Null(backups.Single(b => b.Id == empty).LabelId);

        // The trash's note comes back with its card.
        var hidden = await db.Labels.AsNoTracking().WithDeleted().SingleAsync(l => l.CardId == deletedCard, TestContext.Current.CancellationToken);
        Assert.NotNull(hidden.DeletedAt);
        (await _client.PostAsJsonAsync($"/api/cards/{deletedCard}/restore", new { })).EnsureSuccessStatusCode();
        Assert.Contains((await Board(board.Id)).Labels, l => l.CardId == deletedCard && l.Text == "from the trash");
    }

    [Fact]
    public void Migrating_a_note_too_long_with_its_title_or_with_a_bad_link_never_fails()
    {
        var (longOne, plain) = NoteMigration.BlocksOf("Title", new string('x', BoardLabel.MaxTextLength));
        Assert.False(plain);
        Assert.Equal(BoardLabel.MaxTextLength, longOne![1].Runs[0].Text.Length);

        Assert.Null(NoteMigration.BlocksOf("  ", "\n").Blocks);

        var (bad, fellBack) = NoteMigration.BlocksOf(null, $"[a](https://example.com/{new string('x', BoardLabel.MaxLinkLength)})");
        Assert.True(fellBack);
        Assert.Contains("https://example.com/", Assert.Single(bad!).Runs[0].Text);
    }

    [Fact]
    public async Task A_clip_from_before_labels_pastes_its_notes_as_labels()
    {
        var board = await _client.CreateBoardAsync("Old clip");
        var card = await _client.CreateCardAsync(board.Id, "Noted");
        var response = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/copy", new { cardIds = new[] { card.Id } });
        var clip = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        clip["version"] = 6;
        clip["cards"]![0]!["components"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "Text", ["title"] = "Why", ["text"] = "**Because** of this.", ["x"] = 40, ["y"] = 60, ["width"] = 400, ["height"] = 200,
        });

        var pasted = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip, x = 0, y = 0 });
        var body = await pasted.Content.ReadAsStringAsync();
        Assert.True(pasted.IsSuccessStatusCode, body);
        var items = System.Text.Json.JsonSerializer.Deserialize<PastedItems>(body, System.Text.Json.JsonSerializerOptions.Web)!;
        var copy = Assert.Single(items.Cards);
        Assert.Empty(copy.Components);
        var label = Assert.Single((await Board(board.Id)).Labels, l => l.CardId == copy.Id);
        Assert.Equal((40, 60, 400, true), (label.X, label.Y, label.WrapWidth!.Value, label.Background));
        Assert.Equal("### Why\n\n**Because** of this.", LabelMarkdown.From(label.Content));
    }

    [Fact]
    public async Task A_label_keeps_the_height_of_its_box_and_grows_past_it()
    {
        var board = await _client.CreateBoardAsync("Boxes");
        var boxed = await Create(board.Id, new { markdown = "Few words", x = 0, y = 0, wrapWidth = 300, minHeight = 200 });
        Assert.Equal((300, 200, 200), (boxed.Width, boxed.Height, boxed.MinHeight!.Value));

        var longer = await Update(boxed.Id, new { markdown = string.Join("\n\n", Enumerable.Repeat("A line", 40)) });
        Assert.Equal(200, longer.MinHeight);
        Assert.True(longer.Height > 200, $"Height {longer.Height}");

        var cleared = await Update(boxed.Id, new { markdown = "Few words", minHeight = 0 });
        Assert.Null(cleared.MinHeight);
        Assert.True(cleared.Height < 200, $"Height {cleared.Height}");

        await Update(boxed.Id, new { minHeight = 160 });
        var clip = await CopyAsync(board.Id, new { labelIds = new[] { boxed.Id } });
        Assert.Contains("\"minHeight\":160", clip);
        var pasted = await _client.PostAsync($"/api/boards/{board.Id}/paste", Json($$"""{"clip":{{clip}},"x":1000,"y":1000}"""));
        Assert.Equal(HttpStatusCode.OK, pasted.StatusCode);
        Assert.Equal(160, (await pasted.Content.ReadFromJsonAsync<PastedItems>())!.Labels!.Single().MinHeight);
    }

    [Fact]
    public async Task Card_search_finds_the_text_of_labels_on_cards_also_in_the_trash()
    {
        var board = await _client.CreateBoardAsync("Finding");
        var card = await _client.CreateCardAsync(board.Id, "Plain title");
        await _client.CreateCardAsync(board.Id, "Another");
        await Create(board.Id, new { cardId = card.Id, markdown = "the **needle** here" });
        await Create(board.Id, new { x = 0, y = 0, markdown = "needle on the board" });

        var found = (await _client.GetFromJsonAsync<List<CardDetail>>($"/api/boards/{board.Id}/cards?search=needle"))!;
        Assert.Equal(card.Id, Assert.Single(found).Id);

        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        var trash = (await _client.GetFromJsonAsync<TrashContents>($"/api/boards/{board.Id}/trash?search=needle"))!;
        Assert.Equal(card.Id, Assert.Single(trash.Cards).Id);
    }

    [Fact]
    public async Task A_change_of_text_on_a_card_is_in_its_history()
    {
        var board = await _client.CreateBoardAsync("Told");
        var card = await _client.CreateCardAsync(board.Id, "With a label");
        var onCard = await Create(board.Id, new { cardId = card.Id, markdown = "first" });
        await Update(onCard.Id, new { markdown = "second" });
        await Update(onCard.Id, new { markdown = "third" });
        await Update(onCard.Id, new { x = 200, y = 200 });

        var history = (await _client.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{card.Id}/history"))!;
        Assert.Equal(1, history.SelectMany(h => h.Entries).Count(e => e.Kind == CardChangeKind.LabelChanged));
    }

    private async Task<LabelDetail> Update(Guid labelId, object request)
    {
        var response = await _client.PatchAsJsonAsync($"/api/labels/{labelId}", request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return System.Text.Json.JsonSerializer.Deserialize<LabelDetail>(body, System.Text.Json.JsonSerializerOptions.Web)!;
    }

    private async Task<BoardDetail> Board(Guid boardId) => (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{boardId}"))!;

    private async Task<LabelDetail> Create(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/labels", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LabelDetail>())!;
    }

    private async Task<LabelDetail> Patch(Guid labelId, object request)
    {
        var response = await _client.PatchAsJsonAsync($"/api/labels/{labelId}", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LabelDetail>())!;
    }

    private async Task<string> CopyAsync(Guid boardId, object what)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/copy", what);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsStringAsync();
    }

    private async Task AssertInvalid(string url, object request, string field)
    {
        var response = await _client.PostAsJsonAsync(url, request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains($"\"{field}\"", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertPatchInvalid(Guid labelId, object request, string field)
    {
        var response = await _client.PatchAsJsonAsync($"/api/labels/{labelId}", request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains($"\"{field}\"", body, StringComparison.OrdinalIgnoreCase);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
