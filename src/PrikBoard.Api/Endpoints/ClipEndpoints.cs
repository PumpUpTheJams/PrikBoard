using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;
using PastedImage = (PrikBoard.Api.Domain.BoardImage Image, PrikBoard.Api.Contracts.ClippedImage Source, byte[] Data);
using PastedDrawing = (PrikBoard.Api.Domain.BoardDrawing Drawing, PrikBoard.Api.Contracts.ClippedDrawing Source);

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// Copy and paste: copyItems turns buckets, cards, images and drawings into a clip (<see cref="BoardClip"/>) holding
/// everything on them, and pasteItems adds a clip to a board. The clip is self-contained, so it pastes on another PrikBoard
/// server too.
/// </summary>
public static class ClipEndpoints
{
    /// <summary><see cref="BoardClip.MaxImageBytes"/> in MB, for messages.</summary>
    private const long MaxClipMegabytes = BoardClip.MaxImageBytes / 1024 / 1024;

    public static RouteGroupBuilder MapClipEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Cards");

        boards.MapPost("/{boardId:guid}/copy", CopyItems)
            .RequireBoardRole(BoardRole.Viewer)
            .Quiet()
            .WithName("copyItems")
            .WithSummary("Copy cards, buckets, images and drawings")
            .WithDescription("Returns the items as a clip: one self-contained piece of JSON with the cards' title, fill, tags, assignees " +
                             "and content, the images (bytes as base64) and drawings on them, buckets with everything in them, and images " +
                             "and drawings on the board. Changes nothing, even with cut (Editors only): that only marks the clip, so " +
                             "pasting it on another board of this server moves the items. Pass the clip to pasteItems on any board, on this server or " +
                             $"another PrikBoard server. At most {BoardClip.MaxCards} cards, {BoardClip.MaxBuckets} buckets and " +
                             $"{BoardClip.MaxImageBytes / 1024 / 1024} MB of images per clip.")
            .ProducesValidationProblem();

        boards.MapPost("/{boardId:guid}/paste", PasteItems)
            .RequireBoardRole(BoardRole.Editor)
            .WithMetadata(new RequestSizeLimitAttribute(BoardTransferEndpoints.MaxImportBytes), new BufferedBody())
            .WithName("pasteItems")
            .WithSummary("Paste copied items")
            .WithDescription("Adds what is in a clip from copyItems to the board as new items: cards created by you with new numbers, " +
                             "their images and drawings, buckets (on top of the others) with what was in them, and images and drawings. " +
                             "With X and Y everything keeps its place relative to the rest, the top-left at X and Y; an item joins its " +
                             "pasted bucket, or else the bucket it lands in, and a pasted bucket takes the loose items it covers. With a " +
                             "bucketId (a clip of cards alone) the cards go into free spots in that bucket; with neither, cards go into " +
                             "the board's first bucket and anything else to a free spot. Tags the board doesn't have yet are added with " +
                             "their colors; assignees stay only when they can be assigned here. Links between the pasted cards point at " +
                             "the new cards; links to other cards stay, or, for a clip from another server, become full links to it. " +
                             "A cut pasted on another board of this server moves the items: the originals go to their board's trash " +
                             "(you need Editor there) and come back as moved; on the board it was cut from it is a 400. A clip " +
                             "from a newer PrikBoard (a newer version, or fields this server doesn't know) is refused with a 400 keyed on " +
                             "clip.version.")
            .ProducesValidationProblem();

