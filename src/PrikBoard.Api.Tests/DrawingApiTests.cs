using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class DrawingApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly double[][] Squiggle = [[2, 2, 0.5], [40.04, 10.06, 0.123], [100.2, 30, 1]];

    [Fact]
    public async Task A_stroke_snaps_its_corner_keeps_its_points_and_can_be_fetched()
    {
        var board = await _client.CreateBoardAsync("Drawings");

        var drawing = await Create(board.Id, new { x = 131, y = 289, points = Squiggle, color = "Sky", strokeWidth = 4 });

        Assert.Equal((140, 280), (drawing.X, drawing.Y));
        // The box holds the furthest point (rounded up) plus half the stroke width.
        Assert.Equal((103, 32), (drawing.Width, drawing.Height));
        Assert.Equal((InkColor.Sky, 3), (Assert.Single(drawing.Colors), drawing.PointCount));
        Assert.Null(drawing.CardId);
        var me = await _client.MeAsync();
        Assert.Equal((me.DisplayName, me.Id), (drawing.AddedBy, drawing.AddedById));

        var response = await _client.GetAsync(drawing.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        var content = (await response.Content.ReadFromJsonAsync<DrawingContentDetail>())!;
        Assert.Equal(drawing.Id, content.DrawingId);
        // Points are free-form, kept to a tenth of a pixel and a hundredth of pressure.
        var stroke = Assert.Single(content.Strokes);
        Assert.Equal(4, stroke.StrokeWidth);
        Assert.Equal([[2, 2, 0.5], [40, 10.1, 0.12], [100.2, 30, 1]], stroke.Points);

        var snapshot = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(drawing.Id, Assert.Single(snapshot.Drawings).Id);
    }

    [Fact]
    public async Task Color_and_stroke_width_have_defaults_and_the_margin_follows_the_width()
    {
        var board = await _client.CreateBoardAsync("Defaults");

        var plain = await Create(board.Id, new { x = 0, y = 0, points = new[] { new[] { 10.0, 10.0, 0.5 } } });
        var bold = await Create(board.Id, new { x = 0, y = 0, points = new[] { new[] { 10.0, 10.0, 0.5 } }, strokeWidth = 8, color = "Brick" });

        Assert.Equal((InkColor.Chalk, 4, 12, 12), (Assert.Single(plain.Colors), await StrokeWidth(plain), plain.Width, plain.Height));
        Assert.Equal((InkColor.Brick, 8, 14, 14), (Assert.Single(bold.Colors), await StrokeWidth(bold), bold.Width, bold.Height));
    }

    [Fact]
    public async Task Bad_strokes_are_rejected_with_the_field_that_is_wrong()
    {
        var board = await _client.CreateBoardAsync("Rejects");
        var other = await _client.CreateBoardAsync("Other");
        var foreign = await _client.CreateCardAsync(other.Id, "Elsewhere");
        var url = $"/api/boards/{board.Id}/drawings";
        double[] ok = [1, 1, 0.5];

        await AssertInvalid(url, new { x = 0, y = 0, points = Array.Empty<double[]>() }, "Points");
        await AssertInvalid(url, new { x = 0, y = 0, points = Enumerable.Repeat(ok, BoardDrawing.MaxPoints + 1) }, "Points");
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { ok, new[] { 1.0, 2.0 } } }, "points[1]");
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { new[] { -1.0, 2.0, 0.5 } } }, "points[0]");
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { new[] { 1.0, BoardDrawing.MaxSize + 1.0, 0.5 } } }, "points[0]");
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { ok, ok, new[] { 1.0, 2.0, 1.5 } } }, "points[2]");
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { ok }, strokeWidth = 3 }, "StrokeWidth");
        await AssertInvalid(url, new { y = 0, points = new[] { ok } }, "X");
        await AssertInvalid(url, new { x = Layout.MaxCoordinate + 1, y = 0, points = new[] { ok } }, "X");
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { ok }, cardId = foreign.Id }, "cardId");

        // Numbers JSON can't hold as a finite double, and an ink that doesn't exist.
        var huge = await _client.PostAsync(url, Json("""{"x":0,"y":0,"points":[[1e400,1,0.5]]}"""));
        var unknownInk = await _client.PostAsync(url, Json("""{"x":0,"y":0,"points":[[1,1,0.5]],"color":"Glitter"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownInk.StatusCode);

        var snapshot = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Empty(snapshot.Drawings);
    }

    [Fact]
    public async Task Drawings_move_recolor_and_go_between_the_board_and_cards()
    {
        var board = await _client.CreateBoardAsync("Edits");
        var other = await _client.CreateBoardAsync("Other");
        var card = await _client.CreateCardAsync(board.Id, "Target");
        var foreign = await _client.CreateCardAsync(other.Id, "Foreign");
        var drawing = await Create(board.Id, new { x = 0, y = 0, points = Squiggle });

        var moved = await Patch(drawing.Id, new { x = 1201, y = 99, color = "Mint" });
        Assert.Equal((1200, 100, InkColor.Mint), (moved.X, moved.Y, Assert.Single(moved.Colors)));
        var onlyY = await Patch(drawing.Id, new { y = 41 });
        Assert.Equal((1200, 40), (onlyY.X, onlyY.Y));

        var attached = await Patch(drawing.Id, new { cardId = card.Id, x = 61, y = 39, color = "Pine" });
        Assert.Equal((card.Id, 60, 40, InkColor.Pine), (attached.CardId, attached.X, attached.Y, Assert.Single(attached.Colors)));
        // On a card's canvas it stays on the canvas, at the last grid line where it fits (10000 - 103 → 9880).
        var pushed = await Patch(drawing.Id, new { x = 20_000, y = 0 });
        Assert.Equal((9880, 0), (pushed.X, pushed.Y));

        var detached = await Patch(drawing.Id, new { detach = true, x = 1001, y = 2001 });
        Assert.Equal((null, 1000, 2000), (detached.CardId, detached.X, detached.Y));

        var both = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { cardId = card.Id, detach = true });
        var elsewhere = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { cardId = foreign.Id, x = 0, y = 0 });
        var noPlace = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { cardId = card.Id, x = 40 });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noPlace.StatusCode);

        // Moving and recoloring leave the strokes as they are.
        Assert.Equal(drawing.ContentUrl, detached.ContentUrl);
        var content = (await _client.GetFromJsonAsync<DrawingContentDetail>(drawing.ContentUrl))!;
        Assert.Equal(3, Assert.Single(content.Strokes).Points.Count);
    }

    [Fact]
    public async Task A_deleted_drawing_is_hidden_and_comes_back_with_the_same_id()
    {
        var board = await _client.CreateBoardAsync("Delete");
        var drawing = await Create(board.Id, new { x = 40, y = 40, points = Squiggle, color = "Coral" });

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/drawings/{drawing.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/drawings/{drawing.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(drawing.ContentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/drawings/{drawing.Id}")).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Drawings);

        var restored = await _client.PostAsync($"/api/drawings/{drawing.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var back = (await restored.Content.ReadFromJsonAsync<DrawingDetail>())!;
        Assert.Equal((drawing.Id, 40, 40, InkColor.Coral), (back.Id, back.X, back.Y, Assert.Single(back.Colors)));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(drawing.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task Drawings_on_a_card_go_and_come_back_with_the_card()
    {
        var board = await _client.CreateBoardAsync("Cascade");
        var card = await _client.CreateCardAsync(board.Id, "Explained with a sketch");
        var onCard = await Create(board.Id, new { x = 505, y = 611, points = Squiggle, cardId = card.Id, color = "Ink" });
        var removedEarlier = await Create(board.Id, new { x = 0, y = 0, points = Squiggle, cardId = card.Id });
        var onBoard = await Create(board.Id, new { x = 0, y = 0, points = Squiggle });
        Assert.Equal((card.Id, 500, 620), (onCard.CardId, onCard.X, onCard.Y));
        (await _client.DeleteAsync($"/api/drawings/{removedEarlier.Id}")).EnsureSuccessStatusCode();

        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        var hidden = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(onBoard.Id, Assert.Single(hidden.Drawings).Id);
        var restoreAlone = await _client.PostAsync($"/api/drawings/{onCard.Id}/restore", null);
        Assert.Equal(HttpStatusCode.Conflict, restoreAlone.StatusCode);

        (await _client.PostAsync($"/api/cards/{card.Id}/restore", null)).EnsureSuccessStatusCode();
        var back = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        // The one deleted on its own earlier stays deleted.
        Assert.Equal([onCard.Id, onBoard.Id], back.Drawings.Select(d => d.Id).Order());
    }

    [Fact]
    public async Task Old_deleted_drawings_are_purged_with_their_points()
    {
        var board = await _client.CreateBoardAsync("Purge");
        var drawing = await Create(board.Id, new { x = 0, y = 0, points = Squiggle });
        (await _client.DeleteAsync($"/api/drawings/{drawing.Id}")).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var longAgo = DateTimeOffset.UtcNow - Tombstones.Retention - TimeSpan.FromDays(1);
        await db.Drawings.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).Where(d => d.Id == drawing.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, longAgo));

        Assert.True(await Tombstones.PurgeAsync(db, DateTimeOffset.UtcNow - Tombstones.Retention, CancellationToken.None) >= 1);
        Assert.False(await db.DrawingContents.AnyAsync(c => c.DrawingId == drawing.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/drawings/{drawing.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Viewers_can_look_editors_can_draw_and_outsiders_see_nothing()
    {
        var board = await _client.CreateBoardAsync("Roles");
        var drawing = await Create(board.Id, new { x = 0, y = 0, points = Squiggle });
        var viewer = await factory.CreateSignedInClientAsync("drawing-viewer@example.com");
        var editor = await factory.CreateSignedInClientAsync("drawing-editor@example.com");
        var outsider = await factory.CreateSignedInClientAsync("drawing-outsider@example.com");
        await _client.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        await _client.ShareAsync(board.Id, editor, BoardRole.Editor);
        var stroke = new { x = 0, y = 0, points = Squiggle };

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(drawing.ContentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/boards/{board.Id}/drawings", stroke)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { x = 40 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/drawings/{drawing.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync($"/api/boards/{board.Id}/drawings", stroke)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { x = 40 })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(drawing.ContentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/boards/{board.Id}/drawings", stroke)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/api/drawings/{drawing.Id}")).StatusCode);

        var anonymous = factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(drawing.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task Drawings_travel_with_an_exported_board()
    {
        var board = await _client.CreateBoardAsync("Sketches");
        var card = await _client.CreateCardAsync(board.Id, "With a doodle");
        await Create(board.Id, new { x = 300, y = 400, points = Squiggle, color = "Lilac", strokeWidth = 2 });
        await Create(board.Id, new { x = 20, y = 40, points = Squiggle, color = "Violet", cardId = card.Id });

        var file = await _client.GetStringAsync($"/api/boards/{board.Id}/export");
        var imported = await _client.PostAsync("/api/boards/import", Json(file));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal(2, copy.Drawings.Count);
        var loose = copy.Drawings.Single(d => d.CardId is null);
        var attached = copy.Drawings.Single(d => d.CardId is not null);
        Assert.Equal((300, 400, InkColor.Lilac, 2), (loose.X, loose.Y, Assert.Single(loose.Colors), await StrokeWidth(loose)));
        Assert.Equal((Assert.Single(copy.Cards).Id, 20, 40, InkColor.Violet), (attached.CardId, attached.X, attached.Y, Assert.Single(attached.Colors)));
        var points = Assert.Single((await _client.GetFromJsonAsync<DrawingContentDetail>(loose.ContentUrl))!.Strokes).Points;
        Assert.Equal([[2, 2, 0.5], [40, 10.1, 0.12], [100.2, 30, 1]], points);

        // A drawing pointing at a card that isn't in the file, or with bad points, refuses the whole import.
        var badCard = file.Replace($"\"cardId\":\"{card.Id}\"", $"\"cardId\":\"{Guid.NewGuid()}\"");
        Assert.NotEqual(file, badCard);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/boards/import", Json(badCard))).StatusCode);
        var badPoints = file.Replace("[100.2,30,1]", "[100.2,30,7]");
        Assert.NotEqual(file, badPoints);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/boards/import", Json(badPoints))).StatusCode);
    }

    [Fact]
    public async Task A_drawing_joins_the_bucket_its_centre_is_in()
    {
        var board = await _client.CreateBoardAsync("Drawings in buckets");
        var todo = board.Buckets[0];
        var card = (await (await _client.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Canvas" })).Content.ReadFromJsonAsync<CardDetail>())!;

        // The squiggle is 103 by 32, so its centre is 51 right of and 16 below its corner.
        var inside = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, points = Squiggle });
        var outside = await Create(board.Id, new { x = 3000, y = 100, points = Squiggle });
        var onCard = await Create(board.Id, new { cardId = card.Id, x = 20, y = 20, points = Squiggle });
        Assert.Equal(todo.Id, inside.BucketId);
        Assert.Null(outside.BucketId);
        Assert.Null(onCard.BucketId);

        // Moving it out leaves the bucket; a new color alone keeps it where it is.
        Assert.Null((await Patch(inside.Id, new { x = 3000 })).BucketId);
        Assert.Equal(todo.Id, (await Patch(outside.Id, new { x = todo.X + 20, y = todo.Y + 200 })).BucketId);
        Assert.Equal(todo.Id, (await Patch(outside.Id, new { color = "Coral" })).BucketId);
        // Put on the board from a card, it joins the bucket it lands in.
        Assert.Equal(todo.Id, (await Patch(onCard.Id, new { detach = true, x = todo.X + 20, y = todo.Y + 300 })).BucketId);
    }

    [Fact]
    public async Task Moving_a_bucket_carries_its_drawings_along()
    {
        var board = await _client.CreateBoardAsync("Docked drawings");
        var todo = board.Buckets[0];
        var inside = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, points = Squiggle });
        var outside = await Create(board.Id, new { x = 3000, y = 100, points = Squiggle });

        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = todo.X, y = todo.Y + 1000 })).EnsureSuccessStatusCode();
        var drawings = await Drawings(board.Id);
        Assert.Equal((todo.X + 20, todo.Y + 1100, todo.Id), Spot(drawings, inside.Id));
        Assert.Equal((3000, 100, (Guid?)null), Spot(drawings, outside.Id));

        // Undo moves it back with exactly the drawings that came along: here none, so the drawing is left behind, loose.
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = todo.X, y = todo.Y, carryDrawingIds = Array.Empty<Guid>() })).EnsureSuccessStatusCode();
        Assert.Equal((todo.X + 20, todo.Y + 1100, (Guid?)null), Spot(await Drawings(board.Id), inside.Id));

        var resizeWithCarry = await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = todo.X, width = 400, carryDrawingIds = new[] { inside.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, resizeWithCarry.StatusCode);
        Assert.Contains("carryDrawingIds", await resizeWithCarry.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Moving_a_bucket_onto_another_keeps_both_their_drawings()
    {
        var board = await _client.CreateBoardAsync("Overlapping drawings");
        var (todo, doing) = (board.Buckets[0], board.Buckets[1]);
        var todos = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 20, points = Squiggle });
        var doings = await Create(board.Id, new { x = doing.X + 20, y = doing.Y + 20, points = Squiggle });

        // Doing (on top) lands on To do, then To do moves away from under it: each takes only its own drawing.
        (await _client.PatchAsJsonAsync($"/api/buckets/{doing.Id}", new { x = todo.X, y = todo.Y })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = 1200, y = todo.Y })).EnsureSuccessStatusCode();

        var drawings = await Drawings(board.Id);
        Assert.Equal((1220, todo.Y + 20, todo.Id), Spot(drawings, todos.Id));
        Assert.Equal((todo.X + 20, todo.Y + 20, doing.Id), Spot(drawings, doings.Id));
    }

    [Fact]
    public async Task Shrinking_or_deleting_a_bucket_lets_its_drawings_go_and_restoring_takes_them_back()
    {
        var board = await _client.CreateBoardAsync("Released drawings");
        var todo = board.Buckets[0];
        var top = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 20, points = Squiggle });
        var low = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 400, points = Squiggle });

        // Shrunk to its smallest, it keeps the drawing still inside and lets the low one go.
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { height = Layout.MinBucketHeight })).EnsureSuccessStatusCode();
        var drawings = await Drawings(board.Id);
        Assert.Equal(todo.Id, drawings.Single(d => d.Id == top.Id).BucketId);
        Assert.Null(drawings.Single(d => d.Id == low.Id).BucketId);

        // Grown back, it takes the loose drawing it now covers again.
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { height = 600 })).EnsureSuccessStatusCode();
        Assert.Equal(todo.Id, (await Drawings(board.Id)).Single(d => d.Id == low.Id).BucketId);

        // Deleted, it lets all of them go, and restored, it takes back the ones it covers; the drawings never move.
        (await _client.DeleteAsync($"/api/buckets/{todo.Id}")).EnsureSuccessStatusCode();
        Assert.All(await Drawings(board.Id), d => Assert.Null(d.BucketId));
        (await _client.PostAsync($"/api/buckets/{todo.Id}/restore", null)).EnsureSuccessStatusCode();
        drawings = await Drawings(board.Id);
        Assert.All(drawings, d => Assert.Equal(todo.Id, d.BucketId));
        Assert.Equal((todo.X + 20, todo.Y + 400, todo.Id), Spot(drawings, low.Id));
    }

    [Fact]
    public async Task A_restored_drawing_and_an_imported_one_find_their_bucket()
    {
        var board = await _client.CreateBoardAsync("Restored drawing");
        var todo = board.Buckets[0];
        var drawing = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, points = Squiggle });

        // Deleted, then its bucket moves away: it comes back where it was, now loose.
        (await _client.DeleteAsync($"/api/drawings/{drawing.Id}")).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = 3000, y = todo.Y })).EnsureSuccessStatusCode();
        var restored = await _client.PostAsync($"/api/drawings/{drawing.Id}/restore", null);
        Assert.Null((await restored.Content.ReadFromJsonAsync<DrawingDetail>())!.BucketId);

        // An imported board places its drawings by position too.
        (await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { x = 3020, y = todo.Y + 100 })).EnsureSuccessStatusCode();
        var file = await _client.GetStringAsync($"/api/boards/{board.Id}/export");
        var imported = await _client.PostAsync("/api/boards/import", Json(file));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;
        var copyTodo = copy.Buckets.Single(b => b.Name == todo.Name);
        Assert.Equal(copyTodo.Id, Assert.Single(copy.Drawings).BucketId);
    }

    /// <summary>A stroke the way the web app sends one: on the grid, half a line at full pressure clear of its points.</summary>
    private static readonly double[][] Neat = [[10, 10, 0.5], [50, 30, 0.5], [90, 10, 0.5]];

    [Fact]
    public async Task Strokes_added_to_a_drawing_make_one_drawing_that_grows_to_hold_them()
    {
        var board = await _client.CreateBoardAsync("Sketch");
        var drawing = await Create(board.Id, new { x = 100, y = 100, points = Neat, color = "Sky" });

        // A second stroke starting above and left of it moves the corner there and shifts the first stroke's points.
        var grown = await AddStroke(drawing.Id, new { x = 41, y = 199, points = Neat, color = "Brick", strokeWidth = 8 });
        Assert.Equal((drawing.Id, 40, 100), (grown.Id, grown.X, grown.Y));
        Assert.Equal([InkColor.Sky, InkColor.Brick], grown.Colors);
        Assert.Equal((6, 152, 134), (grown.PointCount, grown.Width, grown.Height));
        Assert.NotEqual(drawing.ContentUrl, grown.ContentUrl);

        var content = (await _client.GetFromJsonAsync<DrawingContentDetail>(grown.ContentUrl))!;
        Assert.Equal([4, 8], content.Strokes.Select(s => s.StrokeWidth));
        Assert.Equal([[70, 10, 0.5], [110, 30, 0.5], [150, 10, 0.5]], content.Strokes[0].Points);
        Assert.Equal([[10, 110, 0.5], [50, 130, 0.5], [90, 110, 0.5]], content.Strokes[1].Points);

        // The new URL can be cached; the old one now answers with the new strokes, so it must not be.
        var fresh = await _client.GetAsync(grown.ContentUrl);
        Assert.Contains("immutable", fresh.Headers.CacheControl!.ToString());
        var stale = await _client.GetAsync(drawing.ContentUrl);
        Assert.Contains("no-cache", stale.Headers.CacheControl!.ToString());

        // It stays one item on the board, and moves and recolors as one.
        Assert.Equal(grown.Id, Assert.Single(await Drawings(board.Id)).Id);
        var mint = await Patch(drawing.Id, new { color = "Mint" });
        Assert.Equal([InkColor.Mint, InkColor.Mint], mint.Colors);
        var mixed = await Patch(drawing.Id, new { colors = new[] { "Coral", "Ink" } });
        Assert.Equal([InkColor.Coral, InkColor.Ink], mixed.Colors);
        var wrongCount = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { colors = new[] { "Coral" } });
        var both = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { color = "Sky", colors = new[] { "Coral", "Ink" } });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCount.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task Taking_off_the_stroke_just_added_gives_back_the_drawing_as_it_was()
    {
        var board = await _client.CreateBoardAsync("Undo a stroke");
        var drawing = await Create(board.Id, new { x = 100, y = 100, points = Neat, color = "Sky" });
        var grown = await AddStroke(drawing.Id, new { x = 0, y = 0, points = Neat, color = "Coral" });
        Assert.Equal((0, 0), (grown.X, grown.Y));

        var back = await _client.DeleteAsync($"/api/drawings/{drawing.Id}/strokes/1");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        var shrunk = (await back.Content.ReadFromJsonAsync<DrawingDetail>())!;
        Assert.Equal((drawing.X, drawing.Y, drawing.Width, drawing.Height), (shrunk.X, shrunk.Y, shrunk.Width, shrunk.Height));
        Assert.Equal([InkColor.Sky], shrunk.Colors);
        var content = (await _client.GetFromJsonAsync<DrawingContentDetail>(shrunk.ContentUrl))!;
        Assert.Equal(Neat, Assert.Single(content.Strokes).Points);

        // The first stroke can go too, as long as another is left.
        await AddStroke(drawing.Id, new { x = 300, y = 100, points = Neat, color = "Mint" });
        var second = (await (await _client.DeleteAsync($"/api/drawings/{drawing.Id}/strokes/0")).Content.ReadFromJsonAsync<DrawingDetail>())!;
        Assert.Equal((300, 100, InkColor.Mint), (second.X, second.Y, Assert.Single(second.Colors)));

        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/drawings/{drawing.Id}/strokes/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/drawings/{drawing.Id}/strokes/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/drawings/{drawing.Id}/strokes/-1")).StatusCode);
    }

    [Fact]
    public async Task A_stroke_is_checked_like_a_new_drawing_and_a_drawing_has_a_size_limit()
    {
        var board = await _client.CreateBoardAsync("Stroke checks");
        var card = await _client.CreateCardAsync(board.Id, "Canvas");
        var drawing = await Create(board.Id, new { x = 0, y = 0, points = Neat });
        var url = $"/api/drawings/{drawing.Id}/strokes";

        await AssertInvalid(url, new { x = 0, y = 0, points = Array.Empty<double[]>() }, "Points");
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { new[] { 1.0, 2.0 } } }, "points[0]");
        await AssertInvalid(url, new { x = 0, y = 0, points = Neat, strokeWidth = 3 }, "StrokeWidth");
        await AssertInvalid(url, new { y = 0, points = Neat }, "X");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/drawings/{Guid.NewGuid()}/strokes", new { x = 0, y = 0, points = Neat })).StatusCode);

        // A stroke too far from the rest would make the drawing bigger than a card's canvas.
        var far = await _client.PostAsJsonAsync(url, new { x = BoardDrawing.MaxSize, y = 0, points = Neat });
        Assert.Equal(HttpStatusCode.Conflict, far.StatusCode);

        // On a card, the corner is on the card's canvas.
        var onCard = await Create(board.Id, new { x = 200, y = 200, points = Neat, cardId = card.Id });
        var grown = await AddStroke(onCard.Id, new { x = 20, y = 400, points = Neat });
        Assert.Equal((card.Id, 20, 200), (grown.CardId, grown.X, grown.Y));
    }

    [Fact]
    public async Task A_drawing_with_several_strokes_travels_with_an_exported_board_and_old_files_still_import()
    {
        var board = await _client.CreateBoardAsync("Multi");
        var drawing = await Create(board.Id, new { x = 100, y = 100, points = Neat, color = "Sky" });
        await AddStroke(drawing.Id, new { x = 200, y = 100, points = Neat, color = "Coral", strokeWidth = 8 });

        var file = await _client.GetStringAsync($"/api/boards/{board.Id}/export");
        var imported = await _client.PostAsync("/api/boards/import", Json(file));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = Assert.Single((await imported.Content.ReadFromJsonAsync<BoardDetail>())!.Drawings);
        Assert.Equal((100, 100, 6), (copy.X, copy.Y, copy.PointCount));
        Assert.Equal([InkColor.Sky, InkColor.Coral], copy.Colors);
        var content = (await _client.GetFromJsonAsync<DrawingContentDetail>(copy.ContentUrl))!;
        Assert.Equal([4, 8], content.Strokes.Select(s => s.StrokeWidth));

        // A version 4 or 5 file has one stroke per drawing, as color, strokeWidth and points.
        var old = $$"""
            {"format":"prikboard-board","version":4,"exportedAt":"2026-10-20T10:00:00Z","name":"Old","description":null,"rules":null,
             "buckets":[],"tags":[],"cards":[],"images":[],
             "drawings":[{"cardId":null,"x":40,"y":60,"color":"Lilac","strokeWidth":2,"points":[[10,10,0.5],[30,20,0.5]]}]}
            """;
        var fromOld = await _client.PostAsync("/api/boards/import", Json(old));
        Assert.Equal(HttpStatusCode.Created, fromOld.StatusCode);
        var oldDrawing = Assert.Single((await fromOld.Content.ReadFromJsonAsync<BoardDetail>())!.Drawings);
        Assert.Equal((40, 60, InkColor.Lilac, 2), (oldDrawing.X, oldDrawing.Y, Assert.Single(oldDrawing.Colors), await StrokeWidth(oldDrawing)));
    }

    private async Task<int> StrokeWidth(DrawingDetail drawing) =>
        Assert.Single((await _client.GetFromJsonAsync<DrawingContentDetail>(drawing.ContentUrl))!.Strokes).StrokeWidth;

    private async Task<DrawingDetail> AddStroke(Guid drawingId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/drawings/{drawingId}/strokes", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DrawingDetail>())!;
    }

    private async Task<IReadOnlyList<DrawingDetail>> Drawings(Guid boardId) =>
        (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{boardId}"))!.Drawings;

    private static (int X, int Y, Guid? BucketId) Spot(IEnumerable<DrawingDetail> drawings, Guid id)
    {
        var drawing = drawings.Single(d => d.Id == id);
        return (drawing.X, drawing.Y, drawing.BucketId);
    }

    private async Task AssertInvalid(string url, object request, string field)
    {
        var response = await _client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"{field}\"", body, StringComparison.OrdinalIgnoreCase);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<DrawingDetail> Create(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/drawings", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DrawingDetail>())!;
    }

    private async Task<DrawingDetail> Patch(Guid drawingId, object request)
    {
        var response = await _client.PatchAsJsonAsync($"/api/drawings/{drawingId}", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DrawingDetail>())!;
    }
}
