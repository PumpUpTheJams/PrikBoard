using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class BoardApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task New_board_starts_with_default_columns_and_is_listed()
    {
        var board = await CreateBoard("Launch");

        Assert.Equal(["To do", "Doing", "Done"], board.Columns.Select(c => c.Name));
        Assert.Equal([0, 1, 2], board.Columns.Select(c => c.Position));

        var boards = await _client.GetFromJsonAsync<List<BoardSummary>>("/api/boards");
        Assert.Contains(boards!, b => b.Id == board.Id && b.Name == "Launch");
    }

    [Fact]
    public async Task Board_name_is_required()
    {
        var response = await _client.PostAsJsonAsync("/api/boards", new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Note_requests_are_validated()
    {
        var board = await CreateBoard("Validation");

        var tooLong = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new { title = new string('x', 201) });
        var negative = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new { title = "Ok", position = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task Notes_default_to_first_column_and_can_move_between_columns()
    {
        var board = await CreateBoard("Moves");
        var (todo, done) = (board.Columns[0], board.Columns[2]);

        var first = await CreateNote(board.Id, new { title = "First" });
        var second = await CreateNote(board.Id, new { title = "Second", color = "Pink" });
        Assert.Equal(todo.Id, first.ColumnId);
        Assert.Equal(NoteColor.Yellow, first.Color);
        Assert.Equal(NoteColor.Pink, second.Color);
        Assert.Equal(1, second.Position);

        var moveResponse = await _client.PostAsJsonAsync($"/api/notes/{first.Id}/move", new { columnId = done.Id });
        moveResponse.EnsureSuccessStatusCode();

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var todoNotes = snapshot!.Columns[0].Notes;
        var doneNotes = snapshot.Columns[2].Notes;
        Assert.Equal("Second", Assert.Single(todoNotes).Title);
        Assert.Equal(0, todoNotes[0].Position);
        Assert.Equal("First", Assert.Single(doneNotes).Title);
    }

    [Theory]
    [InlineData("C", 0, "C,A,B")]
    [InlineData("A", 1, "B,A,C")]
    [InlineData("A", null, "B,C,A")]
    public async Task Notes_can_be_reordered_within_a_column(string title, int? position, string expectedOrder)
    {
        var board = await CreateBoard("Reorder");
        var notes = new Dictionary<string, NoteDetail>();
        foreach (var t in new[] { "A", "B", "C" }) notes[t] = await CreateNote(board.Id, new { title = t });

        var response = await _client.PostAsJsonAsync(
            $"/api/notes/{notes[title].Id}/move", new { columnId = board.Columns[0].Id, position });
        response.EnsureSuccessStatusCode();

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var column = snapshot!.Columns[0].Notes;
        Assert.Equal(expectedOrder, string.Join(",", column.Select(n => n.Title)));
        Assert.Equal([0, 1, 2], column.Select(n => n.Position));
    }

    [Fact]
    public async Task Columns_can_be_inserted_and_reordered()
    {
        var board = await CreateBoard("Columns");

        var created = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/columns", new { name = "Review", position = 2 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var moved = await _client.PatchAsJsonAsync($"/api/columns/{board.Columns[2].Id}", new { position = 0 });
        moved.EnsureSuccessStatusCode();

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal(["Done", "To do", "Doing", "Review"], snapshot!.Columns.Select(c => c.Name));
        Assert.Equal([0, 1, 2, 3], snapshot.Columns.Select(c => c.Position));
    }

    [Fact]
    public async Task Moving_to_a_column_on_another_board_is_rejected()
    {
        var board = await CreateBoard("Source");
        var other = await CreateBoard("Other");
        var note = await CreateNote(board.Id, new { title = "Stay here" });

        var response = await _client.PostAsJsonAsync($"/api/notes/{note.Id}/move", new { columnId = other.Columns[0].Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_finds_notes_by_title_or_body_case_insensitively()
    {
        var board = await CreateBoard("Search");
        await CreateNote(board.Id, new { title = "Design LOGO" });
        await CreateNote(board.Id, new { title = "Write copy", body = "Mention the logo" });
        await CreateNote(board.Id, new { title = "Book venue" });

        var hits = await _client.GetFromJsonAsync<List<NoteDetail>>($"/api/boards/{board.Id}/notes?search=logo");

        Assert.Equal(["Design LOGO", "Write copy"], hits!.Select(n => n.Title));
    }

    [Fact]
    public async Task Column_with_notes_cannot_be_deleted()
    {
        var board = await CreateBoard("Guarded");
        await CreateNote(board.Id, new { title = "Blocker" });

        var response = await _client.DeleteAsync($"/api/columns/{board.Columns[0].Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_board_removes_its_notes()
    {
        var board = await CreateBoard("Temporary");
        var note = await CreateNote(board.Id, new { title = "Gone soon" });

        (await _client.DeleteAsync($"/api/boards/{board.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_exposes_named_operations_for_ai_clients()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));

        var operationIds = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(op => op.Value.TryGetProperty("operationId", out _))
            .Select(op => op.Value.GetProperty("operationId").GetString())
            .ToHashSet();

        Assert.Superset(
            new HashSet<string?> { "listBoards", "getBoard", "createNote", "moveNote", "updateNote", "deleteNote" },
            operationIds);
    }

    private async Task<BoardDetail> CreateBoard(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/boards", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BoardDetail>())!;
    }

    private async Task<NoteDetail> CreateNote(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/notes", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
    }
}