        return api;
    }

    private static async Task<Results<Ok<BoardClip>, NotFound, ValidationProblem>> CopyItems(
        Guid boardId, CopyItemsRequest request, HttpRequest http, PrikBoardDbContext db, FileStore files, CancellationToken ct)
    {
        if (request.Cut && http.HttpContext.BoardRole() < BoardRole.Editor)
        {
            return Problems.Invalid("cut", "You can only view this board, so its items can't be cut. Copy them instead.");
        }
        return await BuildClipAsync(db, files, boardId, request, ServerUrl(http), ct);
    }

    private static async Task<Results<Ok<PastedItems>, NotFound, ValidationProblem>> PasteItems(
        Guid boardId, PasteItemsRequest request, HttpRequest http, IOptions<HttpJsonOptions> json, ClaimsPrincipal principal,
        PrikBoardDbContext db, FileStore files, BoardEvents events, CancellationToken ct)
    {
        // A clip from a newer PrikBoard is refused rather than pasted without what this server can't read.
        if (request.Clip.Version <= BoardClip.CurrentVersion
            && await BoardTransferEndpoints.UnknownFieldsAsync(http, json.Value.SerializerOptions, typeof(PasteItemsRequest), ct) is { Count: > 0 } unknown)
        {
            return Problems.Invalid("clip.version", $"These items have fields this server doesn't know ({BoardTransferEndpoints.FieldList(unknown)}). {MadeWith(request.Clip)} " +
                                           "Update PrikBoard to paste them without losing them.");
        }
        var result = await PasteAsync(boardId, request, ServerUrl(http), principal, db, files, ct);
        // The board they were cut from changed too (this one is announced by the role filter).
        if (result.Result is Ok<PastedItems> { Value.Moved: { } moved }) await events.ChangedAsync(moved.BoardId, http.HttpContext);
        return result;
    }

    /// <summary>Where this server is, as clips name it: scheme and host of the request (the Host header, as for sign-in callbacks).</summary>
    internal static string ServerUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>The clip of what <paramref name="what"/> names, all on <paramref name="boardId"/>; 400 keyed on the list otherwise.</summary>
    internal static async Task<Results<Ok<BoardClip>, NotFound, ValidationProblem>> BuildClipAsync(
        PrikBoardDbContext db, FileStore files, Guid boardId, CopyItemsRequest what, string serverUrl, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking().Where(b => b.Id == boardId).Select(b => new { b.ShortId, b.Key }).FirstOrDefaultAsync(ct);
        if (board is null) return TypedResults.NotFound();

        var cardIds = (what.CardIds ?? []).Distinct().ToList();
        var bucketIds = (what.BucketIds ?? []).Distinct().ToList();
        var imageIds = (what.ImageIds ?? []).Distinct().ToList();
        var drawingIds = (what.DrawingIds ?? []).Distinct().ToList();
        if (cardIds.Count + bucketIds.Count + imageIds.Count + drawingIds.Count == 0)
        {
            return Problems.Invalid("cardIds", "Name at least one card, bucket, image or drawing to copy.");
        }

        var buckets = await db.Buckets.AsNoTracking().Where(b => b.BoardId == boardId && bucketIds.Contains(b.Id)).ToListAsync(ct);
        if (buckets.Count != bucketIds.Count) return Problems.Invalid("bucketIds", "Every bucket must be on this board.");
        // A bucket comes with what is in it, and the items in it name it.
        Guid? CopiedBucket(Guid? bucketId) => bucketId is { } id && bucketIds.Contains(id) ? bucketId : null;
        var cards = await db.Cards.AsNoTracking()
            .Where(n => n.BoardId == boardId && (cardIds.Contains(n.Id) || (n.BucketId != null && bucketIds.Contains(n.BucketId.Value))))
            .ToListAsync(ct);
        if (cardIds.Except(cards.Select(n => n.Id)).Any()) return Problems.Invalid("cardIds", "Every card must be on this board.");
        var ids = cards.Select(n => n.Id).ToList();

        var images = await db.Images.AsNoTracking().Include(i => i.File)
            .Where(i => i.BoardId == boardId && (i.CardId != null
                ? ids.Contains(i.CardId.Value)
                : imageIds.Contains(i.Id) || (i.BucketId != null && bucketIds.Contains(i.BucketId.Value))))
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(ct);
        if (imageIds.Except(images.Where(i => i.CardId is null).Select(i => i.Id)).Any())
        {
            return Problems.Invalid("imageIds", "Every image must be on this board, and not on a card (those come with their card).");
        }
        if (images.Count > BoardClip.MaxImages || images.Sum(i => i.File!.Size) > BoardClip.MaxImageBytes)
        {
            return Problems.Invalid("cardIds", $"These hold more than {BoardClip.MaxImages} images or {MaxClipMegabytes} MB " +
                                      "of them, more than one copy takes. Copy fewer at a time.");
        }
        var drawings = await db.Drawings.AsNoTracking().Include(d => d.Content)
            .Where(d => d.BoardId == boardId && (d.CardId != null
                ? ids.Contains(d.CardId.Value)
                : drawingIds.Contains(d.Id) || (d.BucketId != null && bucketIds.Contains(d.BucketId.Value))))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(ct);
        if (drawingIds.Except(drawings.Where(d => d.CardId is null).Select(d => d.Id)).Any())
        {
            return Problems.Invalid("drawingIds", "Every drawing must be on this board, and not on a card (those come with their card).");
        }
        if (cards.Count > BoardClip.MaxCards || drawings.Count > BoardClip.MaxDrawings)
        {
            return Problems.Invalid("cardIds", $"These hold more than {BoardClip.MaxCards} cards or {BoardClip.MaxDrawings} drawings. Copy fewer at a time.");
        }

        var tagNames = cards.SelectMany(n => n.Tags).Distinct().ToList();
        var tagColors = await db.BoardTags.AsNoTracking()
            .Where(t => t.BoardId == boardId && tagNames.Contains(t.Name) && t.Color != TagColor.Default)
            .ToDictionaryAsync(t => t.Name, t => t.Color, ct);
        var clippedImages = new List<ClippedImage>();
        foreach (var i in images) clippedImages.Add(await ClipImageAsync(files, i, CopiedBucket(i.BucketId), ct));

        return TypedResults.Ok(new BoardClip(
            BoardClip.FormatName, BoardClip.CurrentVersion, DateTimeOffset.UtcNow,
            new ClipSource(serverUrl, board.ShortId, board.Key),
            // Oldest edit first: pasted, they get rising timestamps, so the most recently edited card stays on top.
            [.. cards.OrderBy(n => n.UpdatedAt).ThenBy(n => n.Id).Select(n => ClipCard(n, CopiedBucket(n.BucketId)))],
            clippedImages,
            [.. drawings.Select(d => ClipDrawing(d, CopiedBucket(d.BucketId)))],
            tagColors,
            AppVersion.Number,
            [.. Layout.BackToFront(buckets).Select(ClipBucket)],
            what.Cut));
    }

    /// <summary>A card as a clip holds it; <paramref name="bucketId"/> is its copied bucket, if any.</summary>
    private static ClippedCard ClipCard(Card card, Guid? bucketId) => new(
        card.Id, card.Number, card.Title, card.Color, card.Tags, card.X, card.Y,
        [.. card.Components.Select(b => BoardTransferEndpoints.Exported(b, url => url))], card.AssigneeIds, bucketId);

    /// <summary>An image as a clip holds it, with its bytes; <paramref name="bucketId"/> is its copied bucket, if any.</summary>
    private static async Task<ClippedImage> ClipImageAsync(FileStore files, BoardImage image, Guid? bucketId, CancellationToken ct) => new(
        image.CardId, image.File!.FileName, image.Caption, image.X, image.Y, image.Width, await files.ReadAllBytesAsync(image.File, ct),
        bucketId, image.Id);

    /// <summary>A drawing as a clip holds it; <paramref name="bucketId"/> is its copied bucket, if any.</summary>
    private static ClippedDrawing ClipDrawing(BoardDrawing drawing, Guid? bucketId) =>
        new(drawing.CardId, drawing.X, drawing.Y, BoardTransferEndpoints.ExportedStrokes(drawing), BucketId: bucketId, Id: drawing.Id);

    private static ClippedBucket ClipBucket(Bucket bucket) =>
        new(bucket.Id, bucket.Name, bucket.X, bucket.Y, bucket.Width, bucket.Height, bucket.Collapsed);

    /// <summary>Adds the clip's items to <paramref name="boardId"/>; see pasteItems. <paramref name="serverUrl"/> is this server.</summary>
    internal static async Task<Results<Ok<PastedItems>, NotFound, ValidationProblem>> PasteAsync(
        Guid boardId, PasteItemsRequest request, string serverUrl, ClaimsPrincipal principal, PrikBoardDbContext db, FileStore files,
        CancellationToken ct)
    {
        var clip = request.Clip;
        if (InvalidClip(request) is { } invalidClip) return invalidClip;

        var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null) return TypedResults.NotFound();
        var me = principal.PrincipalId();
        var sameServer = SameServer(clip.Source.Url, serverUrl);

        // A cut moves the items: pasted on another board of this server, the originals go to their board's trash. From
        // another server they can't be reached, so it pastes as a copy (Moved stays null).
        Board? cutFrom = null;
        if (clip.Cut && sameServer)
        {
            cutFrom = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.ShortId == clip.Source.BoardShortId, ct);
            if (cutFrom is not null && cutFrom.Id == boardId)
            {
                return Problems.Invalid("clip.cut", "Cut items paste on another board. To move them on this board, move them.");
            }
            if (cutFrom is null || await BoardAccess.RoleOnBoardAsync(db, cutFrom.Id, principal, ct) is not >= BoardRole.Editor)
            {
                return Problems.Invalid("clip.cut", "These were cut from a board you can't change (any more), so they can't be moved. Copy them instead.");
            }
        }

        var assignable = await AssignableAsync(db, boardId, clip, ct);
        var now = DateTimeOffset.UtcNow;
        if (BuildCards(clip, boardId, me, now, assignable, out var cards) is { } invalidCard) return invalidCard;
        var copies = clip.Cards.Select((n, i) => (n.Id, Card: cards[i])).ToDictionary(p => p.Id, p => p.Card);
        if (BuildImages(clip, boardId, me, now, copies, out var images) is { } invalidImage) return invalidImage;
        if (clip.Drawings.Count > 0
            && await db.Drawings.CountAsync(d => d.BoardId == boardId, ct) + clip.Drawings.Count > BoardDrawing.MaxPerBoard)
        {
            return Problems.Invalid("clip.drawings", $"A board holds at most {BoardDrawing.MaxPerBoard} drawings, and these would pass that. Delete some first.");
        }
        if (BuildDrawings(clip, boardId, me, now, copies, out var drawings) is { } invalidDrawing) return invalidDrawing;
        if (await AddTagsAsync(db, boardId, clip, cards, ct) is { } tooManyTags) return tooManyTags;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var bucket = request.BucketId is { } bucketId ? await LockBucketAsync(db, boardId, bucketId, ct) : null;
        if (request.BucketId is not null && bucket is null) return Problems.Invalid("bucketId", "The bucket does not exist on this board.");
        var buckets = await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct);
        if (bucket is null && CardsOnly(clip) && request.X is null && Layout.InListOrder(buckets).FirstOrDefault() is { } first)
        {
            bucket = await LockBucketAsync(db, boardId, first.Id, ct);
        }
        var others = await db.Cards.Where(n => n.BoardId == boardId).ToListAsync(ct);
        List<Bucket> newBuckets = [];
        // Into a bucket they go where there is room, growing it downwards when it is full.
        if (bucket is not null) await BucketEndpoints.PackCardsAsync(db, bucket, buckets, others, cards, GrowSide.Down, ct);
        else newBuckets = await PlaceOnBoardAsync(db, boardId, request, cards, images, drawings, buckets, others, now, ct);

        var number = cards.Count == 0 ? 0 : await CardEndpoints.TakeNumberAsync(db, boardId, ct, cards.Count);
        foreach (var card in cards) card.Number = number++;
        if (AddContentAndHistory(db, clip, cards, copies, board, me, now, sameServer, moved: cutFrom is not null) is { } invalid) return invalid;

        var moved = cutFrom is null ? null : await RemoveOriginalsAsync(db, cutFrom, clip, clip.Cards.Zip(cards), board, principal, ct);
        if (cutFrom is not null && moved is null)
        {
            return Problems.Invalid("clip.cut", "These were moved already: none of them is on the board they were cut from any more. Copy them from where they are now.");
        }

        // Files go on disk before their rows are saved (see FileStore).
        foreach (var (image, _, data) in images) await files.WriteAsync(image.File!, data, ct);
        db.Buckets.AddRange(newBuckets);
        db.Cards.AddRange(cards);
        db.Images.AddRange(images.Select(m => m.Image));
        db.Drawings.AddRange(drawings.Select(d => d.Drawing));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var principals = await CardEndpoints.PrincipalsOnAsync(db, cards, ct);
        var adder = await db.Principals.FindAsync([me], ct);
        foreach (var (image, _, _) in images) image.AddedBy = adder;
        foreach (var (drawing, _) in drawings) drawing.AddedBy = adder;
        var boardRef = BoardRef.Of(board);
        return TypedResults.Ok(new PastedItems(
            [.. cards.Select(n => CardDetail.From(n, boardRef, principals))],
            [.. images.Select(m => ImageDetail.From(m.Image))],
            [.. drawings.Select(d => DrawingDetail.From(d.Drawing))],
            bucket is null ? null : BucketDetail.From(bucket),
            [.. newBuckets.Select(BucketDetail.From)],
            moved));
    }

    /// <summary>The checks on a clip that need no database, in the order pasteItems answers them.</summary>
    private static ValidationProblem? InvalidClip(PasteItemsRequest request)
    {
        var clip = request.Clip;
        if (clip.Version > BoardClip.CurrentVersion)
        {
            return Problems.Invalid("clip.version", $"These items were copied in a newer format (version {clip.Version}; this server reads up to " +
                                           $"{BoardClip.CurrentVersion}). {MadeWith(clip)} Update PrikBoard to paste them.");
        }
        if (clip.Format != BoardClip.FormatName) return Problems.Invalid("clip.format", $"This is not a PrikBoard clip: format must be \"{BoardClip.FormatName}\".");
        if (Problems.XWithoutY(request.X, request.Y) is { } apart) return apart;
        if (request.BucketId is not null && request.X is not null) return Problems.Invalid("bucketId", "Give a bucketId, or X and Y, not both.");
        if (clip.Cards.Select(n => n.Id).Distinct().Count() != clip.Cards.Count) return Problems.Invalid("clip.cards", "Card ids must be unique within the clip.");
        var clipBuckets = clip.Buckets ?? [];
        if (clipBuckets.Select(b => b.Id).Distinct().Count() != clipBuckets.Count) return Problems.Invalid("clip.buckets", "Bucket ids must be unique within the clip.");
        var cardsOnly = CardsOnly(clip);
        if (cardsOnly && clip.Cards.Count == 0) return Problems.Invalid("clip", "The clip holds nothing to paste.");
        if (!cardsOnly && request.BucketId is not null)
        {
            return Problems.Invalid("bucketId", "Only cards go into a bucket by bucketId; paste buckets, images and drawings at X and Y.");
        }
        var inBucket = clipBuckets.Select(b => b.Id).ToHashSet();
        bool Outside(Guid? bucketId) => bucketId is { } b && !inBucket.Contains(b);
        if (clip.Cards.Any(n => Outside(n.BucketId)) || LooseImages(clip).Any(m => Outside(m.BucketId)) || LooseDrawings(clip).Any(d => Outside(d.BucketId)))
        {
            return Problems.Invalid("clip.buckets", "An item names a bucket that is not in the clip.");
        }
        return null;
    }

    /// <summary>A clip of cards alone: no buckets, and no images or drawings on the board itself.</summary>
    private static bool CardsOnly(BoardClip clip) =>
        (clip.Buckets ?? []).Count == 0 && !LooseImages(clip).Any() && !LooseDrawings(clip).Any();

    private static IEnumerable<ClippedImage> LooseImages(BoardClip clip) => clip.Images.Where(m => m.CardId is null);

    private static IEnumerable<ClippedDrawing> LooseDrawings(BoardClip clip) => clip.Drawings.Where(d => d.CardId is null);

    /// <summary>The clip's assignees who can be assigned on the board: only they stay on the copies.</summary>
    private static async Task<HashSet<Guid>> AssignableAsync(PrikBoardDbContext db, Guid boardId, BoardClip clip, CancellationToken ct)
    {
        var people = clip.Cards.SelectMany(n => n.AssigneeIds ?? []).Distinct().ToList();
        if (people.Count == 0) return [];
        return (await BoardAccess.AssignableIds(db, boardId).Where(id => people.Contains(id)).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>The clip's cards as new cards by <paramref name="me"/>, in clip order, not placed or numbered yet.</summary>
    private static ValidationProblem? BuildCards(
        BoardClip clip, Guid boardId, Guid me, DateTimeOffset now, HashSet<Guid> assignable, out List<Card> cards)
    {
        cards = [];
        foreach (var (i, source) in clip.Cards.Index())
        {
            var tags = CardTags.Normalize(source.Tags);
            if (tags.Any(t => t.Length > CardTags.MaxLength)) return Problems.Invalid($"clip.cards[{i}].tags", $"A tag is at most {CardTags.MaxLength} characters.");
            cards.Add(new Card
            {
                BoardId = boardId,
                Title = source.Title.Trim(),
                Color = source.Color,
                Tags = tags,
                AssigneeIds = [.. (source.AssigneeIds ?? []).Where(assignable.Contains).Distinct()],
                CreatedById = me,
                CreatedAt = now.AddMilliseconds(i),
                UpdatedAt = now.AddMilliseconds(i),
            });
        }
        return null;
    }

    /// <summary>
    /// The clip's images as new images, in clip order: those on cards on their copy's canvas, the others not placed yet.
    /// <paramref name="copies"/> maps the clip's card ids to the copies.
    /// </summary>
    private static ValidationProblem? BuildImages(
        BoardClip clip, Guid boardId, Guid me, DateTimeOffset now, Dictionary<Guid, Card> copies, out List<PastedImage> images)
    {
        images = [];
        if (clip.Images.Sum(m => (long)m.Data.Length) > BoardClip.MaxImageBytes)
        {
            return Problems.Invalid("clip.images", $"A clip holds at most {MaxClipMegabytes} MB of images.");
        }
        foreach (var (i, source) in clip.Images.Index())
        {
            Card? card = null;
            if (source.CardId is { } on && !copies.TryGetValue(on, out card))
            {
                return Problems.Invalid($"clip.images[{i}].cardId", "The card is not in the clip.");
            }
            if (!ImageEndpoints.TryFromBytes(boardId, source.Data, source.FileName, source.Caption, source.Width, out var image, out var problem))
            {
                return Problems.Invalid($"clip.images[{i}].data", problem);
            }
            image.CardId = card?.Id;
            image.AddedById = me;
            image.CreatedAt = image.UpdatedAt = now.AddMilliseconds(i);
            // On the board it gets its place with the rest (PlaceOnBoardAsync).
            if (card is not null) (image.X, image.Y) = Layout.OnCardCanvas(source.X, source.Y, image.Width, image.Height);
            images.Add((image, source, source.Data));
        }
        return null;
    }

    /// <summary>The clip's drawings as new drawings, in clip order: those on the board built where they were, to be moved with the rest.</summary>
    private static ValidationProblem? BuildDrawings(
        BoardClip clip, Guid boardId, Guid me, DateTimeOffset now, Dictionary<Guid, Card> copies, out List<PastedDrawing> drawings)
    {
        drawings = [];
        foreach (var (i, source) in clip.Drawings.Index())
        {
            Card? card = null;
            if (source.CardId is { } on && !copies.TryGetValue(on, out card))
            {
                return Problems.Invalid($"clip.drawings[{i}].cardId", "The card is not in the clip.");
            }
            var drawing = new BoardDrawing
            {
                BoardId = boardId,
                CardId = card?.Id,
                AddedById = me,
                CreatedAt = now.AddMilliseconds(i),
                UpdatedAt = now.AddMilliseconds(i),
            };
            if (DrawingEndpoints.Build(drawing, source.AllStrokes(), source.X, source.Y, $"clip.drawings[{i}].") is { } invalid) return invalid;
            drawings.Add((drawing, source));
        }
        return null;
    }

    /// <summary>Adds the copies' tags that are new to the board, with their colors from the clip.</summary>
    private static async Task<ValidationProblem?> AddTagsAsync(
        PrikBoardDbContext db, Guid boardId, BoardClip clip, List<Card> cards, CancellationToken ct)
    {
        if (await TagEndpoints.EnsureBoardTagsAsync(db, boardId, [.. cards.SelectMany(n => n.Tags).Distinct()], ct) is { } tooMany) return tooMany;
        var colors = (clip.TagColors ?? []).GroupBy(p => CardTags.Normalize(p.Key)).ToDictionary(g => g.Key, g => g.First().Value);
        foreach (var tag in db.ChangeTracker.Entries<BoardTag>().Where(e => e.State == EntityState.Added).Select(e => e.Entity))
        {
            if (colors.TryGetValue(tag.Name, out var color)) tag.Color = color;
        }
        return null;
    }

    /// <summary>
    /// Places everything at X and Y (or a free spot), moved by one step from the top-left of what is on the board. Pasted
    /// buckets go on top of the board's, in their own order, and what was in them joins them again; other items join the
    /// bucket they land in. Returns the new buckets.
    /// </summary>
    private static async Task<List<Bucket>> PlaceOnBoardAsync(
        PrikBoardDbContext db, Guid boardId, PasteItemsRequest request, List<Card> cards, List<PastedImage> images,
        List<PastedDrawing> drawings, List<Bucket> buckets, List<Card> others, DateTimeOffset now, CancellationToken ct)
    {
        var clip = request.Clip;
        var clipBuckets = clip.Buckets ?? [];
        var (x, y) = request is { X: { } px, Y: { } py } ? (Layout.Snap(px), Layout.Snap(py)) : Layout.FreeSpotOutside(buckets, others);
        List<(int X, int Y)> corners =
        [
            .. clip.Cards.Select(n => (n.X, n.Y)),
            .. clipBuckets.Select(b => (b.X, b.Y)),
            .. LooseImages(clip).Select(m => (m.X, m.Y)),
            .. LooseDrawings(clip).Select(d => (d.X, d.Y)),
        ];
        var (dx, dy) = (x - Layout.Snap(corners.Min(c => c.X)), y - Layout.Snap(corners.Min(c => c.Y)));
        static int Shift(int value, int by) => Math.Clamp(Layout.Snap(value) + by, Layout.MinCoordinate, Layout.MaxCoordinate);

        var pasted = new Dictionary<Guid, Bucket>();
        var newBuckets = new List<Bucket>();
        foreach (var (i, source) in clipBuckets.Index())
        {
            var (width, height) = BoardTransferEndpoints.FittedBucketSize(source.Width, source.Height);
            var made = new Bucket
            {
                BoardId = boardId,
                Name = source.Name.Trim(),
                X = Shift(source.X, dx),
                Y = Shift(source.Y, dy),
                Width = width,
                Height = height,
                CreatedAt = now.AddMilliseconds(i),
                StackedAt = now.AddMilliseconds(i),
            };
            pasted[source.Id] = made;
            newBuckets.Add(made);
        }
        var stacked = Layout.BackToFront([.. buckets, .. newBuckets]);

        foreach (var (source, card) in clip.Cards.Zip(cards))
        {
            (card.X, card.Y) = (Shift(source.X, dx), Shift(source.Y, dy));
            if (source.BucketId is { } b) card.BucketId = pasted[b].Id;
            else Layout.Place(card, stacked);
        }
        foreach (var (image, source, _) in images.Where(m => m.Image.CardId is null))
        {
            (image.X, image.Y) = (Shift(source.X, dx), Shift(source.Y, dy));
            if (source.BucketId is { } b) image.BucketId = pasted[b].Id;
            else Layout.Place(image, stacked);
        }
        foreach (var (drawing, source) in drawings.Where(d => d.Drawing.CardId is null))
        {
            (drawing.X, drawing.Y) = (Shift(drawing.X, dx), Shift(drawing.Y, dy));
            if (source.BucketId is { } b) drawing.BucketId = pasted[b].Id;
            else Layout.Place(drawing, stacked);
        }

        // What was in them has joined them by id, so they can fold up now. An open one then takes the board's loose items
        // it covers, like a bucket drawn there (not those in other buckets); a folded one takes none, as they would vanish.
        foreach (var source in clipBuckets) pasted[source.Id].Collapsed = source.Collapsed;
        if (newBuckets.Count > 0)
        {
            List<ICanvasItem> loose =
            [
                .. await db.Images.Where(i => i.BoardId == boardId && i.CardId == null && i.BucketId == null).ToListAsync(ct),
                .. await db.Drawings.Where(d => d.BoardId == boardId && d.CardId == null && d.BucketId == null).ToListAsync(ct),
            ];
            foreach (var made in newBuckets) Layout.Adopt(made, stacked, others, loose, looseOnly: true);
        }
        return newBuckets;
    }

    /// <summary>
    /// Gives each copy (numbered by now) its components, with their links followed (<see cref="FollowLinks"/>), and its
    /// history: one Copied entry, or Moved for a cut, naming the original.
    /// </summary>
    private static ValidationProblem? AddContentAndHistory(
        PrikBoardDbContext db, BoardClip clip, List<Card> cards, Dictionary<Guid, Card> copies, Board board, Guid me,
        DateTimeOffset now, bool sameServer, bool moved)
    {
        var byNumber = clip.Cards.Zip(cards).ToDictionary(p => p.First.Number, p => p.Second);
        foreach (var (i, source) in clip.Cards.Index())
        {
            var components = (source.Components ?? [])
                .Select(b => b with { Links = b.Links is null ? null : FollowLinks(b.Links, clip.Source, sameServer, copies, byNumber, board.ShortId) })
                .ToList();
            if (CardComponentEndpoints.BuildAll(cards[i], components, $"clip.cards[{i}].") is { } invalid) return invalid;
            db.CardChanges.Add(new CardChange
            {
                CardId = cards[i].Id,
                BoardId = board.Id,
                ById = me,
                At = now,
                Entries = [new(moved ? CardChangeKind.Moved : CardChangeKind.Copied, CardLinks.ReadableId(clip.Source.BoardKey, source.Number),
                    LinkTo(CardLinks.CardPath(clip.Source.BoardShortId, source.Number), clip.Source, sameServer))],
            });
        }
        return null;
    }

    /// <summary>
    /// Deletes the originals of a cut that are still on <paramref name="from"/> (soft, as deleteCard and the others do, so
    /// they can be restored), and returns them as they were; null when none is left. Their cards' history says where they
    /// went. Runs inside the paste's transaction.
    /// </summary>
    private static async Task<MovedOriginals?> RemoveOriginalsAsync(
        PrikBoardDbContext db, Board from, BoardClip clip, IEnumerable<(ClippedCard Source, Card Copy)> copies, Board to,
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var copyOf = copies.ToDictionary(p => p.Source.Id, p => p.Copy);
        var cardIds = copyOf.Keys.ToList();
        var bucketIds = (clip.Buckets ?? []).Select(b => b.Id).ToList();
        var imageIds = clip.Images.Where(m => m.CardId is null && m.Id is not null).Select(m => m.Id!.Value).ToList();
        var drawingIds = clip.Drawings.Where(d => d.CardId is null && d.Id is not null).Select(d => d.Id!.Value).ToList();

        var cards = await db.Cards.AsNoTracking().Where(n => n.BoardId == from.Id && cardIds.Contains(n.Id)).ToListAsync(ct);
        var buckets = await db.Buckets.Where(b => b.BoardId == from.Id && bucketIds.Contains(b.Id)).ToListAsync(ct);
        var images = await db.Images.AsNoTracking().Include(i => i.File).Include(i => i.AddedBy)
            .Where(i => i.BoardId == from.Id && i.CardId == null && imageIds.Contains(i.Id)).ToListAsync(ct);
        var drawings = await db.Drawings.AsNoTracking().Include(d => d.AddedBy)
            .Where(d => d.BoardId == from.Id && d.CardId == null && drawingIds.Contains(d.Id)).ToListAsync(ct);
        if (cards.Count + buckets.Count + images.Count + drawings.Count == 0) return null;

        var principals = await CardEndpoints.PrincipalsOnAsync(db, cards, ct);
        var result = new MovedOriginals(from.Id, from.Name,
            [.. cards.Select(n => CardDetail.From(n, BoardRef.Of(from), principals))],
            [.. buckets.Select(BucketDetail.From)],
            [.. images.Select(ImageDetail.From)],
            [.. drawings.Select(DrawingDetail.From)]);

        var stamp = DateTimeOffset.UtcNow;
        await Tombstones.DeleteCardsAsync(db, [.. cards.Select(n => n.Id)], stamp, ct);
        var looseImages = images.Select(i => i.Id).ToList();
        var looseDrawings = drawings.Select(d => d.Id).ToList();
        await db.Images.Where(i => looseImages.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, stamp), ct);
        await db.Drawings.Where(d => looseDrawings.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, stamp), ct);
        foreach (var card in cards)
        {
            var copy = copyOf[card.Id];
            CardHistory.Record(db, principal, card.Id, from.Id,
                new(CardChangeKind.MovedAway, CardLinks.ReadableId(to.Key, copy.Number), CardLinks.CardPath(to.ShortId, copy.Number)));
        }

        // A bucket goes like deleteBucket: what else is in it now stays, loose or in the bucket under it.
        if (buckets.Count > 0)
        {
            foreach (var bucket in buckets) bucket.DeletedAt = stamp;
            var remaining = Layout.BackToFront(await db.Buckets.Where(b => b.BoardId == from.Id && !bucketIds.Contains(b.Id)).ToListAsync(ct));
            var left = await db.Cards.Where(n => n.BoardId == from.Id).ToListAsync(ct);
            var items = await BucketEndpoints.CanvasItemsAsync(db, from.Id, ct);
            foreach (var bucket in buckets) Layout.Release(bucket, remaining, left, items);
        }
        return result;
    }

    /// <summary>The bucket, locked until the transaction ends so two pastes into it don't take the same free spots; null when it isn't on the board.</summary>
    private static async Task<Bucket?> LockBucketAsync(PrikBoardDbContext db, Guid boardId, Guid bucketId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Buckets" WHERE "Id" = {bucketId} FOR UPDATE""", ct);
        return await db.Buckets.FirstOrDefaultAsync(b => b.Id == bucketId && b.BoardId == boardId, ct);
    }

    /// <summary>
    /// Links to the copied cards point at their copies; web links stay. Links to other cards stay on the same server, and
    /// become full links to the source server on another one (or are dropped when the clip names no usable server).
    /// </summary>
    private static List<CardLink> FollowLinks(
        IEnumerable<CardLink> links, ClipSource source, bool sameServer, Dictionary<Guid, Card> copies, Dictionary<int, Card> byNumber, string shortId)
    {
        var followed = new List<CardLink>();
        foreach (var link in links)
        {
            if (!CardLinks.TryParse(link.Url, out var target))
            {
                followed.Add(link);
                continue;
            }
            var copy = target.ByNumber
                ? target.BoardShortId == source.BoardShortId ? byNumber.GetValueOrDefault(target.Number) : null
                : copies.GetValueOrDefault(target.CardId);
            if (copy is not null) followed.Add(link with { Url = CardLinks.CardPath(shortId, copy.Number) });
            else if (LinkTo(link.Url, source, sameServer) is { } url) followed.Add(link with { Url = url });
        }
        return followed;
    }

    /// <summary>A path on the source server as a link from here: the path itself on the same server, a full URL otherwise.</summary>
    private static string? LinkTo(string path, ClipSource source, bool sameServer)
    {
        if (sameServer) return path;
        var url = source.Url.TrimEnd('/') + path.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? url : null;
    }

    private static bool SameServer(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && Uri.Compare(x, y, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static string MadeWith(BoardClip clip) => BoardTransferEndpoints.MadeWith(clip.AppVersion, "They were copied");
}
