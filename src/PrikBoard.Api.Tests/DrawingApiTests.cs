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
        Assert.Equal((InkColor.Sky, 4, 3), (drawing.Color, drawing.StrokeWidth, drawing.PointCount));
        Assert.Null(drawing.NoteId);
        var me = await _client.MeAsync();
        Assert.Equal((me.DisplayName, me.Id), (drawing.AddedBy, drawing.AddedById));

        var response = await _client.GetAsync(drawing.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        var content = (await response.Content.ReadFromJsonAsync<DrawingContentDetail>())!;
        Assert.Equal(drawing.Id, content.DrawingId);
        // Points are free-form, kept to a tenth of a pixel and a hundredth of pressure.
        Assert.Equal([[2, 2, 0.5], [40, 10.1, 0.12], [100.2, 30, 1]], content.Points);

        var snapshot = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(drawing.Id, Assert.Single(snapshot.Drawings).Id);
    }

    [Fact]
    public async Task Colour_and_stroke_width_have_defaults_and_the_margin_follows_the_width()
    {
        var board = await _client.CreateBoardAsync("Defaults");

        var plain = await Create(board.Id, new { x = 0, y = 0, points = new[] { new[] { 10.0, 10.0, 0.5 } } });
        var bold = await Create(board.Id, new { x = 0, y = 0, points = new[] { new[] { 10.0, 10.0, 0.5 } }, strokeWidth = 8, color = "Brick" });

        Assert.Equal((InkColor.Chalk, 4, 12, 12), (plain.Color, plain.StrokeWidth, plain.Width, plain.Height));
        Assert.Equal((InkColor.Brick, 8, 14, 14), (bold.Color, bold.StrokeWidth, bold.Width, bold.Height));
    }

    [Fact]
    public async Task Bad_strokes_are_rejected_with_the_field_that_is_wrong()
    {
        var board = await _client.CreateBoardAsync("Rejects");
        var other = await _client.CreateBoardAsync("Other");
        var foreign = await _client.CreateNoteAsync(other.Id, "Elsewhere");
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
        await AssertInvalid(url, new { x = 0, y = 0, points = new[] { ok }, noteId = foreign.Id }, "noteId");

        // Numbers JSON can't hold as a finite double, and an ink that doesn't exist.
        var huge = await _client.PostAsync(url, Json("""{"x":0,"y":0,"points":[[1e400,1,0.5]]}"""));
        var unknownInk = await _client.PostAsync(url, Json("""{"x":0,"y":0,"points":[[1,1,0.5]],"color":"Glitter"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownInk.StatusCode);

        var snapshot = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Empty(snapshot.Drawings);
    }

    [Fact]
    public async Task Drawings_move_recolour_and_go_between_the_board_and_notes()
    {
        var board = await _client.CreateBoardAsync("Edits");
        var other = await _client.CreateBoardAsync("Other");
        var note = await _client.CreateNoteAsync(board.Id, "Target");
        var foreign = await _client.CreateNoteAsync(other.Id, "Foreign");
        var drawing = await Create(board.Id, new { x = 0, y = 0, points = Squiggle });

        var moved = await Patch(drawing.Id, new { x = 1201, y = 99, color = "Mint" });
        Assert.Equal((1200, 100, InkColor.Mint), (moved.X, moved.Y, moved.Color));
        var onlyY = await Patch(drawing.Id, new { y = 41 });
        Assert.Equal((1200, 40), (onlyY.X, onlyY.Y));

        var attached = await Patch(drawing.Id, new { noteId = note.Id, x = 61, y = 39, color = "Pine" });
        Assert.Equal((note.Id, 60, 40, InkColor.Pine), (attached.NoteId, attached.X, attached.Y, attached.Color));
        // On a note's canvas it stays on the canvas, at the last grid line where it fits (10000 - 103 → 9880).
        var pushed = await Patch(drawing.Id, new { x = 20_000, y = 0 });
        Assert.Equal((9880, 0), (pushed.X, pushed.Y));

        var detached = await Patch(drawing.Id, new { detach = true, x = 1001, y = 2001 });
        Assert.Equal((null, 1000, 2000), (detached.NoteId, detached.X, detached.Y));

        var both = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { noteId = note.Id, detach = true });
        var elsewhere = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { noteId = foreign.Id, x = 0, y = 0 });
        var noPlace = await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { noteId = note.Id, x = 40 });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noPlace.StatusCode);

        // The points never change.
        var content = (await _client.GetFromJsonAsync<DrawingContentDetail>(drawing.ContentUrl))!;
        Assert.Equal(3, content.Points.Count);
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
        Assert.Equal((drawing.Id, 40, 40, InkColor.Coral), (back.Id, back.X, back.Y, back.Color));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(drawing.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task Drawings_on_a_note_go_and_come_back_with_the_note()
    {
        var board = await _client.CreateBoardAsync("Cascade");
        var note = await _client.CreateNoteAsync(board.Id, "Explained with a sketch");
        var onNote = await Create(board.Id, new { x = 505, y = 611, points = Squiggle, noteId = note.Id, color = "Ink" });
        var removedEarlier = await Create(board.Id, new { x = 0, y = 0, points = Squiggle, noteId = note.Id });
        var onBoard = await Create(board.Id, new { x = 0, y = 0, points = Squiggle });
        Assert.Equal((note.Id, 500, 620), (onNote.NoteId, onNote.X, onNote.Y));
        (await _client.DeleteAsync($"/api/drawings/{removedEarlier.Id}")).EnsureSuccessStatusCode();

        (await _client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();
        var hidden = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(onBoard.Id, Assert.Single(hidden.Drawings).Id);
        var restoreAlone = await _client.PostAsync($"/api/drawings/{onNote.Id}/restore", null);
        Assert.Equal(HttpStatusCode.Conflict, restoreAlone.StatusCode);

        (await _client.PostAsync($"/api/notes/{note.Id}/restore", null)).EnsureSuccessStatusCode();
        var back = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        // The one deleted on its own earlier stays deleted.
        Assert.Equal([onNote.Id, onBoard.Id], back.Drawings.Select(d => d.Id).Order());
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(drawing.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task Drawings_travel_with_an_exported_board()
    {
        var board = await _client.CreateBoardAsync("Sketches");
        var note = await _client.CreateNoteAsync(board.Id, "With a doodle");
        await Create(board.Id, new { x = 300, y = 400, points = Squiggle, color = "Lilac", strokeWidth = 2 });
        await Create(board.Id, new { x = 20, y = 40, points = Squiggle, color = "Violet", noteId = note.Id });

        var file = await _client.GetStringAsync($"/api/boards/{board.Id}/export");
        var imported = await _client.PostAsync("/api/boards/import", Json(file));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal(2, copy.Drawings.Count);
        var loose = copy.Drawings.Single(d => d.NoteId is null);
        var attached = copy.Drawings.Single(d => d.NoteId is not null);
        Assert.Equal((300, 400, InkColor.Lilac, 2), (loose.X, loose.Y, loose.Color, loose.StrokeWidth));
        Assert.Equal((Assert.Single(copy.Notes).Id, 20, 40, InkColor.Violet), (attached.NoteId, attached.X, attached.Y, attached.Color));
        var points = (await _client.GetFromJsonAsync<DrawingContentDetail>(loose.ContentUrl))!.Points;
        Assert.Equal([[2, 2, 0.5], [40, 10.1, 0.12], [100.2, 30, 1]], points);

        // A drawing pointing at a note that isn't in the file, or with bad points, refuses the whole import.
        var badNote = file.Replace($"\"noteId\":\"{note.Id}\"", $"\"noteId\":\"{Guid.NewGuid()}\"");
        Assert.NotEqual(file, badNote);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/boards/import", Json(badNote))).StatusCode);
        var badPoints = file.Replace("[100.2,30,1]", "[100.2,30,7]");
        Assert.NotEqual(file, badPoints);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/boards/import", Json(badPoints))).StatusCode);
    }

    [Fact]
    public async Task A_drawing_joins_the_bucket_its_centre_is_in()
    {
        var board = await _client.CreateBoardAsync("Drawings in buckets");
        var todo = board.Buckets[0];
        var note = (await (await _client.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new { title = "Canvas" })).Content.ReadFromJsonAsync<NoteDetail>())!;

        // The squiggle is 103 by 32, so its centre is 51 right of and 16 below its corner.
        var inside = await Create(board.Id, new { x = todo.X + 20, y = todo.Y + 100, points = Squiggle });
        var outside = await Create(board.Id, new { x = 3000, y = 100, points = Squiggle });
        var onNote = await Create(board.Id, new { noteId = note.Id, x = 20, y = 20, points = Squiggle });
        Assert.Equal(todo.Id, inside.BucketId);
        Assert.Null(outside.BucketId);
        Assert.Null(onNote.BucketId);

        // Moving it out leaves the bucket; a new colour alone keeps it where it is.
        Assert.Null((await Patch(inside.Id, new { x = 3000 })).BucketId);
        Assert.Equal(todo.Id, (await Patch(outside.Id, new { x = todo.X + 20, y = todo.Y + 200 })).BucketId);
        Assert.Equal(todo.Id, (await Patch(outside.Id, new { color = "Coral" })).BucketId);
        // Put on the board from a note, it joins the bucket it lands in.
        Assert.Equal(todo.Id, (await Patch(onNote.Id, new { detach = true, x = todo.X + 20, y = todo.Y + 300 })).BucketId);
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
