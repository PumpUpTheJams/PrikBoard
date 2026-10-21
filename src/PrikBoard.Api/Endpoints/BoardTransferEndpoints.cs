using System.Security.Claims;
using System.Text.RegularExpressions;
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
                             "buckets, tags, cards and images with their bytes as base64. Members, roles and user ids are not included. " +
                             "Pass the file to importBoard to make a new board from it.");

        boards.MapPost("/import", ImportBoard)
            .NoBoardScope()
            .WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes))
            .WithName("importBoard")
            .WithSummary("Import a board from an export file")
            .WithDescription("Send the file from exportBoard as the request body. It always creates a new board, which you own, and never changes " +
                             "an existing one. Ids and the short id are made new (cards keep their numbers), positions are snapped to the grid, " +
                             "and each card's bucket follows from where it sits. " +
                             "Cards are created by you and have no assignees. Tokens limited to chosen boards cannot import.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<Results<Ok<BoardExport>, NotFound>> ExportBoard(
        Guid boardId, HttpResponse response, PrikBoardDbContext db, FileStore files, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking()
            .Include(b => b.Buckets)
            .Include(b => b.Cards)
            .Include(b => b.Images).ThenInclude(i => i.File)
            .Include(b => b.Drawings).ThenInclude(d => d.Content)
            .Include(b => b.Tags)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null) return TypedResults.NotFound();

        // Links to cards on this board point at them by id, the one thing the file keeps of them (see FollowLinks).
        var ids = board.Cards.ToDictionary(n => n.Number, n => n.Id);
        string InFile(string url) =>
            CardLinks.TryParse(url, out var target) && target.BoardShortId == board.ShortId && ids.TryGetValue(target.Number, out var id)
                ? CardLinks.CardPath(board.Id, id)
                : url;

        var images = new List<ExportedImage>();
        foreach (var i in board.Images.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id))
        {
            images.Add(new ExportedImage(i.CardId, i.File!.FileName, i.Caption, i.X, i.Y, i.Width, await files.ReadAllBytesAsync(i.File, ct)));
        }
        var export = new BoardExport(
            BoardExport.FormatName, BoardExport.CurrentVersion, DateTimeOffset.UtcNow,
            board.Name, board.Description, board.Rules,
            Layout.BackToFront(board.Buckets)
                .Select(b => new ExportedBucket(b.Name, b.Color, b.X, b.Y, b.Width, b.Height, b.Collapsed)).ToList(),
            board.Tags.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Select(t => t.Name).ToList(),
            // Oldest edit first: the importer gives them rising timestamps, so the most recently edited card stays on top.
            board.Cards.OrderBy(n => n.UpdatedAt).ThenBy(n => n.Id)
                .Select(n => new ExportedCard(n.Id, n.Title, n.Color, n.Tags, n.X, n.Y, n.Components.Select(b => Exported(b, InFile)).ToList(),
                    Number: n.Number)).ToList(),
            images,
            board.Tags.Where(t => t.Color != TagColor.Default).ToDictionary(t => t.Name, t => t.Color),
            board.Drawings.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
                .Select(d => new ExportedDrawing(d.CardId, d.X, d.Y, d.Color, d.StrokeWidth, [.. d.Content!.Points])).ToList(),
            board.Key);

        var fileName = $"{CleanName(board.Name)}.prikboard.json";
        response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        return TypedResults.Ok(export);
    }

    private static async Task<Results<Created<BoardDetail>, ValidationProblem, ProblemHttpResult>> ImportBoard(
        BoardExport file, ClaimsPrincipal principal, PrikBoardDbContext db, FileStore files, CancellationToken ct)
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
        if (file.Cards.Select(n => n.Id).Distinct().Count() != file.Cards.Count) errors["cards"] = ["Card ids must be unique within the file."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var key = file.Key is null ? null : Board.NormalizeKey(file.Key);
        var board = new Board
        {
            ShortId = await BoardEndpoints.NewShortIdAsync(db, ct),
            Name = file.Name,
            Key = key is not null && Regex.IsMatch(key, Board.KeyPattern) ? key : Board.KeyFrom(file.Name),
            Description = PatchText.Apply(null, file.Description),
            Rules = PatchText.Apply(null, file.Rules),
        };
        if (board.Rules is not null) board.RulesUpdatedAt = board.CreatedAt;

        // Distinct timestamps keep the stacking order of buckets and the drawing order of cards.
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
        var newIds = file.Cards.ToDictionary(n => n.Id, _ => Guid.CreateVersion7());
        // The file's numbers when every card has its own, so MPCT-415 is 415 on the copy too; otherwise in file order.
        var keepNumbers = file.Cards.All(n => n.Number is not null) && file.Cards.Select(n => n.Number).Distinct().Count() == file.Cards.Count;
        var numbers = file.Cards.Select((n, i) => (n.Id, Number: keepNumbers ? n.Number!.Value : i + 1))
            .ToDictionary(n => newIds[n.Id], n => n.Number);
        board.NextCardNumber = numbers.Values.DefaultIfEmpty(0).Max() + 1;
        var createdBy = principal.PrincipalId();
        foreach (var (source, i) in file.Cards.Select((n, i) => (n, i)))
        {
            var cardTags = CardTags.Normalize(source.Tags);
            if (cardTags.Any(t => t.Length > CardTags.MaxLength))
            {
                return Invalid($"cards[{i}].tags", $"A tag is at most {CardTags.MaxLength} characters.");
            }
            tags.AddRange(cardTags);

            var card = new Card
            {
                Id = newIds[source.Id],
                Number = numbers[newIds[source.Id]],
                Title = source.Title,
                Color = source.Color,
                Tags = cardTags,
                X = Layout.Snap(source.X),
                Y = Layout.Snap(source.Y),
                CreatedById = createdBy,
                CreatedAt = clock.AddMilliseconds(i),
                UpdatedAt = clock.AddMilliseconds(i),
            };
            var components = (source.Components ?? [])
                .Select(b => b with { Links = b.Links is null ? null : FollowLinks(b.Links, newIds, board.ShortId, numbers) }).ToList();
            if (CardComponentEndpoints.BuildAll(card, components, $"cards[{i}].") is { } invalidComponent) return invalidComponent;
            board.Cards.Add(card);
        }

        // The tags the file lists (also unused ones), then any a card uses that the list missed.
        var colors = new Dictionary<string, TagColor>();
        foreach (var (name, color) in file.TagColors ?? []) colors[CardTags.Normalize(name)] = color;
        foreach (var (name, i) in CardTags.Normalize(file.Tags).Concat(tags).Distinct().Select((t, i) => (t, i)))
        {
            if (name.Length > CardTags.MaxLength) return Invalid("tags", $"A tag is at most {CardTags.MaxLength} characters.");
            board.Tags.Add(new BoardTag { Name = name, Color = colors.GetValueOrDefault(name), CreatedAt = clock.AddMilliseconds(i) });
        }
        if (board.Tags.Count > BoardTag.MaxPerBoard) return Invalid("tags", $"A board has at most {BoardTag.MaxPerBoard} tags.");

        // Files are written once the whole file has proved valid, so a rejected import leaves none behind.
        var contents = new List<(StoredFile File, byte[] Data)>();
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
            Guid? cardId = null;
            if (source.CardId is { } sourceCard)
            {
                if (!newIds.TryGetValue(sourceCard, out var mapped)) return Invalid($"images[{i}].cardId", "The card is not in this file.");
                cardId = mapped;
            }

            var image = new BoardImage
            {
                CardId = cardId,
                PixelWidth = info.Width,
                PixelHeight = info.Height,
                Caption = PatchText.Apply(null, source.Caption),
                AddedById = createdBy,
                File = StoredFile.For(board.Id, ImageEndpoints.CleanFileName(source.FileName, info.ContentType), info.ContentType, source.Data),
                CreatedAt = clock.AddMilliseconds(i),
                UpdatedAt = clock.AddMilliseconds(i),
            };
            image.Resize(source.Width);
            if (cardId is null) (image.X, image.Y) = (Layout.Snap(source.X), Layout.Snap(source.Y));
            else (image.X, image.Y) = Layout.OnCardCanvas(source.X, source.Y, image.Width, image.Height);
            board.Images.Add(image);
            contents.Add((image.File, source.Data));
        }

        foreach (var (source, i) in (file.Drawings ?? []).Select((d, i) => (d, i)))
        {
            Guid? cardId = null;
            if (source.CardId is { } sourceCard)
            {
                if (!newIds.TryGetValue(sourceCard, out var mapped)) return Invalid($"drawings[{i}].cardId", "The card is not in this file.");
                cardId = mapped;
            }
            var drawing = new BoardDrawing
            {
                CardId = cardId,
                Color = source.Color,
                StrokeWidth = source.StrokeWidth,
                AddedById = createdBy,
                CreatedAt = clock.AddMilliseconds(i),
                UpdatedAt = clock.AddMilliseconds(i),
            };
            if (DrawingEndpoints.Build(drawing, source.Points, source.X, source.Y, $"drawings[{i}].") is { } invalidDrawing) return invalidDrawing;
            board.Drawings.Add(drawing);
        }

        Layout.PlaceAll(board.Cards, [.. board.Images, .. board.Drawings], board.Buckets);
        // Folded up only now, so their cards, images and drawings joined them first.
        foreach (var (bucket, i) in board.Buckets.Select((b, i) => (b, i))) bucket.Collapsed = file.Buckets[i].Collapsed;
        // Like createBoard: a personal access token imports for its owner, who owns the board; the token keeps its capped role.
        board.Members.Add(new BoardMember { PrincipalId = caller.MemberId, Role = BoardRole.Owner });

        foreach (var (stored, data) in contents) await files.WriteAsync(stored, data, ct);
        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);

        var principals = await CardEndpoints.PrincipalsOnAsync(db, board.Cards, ct);
        var importer = await db.Principals.FindAsync([createdBy], ct);
        foreach (var image in board.Images) image.AddedBy = importer;
        foreach (var drawing in board.Drawings) drawing.AddedBy = importer;
        var people = await BoardAccess.PeopleAsync(db, board.Id, ct);
        return TypedResults.Created($"/api/boards/{board.Id}",
            BoardDetail.From(board, caller.Apply(board.Id, BoardRole.Owner)!.Value, principals, people, createdBy));
    }

    /// <summary>
    /// A component as it goes in the file: like a new component, with its place and only the content of its kind, and each link's
    /// url through <paramref name="url"/>.
    /// </summary>
    private static NewCardComponent Exported(CardComponent component, Func<string, string> url)
    {
        var detail = CardComponentDetail.From(component);
        return new NewCardComponent(component.Kind, component.Title, detail.Text, detail.Items?.ToList(),
            detail.Links?.Select(l => l with { Url = url(l.Url) }).ToList(),
            component.X, component.Y, component.Width, component.Height);
    }

    /// <summary>
    /// A link to another card in the file (by its id in the file) becomes that card's new path; web links stay (and invalid
    /// ones are left for the component's own check); a path to a card outside the file is dropped, as it would dangle.
    /// </summary>
    private static List<CardLink> FollowLinks(
        IEnumerable<CardLink> links, Dictionary<Guid, Guid> newIds, string shortId, Dictionary<Guid, int> numbers)
    {
        var followed = new List<CardLink>();
        foreach (var link in links)
        {
            if (!CardLinks.TryParse(link.Url, out var target)) followed.Add(link);
            else if (!target.ByNumber && newIds.TryGetValue(target.CardId, out var newTarget))
            {
                followed.Add(link with { Url = CardLinks.CardPath(shortId, numbers[newTarget]) });
            }
        }
        return followed;
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
