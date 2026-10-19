using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Exporting a board to a file and importing it as a new board.</summary>
public class BoardTransferApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task An_exported_board_imports_as_a_new_board_with_everything_on_it()
    {
        var owner = await factory.CreateSignedInClientAsync("export-owner@example.com");
        var board = await owner.CreateBoardAsync("Release 2.0");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { description = "Ship it", rules = "Be brief." })).EnsureSuccessStatusCode();
        var doing = board.Buckets.Single(b => b.Name == "Doing");

        var target = await owner.CreateNoteAsync(board.Id, "Target");
        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new
        {
            title = "Write the changelog",
            body = "Everything that changed.",
            color = "Mint",
            bucketId = doing.Id,
            tags = new[] { "Docs", "release" },
            checklist = new[] { new { text = "Draft", done = true }, new { text = "Review", done = false } },
            links = new[]
            {
                new { title = "Spec", url = "https://example.com/spec" },
                new { title = "The target", url = target.Path },
                new { title = "Elsewhere", url = $"/#/boards/{Guid.NewGuid()}/notes/{Guid.NewGuid()}" },
            },
        });
        response.EnsureSuccessStatusCode();
        var note = (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
        (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "unused" })).EnsureSuccessStatusCode();
        var png = TestImages.Png(400, 200);
        var onCanvas = await Upload(owner, board.Id, png, "?fileName=canvas.png&x=600&y=700&width=200&caption=Diagram");
        var attached = await Upload(owner, board.Id, TestImages.Png(100, 100), $"?fileName=attached.png&noteId={note.Id}");

        var exported = await owner.GetAsync($"/api/boards/{board.Id}/export");
        exported.EnsureSuccessStatusCode();
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Release 2.0.prikboard.json", exported.Content.Headers.ContentDisposition?.FileNameStar);
        var file = await exported.Content.ReadAsStringAsync();
        Assert.Contains("\"format\":\"prikboard-board\"", file);
        Assert.DoesNotContain(board.Id.ToString(), file);

        // Someone else imports it: a new board they own, and nothing changes on the original.
        var importer = await factory.CreateSignedInClientAsync("export-importer@example.com");
        var imported = await Import(importer, file);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;
        var me = await importer.MeAsync();

        Assert.NotEqual(board.Id, copy.Id);
        Assert.Equal(("Release 2.0", "Ship it", "Be brief.", BoardRole.Owner), (copy.Name, copy.Description, copy.Rules, copy.YourRole));
        Assert.Equal(board.Buckets.Select(b => (b.Name, b.Color, b.X, b.Y, b.Width, b.Height)),
            copy.Buckets.Select(b => (b.Name, b.Color, b.X, b.Y, b.Width, b.Height)));
        Assert.Equal(["docs", "release", "unused"], copy.Tags.Select(t => t.Name).Order());

        var copiedNote = copy.Notes.Single(n => n.Title == "Write the changelog");
        var copiedTarget = copy.Notes.Single(n => n.Title == "Target");
        Assert.Equal(copy.Buckets.Single(b => b.Name == "Doing").Id, copiedNote.BucketId);
        Assert.Equal(("Everything that changed.", NoteColor.Mint, new[] { "docs", "release" }),
            (copiedNote.Body, copiedNote.Color, copiedNote.Tags.ToArray()));
        Assert.Equal(("Draft", true), (copiedNote.Checklist[0].Text, copiedNote.Checklist[0].Done));
        Assert.Equal((note.X, note.Y), (copiedNote.X, copiedNote.Y));
        Assert.Equal([("Spec", "https://example.com/spec"), ("The target", copiedTarget.Path)],
            copiedNote.Links.Select(l => (l.Title, l.Url)));
        Assert.Equal((me.Id, me.DisplayName), (copiedNote.CreatedById, copiedNote.CreatedBy));
        Assert.Empty(copiedNote.Assignees);

        Assert.Equal(2, copy.Images.Count);
        var copiedCanvas = copy.Images.Single(i => i.NoteId is null);
        var copiedAttached = copy.Images.Single(i => i.NoteId is not null);
        Assert.Equal(copiedNote.Id, copiedAttached.NoteId);
        Assert.Equal(("canvas.png", "Diagram", onCanvas.X, onCanvas.Y, onCanvas.Width, onCanvas.Height),
            (copiedCanvas.FileName, copiedCanvas.Caption, copiedCanvas.X, copiedCanvas.Y, copiedCanvas.Width, copiedCanvas.Height));
        Assert.Equal(png, await (await importer.GetAsync(copiedCanvas.ContentUrl)).Content.ReadAsByteArrayAsync());
        Assert.Equal("attached.png", copiedAttached.FileName);
        Assert.Equal(attached.Size, copiedAttached.Size);

        var original = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(board.Id, original.Id);
        Assert.Equal(2, original.Notes.Count);
        var boards = (await importer.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!;
        Assert.Equal(copy.Id, Assert.Single(boards).Id);
    }

    [Fact]
    public async Task Importing_the_same_file_twice_makes_two_boards()
    {
        var client = await factory.CreateSignedInClientAsync("export-twice@example.com");
        var board = await client.CreateBoardAsync("Twice");
        await client.CreateNoteAsync(board.Id);
        var file = await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync();

        var first = (await (await Import(client, file)).Content.ReadFromJsonAsync<BoardDetail>())!;
        var second = (await (await Import(client, file)).Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Notes.Single().Id, second.Notes.Single().Id);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!.Count);
    }

    [Fact]
    public async Task Viewers_can_export_but_outsiders_cannot()
    {
        var owner = await factory.CreateSignedInClientAsync("export-share-owner@example.com");
        var viewer = await factory.CreateSignedInClientAsync("export-share-viewer@example.com");
        var outsider = await factory.CreateSignedInClientAsync("export-share-outsider@example.com");
        var board = await owner.CreateBoardAsync("Shared");
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/boards/{board.Id}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/boards/{board.Id}/export")).StatusCode);
    }

    [Fact]
    public async Task A_token_limited_to_chosen_boards_cannot_import()
    {
        var owner = await factory.CreateSignedInClientAsync("export-limited@example.com");
        var board = await owner.CreateBoardAsync("Limited");
        var file = await (await owner.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync();
        var (limited, _) = await factory.TokenAsync(owner, "only this board", BoardRole.Editor, [board.Id]);
        var (open, _) = await factory.TokenAsync(owner, "any board");

        Assert.Equal(HttpStatusCode.Forbidden, (await Import(limited, file)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Import(open, file)).StatusCode);
    }

    [Theory]
    [InlineData("format", "something-else", "format")]
    [InlineData("version", 99, "version")]
    [InlineData("name", "", "Name")]
    public async Task A_file_that_is_not_a_valid_export_is_refused(string property, object value, string errorKey)
    {
        var client = await factory.CreateSignedInClientAsync("export-invalid@example.com");
        var board = await client.CreateBoardAsync("Valid");
        var file = JsonNode.Parse(await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync())!;
        file[property] = JsonSerializer.SerializeToNode(value);

        var response = await Import(client, file.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains(problem["errors"]!.AsObject().Select(e => e.Key), key => key.Equals(errorKey, StringComparison.OrdinalIgnoreCase));
        Assert.Single(await client.GetFromJsonAsync<List<BoardSummary>>("/api/boards") ?? []);
    }

    [Fact]
    public async Task An_image_that_is_not_an_image_is_refused_and_nothing_is_created()
    {
        var client = await factory.CreateSignedInClientAsync("export-badimage@example.com");
        var board = await client.CreateBoardAsync("Bad image");
        await Upload(client, board.Id, TestImages.Png(100, 100), "");
        var file = JsonNode.Parse(await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync())!;
        file["images"]![0]!["data"] = Convert.ToBase64String("<svg onload=alert(1)/>"u8.ToArray());

        var response = await Import(client, file.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await client.GetFromJsonAsync<List<BoardSummary>>("/api/boards") ?? []);
    }

    private static Task<HttpResponseMessage> Import(HttpClient client, string file) =>
        client.PostAsync("/api/boards/import", new StringContent(file, System.Text.Encoding.UTF8, "application/json"));

    private static async Task<ImageDetail> Upload(HttpClient client, Guid boardId, byte[] bytes, string query)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/png");
        var response = await client.PostAsync($"/api/boards/{boardId}/images{query}", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }
}
