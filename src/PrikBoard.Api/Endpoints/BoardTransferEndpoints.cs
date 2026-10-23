using System.Buffers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>Export a board to a file and import such a file as a new board.</summary>
public static class BoardTransferEndpoints
{
    /// <summary>Room for a board with a good number of images; each image is at most 10 MB and travels as base64.</summary>
    internal const long MaxImportBytes = 64 * 1024 * 1024;

    public static RouteGroupBuilder MapBoardTransferEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Boards");

        boards.MapGet("/{boardId:guid}/export", ExportBoard)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("exportBoard")
            .WithSummary("Export a board to a file")
            .WithDescription("Returns the board's content as one self-contained JSON file (sent as a download): name, description, agent rules, " +
                             "buckets, tags, cards, drawings and images with their bytes as base64. Members, roles and user ids are not included. " +
                             "Pass the file to importBoard to make a new board from it.");

        boards.MapPost("/import", ImportBoard)
            .NoBoardScope()
            .WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes), new BufferedBody())
            .WithName("importBoard")
            .WithSummary("Import a board from an export file")
            .WithDescription("Send the file from exportBoard as the request body (at most 64 MB). It always creates a new board, which you own, " +
                             "and never changes an existing one. Ids and the short id are new (cards keep their numbers when each has its own), " +
                             "positions are snapped to the grid, and each item's bucket follows from where it sits. " +
                             "Cards are created by you and have no assignees. Tokens limited to chosen boards cannot import. " +
                             "A file from a newer PrikBoard (a newer version, or fields this server doesn't know) is refused with a 400 " +
                             "keyed on version, rather than imported without what it can't read.")
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

        // Links to this board's own cards are written in the id form: the file keeps card ids, not the short id, so FollowLinks can map them.
        var ids = board.Cards.ToDictionary(n => n.Number, n => n.Id);
        string InFile(string url) =>
            CardLinks.TryParse(url, out var target) && target.BoardShortId == board.ShortId && ids.TryGetValue(target.Number, out var id)
                ? CardLinks.CardPath(board.Id, id)
                : url;

        var stack = Layout.BackToFront(board.Buckets).Index().ToDictionary(p => p.Item.Id, p => p.Index);
        var images = new List<ExportedImage>();
        foreach (var i in board.Images.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id))
        {
            images.Add(new ExportedImage(i.CardId, i.File!.FileName, i.Caption, i.X, i.Y, i.Width, await files.ReadAllBytesAsync(i.File, ct)));
        }
        var export = new BoardExport(
            BoardExport.FormatName, BoardExport.CurrentVersion, DateTimeOffset.UtcNow,
            board.Name, board.Description, board.Rules,
            Layout.InListOrder(board.Buckets)
                .Select(b => new ExportedBucket(b.Name, b.X, b.Y, b.Width, b.Height, b.Collapsed, stack[b.Id])).ToList(),
            board.Tags.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Select(t => t.Name).ToList(),
            // Oldest edit first: the importer gives them rising timestamps, so the most recently edited card stays on top.
            board.Cards.OrderBy(n => n.UpdatedAt).ThenBy(n => n.Id)
                .Select(n => new ExportedCard(n.Id, n.Title, n.Color, n.Tags, n.X, n.Y, n.Components.Select(b => Exported(b, InFile)).ToList(),
                    Number: n.Number)).ToList(),
            images,
            board.Tags.Where(t => t.Color != TagColor.Default).ToDictionary(t => t.Name, t => t.Color),
            board.Drawings.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
                .Select(d => new ExportedDrawing(d.CardId, d.X, d.Y, ExportedStrokes(d))).ToList(),
            board.Key,
            AppVersion.Number);

        var fileName = $"{CleanName(board.Name)}.prikboard.json";
        response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        return TypedResults.Ok(export);
    }

    private static async Task<Results<Created<BoardDetail>, ValidationProblem, ProblemHttpResult>> ImportBoard(
        BoardExport file, HttpRequest request, IOptions<HttpJsonOptions> json, ClaimsPrincipal principal, PrikBoardDbContext db, FileStore files,
        CancellationToken ct)
    {
        var caller = BoardCaller.Of(principal);
        if (caller.Boards is not null) return Problems.LimitedToken("import new ones");

        // A file from a newer PrikBoard is refused rather than imported without what this server can't read. The version
        // comes first: the web app shows the first error.
        var errors = new Dictionary<string, string[]>();
        var madeWith = MadeWith(file.AppVersion, "It was made");
        if (file.Version > BoardExport.CurrentVersion)
        {
            errors["version"] = [$"This file is version {file.Version}, but this server reads up to version {BoardExport.CurrentVersion}. {madeWith} Update PrikBoard to import it."];
        }
        else if (await UnknownFieldsAsync(request, json.Value.SerializerOptions, ct) is { Count: > 0 } unknown)
        {
            errors["version"] = [$"This file has fields this server doesn't know ({FieldList(unknown)}). {madeWith} Update PrikBoard to import it without losing them."];
        }
        if (file.Format != BoardExport.FormatName) errors["format"] = [$"This is not a PrikBoard export: format must be \"{BoardExport.FormatName}\"."];
        if (file.Cards.Select(n => n.Id).Distinct().Count() != file.Cards.Count) errors["cards"] = ["Card ids must be unique within the file."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var key = file.Key is null ? null : Board.NormalizeKey(file.Key);
        var board = new Board
        {
            ShortId = await BoardEndpoints.NewShortIdAsync(db, ct),
            Name = file.Name,
            Key = key is not null && Board.IsKey(key) ? key : Board.KeyFrom(file.Name),
            Description = PatchText.Apply(null, file.Description),
            Rules = PatchText.Apply(null, file.Rules),
        };
        if (board.Rules is not null) board.RulesUpdatedAt = board.CreatedAt;

        // Distinct timestamps keep the stacking order of buckets and the drawing order of cards.
        var clock = board.CreatedAt;
        foreach (var (i, bucket) in file.Buckets.Index())
        {
            var (width, height) = FittedBucketSize(bucket.Width, bucket.Height);
            board.Buckets.Add(new Bucket
            {
                Name = bucket.Name,
                X = Layout.Snap(bucket.X),
                Y = Layout.Snap(bucket.Y),
                Width = width,
                Height = height,
                CreatedAt = clock.AddMilliseconds(i),
                StackedAt = clock.AddMilliseconds(bucket.Stack ?? i),
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
        foreach (var (i, source) in file.Cards.Index())
        {
            var cardTags = CardTags.Normalize(source.Tags);
            if (cardTags.Any(t => t.Length > CardTags.MaxLength))
            {
                return Problems.Invalid($"cards[{i}].tags", $"A tag is at most {CardTags.MaxLength} characters.");
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
        foreach (var (i, name) in CardTags.Normalize(file.Tags).Concat(tags).Distinct().Index())
        {
            if (name.Length > CardTags.MaxLength) return Problems.Invalid("tags", $"A tag is at most {CardTags.MaxLength} characters.");
            board.Tags.Add(new BoardTag { Name = name, Color = colors.GetValueOrDefault(name), CreatedAt = clock.AddMilliseconds(i) });
        }
        if (board.Tags.Count > BoardTag.MaxPerBoard) return Problems.Invalid("tags", $"A board has at most {BoardTag.MaxPerBoard} tags.");

        // Files are written once the whole file has proved valid, so a rejected import leaves none behind.
        var contents = new List<(StoredFile File, byte[] Data)>();
        foreach (var (i, source) in file.Images.Index())
        {
            if (!ImageEndpoints.TryFromBytes(board.Id, source.Data, source.FileName, source.Caption, source.Width, out var image, out var problem))
            {
                return Problems.Invalid($"images[{i}].data", problem);
            }
            Guid? cardId = null;
            if (source.CardId is { } sourceCard)
            {
                if (!newIds.TryGetValue(sourceCard, out var mapped)) return Problems.Invalid($"images[{i}].cardId", "The card is not in this file.");
                cardId = mapped;
            }

            image.CardId = cardId;
            image.AddedById = createdBy;
            image.CreatedAt = image.UpdatedAt = clock.AddMilliseconds(i);
            if (cardId is null) (image.X, image.Y) = (Layout.Snap(source.X), Layout.Snap(source.Y));
            else (image.X, image.Y) = Layout.OnCardCanvas(source.X, source.Y, image.Width, image.Height);
            board.Images.Add(image);
            contents.Add((image.File!, source.Data));
        }

        foreach (var (i, source) in (file.Drawings ?? []).Index())
        {
            Guid? cardId = null;
            if (source.CardId is { } sourceCard)
            {
                if (!newIds.TryGetValue(sourceCard, out var mapped)) return Problems.Invalid($"drawings[{i}].cardId", "The card is not in this file.");
                cardId = mapped;
            }
            var drawing = new BoardDrawing
            {
                CardId = cardId,
                AddedById = createdBy,
                CreatedAt = clock.AddMilliseconds(i),
                UpdatedAt = clock.AddMilliseconds(i),
            };
            if (DrawingEndpoints.Build(drawing, source.AllStrokes(), source.X, source.Y, $"drawings[{i}].") is { } invalidDrawing) return invalidDrawing;
            board.Drawings.Add(drawing);
        }

        Layout.PlaceAll(board.Cards, [.. board.Images, .. board.Drawings], board.Buckets);
        // Folded up only now, so their cards, images and drawings joined them first.
        foreach (var (i, bucket) in board.Buckets.Index()) bucket.Collapsed = file.Buckets[i].Collapsed;
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

    /// <summary>A drawing's strokes as files and clips hold them, each with its ink. Needs its <c>Content</c> loaded.</summary>
    internal static List<ExportedStroke> ExportedStrokes(BoardDrawing drawing) =>
        [.. drawing.Content!.Strokes.Zip(drawing.Colors, (s, color) => new ExportedStroke(color, s.StrokeWidth, [.. s.Points]))];

    /// <summary>"It was made" or "They were copied" with which PrikBoard, for refusing what a newer one wrote.</summary>
    internal static string MadeWith(string? appVersion, string done) => appVersion is { } made
        ? $"{done} with PrikBoard {made}, and this server runs {AppVersion.Number}."
        : $"{done} with a newer PrikBoard than this server runs.";

    /// <summary>Unknown fields for a message: the first five, and how many more.</summary>
    internal static string FieldList(IReadOnlyList<string> fields) =>
        string.Join(", ", fields.Take(5)) + (fields.Count > 5 ? $" and {fields.Count - 5} more" : "");

    /// <summary>A bucket size from a file or clip: snapped to the grid, within the smallest and largest a bucket can be.</summary>
    internal static (int Width, int Height) FittedBucketSize(int width, int height) => (
        Math.Min(Layout.MaxBucketSize, Layout.SnapSize(width, Layout.MinBucketWidth)),
        Math.Min(Layout.MaxBucketSize, Layout.SnapSize(height, Layout.MinBucketHeight)));

    /// <summary>
    /// A component as it goes in the file: like a new component, with its place and only the content of its kind, and each link's
    /// url through <paramref name="url"/>.
    /// </summary>
    internal static NewCardComponent Exported(CardComponent component, Func<string, string> url)
    {
        var detail = CardComponentDetail.From(component);
        return new NewCardComponent(component.Kind, component.Title, detail.Text, detail.Items?.ToList(),
            detail.Links?.Select(l => l with { Url = url(l.Url) }).ToList(),
            component.X, component.Y, component.Width, component.Height);
    }

    /// <summary>
    /// A link to another card in the file (by its id in the file) becomes that card's new path. Web links stay (invalid ones
    /// are left for the component's own check). Any other card path, by number or to a card outside the file, is dropped, as
    /// it would dangle.
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

    /// <summary>A file name stem from a board name: no path or control characters, a generic name when nothing is left.</summary>
    private static string CleanName(string name)
    {
        var clean = PatchText.Without(name, NotInFileNames);
        return clean.Length > 0 ? clean : "board";
    }

    private static readonly SearchValues<char> NotInFileNames =
        SearchValues.Create(PatchText.ControlCharacters + new string(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// The fields in the request body (an export file) that BoardExport doesn't have, as paths (drawings,
    /// cards[3].priority): a newer PrikBoard wrote them, and binding the file would quietly leave them out. The body is
    /// read again for this (BufferedBody keeps it).
    /// </summary>
    private static Task<List<string>> UnknownFieldsAsync(HttpRequest request, JsonSerializerOptions options, CancellationToken ct) =>
        UnknownFieldsAsync(request, options, typeof(BoardExport), ct);

    /// <summary>
    /// Fields that older files and clips still have but this version dropped: they are known, and binding skips them. A
    /// bucket's color went with export version 7 and clip version 4.
    /// </summary>
    private static readonly HashSet<(Type Type, string Field)> DroppedFields =
    [
        (typeof(ExportedBucket), "color"),
        (typeof(ClippedBucket), "color"),
    ];

    /// <summary>The fields in the request body that <paramref name="type"/> doesn't have, as paths, like the export file's check above.</summary>
    internal static async Task<List<string>> UnknownFieldsAsync(HttpRequest request, JsonSerializerOptions options, Type type, CancellationToken ct)
    {
        if (!request.Body.CanSeek) return [];
        request.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var unknown = new List<string>();
        Collect(document.RootElement, options.GetTypeInfo(type), "");
        return unknown;

        void Collect(JsonElement element, JsonTypeInfo info, string path)
        {
            if (info.Kind == JsonTypeInfoKind.Object && element.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in element.EnumerateObject())
                {
                    var property = info.Properties.FirstOrDefault(p => string.Equals(p.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                    var at = path.Length == 0 ? field.Name : $"{path}.{field.Name}";
                    if (property is null)
                    {
                        if (!DroppedFields.Contains((info.Type, field.Name.ToLowerInvariant()))) unknown.Add(at);
                    }
                    else Collect(field.Value, options.GetTypeInfo(property.PropertyType), at);
                }
            }
            else if (info is { Kind: JsonTypeInfoKind.Enumerable, ElementType: { } elementType } && element.ValueKind == JsonValueKind.Array)
            {
                var elementInfo = options.GetTypeInfo(elementType);
                var i = 0;
                foreach (var item in element.EnumerateArray()) Collect(item, elementInfo, $"{path}[{i++}]");
            }
        }
    }
}

/// <summary>Marks an endpoint whose request body can be read again after binding (importBoard's check for unknown fields).</summary>
public sealed class BufferedBody;
