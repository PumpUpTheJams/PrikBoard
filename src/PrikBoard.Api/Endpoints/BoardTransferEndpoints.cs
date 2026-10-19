using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>Export a board to a file and import such a file as a new board.</summary>
public static class BoardTransferEndpoints
{
    /// <summary>Room for a board with a good number of images; each image is at most 10 MB and travels as base64.</summary>
    private const long MaxImportBytes = 64 * 1024 * 1024;

    public static RouteGroupBuilder MapBoardTransferEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Boards");

        boards.MapGet("/{boardId:guid}/export", ExportBoard)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("exportBoard")
            .WithSummary("Export a board to a file")
            .WithDescription("Returns the board's content as one self-contained JSON file (sent as a download): name, description, agent rules, " +
                             "buckets, tags, notes and images with their bytes as base64. Members, roles and user ids are not included. " +
                             "Pass the file to importBoard to make a new board from it.");

        boards.MapPost("/import", ImportBoard)
            .NoBoardScope()
            .WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes))
            .WithName("importBoard")
            .WithSummary("Import a board from an export file")
            .WithDescription("Send the file from exportBoard as the request body. It always creates a new board, which you own, and never changes " +
                             "an existing one. Ids are made new, positions are snapped to the grid, and each note's bucket follows from where it sits. " +
                             "Notes are created by you and have no assignees. Tokens limited to chosen boards cannot import.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<Results<Ok<BoardExport>, NotFound>> ExportBoard(
        Guid boardId, HttpResponse response, PrikBoardDbContext db, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking()
            .Include(b => b.Buckets)
            .Include(b => b.Notes)
            .Include(b => b.Images).ThenInclude(i => i.Content)
            .Include(b => b.Tags)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null) return TypedResults.NotFound();

        var export = new BoardExport(
            BoardExport.FormatName, BoardExport.CurrentVersion, DateTimeOffset.UtcNow,
            board.Name, board.Description, board.Rules,
            Layout.BackToFront(board.Buckets)
                .Select(b => new ExportedBucket(b.Name, b.Color, b.X, b.Y, b.Width, b.Height)).ToList(),
            board.Tags.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Select(t => t.Name).ToList(),
            // Oldest edit first: the importer gives them rising timestamps, so the most recently edited note stays on top.
            board.Notes.OrderBy(n => n.UpdatedAt).ThenBy(n => n.Id)
                .Select(n => new ExportedNote(n.Id, n.Title, n.Body, n.Color, n.Tags,
                    n.Checklist.Select(c => new ChecklistItem(c.Text, c.Done)).ToList(),
                    n.Links.Select(l => new NoteLink(l.Title, l.Url)).ToList(), n.X, n.Y)).ToList(),
            board.Images.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .Select(i => new ExportedImage(i.NoteId, i.FileName, i.Caption, i.X, i.Y, i.Width, i.Content!.Data)).ToList());

        var fileName = $"{CleanName(board.Name)}.prikboard.json";
        response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        return TypedResults.Ok(export);
    }

    private static async Task<Results<Created<BoardDetail>, ValidationProblem, ProblemHttpResult>> ImportBoard(
        BoardExport file, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var caller = BoardCaller.Of(principal);
        if (caller.Boards is not null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Limited access token",
                detail: "This access token is limited to chosen boards, so it cannot import new ones.");
        }

        var errors = new Dictionary<string, string[]>();
        if (file.Format != BoardExport.FormatName) errors["format"] = [$"This is not a PrikBoard export: format must be \"{BoardExport.FormatName}\"."];
        if (file.Version > BoardExport.CurrentVersion)
        {
            errors["version"] = [$"This file is version {file.Version}, but this server reads up to version {BoardExport.CurrentVersion}. Update PrikBoard to import it."];
        }
        if (file.Notes.Select(n => n.Id).Distinct().Count() != file.Notes.Count) errors["notes"] = ["Note ids must be unique within the file."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var board = new Board
        {
            Name = file.Name,
            Description = PatchText.Apply(null, file.Description),
            Rules = PatchText.Apply(null, file.Rules),
        };
        if (board.Rules is not null) board.RulesUpdatedAt = board.CreatedAt;

        // Distinct timestamps keep the stacking order of buckets and the drawing order of notes.
        var clock = board.CreatedAt;
        foreach (var (bucket, i) in file.Buckets.Select((b, i) => (b, i)))
        {
            board.Buckets.Add(new Bucket
            {
                Name = bucket.Name,
                Color = bucket.Color,
                X = Layout.Snap(bucket.X),
                Y = Layout.Snap(bucket.Y),
                Width = Math.Min(Layout.MaxBucketSize, Layout.SnapSize(bucket.Width, Layout.MinBucketWidth)),
                Height = Math.Min(Layout.MaxBucketSize, Layout.SnapSize(bucket.Height, Layout.MinBucketHeight)),
                CreatedAt = clock.AddMilliseconds(i),
            });
        }

        var tags = new List<string>();
        var newIds = file.Notes.ToDictionary(n => n.Id, _ => Guid.CreateVersion7());
        var createdBy = principal.PrincipalId();
        foreach (var (source, i) in file.Notes.Select((n, i) => (n, i)))
        {
            var noteTags = NoteTags.Normalize(source.Tags);
            if (noteTags.Any(t => t.Length > NoteTags.MaxLength))
            {
                return Invalid($"notes[{i}].tags", $"A tag is at most {NoteTags.MaxLength} characters.");
            }
            tags.AddRange(noteTags);

            // A link to another note in the file follows that note to its new id; web links stay; any other note path would dangle.
            var links = new List<LinkEntry>();
            foreach (var link in source.Links)
            {
                var url = link.Url.Trim();
                if (!NoteLinks.IsValid(url)) return Invalid($"notes[{i}].links", "Links must be absolute http(s) URLs or another note's path.");
                if (!NoteLinks.TryGetNoteId(url, out var target)) links.Add(new LinkEntry { Title = link.Title, Url = url });
                else if (newIds.TryGetValue(target, out var newTarget))
                {
                    links.Add(new LinkEntry { Title = link.Title, Url = NoteLinks.NotePath(board.Id, newTarget) });
                }
            }

            board.Notes.Add(new Note
            {
                Id = newIds[source.Id],
                Title = source.Title,
                Body = PatchText.Apply(null, source.Body),
                Color = source.Color,
                Tags = noteTags,
                Checklist = source.Checklist.Select(c => new ChecklistEntry { Text = c.Text, Done = c.Done }).ToList(),
                Links = links,
                X = Layout.Snap(source.X),
                Y = Layout.Snap(source.Y),
                CreatedById = createdBy,
                CreatedAt = clock.AddMilliseconds(i),
                UpdatedAt = clock.AddMilliseconds(i),
            });
        }

        // The tags the file lists (also unused ones), then any a note uses that the list missed.
        foreach (var (name, i) in NoteTags.Normalize(file.Tags).Concat(tags).Distinct().Select((t, i) => (t, i)))
        {
            if (name.Length > NoteTags.MaxLength) return Invalid("tags", $"A tag is at most {NoteTags.MaxLength} characters.");
            board.Tags.Add(new BoardTag { Name = name, CreatedAt = clock.AddMilliseconds(i) });
        }
        if (board.Tags.Count > BoardTag.MaxPerBoard) return Invalid("tags", $"A board has at most {BoardTag.MaxPerBoard} tags.");

        foreach (var (source, i) in file.Images.Select((m, i) => (m, i)))
        {
            if (source.Data.Length > ImageFormats.MaxBytes)
            {
                return Invalid($"images[{i}].data", $"Images can be at most {ImageFormats.MaxBytes / 1024 / 1024} MB.");
            }
            if (ImageFormats.Detect(source.Data) is not { } info)
            {
                return Invalid($"images[{i}].data", "The file is not a PNG, JPEG, GIF or WebP image (or it is larger than 20000 pixels on a side).");
            }
            Guid? noteId = null;
            if (source.NoteId is { } sourceNote)
            {
                if (!newIds.TryGetValue(sourceNote, out var mapped)) return Invalid($"images[{i}].noteId", "The note is not in this file.");
                noteId = mapped;
            }

            var image = new BoardImage
            {
                NoteId = noteId,
                FileName = ImageEndpoints.CleanFileName(source.FileName, info.ContentType),
                ContentType = info.ContentType,
                Size = source.Data.Length,
                PixelWidth = info.Width,
                PixelHeight = info.Height,
                Caption = PatchText.Apply(null, source.Caption),
                AddedById = createdBy,
                Content = new ImageContent { Data = source.Data },
                CreatedAt = clock.AddMilliseconds(i),
                UpdatedAt = clock.AddMilliseconds(i),
            };
            image.Resize(source.Width);
            if (noteId is null) (image.X, image.Y) = (Layout.Snap(source.X), Layout.Snap(source.Y));
            board.Images.Add(image);
        }

        Layout.AssignBuckets(board.Notes, board.Buckets);
        // Like createBoard: a personal access token imports for its owner, who owns the board; the token keeps its capped role.
        board.Members.Add(new BoardMember { PrincipalId = caller.MemberId, Role = BoardRole.Owner });

        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);

        var principals = await NoteEndpoints.PrincipalsOnAsync(db, board.Notes, ct);
        var importer = await db.Principals.FindAsync([createdBy], ct);
        foreach (var image in board.Images) image.AddedBy = importer;
        var people = await BoardAccess.PeopleAsync(db, board.Id, ct);
        return TypedResults.Created($"/api/boards/{board.Id}",
            BoardDetail.From(board, caller.Apply(board.Id, BoardRole.Owner)!.Value, principals, people, createdBy));
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>A file name stem from a board name: no path or control characters, a generic name when nothing is left.</summary>
    private static string CleanName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim();
        return clean.Length > 0 ? clean : "board";
    }
}
